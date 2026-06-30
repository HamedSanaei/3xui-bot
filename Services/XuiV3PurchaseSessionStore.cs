using System.Collections.Concurrent;
using Adminbot.Domain;

public class XuiV3PurchaseSessionStore
{
    private readonly ConcurrentDictionary<string, XuiV3PurchaseSelection> _selections = new(StringComparer.Ordinal);

    public XuiV3PurchaseSelection GetOrCreate(long telegramUserId)
    {
        return _selections.GetOrAdd(BuildKey(telegramUserId), _ => new XuiV3PurchaseSelection());
    }

    public bool TryGet(long telegramUserId, out XuiV3PurchaseSelection selection)
    {
        return _selections.TryGetValue(BuildKey(telegramUserId), out selection);
    }

    public void Set(long telegramUserId, XuiV3PurchaseSelection selection)
    {
        _selections[BuildKey(telegramUserId)] = selection ?? new XuiV3PurchaseSelection();
    }

    public void Clear(long telegramUserId)
    {
        _selections.TryRemove(BuildKey(telegramUserId), out _);
    }

    private static string BuildKey(long telegramUserId)
    {
        return $"{BotContextAccessor.CurrentBotId}:{telegramUserId}";
    }
}
