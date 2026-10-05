using System.Net;
using Adminbot.Domain;

/// <summary>Shared Persian comparison captions for global and tenant-owner weekly usage charts.</summary>
/// <remarks>Uses the same metric totals and percentage semantics for both audiences. Tenant captions identify only the selected storefront, never other owners or customer identities.</remarks>
internal static class WeeklyUsageReportContent
{
    /// <summary>Builds the HTML caption accompanying the existing three-panel weekly comparison PNG.</summary>
    /// <param name="currentWeek">Required latest seven completed Tehran-local days; contains gross fulfilled sales in toman.</param>
    /// <param name="previousWeek">Required seven completed days immediately preceding the current week; provides the comparison baseline.</param>
    /// <param name="tenantUsername">Optional public BotFather username of the selected tenant, without a required leading at-sign; bounded before HTML encoding.</param>
    /// <param name="tenantBotId">Optional internal users.db storefront identifier; null selects the existing global heading, otherwise only this storefront's reports may be passed.</param>
    /// <returns>Nonempty HTML caption safe for Telegram's photo caption limit; includes daily-unique totals, interactions, sales and change text.</returns>
    /// <remarks>
    /// Daily unique totals sum distinct users within each day, not distinct users across the whole week. Sales are gross completed
    /// purchases and renewals, not wallet funding or profit. Missing/corrupt current activity files are disclosed rather than treated as verified inactivity.
    /// Tenant captions also warn when their previous-week activity baseline is incomplete.
    /// The caller must enforce tenant ownership and ensure the two supplied reports share the same exact storefront scope.
    /// </remarks>
    /// <exception cref="ArgumentNullException">Either required report is null.</exception>
    /// <example><code>var caption = WeeklyUsageReportContent.BuildCaption(current, previous, tenant.Username, tenant.Id);</code></example>
    internal static string BuildCaption(UsageAnalyticsReport currentWeek, UsageAnalyticsReport previousWeek,
        string tenantUsername = null, string tenantBotId = null)
    {
        ArgumentNullException.ThrowIfNull(currentWeek);
        ArgumentNullException.ThrowIfNull(previousWeek);
        var start = UsageAnalyticsService.FormatPersianDate(currentWeek.StartDateIran);
        var end = UsageAnalyticsService.FormatPersianDate(currentWeek.EndDateIran.AddDays(-1));
        var warningParts = new List<string>();
        if (currentWeek.MissingActivityLogDays > 0)
            warningParts.Add($"{currentWeek.MissingActivityLogDays} روز فایل فعالیت موجود نبود");
        if (currentWeek.MalformedLines > 0)
            warningParts.Add($"{currentWeek.MalformedLines} خط خراب نادیده گرفته شد");
        if (tenantBotId != null && (previousWeek.MissingActivityLogDays > 0 || previousWeek.MalformedLines > 0))
            warningParts.Add("داده‌های تعامل هفته قبل ناقص است");
        var warning = warningParts.Count == 0
            ? string.Empty
            : "\n⚠️ " + WebUtility.HtmlEncode(string.Join("؛ ", warningParts));

        var heading = "📊 <b>گزارش هفتگی مصرف کل مجموعه</b>\n";
        if (tenantBotId != null)
        {
            var username = tenantUsername?.Trim().TrimStart('@');
            var label = string.IsNullOrWhiteSpace(username) ? tenantBotId : "@" + username;
            // Bound the untrusted display name before encoding so even malformed stored metadata cannot overflow the photo caption.
            if (label.Length > 40)
                label = label[..(char.IsHighSurrogate(label[39]) ? 39 : 40)];
            heading = "📊 <b>گزارش هفتگی ربات فروشگاهی</b>\n" +
                      $"ربات: <code>{WebUtility.HtmlEncode(label)}</code>\n";
        }

        return heading +
            $"بازه: <code>{start}</code> تا <code>{end}</code>\n\n" +
            $"👤 مجموع کاربران یکتای روزانه: <code>{currentWeek.TotalDailyUniqueUsers:N0}</code> " +
            $"({BuildChangeText(currentWeek.TotalDailyUniqueUsers, previousWeek.TotalDailyUniqueUsers)})\n" +
            $"💬 تعامل‌ها: <code>{currentWeek.TotalInteractions:N0}</code> " +
            $"({BuildChangeText(currentWeek.TotalInteractions, previousWeek.TotalInteractions)})\n" +
            $"💰 فروش موفق: <code>{currentWeek.TotalSalesToman:N0}</code> تومان " +
            $"({BuildChangeText(currentWeek.TotalSalesToman, previousWeek.TotalSalesToman)})" + warning;
    }

    /// <summary>Formats week-over-week growth, decline, equality or growth from a zero baseline.</summary>
    /// <param name="current">Required nonnegative current total in users, interactions or whole toman.</param>
    /// <param name="previous">Required nonnegative preceding-week total in the same unit and storefront scope.</param>
    /// <returns>Persian change text; a zero baseline never causes division by zero or a fabricated percentage.</returns>
    /// <remarks>The chart retains the exact daily values; the caption rounds the absolute percentage change to one decimal place.</remarks>
    /// <example><code>var change = BuildChangeText(150, 100); // 50 percent growth</code></example>
    private static string BuildChangeText(long current, long previous)
    {
        if (previous == 0)
            return current == 0 ? "بدون تغییر" : "رشد از صفر";
        var percent = Math.Abs((current - previous) * 100d / previous);
        if (current == previous)
            return "بدون تغییر";
        return current > previous
            ? $"رشد {percent:0.#}٪ نسبت به هفته قبل"
            : $"کاهش {percent:0.#}٪ نسبت به هفته قبل";
    }
}
