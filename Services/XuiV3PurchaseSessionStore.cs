using System.Collections.Concurrent;
using Adminbot.Domain;

public class XuiV3PurchaseSessionStore
{
    private readonly ConcurrentDictionary<long, XuiV3PurchaseSelection> _selections = new();

    public XuiV3PurchaseSelection GetOrCreate(long telegramUserId)
    {
        return _selections.GetOrAdd(telegramUserId, _ => new XuiV3PurchaseSelection());
    }

    public bool TryGet(long telegramUserId, out XuiV3PurchaseSelection selection)
    {
        return _selections.TryGetValue(telegramUserId, out selection);
    }

    public void Set(long telegramUserId, XuiV3PurchaseSelection selection)
    {
        _selections[telegramUserId] = selection ?? new XuiV3PurchaseSelection();
    }

    public void Clear(long telegramUserId)
    {
        _selections.TryRemove(telegramUserId, out _);
    }
}
