using System.Globalization;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PaymentIntegration.Reports;

public sealed class PaymentReportPdfRenderer
{
    public byte[] Render(DailyPaymentReport report)
    {
        ReportFontResolver.EnsureRegistered();
        using var document = new PdfDocument();
        document.Info.Title = $"Daily payment report - {report.Date:yyyy-MM-dd}";
        document.Info.CreationDate = report.GeneratedAt.UtcDateTime;
        document.Info.Creator = "Zapper payment reporting";
        var regular = new XFont("ReportSans", 10, XFontStyleEx.Regular);
        var bold = new XFont("ReportSans", 10, XFontStyleEx.Bold);
        var small = new XFont("ReportSans", 8, XFontStyleEx.Regular);
        var title = new XFont("ReportSans", 22, XFontStyleEx.Bold);
        var ink = new XSolidBrush(XColor.FromArgb(25, 42, 60));
        var muted = new XSolidBrush(XColor.FromArgb(95, 110, 126));
        var rule = new XPen(XColor.FromArgb(221, 227, 233), 0.5);
        var chunks = report.Summaries.Chunk(2).ToArray();
        var pageCount = Math.Max(1, chunks.Length);

        for (var pageNumber = 0; pageNumber < pageCount; pageNumber++)
        {
            var page = document.AddPage();
            page.Size = PageSize.A4;
            page.Orientation = PageOrientation.Landscape;
            using var graphics = XGraphics.FromPdfPage(page);
            const double margin = 42;
            var width = page.Width.Point - 2 * margin;
            graphics.DrawString("DAILY PAYMENT REPORT", small, muted, margin, 40);
            graphics.DrawString(report.Date.ToString("d MMMM yyyy", CultureInfo.InvariantCulture), title, ink, margin, 72);
            graphics.DrawString("Daily activity and balances at the end of the reporting date (South African time)",
                regular, muted, margin, 94);

            var groups = chunks.Length == 0 ? [] : chunks[pageNumber];
            if (groups.Length == 0)
            {
                graphics.DrawString("No payments existed before the end of this reporting date.", regular, ink, margin, 150);
            }
            else
            {
                const double metricWidth = 225;
                var valueWidth = (width - metricWidth) / groups.Length;
                graphics.DrawRectangle(new XSolidBrush(XColor.FromArgb(25, 42, 60)), margin, 118, width, 51);
                graphics.DrawString("Metric", bold, XBrushes.White, new XRect(margin + 12, 118, metricWidth - 24, 51), XStringFormats.CenterLeft);
                for (var column = 0; column < groups.Length; column++)
                {
                    var group = groups[column];
                    var x = margin + metricWidth + column * valueWidth;
                    graphics.DrawString(Fit(graphics, group.SubMerchantName, bold, valueWidth - 20), bold, XBrushes.White,
                        new XRect(x + 10, 124, valueWidth - 20, 20), XStringFormats.CenterRight);
                    graphics.DrawString($"{group.SubMerchantId} ({group.CurrencyCode})", small, XBrushes.White,
                        new XRect(x + 10, 144, valueWidth - 20, 15), XStringFormats.CenterRight);
                }

                var rows = Rows(groups.Any(group => group.UnknownRemainingPayments > 0));
                var y = 169d;
                foreach (var row in rows)
                {
                    const double height = 23;
                    if (row.Emphasized)
                        graphics.DrawRectangle(new XSolidBrush(XColor.FromArgb(237, 243, 248)), margin, y, width, height);
                    graphics.DrawString(row.Label, row.Emphasized ? bold : regular, ink,
                        new XRect(margin + 12, y, metricWidth - 24, height), XStringFormats.CenterLeft);
                    for (var column = 0; column < groups.Length; column++)
                    {
                        var value = row.Value(groups[column]);
                        var font = row.Emphasized ? bold : regular;
                        var measured = graphics.MeasureString(value, font).Width;
                        if (measured > valueWidth - 20)
                            font = new XFont("ReportSans", font.Size * (valueWidth - 20) / measured,
                                row.Emphasized ? XFontStyleEx.Bold : XFontStyleEx.Regular);
                        graphics.DrawString(value, font, ink,
                            new XRect(margin + metricWidth + column * valueWidth + 10, y, valueWidth - 20, height),
                            XStringFormats.CenterRight);
                    }
                    y += height;
                    graphics.DrawLine(rule, margin, y, margin + width, y);
                }
            }

            var footer = page.Height.Point - 67;
            graphics.DrawString("Amounts: ZAR in rand; other currencies explicitly shown in minor units.", small, muted, margin, footer);
            graphics.DrawString("Subsequent activity flags later financial changes; later transaction details are excluded.", small, muted, margin, footer + 13);
            graphics.DrawString($"Generated {TimeZoneInfo.ConvertTime(report.GeneratedAt, ReportingPeriod.TimeZone):yyyy-MM-dd HH:mm} SAST",
                small, muted, margin, footer + 32);
            graphics.DrawString($"Page {pageNumber + 1} of {pageCount}", small, muted,
                new XRect(margin, footer + 20, width, 20), XStringFormats.CenterRight);
        }

        using var output = new MemoryStream();
        document.Save(output, false);
        return output.ToArray();
    }

    private sealed record Row(string Label, Func<MerchantPaymentSummary, string> Value, bool Emphasized = false);

    private static Row[] Rows(bool hasUnknown) =>
    [
        new("Payments requested", group => Count(group.PaymentsRequested)),
        new("Total requested", group => Money(group.TotalRequested, group.CurrencyCode)),
        new("Payments approved", group => Count(group.PaymentsApproved)),
        new("Total approved", group => Money(group.TotalApproved, group.CurrencyCode)),
        new("Payments declined", group => Count(group.PaymentsDeclined)),
        new("Total declined", group => Money(group.TotalDeclined, group.CurrencyCode)),
        new("Reversals", group => Count(group.Reversals)),
        new("Total reversed", group => Money(group.TotalReversed, group.CurrencyCode)),
        new("Refunds", group => Count(group.Refunds)),
        new("Total refunded", group => Money(group.TotalRefunded, group.CurrencyCode)),
        new("Remaining payment value", group => group.UnknownRemainingPayments > 0 ? "Unknown" : Money(group.RemainingPaymentValue, group.CurrencyCode), true),
        new("Pending approval value", group => Money(group.PendingApprovalValue, group.CurrencyCode)),
        .. hasUnknown ? new Row[]
        {
            new("Known remaining portion", group => Money(group.RemainingPaymentValue, group.CurrencyCode)),
            new("Unknown remaining payments", group => Count(group.UnknownRemainingPayments))
        } : [],
        new("Subsequent activity", group => group.HasSubsequentActivity ? "Yes" : "No", true)
    ];

    private static string Count(long count) => count.ToString("N0", CultureInfo.InvariantCulture);
    private static string Money(decimal minorUnits, string currency) => currency == "ZAR"
        ? "R" + (minorUnits / 100m).ToString("N2", CultureInfo.InvariantCulture)
        : minorUnits.ToString("N0", CultureInfo.InvariantCulture) + " minor units";

    private static string Fit(XGraphics graphics, string text, XFont font, double width)
    {
        if (graphics.MeasureString(text, font).Width <= width)
            return text;
        var length = text.Length;
        while (length > 0 && graphics.MeasureString(text[..length] + "...", font).Width > width)
            length--;
        return text[..length] + "...";
    }
}
