using System.Globalization;
using Adminbot.Domain;
using SkiaSharp;

/// <summary>
/// Renders a Linux-safe PNG dashboard comparing the latest completed week with the preceding week.
/// </summary>
/// <remarks>
/// Chart labels intentionally use ASCII English text and Persian-calendar numeric dates so the server does not depend
/// on an installed Persian shaping font. The Telegram caption carries the full Persian explanation and comparison.
/// </remarks>
public sealed class UsageReportChartRenderer
{
    private const int ImageWidth = 1500;
    private const int ImageHeight = 1650;

    /// <summary>
    /// Renders three daily charts for unique users, interactions, and gross toman sales.
    /// </summary>
    /// <param name="currentWeek">Seven completed Tehran-local days, Saturday through Friday.</param>
    /// <param name="previousWeek">The seven completed days immediately preceding <paramref name="currentWeek"/>.</param>
    /// <returns>Encoded PNG bytes suitable for Telegram <c>SendPhotoAsync</c>.</returns>
    /// <exception cref="ArgumentException">Thrown when either report does not contain exactly seven daily buckets.</exception>
    /// <example>
    /// <code>
    /// var png = renderer.RenderWeeklyComparison(currentWeek, previousWeek);
    /// await botClient.SendPhotoAsync(chatId, InputFile.FromStream(new MemoryStream(png), "usage.png"));
    /// </code>
    /// </example>
    public byte[] RenderWeeklyComparison(UsageAnalyticsReport currentWeek, UsageAnalyticsReport previousWeek)
    {
        ArgumentNullException.ThrowIfNull(currentWeek);
        ArgumentNullException.ThrowIfNull(previousWeek);
        if (currentWeek.Days.Count != 7 || previousWeek.Days.Count != 7)
            throw new ArgumentException("Weekly usage charts require exactly seven current and seven previous daily buckets.");

        var imageInfo = new SKImageInfo(ImageWidth, ImageHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(imageInfo) ?? throw new InvalidOperationException("SkiaSharp could not create the usage-report surface.");
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(247, 249, 252));

        using var titlePaint = new SKPaint { Color = new SKColor(24, 39, 75), IsAntialias = true };
        using var subtitlePaint = new SKPaint { Color = new SKColor(80, 91, 115), IsAntialias = true };
        using var titleFont = new SKFont(SKTypeface.Default, 42);
        using var subtitleFont = new SKFont(SKTypeface.Default, 24);
        canvas.DrawText("Weekly Bot Usage Report", 70, 75, SKTextAlign.Left, titleFont, titlePaint);
        canvas.DrawText(
            $"Current: {FormatDateRange(currentWeek)}    Previous: {FormatDateRange(previousWeek)}",
            70,
            115,
            SKTextAlign.Left,
            subtitleFont,
            subtitlePaint);

        DrawLegend(canvas, 1040, 72);
        DrawChartPanel(
            canvas,
            top: 160,
            title: "Daily Unique Users",
            currentWeek,
            previousWeek,
            valueSelector: day => day.UniqueUsers,
            valueFormatter: FormatCompactNumber);
        DrawChartPanel(
            canvas,
            top: 650,
            title: "Daily Interactions",
            currentWeek,
            previousWeek,
            valueSelector: day => day.Interactions,
            valueFormatter: FormatCompactNumber);
        DrawChartPanel(
            canvas,
            top: 1140,
            title: "Daily Sales (Toman)",
            currentWeek,
            previousWeek,
            valueSelector: day => day.SalesToman,
            valueFormatter: FormatCompactNumber);

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 95)
                         ?? throw new InvalidOperationException("SkiaSharp could not encode the usage-report PNG.");
        return data.ToArray();
    }

    /// <summary>
    /// Draws the color key shared by all three chart panels.
    /// </summary>
    /// <param name="canvas">Target Skia canvas.</param>
    /// <param name="x">Left pixel position.</param>
    /// <param name="y">Text baseline pixel position.</param>
    private static void DrawLegend(SKCanvas canvas, float x, float y)
    {
        using var currentPaint = new SKPaint { Color = new SKColor(31, 111, 235), IsAntialias = true };
        using var previousPaint = new SKPaint { Color = new SKColor(176, 190, 215), IsAntialias = true };
        using var textPaint = new SKPaint { Color = new SKColor(60, 70, 92), IsAntialias = true };
        using var font = new SKFont(SKTypeface.Default, 22);

        canvas.DrawRoundRect(new SKRect(x, y - 20, x + 28, y + 8), 5, 5, currentPaint);
        canvas.DrawText("Last week", x + 40, y + 3, SKTextAlign.Left, font, textPaint);
        canvas.DrawRoundRect(new SKRect(x + 190, y - 20, x + 218, y + 8), 5, 5, previousPaint);
        canvas.DrawText("Previous week", x + 230, y + 3, SKTextAlign.Left, font, textPaint);
    }

    /// <summary>
    /// Draws one paired-bar panel with daily values aligned by weekday.
    /// </summary>
    /// <param name="canvas">Target Skia canvas.</param>
    /// <param name="top">Top pixel coordinate of the panel.</param>
    /// <param name="title">ASCII chart title.</param>
    /// <param name="currentWeek">Current completed-week report.</param>
    /// <param name="previousWeek">Previous completed-week report.</param>
    /// <param name="valueSelector">Metric selector returning a non-negative daily value.</param>
    /// <param name="valueFormatter">Compact axis-label formatter.</param>
    private static void DrawChartPanel(
        SKCanvas canvas,
        float top,
        string title,
        UsageAnalyticsReport currentWeek,
        UsageAnalyticsReport previousWeek,
        Func<UsageDailyStat, long> valueSelector,
        Func<long, string> valueFormatter)
    {
        var panel = new SKRect(50, top, ImageWidth - 50, top + 440);
        using var panelPaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var borderPaint = new SKPaint
        {
            Color = new SKColor(221, 227, 238),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2,
            IsAntialias = true
        };
        canvas.DrawRoundRect(panel, 12, 12, panelPaint);
        canvas.DrawRoundRect(panel, 12, 12, borderPaint);

        using var headingPaint = new SKPaint { Color = new SKColor(31, 45, 75), IsAntialias = true };
        using var headingFont = new SKFont(SKTypeface.Default, 30);
        canvas.DrawText(title, panel.Left + 32, panel.Top + 48, SKTextAlign.Left, headingFont, headingPaint);

        var plot = new SKRect(panel.Left + 110, panel.Top + 82, panel.Right - 35, panel.Bottom - 65);
        var currentValues = currentWeek.Days.Select(valueSelector).Select(value => Math.Max(0, value)).ToArray();
        var previousValues = previousWeek.Days.Select(valueSelector).Select(value => Math.Max(0, value)).ToArray();
        var maxValue = Math.Max(1L, currentValues.Concat(previousValues).DefaultIfEmpty(0).Max());
        var roundedMax = GetRoundedAxisMaximum(maxValue);

        DrawGrid(canvas, plot, roundedMax, valueFormatter);

        using var currentPaint = new SKPaint { Color = new SKColor(31, 111, 235), IsAntialias = true };
        using var previousPaint = new SKPaint { Color = new SKColor(176, 190, 215), IsAntialias = true };
        using var datePaint = new SKPaint { Color = new SKColor(82, 91, 112), IsAntialias = true };
        using var dateFont = new SKFont(SKTypeface.Default, 19);

        var groupWidth = plot.Width / 7f;
        var barWidth = Math.Min(32f, groupWidth * 0.28f);
        for (var index = 0; index < 7; index++)
        {
            var center = plot.Left + groupWidth * (index + 0.5f);
            DrawBar(canvas, previousPaint, center - barWidth - 3, plot, barWidth, previousValues[index], roundedMax);
            DrawBar(canvas, currentPaint, center + 3, plot, barWidth, currentValues[index], roundedMax);
            canvas.DrawText(
                FormatDateLabel(currentWeek.Days[index].DateIran),
                center,
                plot.Bottom + 29,
                SKTextAlign.Center,
                dateFont,
                datePaint);
        }
    }

    /// <summary>
    /// Draws horizontal grid lines and compact numeric axis labels.
    /// </summary>
    /// <param name="canvas">Target Skia canvas.</param>
    /// <param name="plot">Pixel rectangle reserved for bars.</param>
    /// <param name="axisMaximum">Rounded positive maximum represented by the top grid line.</param>
    /// <param name="valueFormatter">Formatter used for numeric labels.</param>
    private static void DrawGrid(
        SKCanvas canvas,
        SKRect plot,
        long axisMaximum,
        Func<long, string> valueFormatter)
    {
        using var gridPaint = new SKPaint
        {
            Color = new SKColor(229, 234, 242),
            StrokeWidth = 1,
            IsAntialias = true
        };
        using var labelPaint = new SKPaint { Color = new SKColor(110, 119, 138), IsAntialias = true };
        using var labelFont = new SKFont(SKTypeface.Default, 18);

        const int lines = 4;
        for (var index = 0; index <= lines; index++)
        {
            var ratio = index / (float)lines;
            var y = plot.Bottom - plot.Height * ratio;
            canvas.DrawLine(plot.Left, y, plot.Right, y, gridPaint);
            var value = (long)Math.Round(axisMaximum * ratio, MidpointRounding.AwayFromZero);
            canvas.DrawText(valueFormatter(value), plot.Left - 15, y + 6, SKTextAlign.Right, labelFont, labelPaint);
        }
    }

    /// <summary>
    /// Draws one non-negative bar scaled to the common panel maximum.
    /// </summary>
    /// <param name="canvas">Target Skia canvas.</param>
    /// <param name="paint">Series color paint.</param>
    /// <param name="left">Left pixel coordinate of the bar.</param>
    /// <param name="plot">Plot rectangle used for scaling.</param>
    /// <param name="width">Bar width in pixels.</param>
    /// <param name="value">Daily non-negative metric value.</param>
    /// <param name="axisMaximum">Positive maximum represented by the plot height.</param>
    private static void DrawBar(
        SKCanvas canvas,
        SKPaint paint,
        float left,
        SKRect plot,
        float width,
        long value,
        long axisMaximum)
    {
        var height = value <= 0 ? 0 : plot.Height * value / axisMaximum;
        var rect = new SKRect(left, plot.Bottom - height, left + width, plot.Bottom);
        canvas.DrawRoundRect(rect, 5, 5, paint);
    }

    /// <summary>
    /// Rounds an observed maximum upward to a readable power-of-ten step.
    /// </summary>
    /// <param name="maximum">Largest observed non-negative metric value.</param>
    /// <returns>A positive rounded axis maximum.</returns>
    private static long GetRoundedAxisMaximum(long maximum)
    {
        if (maximum <= 1)
            return 1;

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(maximum)));
        var normalized = maximum / magnitude;
        var rounded = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return Math.Max(1, (long)Math.Ceiling(rounded * magnitude));
    }

    /// <summary>
    /// Formats a large non-negative chart value with K, M, or B suffixes.
    /// </summary>
    /// <param name="value">Count or Iranian toman value.</param>
    /// <returns>Compact invariant-culture text suitable for a narrow chart axis.</returns>
    private static string FormatCompactNumber(long value)
    {
        if (value >= 1_000_000_000)
            return (value / 1_000_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "B";
        if (value >= 1_000_000)
            return (value / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M";
        if (value >= 1_000)
            return (value / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "K";
        return value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Formats one Tehran-local date as a short Persian calendar month/day label.
    /// </summary>
    /// <param name="dateIran">Tehran-local date.</param>
    /// <returns>Numeric <c>MM/dd</c> Persian-calendar label using ASCII digits.</returns>
    private static string FormatDateLabel(DateTime dateIran)
    {
        var calendar = new PersianCalendar();
        return $"{calendar.GetMonth(dateIran):00}/{calendar.GetDayOfMonth(dateIran):00}";
    }

    /// <summary>
    /// Formats the inclusive start and inclusive final day of one report for the chart header.
    /// </summary>
    /// <param name="report">Seven-day report whose range should be shown.</param>
    /// <returns>ASCII Persian-calendar range.</returns>
    private static string FormatDateRange(UsageAnalyticsReport report)
    {
        return $"{UsageAnalyticsService.FormatPersianDate(report.StartDateIran)} - " +
               UsageAnalyticsService.FormatPersianDate(report.EndDateIran.AddDays(-1));
    }
}
