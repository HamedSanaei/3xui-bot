using Adminbot.Domain;
using Telegram.Bot.Types;

/// <summary>Restores bot identity and owns the disposable service graph of one scheduled execution.</summary>
/// <remarks>Per-operation stores own short database contexts; legacy coordinated workflows share only this execution's unit of work.</remarks>
public sealed class TelegramUpdateExecutor : ITelegramUpdateExecutor
{
    private readonly IServiceScopeFactory _scopes;
    private readonly BotRegistry _registry;
    private readonly BotClientProvider _clients;
    private readonly BotContextAccessor _context;
    private readonly TelegramForegroundDeliveryPolicy _foregroundDelivery;
    /// <summary>Creates an executor without capturing a mutable handler or database context.</summary>
    /// <param name="scopes">Application scope factory for one logical Telegram execution.</param>
    /// <param name="registry">Current owned, tenant, and assistant bot definitions.</param>
    /// <param name="clients">Bot-keyed client provider; tokens are never persisted in work items.</param>
    /// <param name="context">Ambient bot scope accessor, restored after each execution.</param>
    /// <param name="foregroundDelivery">
    /// Immutable interactive Telegram delivery budget applied to this execution's UX calls. A missing value keeps the
    /// production eight-second budget, so production wiring stays unchanged and tests can inject millisecond windows.
    /// </param>
    /// <remarks>The executor is a singleton holding factories and runtime registries; each invocation owns its context scope and restores ambient bot identity on exit.</remarks>
    public TelegramUpdateExecutor(
        IServiceScopeFactory scopes,
        BotRegistry registry,
        BotClientProvider clients,
        BotContextAccessor context,
        TelegramForegroundDeliveryPolicy foregroundDelivery = null)
    {
        _scopes = scopes;
        _registry = registry;
        _clients = clients;
        _context = context;
        _foregroundDelivery = foregroundDelivery ?? TelegramForegroundDeliveryPolicy.Production;
    }

    /// <inheritdoc />
    public bool IsAvailable(string botId)
    {
        var bot = _registry.GetById(botId);
        return bot != null && string.Equals(bot.Id, botId, StringComparison.OrdinalIgnoreCase)
            && bot.Enabled && !string.IsNullOrWhiteSpace(bot.Token);
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(TelegramUpdateWorkItem item, CancellationToken cancellationToken)
    {
        var bot = _registry.GetById(item.Key.BotId);
        if (bot == null || !string.Equals(bot.Id, item.Key.BotId, StringComparison.OrdinalIgnoreCase) || !bot.Enabled)
            throw new InvalidOperationException("Bot became unavailable after the update claim.");
        // Only this update execution receives the bounded client view. The receiver and every background worker keep
        // the raw client, so long polling, durable outbox delivery, and file relay are unaffected by the interactive
        // budget. The decorator wraps the shared instance and never disposes it.
        var client = new ForegroundBoundedTelegramBotClient(_clients.GetClient(bot.Id), _foregroundDelivery);
        var runtime = new BotRuntimeContext { Config = RuntimeSnapshot.Copy(bot), Client = client };
        using (_context.Push(runtime))
        // The interaction actor is resolved once from the durable update and published for the whole execution, so
        // UX-only telemetry such as callback-acknowledgement latency can name the waiting user even though most call
        // sites only hold an opaque callback id. It is diagnostics-only and never participates in authorization.
        using (TelegramInteractionActor.Push(ResolveActor(item.Update)))
        {
            await using var scope = _scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<TelegramBotService>()
                .DispatchUpdateAsync(client, item.Update, runtime, cancellationToken);
        }
    }

    /// <summary>
    /// Resolves the Telegram user id of the actor that produced one update, for UX telemetry attribution only.
    /// </summary>
    /// <param name="update">Private Telegram update that has already been claimed and deserialized by the scheduler.</param>
    /// <returns>
    /// The sender's numeric Telegram user id from the first actor-bearing shape the update carries, or <c>null</c> for
    /// updates that genuinely have no sender (channel posts, poll-answer bookkeeping, and similar shapes).
    /// </returns>
    /// <remarks>
    /// The order mirrors how the update router resolves a customer identity, so the id recorded here matches the id the
    /// handler uses. No text, callback payload, username, or chat title is ever read.
    /// </remarks>
    private static long? ResolveActor(Update update)
    {
        if (update == null)
            return null;

        return update.CallbackQuery?.From?.Id
            ?? update.Message?.From?.Id
            ?? update.EditedMessage?.From?.Id
            ?? update.InlineQuery?.From?.Id
            ?? update.ChosenInlineResult?.From?.Id
            ?? update.PreCheckoutQuery?.From?.Id
            ?? update.ShippingQuery?.From?.Id
            ?? update.PollAnswer?.User?.Id
            ?? update.MyChatMember?.From?.Id
            ?? update.ChatMember?.From?.Id;
    }
}
