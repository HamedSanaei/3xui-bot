using System.Text;
using System.Xml.Linq;
using Adminbot.Domain;
using Xunit;

/// <summary>Consumer-visible Telegram caption safety for malformed persisted storefront labels and large metric totals.</summary>
public sealed class TenantWeeklyReportContentTests
{
    /// <summary>Untrusted storefront labels cannot inject HTML or exceed Telegram's photo caption limit.</summary>
    /// <param name="fragment">Untrusted markup, entity delimiter or astral emoji repeated in a synthetic stored username.</param>
    /// <remarks>Regression boundary: escaping amplifies label length; truncating a UTF-16 surrogate pair can corrupt the caption sent to Telegram.</remarks>
    [Theory]
    [InlineData("<b>")]
    [InlineData("&")]
    [InlineData("😀")]
    public void Tenant_caption_remains_valid_and_bounded_with_untrusted_metadata(string fragment)
    {
        var start = new DateTime(2026, 10, 3);
        var current = new UsageAnalyticsReport(start, Enumerable.Range(0, 7).Select(index => new UsageDailyStat
        {
            DateIran = start.AddDays(index), UniqueUsers = int.MaxValue,
            Interactions = index == 0 ? long.MaxValue : 0,
            SalesToman = index == 0 ? long.MaxValue : 0,
            ActivityLogMissing = true, MalformedLines = 1
        }).ToArray());
        var previous = new UsageAnalyticsReport(start.AddDays(-7), Enumerable.Range(0, 7).Select(index => new UsageDailyStat
        {
            DateIran = start.AddDays(index - 7), UniqueUsers = 1,
            Interactions = 1, SalesToman = 1, ActivityLogMissing = true
        }).ToArray());
        var username = string.Concat(Enumerable.Repeat(fragment, 100));
        var caption = WeeklyUsageReportContent.BuildCaption(current, previous, username, "tenant-private-scope");
        Assert.InRange(caption.Length, 1, 1024);
        _ = new UTF8Encoding(false, true).GetBytes(caption);
        var parsed = XElement.Parse("<root>" + caption + "</root>");
        Assert.All(parsed.Elements(), element => Assert.Contains(element.Name.LocalName, new[] { "b", "code" }));
        Assert.DoesNotContain(parsed.Descendants(), element => element.Parent != parsed);
    }
}
