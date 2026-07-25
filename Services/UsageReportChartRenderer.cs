using System.Globalization;
using Adminbot.Domain;
using SkiaSharp;

/// <summary>
/// Renders high-resolution line-chart PNG dashboards for completed-day bot usage reports.
/// </summary>
/// <remarks>
/// The canvas width stays below Telegram's common photo downscaling boundary while retaining enough pixels for
/// readable axes and point labels. Chart text intentionally uses ASCII English labels and Persian-calendar numeric
/// dates so Linux publication does not depend on an installed Persian shaping font.
/// </remarks>
public sealed class UsageReportChartRenderer
{
    private const int ImageWidth = 2400;
    private const int HeaderHeight = 180;
    private const int PanelHeight = 500;
    private const int PanelGap = 28;
    private const int OuterMargin = 55;

    private static readonly SKColor CanvasColor = new(244, 247, 252);
    private static readonly SKColor CurrentColor = new(21, 101, 216);
    private static readonly SKColor PreviousColor = new(127, 144, 173);
    private static readonly SKColor GridColor = new(218, 225, 237);
    private static readonly SKColor AxisTextColor = new(72, 84, 108);
    private static readonly SKColor HeadingColor = new(25, 39, 72);

    /// <summary>
    /// Renders three weekly line charts comparing unique users, interactions, and gross sales with the prior week.
    /// </summary>
    /// <param name="currentWeek">Seven completed Tehran-local days, ordered Saturday through Friday.</param>
    /// <param name="previousWeek">The seven completed days immediately preceding <paramref name="currentWeek"/>.</param>
    /// <returns>High-resolution encoded PNG bytes suitable for Telegram <c>SendPhotoAsync</c>.</returns>
    /// <exception cref="ArgumentException">Thrown when either report does not contain exactly seven daily buckets.</exception>
    /// <remarks>
    /// Both series share one explicit Y-axis scale in each panel. Current-week points display exact values; the prior
    /// week remains a dashed comparison line so labels do not overlap on small Telegram previews.
    /// </remarks>
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

