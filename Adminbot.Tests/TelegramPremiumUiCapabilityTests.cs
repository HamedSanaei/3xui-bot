using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Adminbot.Domain.TelegramUi;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;

/// <summary>
/// Regression coverage for the tenant-bot premium capability probe.
/// </summary>
/// <remarks>
/// <para>
/// The probe decides whether a storefront owner may opt into premium visuals, so its failure classification is the single
/// most safety-critical part of this feature. Two rules are asserted here repeatedly:
/// </para>
/// <list type="bullet">
/// <item>a definitive Telegram 400 may be followed by exactly ONE plain baseline preview; and</item>
/// <item>every ambiguous outcome — local budget timeout, transport failure, 429, or 5xx — performs NO second send,
/// because the first request may already have been delivered and a duplicate preview would be a duplicate message.</item>
/// </list>
/// <para>No test contacts real Telegram; the transport is an in-process scripted client.</para>
/// </remarks>
public sealed class TelegramPremiumUiCapabilityTests
{
    private const string CustomId = "5368324170671202286";
    private const string BotId = "tenant-711-1";
    private const long OwnerChatId = 711;

    /// <summary>Scripted Telegram transport that records every preview request it receives.</summary>
    private sealed class ScriptedClient : ITelegramBotClient
    {
        /// <summary>Queued behaviours, consumed in order. An empty queue answers successfully.</summary>
        public Queue<Func<SendMessageRequest, Task>> Behaviours { get; } = new();

        /// <summary>Every preview request that reached the transport, in order.</summary>
        public List<SendMessageRequest> Sends { get; } = new();

        /// <summary>Total send attempts, including ones that threw.</summary>
        public int Attempts => Sends.Count;

        /// <inheritdoc />
        public bool LocalBotServer => false;
        /// <inheritdoc />
        public long BotId => 1;
        /// <inheritdoc />
        public TimeSpan Timeout { get; set; }
        /// <inheritdoc />
        public IExceptionParser ExceptionsParser { get; set; } = null!;

        /// <inheritdoc />
        public event AsyncEventHandler<ApiRequestEventArgs>? OnMakingApiRequest { add { } remove { } }
        /// <inheritdoc />
        public event AsyncEventHandler<ApiResponseEventArgs>? OnApiResponseReceived { add { } remove { } }
        /// <inheritdoc />
        public Task<bool> TestApi(CancellationToken cancellationToken = default) => Task.FromResult(true);
        /// <inheritdoc />
        public Task DownloadFile(TGFile file, Stream destination, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        /// <inheritdoc />
        public Task DownloadFile(string filePath, Stream destination, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        /// <summary>Records the preview and applies the next scripted behaviour.</summary>
        /// <typeparam name="TResponse">Requested response type.</typeparam>
        /// <param name="request">Request produced by the probe.</param>
        /// <param name="cancellationToken">Caller cancellation.</param>
        /// <returns>A synthetic Telegram response.</returns>
        public async Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is SendMessageRequest send)
            {
                Sends.Add(send);
                if (Behaviours.Count > 0)
                    await Behaviours.Dequeue()(send);
            }

            object result = typeof(TResponse) == typeof(bool)
                ? true
                : new Message { Id = 1, Chat = new Chat { Id = OwnerChatId } };
            return (TResponse)result;
        }
    }

    /// <summary>Transport resolver that returns one scripted client or fails closed.</summary>
    private sealed class Resolver : ITelegramPremiumUiTransportResolver
    {
        /// <summary>Client returned for the exact requested BotId.</summary>
        public ITelegramBotClient Client { get; set; } = null!;

        /// <summary>When true, resolution fails like a disabled or unknown storefront.</summary>
        public bool Unavailable { get; set; }

        /// <summary>BotId observed by the last resolution attempt.</summary>
        public string? ResolvedBotId { get; private set; }

        /// <inheritdoc />
        public ITelegramBotClient Resolve(string botId)
        {
            ResolvedBotId = botId;
            if (Unavailable)
                throw new BotTransportUnavailableException("bot_disabled_or_token_missing");
            return Client;
        }
    }

