using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Telegram.Bot;
using Telegram.Bot.Exceptions;

/// <summary>
/// Validates referral configuration before the host starts accepting payments.
/// </summary>
public static class ReferralConfigurationValidator
{
    /// <summary>Configuration paths that must be explicitly present so valid zero values are never inferred.</summary>
    private static readonly string[] RequiredConfigurationPaths =
    {
        "enabled",
        "minimumEligiblePaymentAmountToman",
        "firstPayment:referrerRewardPercent",
        "firstPayment:referredRewardPercent",
        "firstPayment:referredMinimumRewardToman",
        "firstPayment:referredMaximumRewardToman",
        "subsequentPayments:referrerRewardPercent"
    };

    /// <summary>
    /// Requires the complete referral JSON section and validates its bound business values without hidden defaults.
    /// </summary>
    /// <param name="configuration">
    /// Application configuration root loaded from <c>Data/configuration.json</c>; it must expose every documented
    /// referral key even when the feature is disabled.
    /// </param>
    /// <remarks>
    /// Numeric zero is valid for selected fields, so object binding alone cannot distinguish an explicit zero from a
    /// missing key. This structural check runs before value validation to keep production pricing intentional.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Thrown when no application configuration root is supplied.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the section/key is absent or a configured value is unsafe.</exception>
    /// <example><code>ReferralConfigurationValidator.ValidateConfigurationAndThrow(configuration);</code></example>
    public static void ValidateConfigurationAndThrow(IConfiguration configuration)
    {
        if (configuration == null)
            throw new ArgumentNullException(nameof(configuration));

        var section = configuration.GetSection("referral");
        if (!section.Exists())
            throw new InvalidOperationException("The referral configuration section is required.");

        foreach (var path in RequiredConfigurationPaths)
        {
            if (section[path] == null)
                throw new InvalidOperationException($"referral.{path.Replace(':', '.')} must be explicitly configured.");
        }

        ValidateAndThrow(section.Get<ReferralOptions>());
    }

    /// <summary>
    /// Validates every explicit referral amount and percentage and throws when enabled configuration is unsafe.
    /// </summary>
    /// <param name="options">
    /// Referral settings bound from <c>configuration.json</c>. A null value is invalid because settlement must not
    /// silently invent reward rules.
    /// </param>
    /// <remarks>
    /// Disabled configuration still validates object presence but does not require a positive payment threshold.
    /// When enabled, percentages must be between zero and one hundred and a non-zero maximum must not be smaller
    /// than the configured referred-user minimum.
    /// </remarks>
    /// <exception cref="InvalidOperationException">Thrown when enabled referral settings are incomplete or contradictory.</exception>
    /// <example>
    /// <code>
    /// var appConfig = configuration.Get&lt;AppConfig&gt;();
    /// ReferralConfigurationValidator.ValidateAndThrow(appConfig.Referral);
    /// </code>
    /// </example>
    public static void ValidateAndThrow(ReferralOptions options)
    {
        if (options == null)
            throw new InvalidOperationException("The referral configuration section is required.");

        if (!options.Enabled)
            return;

        if (options.MinimumEligiblePaymentAmountToman <= 0)
            throw new InvalidOperationException("referral.minimumEligiblePaymentAmountToman must be greater than zero.");
        if (options.FirstPayment == null)
            throw new InvalidOperationException("referral.firstPayment is required when referral is enabled.");
        if (options.SubsequentPayments == null)
            throw new InvalidOperationException("referral.subsequentPayments is required when referral is enabled.");

        ValidatePercent(options.FirstPayment.ReferrerRewardPercent, "referral.firstPayment.referrerRewardPercent");
        ValidatePercent(options.FirstPayment.ReferredRewardPercent, "referral.firstPayment.referredRewardPercent");
        ValidatePercent(options.SubsequentPayments.ReferrerRewardPercent, "referral.subsequentPayments.referrerRewardPercent");

        if (options.FirstPayment.ReferredMinimumRewardToman < 0)
            throw new InvalidOperationException("referral.firstPayment.referredMinimumRewardToman cannot be negative.");
        if (options.FirstPayment.ReferredMaximumRewardToman < 0)
            throw new InvalidOperationException("referral.firstPayment.referredMaximumRewardToman cannot be negative.");
        if (options.FirstPayment.ReferredMaximumRewardToman > 0 &&
            options.FirstPayment.ReferredMaximumRewardToman < options.FirstPayment.ReferredMinimumRewardToman)
        {
            throw new InvalidOperationException(
                "referral.firstPayment.referredMaximumRewardToman must be zero or greater than/equal to the minimum reward.");
        }
    }

    /// <summary>
    /// Validates one configured percentage without rounding or substituting a default.
    /// </summary>
    /// <param name="value">Percentage value in the inclusive range zero through one hundred.</param>
    /// <param name="path">Configuration path included in a startup validation error; it must not contain secrets.</param>
    /// <exception cref="InvalidOperationException">Thrown when the percentage is outside the supported range.</exception>
    private static void ValidatePercent(decimal value, string path)
    {
        if (value < 0 || value > 100)
            throw new InvalidOperationException($"{path} must be between 0 and 100.");
    }
}

/// <summary>
/// Converts positive Telegram user ids to compact, deterministic referral codes and back.
/// </summary>
/// <remarks>
/// Codes are base-36 and contain no secret. They are stable across owned bots so a link created in one brand can
/// establish the same global relationship when opened in another owned brand.
/// </remarks>
public static class ReferralCodeCodec
{
    /// <summary>Ordinal alphabet used for reversible, secret-free base-36 Telegram user identifiers.</summary>
    private const string Alphabet = "0123456789abcdefghijklmnopqrstuvwxyz";

    /// <summary>
    /// Encodes a positive numeric Telegram user id as a lowercase base-36 referral code.
    /// </summary>
    /// <param name="telegramUserId">Positive numeric Telegram user id of the referrer.</param>
    /// <returns>A non-empty lowercase code safe for a Telegram start payload.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the Telegram user id is not positive.</exception>
    /// <example><code>var code = ReferralCodeCodec.Encode(123456789);</code></example>
    public static string Encode(long telegramUserId)
    {
        if (telegramUserId <= 0)
            throw new ArgumentOutOfRangeException(nameof(telegramUserId), "Telegram user id must be positive.");

        var value = telegramUserId;
        var characters = new Stack<char>();
        while (value > 0)
        {
            characters.Push(Alphabet[(int)(value % 36)]);
            value /= 36;
        }

        return new string(characters.ToArray());
    }

