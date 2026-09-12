using Adminbot.Domain;

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
        {
            await using var scope = _scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<TelegramBotService>()
                .DispatchUpdateAsync(client, item.Update, runtime, cancellationToken);
        }
    }
}
