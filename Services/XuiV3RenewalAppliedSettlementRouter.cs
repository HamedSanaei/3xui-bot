using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Adminbot.Domain;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Routes one applied XUI v3 renewal to the existing owned or tenant exactly-once settlement boundary.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a separate type.</b> An applied renewal can be discovered by two independent callers: the durable
/// background reconciliation worker, and a super-admin who confirms a manual-review operation as applied. Both must
/// reach the same single settlement implementation. Extracting this routing here keeps one settlement path for every
/// caller, so a manual confirmation can never grow a second debit, a second ledger, or a second success log.
/// </para>
/// <para>
/// This type performs no panel mutation and holds no durable state. It only resolves the correct scoped service and
/// restores the originating bot context the settlement implementation expects.
/// </para>
/// </remarks>
public class XuiV3RenewalAppliedSettlementRouter
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly BotRegistry _botRegistry;
    private readonly BotClientProvider _botClientProvider;
    private readonly BotContextAccessor _botContextAccessor;

    /// <summary>
    /// Creates the settlement router shared by background recovery and administrator confirmation.
    /// </summary>
    /// <param name="scopeFactory">Creates an isolated service graph for each recovered settlement.</param>
    /// <param name="botRegistry">Runtime registry used to restore the operation's originating owned bot.</param>
    /// <param name="botClientProvider">Provider used to obtain the owned bot's Telegram client for post-settlement notification.</param>
    /// <param name="botContextAccessor">Accessor that scopes recovered logs and ledger metadata to the original bot.</param>
    /// <remarks>
    /// The BotRegistry dependency intentionally does not use <c>ILogger&lt;BotRegistry&gt;</c> anywhere; the registry is
    /// constructed before the logging graph is complete, so injecting a logger into it would close a DI cycle.
    /// </remarks>
    public XuiV3RenewalAppliedSettlementRouter(
        IServiceScopeFactory scopeFactory,
        BotRegistry botRegistry,
        BotClientProvider botClientProvider,
        BotContextAccessor botContextAccessor)
    {
        _scopeFactory = scopeFactory;
        _botRegistry = botRegistry;
        _botClientProvider = botClientProvider;
        _botContextAccessor = botContextAccessor;
    }

    /// <summary>
    /// Settles one applied renewal through its tenant or owned exactly-once settlement path.
    /// </summary>
    /// <param name="operation">
    /// Applied operation that remains account-locked until settlement completes. The caller must have already performed
    /// the single applied transition for this operation; passing a non-applied operation makes this method return
    /// <c>false</c> without any financial effect.
    /// </param>
    /// <param name="cancellationToken">Token that cancels scope creation, wallet, ledger, order, and notification work.</param>
    /// <returns>
    /// <c>true</c> when settlement is durably complete, including the case where an earlier executor had already settled
    /// it; <c>false</c> when the tenant order cannot be fulfilled yet, the owning bot is unavailable, another executor
    /// holds the settlement claim, or the operation was parked for financial review.
    /// </returns>
    /// <remarks>
    /// <para>
    /// A tenant operation keeps the order-level fulfillment gate and the order <c>IsFulfilled</c> check as its financial
    /// idempotency boundary. An owned operation restores the originating bot context before the shared settlement
    /// service writes ledger and log metadata, because the ledger reference and Telegram notification belong to that bot.
    /// </para>
    /// <para>
    /// <c>false</c> is never a failure signal on its own. Both callers treat it as "still locked, try again later", and
    /// repeated calls remain safe because the underlying settlement claim is atomic.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// // After the single manual_review to applied transition:
    /// var settled = await router.SettleAppliedAsync(operation, cancellationToken);
    /// </code>
    /// </example>
    /// <remarks>
    /// Virtual only as a test seam, matching the existing convention on the operation store: a regression test can count
    /// settlement entries without building a live wallet, panel, or Telegram graph. Production always uses this body, so
    /// no second settlement implementation can be introduced through this seam.
    /// </remarks>
    public virtual async Task<bool> SettleAppliedAsync(
        XuiV3RenewalOperation operation,
        CancellationToken cancellationToken)
    {
        if (operation == null || operation.Status != XuiV3RenewalOperationStatuses.Applied)
            return false;

        await using var scope = _scopeFactory.CreateAsyncScope();
        if (!string.IsNullOrWhiteSpace(operation.TenantBotOrderId))
        {
            return await scope.ServiceProvider
                .GetRequiredService<TenantBotService>()
                .SettleRecoveredTenantRenewalAsync(operation, cancellationToken);
        }

        var bot = _botRegistry.Bots.FirstOrDefault(x =>
            string.Equals(x.Id, operation.BotId, StringComparison.OrdinalIgnoreCase));
        if (bot == null || !bot.Enabled || string.IsNullOrWhiteSpace(bot.Token))
            return false;

        var botClient = _botClientProvider.GetClient(bot.Id);
        using (_botContextAccessor.Push(new BotRuntimeContext { Config = bot, Client = botClient }))
        {
            return await scope.ServiceProvider
                .GetRequiredService<XuiV3BotFlowService>()
                .SettleRecoveredOwnedRenewalAsync(botClient, operation, cancellationToken);
        }
    }
}