    /// <summary>Builds a catalog entry set containing only the probe key.</summary>
    /// <param name="customEmojiId">Curated identifier, or null to model an uncurated asset.</param>
    /// <returns>A catalog for probe tests.</returns>
    private static TelegramUiEmojiCatalog Catalog(string? customEmojiId = CustomId)
        => new(new[] { new TelegramUiEmoji(TelegramUiEmojiKeys.PremiumProbe, "✨", customEmojiId) });

    /// <summary>Builds a probe over a scripted transport.</summary>
    /// <param name="client">Scripted client.</param>
    /// <param name="customEmojiId">Curated identifier for the probe key.</param>
    /// <param name="unavailable">Whether transport resolution must fail.</param>
    /// <returns>The probe under test plus its resolver.</returns>
    private static (TelegramPremiumUiCapabilityProbe Probe, Resolver Resolver) Build(
        ScriptedClient client,
        string? customEmojiId = CustomId,
        bool unavailable = false)
    {
        var resolver = new Resolver { Client = client, Unavailable = unavailable };
        return (new TelegramPremiumUiCapabilityProbe(Catalog(customEmojiId), resolver), resolver);
    }

    /// <summary>Builds a valid probe request.</summary>
    /// <param name="ownerIsPremium">Authorized owner's Telegram Premium status.</param>
    /// <returns>A probe request for the tenant storefront.</returns>
    private static TelegramPremiumUiCapabilityProbeRequest Request(bool ownerIsPremium = true)
        => new(BotId, OwnerChatId, OwnerIsPremium: ownerIsPremium);

    /// <summary>A decorated send that succeeds proves the capability.</summary>
    [Fact]
    public async Task Decorated_success_reports_supported()
    {
        var client = new ScriptedClient();
        var (probe, resolver) = Build(client);

        var result = await probe.ProbeAsync(Request(), CancellationToken.None);

        Assert.Equal(TelegramPremiumUiProbeStatus.Supported, result.Status);
        Assert.True(result.IsSupported);
        Assert.Equal(1, client.Attempts);
        Assert.Equal(BotId, resolver.ResolvedBotId);
    }

    /// <summary>The decorated preview carries the curated identifier, the primary style, and an inert callback.</summary>
    [Fact]
    public async Task Decorated_preview_tests_the_emoji_capability_not_the_colour()
    {
        var client = new ScriptedClient();
        var (probe, _) = Build(client);

        await probe.ProbeAsync(Request(), CancellationToken.None);

        var button = Assert.Single(Assert.Single(((InlineKeyboardMarkup)client.Sends[0].ReplyMarkup!).InlineKeyboard));
        Assert.Equal(CustomId, button.IconCustomEmojiId);
        Assert.Equal(KeyboardButtonStyle.Primary, button.Style);
        Assert.Equal(TelegramPremiumUiCapabilityProbe.PreviewCallbackData, button.CallbackData);
        Assert.True(TelegramPremiumUiCapabilityProbe.IsPreviewCallback(button.CallbackData));
        Assert.Equal(OwnerChatId, client.Sends[0].ChatId.Identifier);
    }

    /// <summary>A definitive 400 triggers exactly one baseline retry without the custom emoji.</summary>
    /// <remarks>
    /// The baseline keeps the same text, chat, callback semantics, and style so the ONLY difference under test is custom
    /// emoji decoration; the assertion on a null identifier is what proves the fallback removed the premium-only field.
    /// </remarks>
    [Fact]
    public async Task Definitive_rejection_retries_once_without_decoration()
    {
        var client = new ScriptedClient();
        client.Behaviours.Enqueue(_ => throw new ApiRequestException("bad request", 400));
        var (probe, _) = Build(client);

        var result = await probe.ProbeAsync(Request(), CancellationToken.None);

        Assert.Equal(TelegramPremiumUiProbeStatus.Rejected, result.Status);
        Assert.False(result.IsSupported);
        Assert.Equal(2, client.Attempts);
        Assert.Equal(CustomId, Assert.Single(Assert.Single(((InlineKeyboardMarkup)client.Sends[0].ReplyMarkup!).InlineKeyboard)).IconCustomEmojiId);
        var baseline = Assert.Single(Assert.Single(((InlineKeyboardMarkup)client.Sends[1].ReplyMarkup!).InlineKeyboard));
        Assert.Null(baseline.IconCustomEmojiId);
        Assert.Equal(KeyboardButtonStyle.Primary, baseline.Style);
        Assert.Equal(client.Sends[0].Text, client.Sends[1].Text);
    }

