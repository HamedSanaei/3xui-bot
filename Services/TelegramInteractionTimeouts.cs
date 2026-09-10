/// <summary>
/// Immutable latency budgets for the two UX-only Telegram interactions that must never be allowed
/// to inherit the Telegram client's long default HTTP timeout: callback acknowledgement and
/// mandatory-join membership verification.
/// </summary>
/// <remarks>
/// Why this type exists:
/// A callback acknowledgement and a mandatory-join membership probe are user-experience operations,
/// not business operations. If either one waits on the Telegram HTTP client's default timeout, the
/// user lane stalls (the Sequence-2990 incident showed a simple navigation callback taking tens of
/// seconds). The budgets below bound those two operations so the update lane always releases promptly.
///
/// Production values are fixed and must not drift:
/// <see cref="CallbackAnswer"/> is exactly two seconds and <see cref="MandatoryJoin"/> is exactly five
/// seconds. <see cref="Production"/> is the single shared instance used by production dependency
/// injection.
///
/// Test injection:
/// Tests that need to prove timeout behaviour construct their own instance with millisecond values
/// (for example <c>new TelegramInteractionTimeouts { CallbackAnswer = TimeSpan.FromMilliseconds(40) }</c>)
/// and pass it into the service constructor, or override the registration in the test service provider.
/// Because the properties use <c>init</c> and <see cref="Production"/> is a shared immutable instance,
/// parallel tests can never race over a process-wide mutable timeout.
///
/// Scope:
/// These values intentionally are not bound to <c>configuration.json</c>. They are not operator-tunable
/// settings; the hosting environment must not be able to raise them back to the pathological values.
/// </remarks>
public sealed class TelegramInteractionTimeouts
{
    /// <summary>
    /// The shared production instance. Callback acknowledgement uses two seconds and mandatory-join
    /// verification uses one overall five-second budget.
    /// </summary>
    public static TelegramInteractionTimeouts Production { get; } = new();

    /// <summary>
    /// Gets the maximum time allowed for one Telegram callback acknowledgement before the request is
    /// abandoned as a best-effort UX failure. Production value: two seconds.
    /// </summary>
    /// <remarks>
    /// When this budget expires the acknowledgement is abandoned but the business handler continues.
    /// A missing acknowledgement only costs the user a spinner on the button; it must never abort a
    /// purchase, a renewal, or a wallet operation.
    /// </remarks>
    public TimeSpan CallbackAnswer { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets the single overall budget covering the entire mandatory-join membership evaluation across
    /// all configured channels. Production value: five seconds.
    /// </summary>
    /// <remarks>
    /// This is deliberately one budget for the whole channel loop, not one budget per channel. With N
    /// channels the evaluation must still finish within this value, so a slow Telegram API cannot turn
    /// a three-channel check into a fifteen-second stall. On expiry the caller treats membership as
    /// unverified and fails closed.
    /// </remarks>
    public TimeSpan MandatoryJoin { get; init; } = TimeSpan.FromSeconds(5);
}
