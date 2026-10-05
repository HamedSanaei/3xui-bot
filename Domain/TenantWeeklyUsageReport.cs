namespace Adminbot.Domain;

/// <summary>Identifies one persisted tenant storefront and its current owner for isolated weekly analytics.</summary>
/// <param name="BotId">Required stable internal <see cref="BotInstance.Id"/>; not a Telegram bot id, token, or username.</param>
/// <param name="OwnerTelegramUserId">Positive Telegram user id of the persisted storefront owner at inventory time.</param>
/// <remarks>
/// Separate storefronts owned by the same Telegram user remain separate targets. Fulfilled orders must match both
/// identifiers, so orders belonging to an earlier or different owner cannot enter the current owner's report.
/// Delivery must independently recheck the persisted owner and Telegram bot identity before sending.
/// </remarks>
public sealed record TenantWeeklyReportTarget(string BotId, long OwnerTelegramUserId);

/// <summary>Contains two contiguous completed Saturday-to-Friday weeks for one isolated tenant storefront.</summary>
/// <param name="CurrentWeek">Seven ordered Tehran-local daily buckets for the most recently completed week.</param>
/// <param name="PreviousWeek">Seven ordered Tehran-local daily buckets immediately preceding the current week.</param>
/// <remarks>
/// Usage excludes configured global super-admins and unattributed or other-store activity. Gross sales are fulfilled
/// users.db order SalePriceToman values in whole Iranian toman, never wallet movements or owned-bot log sales.
/// Missing-file and malformed-line diagnostics are retained separately in each week's daily buckets.
/// </remarks>
public sealed record TenantWeeklyUsageComparison(UsageAnalyticsReport CurrentWeek, UsageAnalyticsReport PreviousWeek);