    /// <summary>A baseline that is also rejected proves the transport itself is broken.</summary>
    [Fact]
    public async Task Baseline_rejection_is_reported_separately()
    {
        var client = new ScriptedClient();
        client.Behaviours.Enqueue(_ => throw new ApiRequestException("bad request", 400));
        client.Behaviours.Enqueue(_ => throw new ApiRequestException("bad request", 400));
        var (probe, _) = Build(client);

        var result = await probe.ProbeAsync(Request(), CancellationToken.None);

        Assert.Equal(TelegramPremiumUiProbeStatus.BaselineRejected, result.Status);
        Assert.Equal(2, client.Attempts);
    }

    /// <summary>A baseline transport failure is ambiguous, never a premium verdict.</summary>
    [Fact]
    public async Task Baseline_transport_failure_is_ambiguous()
    {
        var client = new ScriptedClient();
        client.Behaviours.Enqueue(_ => throw new ApiRequestException("bad request", 400));
        client.Behaviours.Enqueue(_ => throw new HttpRequestException("connection reset"));
        var (probe, _) = Build(client);

        var result = await probe.ProbeAsync(Request(), CancellationToken.None);

        Assert.Equal(TelegramPremiumUiProbeStatus.Ambiguous, result.Status);
        Assert.Equal(2, client.Attempts);
    }

    /// <summary>A local budget timeout is ambiguous and performs no baseline send.</summary>
    /// <remarks>
    /// The decorated request may already have been accepted by Telegram, so a second preview would duplicate a message.
    /// </remarks>
    [Fact]
    public async Task Local_timeout_is_ambiguous_without_a_baseline()
    {
        var client = new ScriptedClient();
        client.Behaviours.Enqueue(_ => throw new TelegramForegroundDeliveryTimeoutException("send_message", TimeSpan.FromSeconds(8)));
        var (probe, _) = Build(client);

        var result = await probe.ProbeAsync(Request(), CancellationToken.None);

        Assert.Equal(TelegramPremiumUiProbeStatus.Ambiguous, result.Status);
        Assert.Equal(1, client.Attempts);
    }

    /// <summary>Transport-level failures are ambiguous and perform no baseline send.</summary>
    /// <param name="kind">Which transport failure to raise.</param>
    [Theory]
    [InlineData("http")]
    [InlineData("timeout")]
    [InlineData("request")]
    [InlineData("io")]
    public async Task Transport_failures_are_ambiguous_without_a_baseline(string kind)
    {
        var client = new ScriptedClient();
        client.Behaviours.Enqueue(_ => kind switch
        {
            "http" => throw new HttpRequestException("broken"),
            "timeout" => throw new TimeoutException("slow"),
            "request" => throw new RequestException("transport"),
            _ => throw new IOException("reset")
        });
        var (probe, _) = Build(client);

        var result = await probe.ProbeAsync(Request(), CancellationToken.None);

        Assert.Equal(TelegramPremiumUiProbeStatus.Ambiguous, result.Status);
        Assert.Equal(1, client.Attempts);
    }

    /// <summary>Rate limiting and server errors are reported as transient with no blind retry.</summary>
    /// <param name="errorCode">Telegram HTTP error code.</param>
    [Theory]
    [InlineData(403)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Non_definitive_api_errors_are_transient_without_a_baseline(int errorCode)
    {
        var client = new ScriptedClient();
        client.Behaviours.Enqueue(_ => throw new ApiRequestException("failed", errorCode));
        var (probe, _) = Build(client);

        var result = await probe.ProbeAsync(Request(), CancellationToken.None);

        Assert.Equal(TelegramPremiumUiProbeStatus.TransientFailure, result.Status);
        Assert.Equal(1, client.Attempts);
    }

    /// <summary>Caller cancellation propagates and records no capability conclusion.</summary>
    [Fact]
    public async Task Caller_cancellation_propagates()
    {
        var client = new ScriptedClient();
        client.Behaviours.Enqueue(_ => throw new OperationCanceledException());
        var (probe, _) = Build(client);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.ProbeAsync(Request(), cancellation.Token));

        Assert.Equal(1, client.Attempts);
    }