    /// <summary>
    /// Decodes a lowercase or uppercase base-36 referral code to a positive Telegram user id.
    /// </summary>
    /// <param name="code">Referral code from a Telegram <c>/start ref_...</c> payload.</param>
    /// <param name="telegramUserId">Decoded positive Telegram user id when the method returns <c>true</c>.</param>
    /// <returns><c>true</c> when the complete code is valid and fits in a signed 64-bit integer; otherwise <c>false</c>.</returns>
    /// <example><code>if (ReferralCodeCodec.TryDecode(code, out var referrerId)) { /* register */ }</code></example>
    public static bool TryDecode(string code, out long telegramUserId)
    {
        telegramUserId = 0;
        if (string.IsNullOrWhiteSpace(code))
            return false;

        try
        {
            long value = 0;
            foreach (var rawCharacter in code.Trim())
            {
                var character = char.ToLowerInvariant(rawCharacter);
                var digit = Alphabet.IndexOf(character);
                if (digit < 0)
                    return false;
                value = checked((value * 36) + digit);
            }

            telegramUserId = value;
            return value > 0;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}

/// <summary>
/// Sends one fail-soft referral notification through an owned bot the recipient can reach.
/// </summary>
public interface IReferralNotificationSender
{
    /// <summary>
    /// Attempts to send one plain-text notification without allowing Telegram failure to affect financial settlement.
    /// </summary>
    /// <param name="telegramUserId">Numeric Telegram id of the referral beneficiary.</param>
    /// <param name="preferredBotId">Owned bot id associated with the referral/payment, tried before fallback owned bots.</param>
    /// <param name="text">Plain user-facing text; no parse mode is applied.</param>
    /// <param name="cancellationToken">Cancellation token for users.db discovery and Telegram delivery.</param>
    /// <returns><c>true</c> after the first successful owned-bot delivery; otherwise <c>false</c>.</returns>
    Task<bool> SendAsync(long telegramUserId, string preferredBotId, string text, CancellationToken cancellationToken);
}

/// <summary>
/// Telegram implementation of referral notification delivery with owned-bot-only routing.
/// </summary>
public sealed class ReferralNotificationSender : IReferralNotificationSender
{
    /// <summary>Creates isolated users.db readers for discovering owned bots previously used by the recipient.</summary>
    private readonly UserDbContextFactory _userDbContextFactory;
    /// <summary>Provides the authoritative runtime list used to exclude tenant and assistant bots.</summary>
    private readonly BotRegistry _botRegistry;
    /// <summary>Resolves Telegram clients for owned-bot delivery candidates.</summary>
    private readonly BotClientProvider _botClientProvider;
    /// <summary>Records fail-soft notification delivery failures without affecting financial settlement.</summary>
    private readonly ILogger<ReferralNotificationSender> _logger;

    /// <summary>
    /// Creates an owned-bot referral notification sender.
    /// </summary>
    /// <param name="userDbContextFactory">Factory used to discover bot-scoped user state without sharing a DbContext.</param>
    /// <param name="botRegistry">Registry used to exclude tenant and assistant bots.</param>
    /// <param name="botClientProvider">Telegram client provider for the selected owned bot.</param>
    /// <param name="logger">Fail-soft delivery logger.</param>
    public ReferralNotificationSender(
        UserDbContextFactory userDbContextFactory,
        BotRegistry botRegistry,
        BotClientProvider botClientProvider,
        ILogger<ReferralNotificationSender> logger)
    {
        _userDbContextFactory = userDbContextFactory;
        _botRegistry = botRegistry;
        _botClientProvider = botClientProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<bool> SendAsync(
        long telegramUserId,
        string preferredBotId,
        string text,
        CancellationToken cancellationToken)
    {
        var ownedBots = _botRegistry.Bots
            .Where(x => string.Equals(x.Type, BotInstanceTypes.Owned, StringComparison.OrdinalIgnoreCase) &&
                        x.Enabled &&
                        !string.IsNullOrWhiteSpace(x.Token))
            .ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        if (ownedBots.Count == 0)
            return false;

        await using var context = _userDbContextFactory.CreateDbContext();
        var interactedBotIds = await context.BotUserStates
            .AsNoTracking()
            .Where(x => x.TelegramUserId == telegramUserId)
            .Select(x => x.BotId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var candidates = new List<string>();
        AddCandidate(candidates, preferredBotId, ownedBots);
        foreach (var botId in interactedBotIds)
            AddCandidate(candidates, botId, ownedBots);
        AddCandidate(candidates, _botRegistry.DefaultBot?.Id, ownedBots);

        foreach (var botId in candidates)
        {
            try
            {
                await _botClientProvider.GetClient(botId).SendTextMessageAsync(
                    chatId: telegramUserId,
                    text: text,
                    cancellationToken: cancellationToken);
                return true;
            }
            catch (ApiRequestException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Referral notification delivery skipped. botId={BotId}, userId={TelegramUserId}, errorCode={ErrorCode}",
                    botId,
                    telegramUserId,
                    ex.ErrorCode);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Referral notification delivery failed. botId={BotId}, userId={TelegramUserId}", botId, telegramUserId);
            }
        }

        return false;
    }

    /// <summary>
    /// Adds one distinct owned bot id to the ordered delivery candidates.
    /// </summary>
    /// <param name="candidates">Mutable ordered candidate list.</param>
    /// <param name="botId">Possible owned bot id; empty or unknown ids are ignored.</param>
    /// <param name="ownedBots">Owned bot lookup used as the tenant-exclusion boundary.</param>
    private static void AddCandidate(
        ICollection<string> candidates,
        string botId,
        IReadOnlyDictionary<string, BotInstanceConfig> ownedBots)
    {
        if (!string.IsNullOrWhiteSpace(botId) &&
            ownedBots.ContainsKey(botId) &&
            !candidates.Contains(botId, StringComparer.OrdinalIgnoreCase))
        {
            candidates.Add(botId);
        }
    }
}

/// <summary>
/// Implements global owned-bot referral registration, reward settlement, reporting, and crash recovery.
/// </summary>
/// <remarks>
/// Relationship, payment-event, reward, and ledger uniqueness are enforced by users.db. The existing credentials
/// wallet API and schema remain unchanged. Rewards use persisted <c>crediting</c>/<c>credited</c> states so completed
/// credits can repair their audit row, while an interruption during the wallet call fails closed for manual review.
/// </remarks>
public sealed class ReferralService
{
    /// <summary>Only wallet-charge payment events can enter referral settlement.</summary>
    private const string WalletChargePaymentType = "wallet_charge";
    /// <summary>Real payment providers currently supported for final owned-wallet referral rewards.</summary>
    private static readonly HashSet<string> EligibleProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "nowpayments",
        "hooshpay",
        "zibal"
    };
    /// <summary>
    /// Process-local planner gate that reduces SQLite contention; database unique constraints remain the final
    /// cross-process protection.
    /// </summary>
    private static readonly SemaphoreSlim ProcessingGate = new(1, 1);
    /// <summary>Validated global referral pricing and eligibility snapshot used for newly planned events.</summary>
    private readonly ReferralOptions _options;
    /// <summary>Factory for independent users.db event, reward, and notification contexts.</summary>
    private readonly UserDbContextFactory _userDbContextFactory;
    /// <summary>Unchanged shared credentials wallet/profile store.</summary>
    private readonly CredentialsDbContext _credentialsDbContext;
    /// <summary>Idempotent users.db ledger writer required for every referral wallet mutation.</summary>
    private readonly WalletLedgerService _walletLedgerService;
    /// <summary>Fail-soft owned-bot notification boundary.</summary>
    private readonly IReferralNotificationSender _notificationSender;
    /// <summary>Operational logger for retryable planning and settlement failures.</summary>
    private readonly ILogger<ReferralService> _logger;

    /// <summary>
    /// Creates the global referral service.
    /// </summary>
    /// <param name="configuration">Application configuration containing explicit referral business rules.</param>
    /// <param name="userDbContextFactory">Per-operation users.db context factory.</param>
    /// <param name="credentialsDbContext">Shared credentials context containing existing user wallet balances.</param>
    /// <param name="walletLedgerService">Idempotent users.db wallet-ledger writer.</param>
    /// <param name="notificationSender">Fail-soft owned-bot notification sender.</param>
    /// <param name="logger">Operational logger for retryable reward failures.</param>
    public ReferralService(
        IConfiguration configuration,
        UserDbContextFactory userDbContextFactory,
        CredentialsDbContext credentialsDbContext,
        WalletLedgerService walletLedgerService,
        IReferralNotificationSender notificationSender,
        ILogger<ReferralService> logger)
    {
        ReferralConfigurationValidator.ValidateConfigurationAndThrow(configuration);
        _options = (configuration.Get<AppConfig>() ?? new AppConfig()).Referral;
        _userDbContextFactory = userDbContextFactory;
        _credentialsDbContext = credentialsDbContext;
        _walletLedgerService = walletLedgerService;
        _notificationSender = notificationSender;
        _logger = logger;
    }

    /// <summary>
    /// Extracts a referral code from an exact Telegram <c>/start ref_...</c> payload.
    /// </summary>
    /// <param name="text">Incoming owned-bot message text; payment return and ordinary start payloads are rejected.</param>
    /// <param name="code">Normalized referral code when parsing succeeds.</param>
    /// <returns><c>true</c> only for an exact referral start payload with a non-empty code.</returns>
    public static bool TryParseStartPayload(string text, out string code)
    {
        code = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        const string prefix = "/start ref_";
        var trimmed = text.Trim();
        if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var candidate = trimmed[prefix.Length..].Trim();
        if (candidate.Length == 0 || candidate.Any(char.IsWhiteSpace))
            return false;

        code = candidate;
        return true;
    }

    /// <summary>
    /// Registers the first global referrer for an owned-bot user and never replaces an existing relationship.
    /// </summary>
    /// <param name="referredTelegramUserId">Numeric Telegram id of the user who opened the referral link.</param>
    /// <param name="referralCode">Base-36 code extracted from the owned-bot start payload.</param>
    /// <param name="attributionBotId">Owned bot id that accepted the payload; it is reporting metadata only.</param>
    /// <param name="botType">Current runtime bot type. Tenant and assistant values are rejected.</param>
    /// <param name="cancellationToken">Cancellation token for credentials and users.db operations.</param>
    /// <returns>The exact immutable registration outcome and relationship when one exists.</returns>
    /// <remarks>
    /// A unique database constraint on referred Telegram id is the final first-writer-wins protection across all
    /// owned bots and application instances. The method verifies that the decoded referrer already exists in the
    /// shared credentials database and forbids self-referral before attempting the insert.
    /// </remarks>
    public async Task<ReferralRegistrationResult> RegisterRelationshipAsync(
        long referredTelegramUserId,
        string referralCode,
        string attributionBotId,
        string botType,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled ||
            !string.Equals(botType, BotInstanceTypes.Owned, StringComparison.OrdinalIgnoreCase))
        {
            return new ReferralRegistrationResult(ReferralRegistrationStatus.NotAvailable, null);
        }

        if (!ReferralCodeCodec.TryDecode(referralCode, out var referrerTelegramUserId))
            return new ReferralRegistrationResult(ReferralRegistrationStatus.InvalidCode, null);
        if (referrerTelegramUserId == referredTelegramUserId)
            return new ReferralRegistrationResult(ReferralRegistrationStatus.SelfReferralRejected, null);

        var referrer = await _credentialsDbContext.GetUserStatusWithId(referrerTelegramUserId);
        if (referrer == null)
            return new ReferralRegistrationResult(ReferralRegistrationStatus.InvalidCode, null);

        await using var context = _userDbContextFactory.CreateDbContext();
        var existing = await context.ReferralRelationships
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ReferredTelegramUserId == referredTelegramUserId, cancellationToken);
        if (existing != null)
            return BuildExistingRegistrationResult(existing, referrerTelegramUserId);

        var relationship = new ReferralRelationship
        {
            ReferrerTelegramUserId = referrerTelegramUserId,
            ReferredTelegramUserId = referredTelegramUserId,
            AttributionBotId = string.IsNullOrWhiteSpace(attributionBotId) ? BotContextAccessor.DefaultBotId : attributionBotId,
            ReferralCode = referralCode.Trim(),
            CreatedAtUtc = DateTime.UtcNow
        };
        context.ReferralRelationships.Add(relationship);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return new ReferralRegistrationResult(ReferralRegistrationStatus.Created, relationship);
        }
        catch (DbUpdateException)
        {
            // A different owned bot or process may have inserted the immutable relationship after our lookup.
            context.ChangeTracker.Clear();
            existing = await context.ReferralRelationships
                .AsNoTracking()
                .FirstOrDefaultAsync(x => x.ReferredTelegramUserId == referredTelegramUserId, cancellationToken);
            if (existing != null)
                return BuildExistingRegistrationResult(existing, referrerTelegramUserId);
            throw;
        }
    }

    /// <summary>
    /// Processes referral rewards after a final original owned-bot wallet credit has succeeded.
    /// </summary>
    /// <param name="source">Final provider payment facts and original-credit state.</param>
    /// <param name="cancellationToken">Cancellation token for event planning, wallet, ledger, and notification work.</param>
    /// <returns>A task that completes when rewards are applied or safely left pending/failed for reconciliation.</returns>
    /// <remarks>
    /// Below-minimum payments return before creating an event and therefore do not consume first-payment eligibility.
    /// Provisional, tenant, website-wallet, partial, pending, reversed, and non-provider credits must be represented
    /// with ineligible source fields and are rejected. Notification failure never changes payment or reward status.
    /// </remarks>
    public async Task ProcessFinalOwnedWalletPaymentAsync(
        ReferralPaymentSource source,
        CancellationToken cancellationToken = default)
    {
        if (!IsEligibleSource(source))
            return;

        string sourcePaymentKey;
        try
        {
            sourcePaymentKey = BuildSourcePaymentKey(source.Provider, source.PaymentType, source.ProviderPaymentId);
        }
        catch (ArgumentException ex)
        {
            // Referral failure must never escape after the provider settlement has already credited the wallet.
            _logger.LogError(ex, "Referral source payment identity is invalid. provider={Provider}, userId={TelegramUserId}", source.Provider, source.TelegramUserId);
            return;
        }

        try
        {
            await ProcessingGate.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The original provider credit is already final; persisted reconciliation will resume referral work.
            return;
        }
        try
        {
            var eventId = await EnsureEventAndRewardsAsync(source, cancellationToken);
            if (!eventId.HasValue)
                return;

            await ApplyEventRewardsAsync(eventId.Value, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation never changes the successful original payment; exact keys make later replay safe.
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Referral payment processing was interrupted and remains retryable. sourcePaymentKey={SourcePaymentKey}, userId={TelegramUserId}",
                sourcePaymentKey,
                source.TelegramUserId);
        }
        finally
        {
            ProcessingGate.Release();
        }
    }

    /// <summary>
    /// Retries pending/failed rewards and unsent referral notifications already persisted in users.db.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for reconciliation database, wallet, ledger, and notification work.</param>
    /// <returns>The number of referral events inspected during this reconciliation pass.</returns>
    /// <remarks>
    /// This operation does not discover provider payments by itself. The hosted reconciliation service first feeds
    /// final payment rows back through <see cref="ProcessFinalOwnedWalletPaymentAsync"/>, then invokes this method
    /// to finish any event interrupted after planning.
    /// </remarks>
    public async Task<int> ReconcilePendingAsync(CancellationToken cancellationToken = default)
    {
        List<long> eventIds;
        await using (var context = _userDbContextFactory.CreateDbContext())
        {
            eventIds = await context.ReferralPaymentEvents
                .AsNoTracking()
                .Where(x => x.Status != ReferralProcessingStatuses.Completed ||
                            context.ReferralRewards.Any(r =>
                                r.ReferralPaymentEventId == x.Id &&
                                r.Status == ReferralProcessingStatuses.Applied &&
                                r.NotifiedAtUtc == null))
                .OrderBy(x => x.CreatedAtUtc)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);
        }

        foreach (var eventId in eventIds)
        {
            await ProcessingGate.WaitAsync(cancellationToken);
            try
            {
                await ApplyEventRewardsAsync(eventId, cancellationToken);
            }
            finally
            {
                ProcessingGate.Release();
            }
        }

        return eventIds.Count;
    }

    /// <summary>
    /// Reads global referral statistics for one user across every owned bot.
    /// </summary>
    /// <param name="telegramUserId">Numeric Telegram id of the user viewing their referral dashboard.</param>
    /// <param name="cancellationToken">Cancellation token for users.db aggregate queries.</param>
    /// <returns>Global invited, eligible, applied, pending, and failed reward totals.</returns>
    public async Task<ReferralUserStats> GetUserStatsAsync(
        long telegramUserId,
        CancellationToken cancellationToken = default)
    {
        await using var context = _userDbContextFactory.CreateDbContext();
        var invitedCount = await context.ReferralRelationships
            .CountAsync(x => x.ReferrerTelegramUserId == telegramUserId, cancellationToken);
        var eligibleCount = await context.ReferralPaymentEvents
            .Where(x => x.ReferrerTelegramUserId == telegramUserId &&
                        x.IsFirstEligiblePayment &&
                        x.Status == ReferralProcessingStatuses.Completed)
            .Select(x => x.ReferredTelegramUserId)
            .Distinct()
            .CountAsync(cancellationToken);
        var totalRewards = await context.ReferralRewards
            .Where(x => x.BeneficiaryTelegramUserId == telegramUserId &&
                        x.Status == ReferralProcessingStatuses.Applied)
            .SumAsync(x => (long?)x.RewardAmountToman, cancellationToken) ?? 0;
        var pending = await context.ReferralRewards.CountAsync(
            x => x.BeneficiaryTelegramUserId == telegramUserId &&
                 (x.Status == ReferralProcessingStatuses.Pending ||
                  x.Status == ReferralProcessingStatuses.Credited),
            cancellationToken);
        var failed = await context.ReferralRewards.CountAsync(
            x => x.BeneficiaryTelegramUserId == telegramUserId &&
                 x.Status == ReferralProcessingStatuses.Failed,
            cancellationToken);

        return new ReferralUserStats(invitedCount, eligibleCount, totalRewards, pending, failed);
    }

    /// <summary>
    /// Builds the stable idempotency key for a real provider payment.
    /// </summary>
    /// <param name="provider">Normalized payment provider name without secrets.</param>
    /// <param name="paymentType">Payment purpose such as <c>wallet_charge</c>.</param>
    /// <param name="providerPaymentId">Stable provider-owned payment, invoice, or track id.</param>
    /// <returns>
    /// A <c>provider:payment-type:provider-payment-id</c> key. Provider and payment type are lowercase, while the
    /// provider-owned identifier preserves its original casing so distinct external identifiers cannot collapse.
    /// </returns>
    /// <exception cref="ArgumentException">Thrown when any component is empty or contains a colon.</exception>
    public static string BuildSourcePaymentKey(string provider, string paymentType, string providerPaymentId)
    {
        var components = new[] { provider, paymentType, providerPaymentId };
        if (components.Any(string.IsNullOrWhiteSpace) || components.Any(x => x.Contains(':')))
            throw new ArgumentException("Referral source key components must be non-empty and cannot contain a colon.");

        return $"{provider.Trim().ToLowerInvariant()}:{paymentType.Trim().ToLowerInvariant()}:{providerPaymentId.Trim()}";
    }

    /// <summary>
    /// Calculates a reward percentage in Iranian toman using deterministic floor rounding.
    /// </summary>
    /// <param name="sourceAmountToman">Positive source payment amount in Iranian toman.</param>
    /// <param name="percent">Configured percentage in the inclusive range zero through one hundred.</param>
    /// <returns>The non-negative integer toman reward rounded down to avoid over-crediting.</returns>
    public static long CalculatePercentageReward(long sourceAmountToman, decimal percent)
    {
        if (sourceAmountToman <= 0 || percent <= 0)
            return 0;
        return checked((long)Math.Floor(sourceAmountToman * percent / 100m));
    }

    /// <summary>
    /// Determines whether a source represents a final eligible owned-bot wallet charge.
    /// </summary>
    /// <param name="source">Payment facts supplied by a provider settlement service.</param>
    /// <returns><c>true</c> only when every referral eligibility guard passes.</returns>
    /// <remarks>
    /// Both the declared bot type and the canonical <c>tenant-</c> id namespace are checked. This defense-in-depth
    /// rule prevents a legacy settlement caller with an incorrect bot type from rewarding a tenant payment.
    /// </remarks>
    private bool IsEligibleSource(ReferralPaymentSource source)
    {
        return _options.Enabled &&
               source != null &&
               source.TelegramUserId > 0 &&
               source.AmountToman >= _options.MinimumEligiblePaymentAmountToman &&
               source.OriginalWalletCreditApplied &&
               source.IsProviderFinal &&
               !source.IsProvisional &&
               string.Equals(source.BotType, BotInstanceTypes.Owned, StringComparison.OrdinalIgnoreCase) &&
               !(source.BotId ?? string.Empty).StartsWith("tenant-", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(source.PaymentType, WalletChargePaymentType, StringComparison.OrdinalIgnoreCase) &&
               EligibleProviders.Contains(source.Provider) &&
               !string.IsNullOrWhiteSpace(source.ProviderPaymentId);
    }

    /// <summary>
    /// Creates or reloads the idempotent payment event and its reward plan.
    /// </summary>
    /// <param name="source">Validated final owned-bot wallet payment.</param>
    /// <param name="cancellationToken">Cancellation token for users.db planning work.</param>
    /// <returns>The event id, or <c>null</c> when no prior referral relationship is eligible.</returns>
    private async Task<long?> EnsureEventAndRewardsAsync(
        ReferralPaymentSource source,
        CancellationToken cancellationToken)
    {
        var sourceKey = BuildSourcePaymentKey(source.Provider, source.PaymentType, source.ProviderPaymentId);
        await using var context = _userDbContextFactory.CreateDbContext();
        var existingEvent = await context.ReferralPaymentEvents
            .FirstOrDefaultAsync(x => x.SourcePaymentKey == sourceKey, cancellationToken);
        if (existingEvent != null)
        {
            await EnsureRewardRowsAsync(context, existingEvent, cancellationToken);
            return existingEvent.Id;
        }

        var relationship = await context.ReferralRelationships
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ReferredTelegramUserId == source.TelegramUserId, cancellationToken);
        if (relationship == null || relationship.CreatedAtUtc > source.SettledAtUtc)
            return null;

        var hasFirstEligible = await context.ReferralPaymentEvents
            .AnyAsync(x => x.ReferredTelegramUserId == source.TelegramUserId && x.IsFirstEligiblePayment, cancellationToken);
        var referralEvent = CreatePaymentEvent(source, sourceKey, relationship, isFirstEligible: !hasFirstEligible);
        context.ReferralPaymentEvents.Add(referralEvent);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            context.ChangeTracker.Clear();
            existingEvent = await context.ReferralPaymentEvents
                .FirstOrDefaultAsync(x => x.SourcePaymentKey == sourceKey, cancellationToken);
            if (existingEvent != null)
            {
                await EnsureRewardRowsAsync(context, existingEvent, cancellationToken);
                return existingEvent.Id;
            }

            // Another process consumed the global first-payment slot; this eligible source becomes recurring.
            referralEvent = CreatePaymentEvent(source, sourceKey, relationship, isFirstEligible: false);
            context.ReferralPaymentEvents.Add(referralEvent);
            await context.SaveChangesAsync(cancellationToken);
        }

        await EnsureRewardRowsAsync(context, referralEvent, cancellationToken);
        return referralEvent.Id;
    }

    /// <summary>
    /// Creates the event entity from final payment and immutable relationship snapshots.
    /// </summary>
    /// <param name="source">Validated final source payment.</param>
    /// <param name="sourceKey">Stable provider idempotency key.</param>
    /// <param name="relationship">Immutable referral relationship that predates the payment.</param>
    /// <param name="isFirstEligible">Whether this event owns the database-protected first-payment slot.</param>
    /// <returns>A new untracked payment event ready for insertion.</returns>
    private static ReferralPaymentEvent CreatePaymentEvent(
        ReferralPaymentSource source,
        string sourceKey,
        ReferralRelationship relationship,
        bool isFirstEligible)
    {
        return new ReferralPaymentEvent
        {
            ReferralRelationshipId = relationship.Id,
            SourcePaymentKey = sourceKey,
            Provider = source.Provider.Trim().ToLowerInvariant(),
            PaymentType = source.PaymentType.Trim().ToLowerInvariant(),
            ProviderPaymentId = source.ProviderPaymentId.Trim(),
            BotId = string.IsNullOrWhiteSpace(source.BotId) ? relationship.AttributionBotId : source.BotId,
            ReferredTelegramUserId = relationship.ReferredTelegramUserId,
            ReferrerTelegramUserId = relationship.ReferrerTelegramUserId,
            SourceAmountToman = source.AmountToman,
            IsFirstEligiblePayment = isFirstEligible,
            Status = ReferralProcessingStatuses.Pending,
            SourceSettledAtUtc = source.SettledAtUtc,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Persists all non-zero reward rows for an event using configuration snapshots and unique keys.
    /// </summary>
    /// <param name="context">Per-operation users.db context tracking the parent event.</param>
    /// <param name="referralEvent">Persisted event whose reward plan may be absent after an interrupted process.</param>
    /// <param name="cancellationToken">Cancellation token for reward lookup and insertion.</param>
    /// <returns>A task that completes after the idempotent reward plan is present.</returns>
    private async Task EnsureRewardRowsAsync(
        UserDbContext context,
        ReferralPaymentEvent referralEvent,
        CancellationToken cancellationToken)
    {
        if (await context.ReferralRewards.AnyAsync(x => x.ReferralPaymentEventId == referralEvent.Id, cancellationToken))
            return;

        var planned = BuildRewardPlan(referralEvent);
        if (planned.Count == 0)
        {
            referralEvent.Status = ReferralProcessingStatuses.Completed;
            referralEvent.CompletedAtUtc = DateTime.UtcNow;
            referralEvent.UpdatedAtUtc = DateTime.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            return;
        }

        context.ReferralRewards.AddRange(planned);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Composite reward uniqueness handles concurrent planning; the winning rows are loaded during apply.
            context.ChangeTracker.Clear();
            if (!await context.ReferralRewards.AnyAsync(x => x.ReferralPaymentEventId == referralEvent.Id, cancellationToken))
                throw;
        }
    }

    /// <summary>
    /// Calculates the first or recurring reward plan and captures every configuration input on each row.
    /// </summary>
    /// <param name="referralEvent">Persisted eligible payment event.</param>
    /// <returns>Zero, one, or two non-zero reward rows ready for insertion.</returns>
    private List<ReferralReward> BuildRewardPlan(ReferralPaymentEvent referralEvent)
    {
        var rewards = new List<ReferralReward>();
        if (referralEvent.IsFirstEligiblePayment)
        {
            AddReward(
                rewards,
                referralEvent,
                referralEvent.ReferrerTelegramUserId,
                ReferralRewardKinds.ReferrerFirstPayment,
                _options.FirstPayment.ReferrerRewardPercent,
                minimumToman: 0,
                maximumToman: 0);

            AddReward(
                rewards,
                referralEvent,
                referralEvent.ReferredTelegramUserId,
                ReferralRewardKinds.ReferredFirstPayment,
                _options.FirstPayment.ReferredRewardPercent,
                _options.FirstPayment.ReferredMinimumRewardToman,
                _options.FirstPayment.ReferredMaximumRewardToman);
        }
        else
        {
            AddReward(
                rewards,
                referralEvent,
                referralEvent.ReferrerTelegramUserId,
                ReferralRewardKinds.ReferrerRecurringPayment,
                _options.SubsequentPayments.ReferrerRewardPercent,
                minimumToman: 0,
                maximumToman: 0);
        }

        return rewards;
    }

    /// <summary>
    /// Adds one positive reward to a plan after applying the configured percentage, floor, and optional cap.
    /// </summary>
    /// <param name="rewards">Mutable reward plan.</param>
    /// <param name="referralEvent">Parent event providing source and relationship snapshots.</param>
    /// <param name="beneficiaryTelegramUserId">Numeric Telegram id receiving the bot-wallet credit.</param>
    /// <param name="rewardKind">Persisted reward kind.</param>
    /// <param name="percent">Percentage snapshot used in calculation.</param>
    /// <param name="minimumToman">Non-negative minimum reward in Iranian toman.</param>
    /// <param name="maximumToman">Optional maximum in Iranian toman; zero disables the cap.</param>
    private static void AddReward(
        ICollection<ReferralReward> rewards,
        ReferralPaymentEvent referralEvent,
        long beneficiaryTelegramUserId,
        string rewardKind,
        decimal percent,
        long minimumToman,
        long maximumToman)
    {
        var amount = Math.Max(CalculatePercentageReward(referralEvent.SourceAmountToman, percent), minimumToman);
        if (maximumToman > 0)
            amount = Math.Min(amount, maximumToman);
        if (amount <= 0)
            return;

        var mutationKey = BuildRewardMutationKey(
            referralEvent.SourcePaymentKey,
            beneficiaryTelegramUserId,
            rewardKind);
        rewards.Add(new ReferralReward
        {
            ReferralPaymentEventId = referralEvent.Id,
            ReferralRelationshipId = referralEvent.ReferralRelationshipId,
            SourcePaymentKey = referralEvent.SourcePaymentKey,
            BeneficiaryTelegramUserId = beneficiaryTelegramUserId,
            ReferrerTelegramUserId = referralEvent.ReferrerTelegramUserId,
            ReferredTelegramUserId = referralEvent.ReferredTelegramUserId,
            BotId = referralEvent.BotId,
            RewardKind = rewardKind,
            RewardAmountToman = amount,
            SourceAmountToman = referralEvent.SourceAmountToman,
            RewardPercentSnapshot = percent,
            MinimumRewardTomanSnapshot = minimumToman,
            MaximumRewardTomanSnapshot = maximumToman,
            Status = ReferralProcessingStatuses.Pending,
            WalletMutationKey = mutationKey,
            CreatedAtUtc = DateTime.UtcNow
        });
    }

    /// <summary>
    /// Applies every incomplete reward, completes the parent event, and retries unsent notifications.
    /// </summary>
    /// <param name="eventId">Internal users.db referral event id.</param>
    /// <param name="cancellationToken">Cancellation token for wallet, ledger, state, and notification work.</param>
    /// <returns>A task that completes after this reconciliation attempt.</returns>
    private async Task ApplyEventRewardsAsync(long eventId, CancellationToken cancellationToken)
    {
        List<long> rewardIds;
        await using (var context = _userDbContextFactory.CreateDbContext())
        {
            var referralEvent = await context.ReferralPaymentEvents.FirstOrDefaultAsync(x => x.Id == eventId, cancellationToken);
            if (referralEvent == null)
                return;

            referralEvent.AttemptCount++;
            referralEvent.UpdatedAtUtc = DateTime.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
            rewardIds = await context.ReferralRewards
                .Where(x => x.ReferralPaymentEventId == eventId)
                .OrderBy(x => x.Id)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken);
        }

        foreach (var rewardId in rewardIds)
            await ApplyRewardAsync(rewardId, cancellationToken);

        await using (var context = _userDbContextFactory.CreateDbContext())
        {
            var referralEvent = await context.ReferralPaymentEvents.FirstAsync(x => x.Id == eventId, cancellationToken);
            var rewards = await context.ReferralRewards
                .Where(x => x.ReferralPaymentEventId == eventId)
                .ToListAsync(cancellationToken);
            if (rewards.All(x => x.Status == ReferralProcessingStatuses.Applied))
            {
                referralEvent.Status = ReferralProcessingStatuses.Completed;
                referralEvent.CompletedAtUtc ??= DateTime.UtcNow;
                referralEvent.LastError = null;
            }
            else
            {
                referralEvent.Status = ReferralProcessingStatuses.Failed;
                referralEvent.LastError = rewards.FirstOrDefault(x => x.Status == ReferralProcessingStatuses.Failed)?.LastError;
            }
            referralEvent.UpdatedAtUtc = DateTime.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
        }

        await SendPendingNotificationsAsync(eventId, cancellationToken);
    }

    /// <summary>
    /// Applies one reward with a users.db state barrier while leaving the credentials database schema unchanged.
    /// </summary>
    /// <param name="rewardId">Internal users.db reward id.</param>
    /// <param name="cancellationToken">Cancellation token for both databases.</param>
    /// <returns>A task that completes with the reward applied, safely resumable, or marked for manual review.</returns>
    /// <remarks>
    /// The reward is marked <c>crediting</c> in users.db before calling the existing credentials wallet API and
    /// <c>credited</c> immediately after that API succeeds. A <c>credited</c> reward can safely repair a missing
    /// ledger row without changing the wallet again. A process interruption that leaves <c>crediting</c> is treated
    /// as financially ambiguous and is never retried automatically, because credentials.db intentionally has no
    /// referral/idempotency table. This fail-closed rule prefers manual review over a duplicate wallet credit.
    /// </remarks>
    private async Task ApplyRewardAsync(long rewardId, CancellationToken cancellationToken)
    {
        ReferralReward reward;
        await using (var context = _userDbContextFactory.CreateDbContext())
        {
            var trackedReward = await context.ReferralRewards.FirstAsync(x => x.Id == rewardId, cancellationToken);
            if (trackedReward.Status == ReferralProcessingStatuses.Crediting)
            {
                // There is no atomic transaction across users.db and credentials.db. Retrying this state could
                // duplicate a credit if the previous process stopped after AddFund committed.
                trackedReward.Status = ReferralProcessingStatuses.Failed;
                trackedReward.AttemptCount++;
                trackedReward.LastError = "ambiguous_wallet_credit_requires_manual_review";
                trackedReward.UpdatedAtUtc = DateTime.UtcNow;
                await context.SaveChangesAsync(cancellationToken);
                _logger.LogCritical(
                    "Referral reward was left in an ambiguous wallet-credit state and was not retried. rewardId={RewardId}, userId={TelegramUserId}",
                    trackedReward.Id,
                    trackedReward.BeneficiaryTelegramUserId);
                return;
            }

            if (trackedReward.Status == ReferralProcessingStatuses.Failed &&
                string.Equals(
                    trackedReward.LastError,
                    "ambiguous_wallet_credit_requires_manual_review",
                    StringComparison.Ordinal))
            {
                return;
            }

            reward = trackedReward;
            if (reward.Status == ReferralProcessingStatuses.Applied)
                return;
        }

        try
        {
            if (reward.Status != ReferralProcessingStatuses.Credited)
            {
                var beneficiary = await _credentialsDbContext.GetUserStatusWithId(reward.BeneficiaryTelegramUserId);
                if (beneficiary == null)
                    throw new InvalidOperationException("Referral reward beneficiary does not exist in credentials.db.");

                reward.BalanceBefore = beneficiary.AccountBalance;
                reward.BalanceAfter = checked(beneficiary.AccountBalance + reward.RewardAmountToman);
                reward.Status = ReferralProcessingStatuses.Crediting;
                reward.AttemptCount++;
                reward.LastError = null;
                reward.UpdatedAtUtc = DateTime.UtcNow;
                await using (var intentContext = _userDbContextFactory.CreateDbContext())
                {
                    intentContext.ReferralRewards.Update(reward);
                    await intentContext.SaveChangesAsync(cancellationToken);
                }

                var credited = await _credentialsDbContext.AddFund(
                    reward.BeneficiaryTelegramUserId,
                    reward.RewardAmountToman);
                if (!credited)
                {
                    await using var missingUserContext = _userDbContextFactory.CreateDbContext();
                    var missingUserReward = await missingUserContext.ReferralRewards.FirstAsync(
                        x => x.Id == rewardId,
                        cancellationToken);
                    missingUserReward.Status = ReferralProcessingStatuses.Failed;
                    missingUserReward.BalanceBefore = null;
                    missingUserReward.BalanceAfter = null;
                    missingUserReward.LastError = "Referral reward beneficiary does not exist in credentials.db.";
                    missingUserReward.UpdatedAtUtc = DateTime.UtcNow;
                    await missingUserContext.SaveChangesAsync(cancellationToken);
                    return;
                }

                // Once this durable state is written, reconciliation can repair ledger/state without another credit.
                await using (var creditedContext = _userDbContextFactory.CreateDbContext())
                {
                    var creditedReward = await creditedContext.ReferralRewards.FirstAsync(
                        x => x.Id == rewardId,
                        cancellationToken);
                    creditedReward.Status = ReferralProcessingStatuses.Credited;
                    creditedReward.BalanceBefore = reward.BalanceBefore;
                    creditedReward.BalanceAfter = reward.BalanceAfter;
                    creditedReward.LastError = null;
                    creditedReward.UpdatedAtUtc = DateTime.UtcNow;
                    await creditedContext.SaveChangesAsync(cancellationToken);
                }
            }

            await using (var resumeContext = _userDbContextFactory.CreateDbContext())
            {
                reward = await resumeContext.ReferralRewards.AsNoTracking().FirstAsync(
                    x => x.Id == rewardId,
                    cancellationToken);
            }
            if (!reward.BalanceBefore.HasValue || !reward.BalanceAfter.HasValue)
                throw new InvalidOperationException("Credited referral reward is missing its wallet balance snapshot.");

            var ledger = await _walletLedgerService.RecordAsync(
                reward.BeneficiaryTelegramUserId,
                WalletLedgerDirections.Credit,
                reward.RewardAmountToman,
                reward.BalanceBefore.Value,
                reward.BalanceAfter.Value,
                WalletLedgerReasons.ReferralReward,
                provider: "referral",
                referenceType: nameof(ReferralReward),
                referenceId: reward.Id.ToString(CultureInfo.InvariantCulture),
                orderId: reward.SourcePaymentKey,
                description: reward.RewardKind,
                counterpartyTelegramUserId: reward.BeneficiaryTelegramUserId == reward.ReferrerTelegramUserId
                    ? reward.ReferredTelegramUserId
                    : reward.ReferrerTelegramUserId,
                botId: reward.BotId,
                botType: BotInstanceTypes.Owned,
                idempotencyKey: reward.WalletMutationKey,
                cancellationToken: cancellationToken);

            await using var updateContext = _userDbContextFactory.CreateDbContext();
            var trackedReward = await updateContext.ReferralRewards.FirstAsync(x => x.Id == rewardId, cancellationToken);
            trackedReward.Status = ReferralProcessingStatuses.Applied;
            trackedReward.WalletLedgerEntryId = ledger?.Id;
            trackedReward.LastError = null;
            trackedReward.AppliedAtUtc ??= DateTime.UtcNow;
            trackedReward.UpdatedAtUtc = DateTime.UtcNow;
            await updateContext.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Persisted Credited work is retryable; Crediting work is deliberately resolved as ambiguous next pass.
            _logger.LogInformation("Referral reward application was canceled and retained for reconciliation. rewardId={RewardId}", rewardId);
        }
        catch (Exception ex)
        {
            await using var failureContext = _userDbContextFactory.CreateDbContext();
            var trackedReward = await failureContext.ReferralRewards.FirstAsync(x => x.Id == rewardId, cancellationToken);
            if (trackedReward.Status is not ReferralProcessingStatuses.Crediting and
                not ReferralProcessingStatuses.Credited)
            {
                trackedReward.Status = ReferralProcessingStatuses.Failed;
            }
            trackedReward.LastError = LimitError(ex.Message);
            trackedReward.UpdatedAtUtc = DateTime.UtcNow;
            await failureContext.SaveChangesAsync(cancellationToken);
            _logger.LogError(ex, "Referral reward application was interrupted. rewardId={RewardId}, status={Status}", rewardId, trackedReward.Status);
        }
    }

    /// <summary>
    /// Sends any first/recurring reward notifications that were not previously delivered.
    /// </summary>
    /// <param name="eventId">Internal referral event id.</param>
    /// <param name="cancellationToken">Cancellation token for Telegram and notification state updates.</param>
    /// <returns>A task that completes after all eligible notification attempts.</returns>
    private async Task SendPendingNotificationsAsync(long eventId, CancellationToken cancellationToken)
    {
        List<ReferralReward> rewards;
        await using (var context = _userDbContextFactory.CreateDbContext())
        {
            rewards = await context.ReferralRewards
                .AsNoTracking()
                .Where(x => x.ReferralPaymentEventId == eventId &&
                            x.Status == ReferralProcessingStatuses.Applied &&
                            x.NotifiedAtUtc == null)
                .ToListAsync(cancellationToken);
        }

        foreach (var reward in rewards)
        {
            var text = BuildNotificationText(reward);
            var sent = await _notificationSender.SendAsync(
                reward.BeneficiaryTelegramUserId,
                reward.BotId,
                text,
                cancellationToken);
            if (!sent)
                continue;

            await using var context = _userDbContextFactory.CreateDbContext();
            var trackedReward = await context.ReferralRewards.FirstAsync(x => x.Id == reward.Id, cancellationToken);
            trackedReward.NotifiedAtUtc ??= DateTime.UtcNow;
            trackedReward.UpdatedAtUtc = DateTime.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Builds a plain Persian notification for one applied reward kind.
    /// </summary>
    /// <param name="reward">Applied reward containing beneficiary and amount snapshots.</param>
    /// <returns>Plain text safe to send without Telegram parse mode.</returns>
    private static string BuildNotificationText(ReferralReward reward)
    {
        var amount = reward.RewardAmountToman.ToString("N0", CultureInfo.InvariantCulture);
        return reward.RewardKind switch
        {
            ReferralRewardKinds.ReferredFirstPayment =>
                $"🎁 اولین پاداش دعوت شما به مبلغ {amount} تومان به کیف پول ربات اضافه شد.",
            ReferralRewardKinds.ReferrerFirstPayment =>
                $"🎉 یکی از دوستان دعوت‌شده اولین پرداخت واجدشرایط خود را انجام داد و {amount} تومان پاداش به کیف پول شما اضافه شد.",
            _ =>
                $"🎁 بابت پرداخت جدید یکی از دوستان دعوت‌شده، {amount} تومان پاداش به کیف پول شما اضافه شد."
        };
    }

    /// <summary>
    /// Builds the registration result for an already-persisted immutable relationship.
    /// </summary>
    /// <param name="existing">Existing global relationship loaded from users.db.</param>
    /// <param name="requestedReferrerTelegramUserId">Referrer decoded from the newest start payload.</param>
    /// <returns>Same-referrer or different-first-referrer outcome.</returns>
    private static ReferralRegistrationResult BuildExistingRegistrationResult(
        ReferralRelationship existing,
        long requestedReferrerTelegramUserId)
    {
        var status = existing.ReferrerTelegramUserId == requestedReferrerTelegramUserId
            ? ReferralRegistrationStatus.AlreadyRegistered
            : ReferralRegistrationStatus.DifferentReferrerAlreadyRegistered;
        return new ReferralRegistrationResult(status, existing);
    }

    /// <summary>
    /// Creates a compact fixed-length mutation key from reward uniqueness inputs.
    /// </summary>
    /// <param name="sourcePaymentKey">Stable parent payment key.</param>
    /// <param name="beneficiaryTelegramUserId">Numeric Telegram id receiving the reward.</param>
    /// <param name="rewardKind">Persisted reward classification.</param>
    /// <returns>A non-secret SHA-256 based key shorter than database limits.</returns>
    private static string BuildRewardMutationKey(
        string sourcePaymentKey,
        long beneficiaryTelegramUserId,
        string rewardKind)
    {
        var raw = $"{sourcePaymentKey}|{beneficiaryTelegramUserId}|{rewardKind}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        return $"referral-reward:{hash}";
    }

    /// <summary>
    /// Limits persisted retry errors to the users.db column size without exposing exception stacks in user data.
    /// </summary>
    /// <param name="message">Non-secret exception message.</param>
    /// <returns>At most two thousand characters, or a generic fallback.</returns>
    private static string LimitError(string message)
    {
        var value = string.IsNullOrWhiteSpace(message) ? "Unknown referral processing error." : message.Trim();
        return value.Length <= 2000 ? value : value[..2000];
    }
}

/// <summary>
/// Replays already-settled provider rows and incomplete referral work once after application startup.
/// </summary>
/// <remarks>
/// This hosted service is a crash-recovery safety net, not a payment status checker. It only reads local rows whose
/// original wallet credit and final provider status were already persisted, excludes tenant/provisional/partial
/// payments, and delegates all duplicate prevention to <see cref="ReferralService"/> and database constraints.
/// </remarks>
public sealed class ReferralReconciliationHostedService : BackgroundService
{
    /// <summary>Validated referral settings used to skip replay when explicitly disabled.</summary>
    private readonly ReferralOptions _options;
    /// <summary>Factory used to read final provider rows without sharing an EF change tracker.</summary>
    private readonly UserDbContextFactory _userDbContextFactory;
    /// <summary>Idempotent referral engine used for source replay and pending reward repair.</summary>
    private readonly ReferralService _referralService;
    /// <summary>Runtime registry used to distinguish owned bot ids from tenant/assistant ids.</summary>
    private readonly BotRegistry _botRegistry;
    /// <summary>Startup reconciliation diagnostics logger.</summary>
    private readonly ILogger<ReferralReconciliationHostedService> _logger;

    /// <summary>
    /// Creates the one-pass referral crash-recovery worker.
    /// </summary>
    /// <param name="configuration">Application configuration used to skip all work when referral is disabled.</param>
    /// <param name="userDbContextFactory">Per-operation users.db context factory.</param>
    /// <param name="referralService">Idempotent referral settlement and pending-work service.</param>
    /// <param name="botRegistry">Runtime registry used to recognize owned bot ids and exclude tenant rows.</param>
    /// <param name="logger">Operational logger for row-level replay failures.</param>
    public ReferralReconciliationHostedService(
        IConfiguration configuration,
        UserDbContextFactory userDbContextFactory,
        ReferralService referralService,
        BotRegistry botRegistry,
        ILogger<ReferralReconciliationHostedService> logger)
    {
        _options = (configuration.Get<AppConfig>() ?? new AppConfig()).Referral;
        _userDbContextFactory = userDbContextFactory;
        _referralService = referralService;
        _botRegistry = botRegistry;
        _logger = logger;
    }

    /// <summary>
    /// Replays final local payment rows and then retries any planned reward or notification left incomplete.
    /// </summary>
    /// <param name="stoppingToken">Host shutdown token.</param>
    /// <returns>A task that completes after the single startup reconciliation pass.</returns>
    /// <remarks>
    /// Provider APIs are not called. Pending, failed, refunded, provisional, partial, website-wallet, and tenant
    /// records cannot become eligible through this worker. Individual failures are logged and do not stop later rows.
    /// </remarks>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
            return;

        try
        {
            await ReplaySettledPaymentsAsync(stoppingToken);
            var pendingCount = await _referralService.ReconcilePendingAsync(stoppingToken);
            _logger.LogInformation("Referral startup reconciliation completed. pendingEventsInspected={PendingCount}", pendingCount);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Referral startup reconciliation stopped unexpectedly; persisted work remains retryable.");
        }
    }

    /// <summary>
    /// Converts locally settled real-provider rows into referral payment sources and replays them idempotently.
    /// </summary>
    /// <param name="cancellationToken">Host shutdown token for database reads and referral processing.</param>
    /// <returns>A task that completes after every locally final owned-bot payment has been inspected.</returns>
    private async Task ReplaySettledPaymentsAsync(CancellationToken cancellationToken)
    {
        var ownedBotIds = _botRegistry.Bots
            .Where(x => string.Equals(x.Type, BotInstanceTypes.Owned, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (_botRegistry.DefaultBot != null)
            ownedBotIds.Add(_botRegistry.DefaultBot.Id);

        List<ReferralPaymentSource> sources;
        await using (var context = _userDbContextFactory.CreateDbContext())
        {
            var nowPayments = await context.SwapinoPaymentInfos
                .AsNoTracking()
                .Where(x => x.IsAddedToBalance &&
                            x.PaymentPurpose == TenantBotPaymentPurposes.WalletCharge &&
                            x.ErrorCode != "partial_settlement")
                .ToListAsync(cancellationToken);
            var hooshPay = await context.HooshPayPaymentInfos
                .AsNoTracking()
                .Where(x => x.IsAddedToBalance &&
                            !x.IsProvisionallyApproved &&
                            x.PaymentPurpose == TenantBotPaymentPurposes.WalletCharge)
                .ToListAsync(cancellationToken);
            var zibal = await context.ZibalPaymentInfos
                .AsNoTracking()
                .Where(x => x.IsAddedToBallance && x.IsPaid)
                .ToListAsync(cancellationToken);

            sources = nowPayments
                .Where(x => NowPaymentsStatuses.IsPaid(x.PaymentStatus) && IsOwnedBotId(x.BotId, ownedBotIds))
                .Select(x => new ReferralPaymentSource(
                    "nowpayments",
                    TenantBotPaymentPurposes.WalletCharge,
                    FirstNonEmpty(x.PaymentId, x.InvoiceId, x.OrderId, x.Id.ToString(CultureInfo.InvariantCulture)),
                    NormalizeOwnedBotId(x.BotId),
                    BotInstanceTypes.Owned,
                    x.TelegramUserId,
                    x.AmountToman,
                    x.SettledAtUtc ?? x.PaidAtUtc ?? x.CreatedAtUtc,
                    true,
                    true,
                    false))
                .Concat(hooshPay
                    .Where(x => HooshPayStatuses.IsPaid(x.PaymentStatus) && IsOwnedBotId(x.BotId, ownedBotIds))
                    .Select(x => new ReferralPaymentSource(
                        "hooshpay",
                        TenantBotPaymentPurposes.WalletCharge,
                        FirstNonEmpty(x.InvoiceUid, x.OrderId, x.Id.ToString(CultureInfo.InvariantCulture)),
                        NormalizeOwnedBotId(x.BotId),
                        BotInstanceTypes.Owned,
                        x.TelegramUserId,
                        x.AmountToman,
                        x.SettledAtUtc ?? x.PaidAtUtc ?? x.CreatedAtUtc,
                        true,
                        true,
                        false)))
                .Concat(zibal
                    .Where(x => IsOwnedBotId(x.BotId, ownedBotIds))
                    .Select(x => new ReferralPaymentSource(
                        "zibal",
                        TenantBotPaymentPurposes.WalletCharge,
                        x.TrackId.ToString(CultureInfo.InvariantCulture),
                        NormalizeOwnedBotId(x.BotId),
                        BotInstanceTypes.Owned,
                        x.TelegramUserId,
                        x.Amount / 10,
                        x.PaidAt == default ? x.CreatedAt : x.PaidAt,
                        true,
                        true,
                        false)))
                .ToList();
        }

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await _referralService.ProcessFinalOwnedWalletPaymentAsync(source, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Referral startup replay failed for one provider row. provider={Provider}, providerPaymentId={ProviderPaymentId}",
                    source.Provider,
                    source.ProviderPaymentId);
            }
        }
    }

    /// <summary>
    /// Checks a payment bot id against the exact owned-bot registry set.
    /// </summary>
    /// <param name="botId">Bot id persisted with the payment; legacy empty values map to the default owned bot.</param>
    /// <param name="ownedBotIds">Exact owned-bot ids from the runtime registry.</param>
    /// <returns><c>true</c> only for the default legacy id or an exact owned bot id.</returns>
    private static bool IsOwnedBotId(string botId, IReadOnlySet<string> ownedBotIds)
    {
        return ownedBotIds.Contains(NormalizeOwnedBotId(botId));
    }

    /// <summary>
    /// Normalizes an empty legacy payment bot id to the default owned bot id.
    /// </summary>
    /// <param name="botId">Persisted payment bot id.</param>
    /// <returns>Trimmed bot id or the default owned id.</returns>
    private static string NormalizeOwnedBotId(string botId)
    {
        return string.IsNullOrWhiteSpace(botId) ? BotContextAccessor.DefaultBotId : botId.Trim();
    }

    /// <summary>
    /// Returns the first non-empty provider identifier in precedence order.
    /// </summary>
    /// <param name="values">Provider and local identifiers ordered from strongest to fallback.</param>
    /// <returns>A non-empty stable identifier because every caller includes a local row-id fallback.</returns>
    private static string FirstNonEmpty(params string[] values)
    {
        return values.First(x => !string.IsNullOrWhiteSpace(x)).Trim();
    }
}