        return Render(
            currentWeek,
            previousWeek,
            includeSales: true,
            title: "Weekly Bot Usage Report",
            currentSeriesLabel: "Completed week",
            previousSeriesLabel: "Previous week");
    }

    /// <summary>
    /// Renders a high-resolution line-chart dashboard for a completed seven-day or thirty-day admin report.
    /// </summary>
    /// <param name="report">
    /// Completed Tehran-local daily buckets ordered from oldest to newest. The collection must contain at least two
    /// days and must not include the current incomplete day.
    /// </param>
    /// <param name="includeSales">
    /// <c>true</c> to add the gross successful-sales panel; <c>false</c> to render only unique users and interactions.
    /// </param>
    /// <returns>Encoded PNG bytes with readable axes, point markers, adaptive date labels, and metric summaries.</returns>
    /// <exception cref="ArgumentException">Thrown when fewer than two daily buckets are supplied.</exception>
    /// <remarks>
    /// Seven-day reports label every point. Longer reports label the first, last, maximum, and periodic points while
    /// retaining every daily marker and line segment, which keeps the image readable after Telegram scaling.
    /// </remarks>
    /// <example>
    /// <code>
    /// var png = renderer.RenderCompletedPeriod(report, includeSales: false);
    /// await botClient.SendPhotoAsync(chatId, InputFile.FromStream(new MemoryStream(png), "monthly-usage.png"));
    /// </code>
    /// </example>
    public byte[] RenderCompletedPeriod(UsageAnalyticsReport report, bool includeSales)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.Days.Count < 2)
            throw new ArgumentException("Usage charts require at least two completed daily buckets.", nameof(report));

        var title = report.Days.Count == 7
            ? "7-Day Bot Usage Report"
            : $"{report.Days.Count}-Day Bot Usage Report";
        return Render(
            report,
            previousReport: null,
            includeSales,
            title,
            currentSeriesLabel: "Completed days",
            previousSeriesLabel: null);
    }

    /// <summary>
    /// Creates the shared dashboard canvas and renders two or three metric panels.
    /// </summary>
    /// <param name="currentReport">Primary completed-day report represented by the blue line.</param>
    /// <param name="previousReport">Optional same-length comparison report represented by a dashed gray line.</param>
    /// <param name="includeSales">Whether the gross-sales panel should be included.</param>
    /// <param name="title">ASCII dashboard heading.</param>
    /// <param name="currentSeriesLabel">Legend label for the primary report.</param>
    /// <param name="previousSeriesLabel">Optional legend label for the comparison report.</param>
    /// <returns>Encoded PNG bytes.</returns>
    private static byte[] Render(
        UsageAnalyticsReport currentReport,
        UsageAnalyticsReport previousReport,
        bool includeSales,
        string title,
        string currentSeriesLabel,
        string previousSeriesLabel)
    {
        if (previousReport != null && previousReport.Days.Count != currentReport.Days.Count)
            throw new ArgumentException("Compared usage reports must contain the same number of daily buckets.");

        var panelCount = includeSales ? 3 : 2;
        var imageHeight = HeaderHeight + OuterMargin + panelCount * PanelHeight + (panelCount - 1) * PanelGap;
        var imageInfo = new SKImageInfo(ImageWidth, imageHeight, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(imageInfo)
                            ?? throw new InvalidOperationException("SkiaSharp could not create the usage-report surface.");
        var canvas = surface.Canvas;
        canvas.Clear(CanvasColor);

        DrawHeader(
            canvas,
            title,
            currentReport,
            previousReport,
            currentSeriesLabel,
            previousSeriesLabel);

        var top = HeaderHeight;
        DrawLineChartPanel(
            canvas,
            top,
            "Daily Unique Users",
            currentReport,
            previousReport,
            day => day.UniqueUsers);
        top += PanelHeight + PanelGap;
        DrawLineChartPanel(
            canvas,
            top,
            "Daily Interactions",
            currentReport,
            previousReport,
            day => day.Interactions);

        if (includeSales)
        {
            top += PanelHeight + PanelGap;
            DrawLineChartPanel(
                canvas,
                top,
                "Daily Sales (Toman)",
                currentReport,
                previousReport,
                day => day.SalesToman);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100)
                         ?? throw new InvalidOperationException("SkiaSharp could not encode the usage-report PNG.");
        return data.ToArray();
    }

    /// <summary>
    /// Draws the report title, date ranges, and line-series legend.
    /// </summary>
    /// <param name="canvas">Target Skia canvas.</param>
    /// <param name="title">ASCII report heading.</param>
    /// <param name="currentReport">Primary report used for the first date range.</param>
    /// <param name="previousReport">Optional comparison report.</param>
    /// <param name="currentSeriesLabel">Legend label for the blue series.</param>
    /// <param name="previousSeriesLabel">Optional legend label for the dashed gray series.</param>
    private static void DrawHeader(
        SKCanvas canvas,
        string title,
        UsageAnalyticsReport currentReport,
        UsageAnalyticsReport previousReport,
        string currentSeriesLabel,
        string previousSeriesLabel)
    {
        using var titlePaint = CreatePaint(HeadingColor);
        using var subtitlePaint = CreatePaint(AxisTextColor);
        using var titleFont = new SKFont(SKTypeface.Default, 46);
        using var subtitleFont = new SKFont(SKTypeface.Default, 25);
        canvas.DrawText(title, 70, 66, SKTextAlign.Left, titleFont, titlePaint);
        canvas.DrawText(
            $"Range: {FormatDateRange(currentReport)}",
            70,
            111,
            SKTextAlign.Left,
            subtitleFont,
            subtitlePaint);

        if (previousReport != null)
        {
            canvas.DrawText(
                $"Compare: {FormatDateRange(previousReport)}",
                70,
                148,
                SKTextAlign.Left,
                subtitleFont,
                subtitlePaint);
        }
        else
        {
            canvas.DrawText(
                "Current incomplete day is excluded",
                70,
                148,
                SKTextAlign.Left,
                subtitleFont,
                subtitlePaint);
        }

        DrawLegend(
            canvas,
            ImageWidth - 720,
            83,
            currentSeriesLabel,
            previousSeriesLabel);
    }

    /// <summary>
    /// Draws the color and line-style key for the primary and optional comparison series.
    /// </summary>
    /// <param name="canvas">Target Skia canvas.</param>
    /// <param name="x">Left pixel position of the legend.</param>
    /// <param name="y">First legend baseline.</param>
    /// <param name="currentLabel">Primary-series label.</param>
    /// <param name="previousLabel">Optional comparison-series label.</param>
    private static void DrawLegend(
        SKCanvas canvas,
        float x,
        float y,
        string currentLabel,
        string previousLabel)
    {
        using var textPaint = CreatePaint(AxisTextColor);
        using var currentPaint = CreateLinePaint(CurrentColor, 6);
        using var previousPaint = CreateLinePaint(PreviousColor, 5, dashed: true);
        using var font = new SKFont(SKTypeface.Default, 24);

        canvas.DrawLine(x, y - 8, x + 70, y - 8, currentPaint);
        canvas.DrawCircle(x + 35, y - 8, 8, currentPaint);
        canvas.DrawText(currentLabel, x + 90, y, SKTextAlign.Left, font, textPaint);
        if (string.IsNullOrWhiteSpace(previousLabel))
            return;

        canvas.DrawLine(x, y + 42, x + 70, y + 42, previousPaint);
        canvas.DrawText(previousLabel, x + 90, y + 50, SKTextAlign.Left, font, textPaint);
    }

    /// <summary>
    /// Draws one scaled line-chart panel with daily points, explicit axes, and a total/average/maximum summary.
    /// </summary>
    /// <param name="canvas">Target Skia canvas.</param>
    /// <param name="top">Top pixel coordinate of the panel.</param>
    /// <param name="title">ASCII metric title.</param>
    /// <param name="currentReport">Primary report represented by the blue line.</param>
    /// <param name="previousReport">Optional same-length comparison report.</param>
    /// <param name="valueSelector">Selector returning a non-negative daily metric value.</param>
    private static void DrawLineChartPanel(
        SKCanvas canvas,
        float top,
        string title,
        UsageAnalyticsReport currentReport,
        UsageAnalyticsReport previousReport,
        Func<UsageDailyStat, long> valueSelector)
    {
        var panel = new SKRect(OuterMargin, top, ImageWidth - OuterMargin, top + PanelHeight);
        using var panelPaint = CreatePaint(SKColors.White);
        using var borderPaint = CreatePaint(GridColor, SKPaintStyle.Stroke, 2);
        canvas.DrawRoundRect(panel, 14, 14, panelPaint);
        canvas.DrawRoundRect(panel, 14, 14, borderPaint);

        var currentValues = currentReport.Days.Select(valueSelector).Select(value => Math.Max(0, value)).ToArray();
        var previousValues = previousReport?.Days.Select(valueSelector).Select(value => Math.Max(0, value)).ToArray();
        var observedMaximum = currentValues
            .Concat(previousValues ?? Array.Empty<long>())
            .DefaultIfEmpty(0)
            .Max();
        var scale = CalculateAxisScale(observedMaximum);

        using var headingPaint = CreatePaint(HeadingColor);
        using var summaryPaint = CreatePaint(AxisTextColor);
        using var headingFont = new SKFont(SKTypeface.Default, 32);
        using var summaryFont = new SKFont(SKTypeface.Default, 24);
        canvas.DrawText(title, panel.Left + 34, panel.Top + 47, SKTextAlign.Left, headingFont, headingPaint);
        canvas.DrawText(
            BuildMetricSummary(currentValues),
            panel.Right - 34,
            panel.Top + 45,
            SKTextAlign.Right,
            summaryFont,
            summaryPaint);

        var plot = new SKRect(panel.Left + 155, panel.Top + 90, panel.Right - 55, panel.Bottom - 78);
        DrawGridAndAxes(canvas, plot, scale.Maximum, scale.Step);

        if (previousValues != null)
        {
            DrawSeries(
                canvas,
                plot,
                previousValues,
                scale.Maximum,
                PreviousColor,
                dashed: true,
                showValueLabels: false);
        }

        DrawCurrentArea(canvas, plot, currentValues, scale.Maximum);
        DrawSeries(
            canvas,
            plot,
            currentValues,
            scale.Maximum,
            CurrentColor,
            dashed: false,
            showValueLabels: true);
        DrawDateAxis(canvas, plot, currentReport.Days);
    }

    /// <summary>
    /// Draws a subtle area fill below the primary line without obscuring grid lines or point labels.
    /// </summary>
    /// <param name="canvas">Target Skia canvas.</param>
    /// <param name="plot">Plot rectangle used for point scaling.</param>
    /// <param name="values">Primary non-negative daily values.</param>
    /// <param name="axisMaximum">Positive Y-axis maximum.</param>
    private static void DrawCurrentArea(SKCanvas canvas, SKRect plot, IReadOnlyList<long> values, long axisMaximum)
    {
        if (values.Count == 0)
            return;

        using var pathBuilder = new SKPathBuilder();
        var firstPoint = GetPoint(plot, 0, values.Count, values[0], axisMaximum);
        pathBuilder.MoveTo(firstPoint.X, plot.Bottom);
        pathBuilder.LineTo(firstPoint);
        for (var index = 1; index < values.Count; index++)
            pathBuilder.LineTo(GetPoint(plot, index, values.Count, values[index], axisMaximum));
        var lastPoint = GetPoint(plot, values.Count - 1, values.Count, values[^1], axisMaximum);
        pathBuilder.LineTo(lastPoint.X, plot.Bottom);
        pathBuilder.Close();

        using var path = pathBuilder.Detach();
        using var fillPaint = CreatePaint(new SKColor(CurrentColor.Red, CurrentColor.Green, CurrentColor.Blue, 28));
        canvas.DrawPath(path, fillPaint);
    }

    /// <summary>
    /// Draws horizontal scale lines, numeric Y labels, and solid X/Y axes.
    /// </summary>
    /// <param name="canvas">Target Skia canvas.</param>
    /// <param name="plot">Pixel rectangle reserved for the chart.</param>
    /// <param name="axisMaximum">Rounded positive maximum represented by the top line.</param>
    /// <param name="axisStep">Positive numeric increment between horizontal lines.</param>
    private static void DrawGridAndAxes(SKCanvas canvas, SKRect plot, long axisMaximum, long axisStep)
    {
        using var gridPaint = CreateLinePaint(GridColor, 2);
        using var axisPaint = CreateLinePaint(new SKColor(155, 166, 187), 3);
        using var labelPaint = CreatePaint(AxisTextColor);
        using var labelFont = new SKFont(SKTypeface.Default, 23);

        for (var value = 0L; value <= axisMaximum; value += axisStep)
        {
            var y = plot.Bottom - plot.Height * value / axisMaximum;
            canvas.DrawLine(plot.Left, y, plot.Right, y, gridPaint);
            canvas.DrawText(
                FormatCompactNumber(value),
                plot.Left - 22,
                y + 8,
                SKTextAlign.Right,
                labelFont,
                labelPaint);
        }

        canvas.DrawLine(plot.Left, plot.Top, plot.Left, plot.Bottom, axisPaint);
        canvas.DrawLine(plot.Left, plot.Bottom, plot.Right, plot.Bottom, axisPaint);
    }

    /// <summary>
    /// Draws one line series and all daily markers using the panel's common Y-axis scale.
    /// </summary>
    /// <param name="canvas">Target Skia canvas.</param>
    /// <param name="plot">Plot rectangle used for point scaling.</param>
    /// <param name="values">Ordered daily non-negative values.</param>
    /// <param name="axisMaximum">Positive Y-axis maximum.</param>
    /// <param name="color">Series color.</param>
    /// <param name="dashed">Whether line segments use a dashed style.</param>
    /// <param name="showValueLabels">Whether adaptive exact-value labels should be drawn.</param>
    private static void DrawSeries(
        SKCanvas canvas,
        SKRect plot,
        IReadOnlyList<long> values,
        long axisMaximum,
        SKColor color,
        bool dashed,
        bool showValueLabels)
    {
        if (values.Count == 0)
            return;

        using var pathBuilder = new SKPathBuilder();
        for (var index = 0; index < values.Count; index++)
        {
            var point = GetPoint(plot, index, values.Count, values[index], axisMaximum);
            if (index == 0)
                pathBuilder.MoveTo(point);
            else
                pathBuilder.LineTo(point);
        }

        using var path = pathBuilder.Detach();
        using var linePaint = CreateLinePaint(color, dashed ? 5 : 7, dashed);
        canvas.DrawPath(path, linePaint);

        using var outerPointPaint = CreatePaint(SKColors.White);
        using var innerPointPaint = CreatePaint(color);
        for (var index = 0; index < values.Count; index++)
        {
            var point = GetPoint(plot, index, values.Count, values[index], axisMaximum);
            canvas.DrawCircle(point, dashed ? 7 : 10, outerPointPaint);
            canvas.DrawCircle(point, dashed ? 4 : 6, innerPointPaint);
            if (showValueLabels && ShouldDrawValueLabel(index, values))
                DrawPointValueLabel(canvas, plot, point, values[index], color);
        }
    }

    /// <summary>
    /// Draws readable exact-value text above a selected point using a white outline.
    /// </summary>
    /// <param name="canvas">Target Skia canvas.</param>
    /// <param name="plot">Plot bounds used to keep the baseline visible.</param>
    /// <param name="point">Scaled point position.</param>
    /// <param name="value">Exact daily metric value.</param>
    /// <param name="color">Series color used for the foreground text.</param>
    private static void DrawPointValueLabel(
        SKCanvas canvas,
        SKRect plot,
        SKPoint point,
        long value,
        SKColor color)
    {
        var baseline = Math.Max(plot.Top + 27, point.Y - 16);
        using var font = new SKFont(SKTypeface.Default, 23);
        using var outlinePaint = CreatePaint(SKColors.White, SKPaintStyle.Stroke, 7);
        using var textPaint = CreatePaint(color);
        var text = FormatCompactNumber(value);
        canvas.DrawText(text, point.X, baseline, SKTextAlign.Center, font, outlinePaint);
        canvas.DrawText(text, point.X, baseline, SKTextAlign.Center, font, textPaint);
    }

    /// <summary>
    /// Draws adaptive Persian-calendar date labels below the X axis.
    /// </summary>
    /// <param name="canvas">Target Skia canvas.</param>
    /// <param name="plot">Plot rectangle defining daily X positions.</param>
    /// <param name="days">Ordered Tehran-local daily buckets.</param>
    private static void DrawDateAxis(
        SKCanvas canvas,
        SKRect plot,
        IReadOnlyList<UsageDailyStat> days)
    {
        using var datePaint = CreatePaint(AxisTextColor);
        using var tickPaint = CreateLinePaint(new SKColor(155, 166, 187), 2);
        using var dateFont = new SKFont(SKTypeface.Default, days.Count <= 10 ? 22 : 20);
        for (var index = 0; index < days.Count; index++)
        {
            if (!ShouldDrawDateLabel(index, days.Count))
                continue;

            var point = GetPoint(plot, index, days.Count, 0, 1);
            canvas.DrawLine(point.X, plot.Bottom, point.X, plot.Bottom + 10, tickPaint);
            canvas.DrawText(
                FormatDateLabel(days[index].DateIran),
                point.X,
                plot.Bottom + 39,
                SKTextAlign.Center,
                dateFont,
                datePaint);
        }
    }

    /// <summary>
    /// Converts a daily value and index into a pixel point inside the chart.
    /// </summary>
    /// <param name="plot">Chart rectangle.</param>
    /// <param name="index">Zero-based day index.</param>
    /// <param name="count">Number of daily points; must be positive.</param>
    /// <param name="value">Non-negative daily value.</param>
    /// <param name="axisMaximum">Positive Y-axis maximum.</param>
    /// <returns>Scaled point whose X and Y coordinates remain inside <paramref name="plot"/>.</returns>
    private static SKPoint GetPoint(
        SKRect plot,
        int index,
        int count,
        long value,
        long axisMaximum)
    {
        const float horizontalPointInset = 32;
        var x = count <= 1
            ? plot.MidX
            : plot.Left + horizontalPointInset +
              (plot.Width - horizontalPointInset * 2) * index / (count - 1f);
        var clampedValue = Math.Clamp(value, 0L, axisMaximum);
        var y = plot.Bottom - plot.Height * clampedValue / axisMaximum;
        return new SKPoint(x, y);
    }

    /// <summary>
    /// Calculates a human-readable integer Y-axis maximum and step from the observed maximum.
    /// </summary>
    /// <param name="observedMaximum">Largest observed non-negative value across visible series.</param>
    /// <returns>A tuple containing a positive rounded maximum and a positive tick step.</returns>
    private static (long Maximum, long Step) CalculateAxisScale(long observedMaximum)
    {
        const int targetTickCount = 5;
        if (observedMaximum <= targetTickCount)
            return (targetTickCount, 1);

        var roughStep = observedMaximum / (double)targetTickCount;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(roughStep)));
        var normalized = roughStep / magnitude;
        var niceNormalized = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        var step = Math.Max(1L, (long)Math.Ceiling(niceNormalized * magnitude));
        var maximum = checked((long)Math.Ceiling(observedMaximum / (double)step) * step);
        return (maximum, step);
    }

    /// <summary>
    /// Selects exact point labels without overcrowding long monthly series.
    /// </summary>
    /// <param name="index">Zero-based point index.</param>
    /// <param name="values">Full ordered series.</param>
    /// <returns><c>true</c> for all weekly points or important/periodic monthly points.</returns>
    private static bool ShouldDrawValueLabel(int index, IReadOnlyList<long> values)
    {
        if (values.Count <= 10)
            return true;

        var maximumIndex = 0;
        for (var candidate = 1; candidate < values.Count; candidate++)
        {
            if (values[candidate] > values[maximumIndex])
                maximumIndex = candidate;
        }

        return index == 0 ||
               index == values.Count - 1 ||
               index == maximumIndex ||
               index % 7 == 0;
    }

    /// <summary>
    /// Selects X-axis date labels based on report length.
    /// </summary>
    /// <param name="index">Zero-based day index.</param>
    /// <param name="count">Total daily buckets.</param>
    /// <returns><c>true</c> when the date should be drawn.</returns>
    private static bool ShouldDrawDateLabel(int index, int count)
    {
        if (count <= 10)
            return true;

        var interval = count <= 16 ? 2 : 5;
        return index == 0 || index == count - 1 || index % interval == 0;
    }

    /// <summary>
    /// Builds the compact total, average, and maximum text shown in a panel heading.
    /// </summary>
    /// <param name="values">Ordered non-negative daily values.</param>
    /// <returns>ASCII summary text using compact numeric formatting.</returns>
    private static string BuildMetricSummary(IReadOnlyList<long> values)
    {
        var total = values.Sum();
        var average = values.Count == 0 ? 0 : (long)Math.Round(total / (double)values.Count);
        var maximum = values.Count == 0 ? 0 : values.Max();
        return $"Total {FormatCompactNumber(total)}   Avg {FormatCompactNumber(average)}   Max {FormatCompactNumber(maximum)}";
    }

    /// <summary>
    /// Creates an anti-aliased Skia paint for text, fills, or borders.
    /// </summary>
    /// <param name="color">Paint color.</param>
    /// <param name="style">Fill or stroke style.</param>
    /// <param name="strokeWidth">Stroke width in pixels when <paramref name="style"/> is stroke.</param>
    /// <returns>A disposable configured paint.</returns>
    private static SKPaint CreatePaint(
        SKColor color,
        SKPaintStyle style = SKPaintStyle.Fill,
        float strokeWidth = 1)
    {
        return new SKPaint
        {
            Color = color,
            Style = style,
            StrokeWidth = strokeWidth,
            IsAntialias = true
        };
    }

    /// <summary>
    /// Creates a rounded line paint and optionally applies a dash pattern.
    /// </summary>
    /// <param name="color">Line color.</param>
    /// <param name="strokeWidth">Line width in pixels.</param>
    /// <param name="dashed">Whether a repeating dash pattern should be applied.</param>
    /// <returns>A disposable configured line paint.</returns>
    private static SKPaint CreateLinePaint(SKColor color, float strokeWidth, bool dashed = false)
    {
        return new SKPaint
        {
            Color = color,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = strokeWidth,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true,
            PathEffect = dashed ? SKPathEffect.CreateDash(new[] { 18f, 12f }, 0) : null
        };
    }

    /// <summary>
    /// Formats a large non-negative chart value with K, M, or B suffixes.
    /// </summary>
    /// <param name="value">Count or Iranian toman value.</param>
    /// <returns>Compact invariant-culture text suitable for chart axes and point labels.</returns>
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
    /// Formats one Tehran-local date as a short Persian-calendar month/day label.
    /// </summary>
    /// <param name="dateIran">Tehran-local date.</param>
    /// <returns>Numeric <c>MM/dd</c> Persian-calendar label using ASCII digits.</returns>
    private static string FormatDateLabel(DateTime dateIran)
    {
        var calendar = new PersianCalendar();
        return $"{calendar.GetMonth(dateIran):00}/{calendar.GetDayOfMonth(dateIran):00}";
    }

    /// <summary>
    /// Formats the inclusive start and final completed day of one report for the chart header.
    /// </summary>
    /// <param name="report">Completed-day report whose range should be shown.</param>
    /// <returns>ASCII Persian-calendar range.</returns>
    private static string FormatDateRange(UsageAnalyticsReport report)
    {
        return $"{UsageAnalyticsService.FormatPersianDate(report.StartDateIran)} - " +
               UsageAnalyticsService.FormatPersianDate(report.EndDateIran.AddDays(-1));
    }
}
