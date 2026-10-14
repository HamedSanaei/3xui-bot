using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Adminbot.Domain;

/// <summary>Restricts receiver persistence to update shapes actually handled by owned, tenant and assistant bots.</summary>
/// <remarks>Channel publication is outbound; incoming channel posts/boosts/membership bookkeeping have no handler. Direct inbox APIs retain their existing contracts.</remarks>
public static class TelegramUpdateAdmissionPolicy
{
    /// <summary>Fresh SDK subscription array; callers cannot mutate another receiver's configuration.</summary>
    public static UpdateType[] AllowedUpdates => [UpdateType.Message, UpdateType.CallbackQuery];

    /// <summary>Checks whether an incoming batch item needs the existing message/callback business dispatcher.</summary>
    /// <param name="update">Nullable SDK update; no content, identifiers or credentials are examined.</param>
    /// <returns>True for message/callback shapes; false means a deliberate no-op before any inbox write.</returns>
    /// <remarks>Older subscription batches can still contain excluded updates. This guard never skips a supported customer/admin message.</remarks>
    /// <example><code>if (!TelegramUpdateAdmissionPolicy.RequiresHandling(update)) return;</code></example>
    public static bool RequiresHandling(Update update) => update?.Message != null || update?.CallbackQuery != null;
}
