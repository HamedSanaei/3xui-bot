using Adminbot.Domain;

/// <summary>Restores bot identity and owns the disposable service graph of one scheduled execution.</summary>
/// <remarks>Per-operation stores own short database contexts; legacy coordinated workflows share only this execution's unit of work.</remarks>
public sealed class TelegramUpdateExecutor : ITelegramUpdateExecutor
{
    private readonly IServiceScopeFactory _scopes;
    private readonly BotRegistry _registry;
    private readonly BotClientProvider _clients;
    private readonly BotContextAccessor _context;
    /// <summary>Creates an executor without capturing a mutable handler or database context.</summary>
    /// <param name="scopes">Application scope factory for one logical Telegram execution.</param>
    /// <param name="registry">Current owned, tenant, and assistant bot definitions.</param>
    /// <param name="clients">Bot-keyed client provider; tokens are never persisted in work items.</param>
    /// <param name="context">Ambient bot scope accessor, restored after each execution.</param>
    /// <remarks>The executor is a singleton holding factories and runtime registries; each invocation owns its context scope and restores ambient bot identity on exit.</remarks>
    public TelegramUpdateExecutor(IServiceScopeFactory scopes, BotRegistry registry, BotClientProvider clients, BotContextAccessor context)
    { _scopes = scopes; _registry = registry; _clients = clients; _context = context; }

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
        var client = _clients.GetClient(bot.Id);
        var runtime = new BotRuntimeContext { Config = RuntimeSnapshot.Copy(bot), Client = client };
        using (_context.Push(runtime))
        {
            await using var scope = _scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<TelegramBotService>()
                .DispatchUpdateAsync(client, item.Update, runtime, cancellationToken);
        }
    }
}