    /// <summary>A non-Premium owner is answered without sending anything at all.</summary>
    /// <remarks>
    /// This is the fail-closed default: forgetting to assert the owner's Premium status must send nothing rather than
    /// probing with an unverified premise.
    /// </remarks>
    [Fact]
    public async Task Non_premium_owner_sends_nothing()
    {
        var client = new ScriptedClient();
        var (probe, resolver) = Build(client);

        var result = await probe.ProbeAsync(Request(ownerIsPremium: false), CancellationToken.None);

        Assert.Equal(TelegramPremiumUiProbeStatus.PremiumRequired, result.Status);
        Assert.Equal(0, client.Attempts);
        Assert.Null(resolver.ResolvedBotId);
    }

    /// <summary>A catalog without a curated identifier fails closed before any transport work.</summary>
    [Fact]
    public async Task Uncurated_catalog_fails_closed()
    {
        var client = new ScriptedClient();
        var (probe, resolver) = Build(client, customEmojiId: null);

        var result = await probe.ProbeAsync(Request(), CancellationToken.None);

        Assert.Equal(TelegramPremiumUiProbeStatus.CatalogUnavailable, result.Status);
        Assert.Equal(0, client.Attempts);
        Assert.Null(resolver.ResolvedBotId);
    }

    /// <summary>An unresolved storefront transport fails closed and never falls back to another bot.</summary>
    [Fact]
    public async Task Unresolved_transport_fails_closed()
    {
        var client = new ScriptedClient();
        var (probe, _) = Build(client, unavailable: true);

        var result = await probe.ProbeAsync(Request(), CancellationToken.None);

        Assert.Equal(TelegramPremiumUiProbeStatus.TransportUnavailable, result.Status);
        Assert.Equal(0, client.Attempts);
    }

    /// <summary>Invalid probe input is refused before any Telegram work.</summary>
    [Theory]
    [InlineData(null, 711L)]
    [InlineData("", 711L)]
    [InlineData(BotId, 0L)]
    [InlineData(BotId, -5L)]
    public async Task Invalid_probe_input_is_unauthorized(string? botId, long chatId)
    {
        var client = new ScriptedClient();
        var (probe, _) = Build(client);

        var result = await probe.ProbeAsync(new TelegramPremiumUiCapabilityProbeRequest(botId!, chatId), CancellationToken.None);

        Assert.Equal(TelegramPremiumUiProbeStatus.Unauthorized, result.Status);
        Assert.Equal(0, client.Attempts);
    }

    /// <summary>Only the exact preview payload is recognised as the inert probe button.</summary>
    [Theory]
    [InlineData("PUI:preview", true)]
    [InlineData("PUI:preview:1", false)]
    [InlineData("TBM:panel", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Preview_callback_match_is_exact(string? data, bool expected)
        => Assert.Equal(expected, TelegramPremiumUiCapabilityProbe.IsPreviewCallback(data));

    /// <summary>The shared classifier keeps definitive and ambiguous outcomes disjoint.</summary>
    [Fact]
    public void Failure_classification_is_disjoint()
    {
        Assert.True(TelegramPremiumUiFailureClassification.IsDefinitiveRejection(new ApiRequestException("bad", 400)));
        Assert.False(TelegramPremiumUiFailureClassification.IsDefinitiveRejection(new ApiRequestException("no", 403)));
        Assert.False(TelegramPremiumUiFailureClassification.IsDefinitiveRejection(new HttpRequestException("x")));

        Assert.True(TelegramPremiumUiFailureClassification.IsAmbiguousTransmission(
            new TelegramForegroundDeliveryTimeoutException("send_message", TimeSpan.FromSeconds(8))));
        Assert.True(TelegramPremiumUiFailureClassification.IsAmbiguousTransmission(new HttpRequestException("x")));
        Assert.False(TelegramPremiumUiFailureClassification.IsAmbiguousTransmission(new ApiRequestException("bad", 400)));
    }
}
