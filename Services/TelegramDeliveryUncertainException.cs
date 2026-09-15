/// <summary>Signals a Telegram request that may have been accepted and must not be automatically replayed.</summary>
/// <remarks>Contains no request body, chat text, URL or token. Financial delivery outboxes retain this outcome for review.</remarks>
public sealed class TelegramDeliveryUncertainException : TimeoutException
{
    /// <summary>Creates a payload-free ambiguous-delivery failure for the original awaited caller.</summary>
    public TelegramDeliveryUncertainException() : base("Telegram delivery outcome is uncertain; automatic replay is disabled.") { }
}
