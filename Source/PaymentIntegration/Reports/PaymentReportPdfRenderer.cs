using System.Globalization;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PaymentIntegration.Reports;

public sealed class PaymentReportPdfRenderer
{
    private const double Margin = 33;
    private const double TableTop = 232;
    private const double RowHeight = 51;
    private const int MerchantsPerPage = 3;
    private static readonly XColor Navy = XColor.FromArgb(17, 43, 60);
    private static readonly XColor Teal = XColor.FromArgb(0, 113, 119);
    private static readonly XColor Muted = XColor.FromArgb(109, 129, 149);
    private static readonly XColor PaleTeal = XColor.FromArgb(233, 246, 245);
    private static readonly XColor Rule = XColor.FromArgb(214, 225, 234);
    private static readonly double[] ColumnWidths = [130, 38, 80, 80, 76, 72, 76, 80, 88, 56];

    public byte[] Render(DailyPaymentReport report)
    {
        ReportFontResolver.EnsureRegistered();
        using var document = new PdfDocument();
        document.Info.Title = $"Daily payment summary - {report.Date:yyyy-MM-dd}";
        document.Info.CreationDate = report.GeneratedAt.UtcDateTime;
        document.Info.Creator = "Zapper payment reporting";
        var pages = report.CurrencySummaries.SelectMany(currency => report.Summaries
            .Where(merchant => merchant.CurrencyCode == currency.CurrencyCode)
            .Chunk(MerchantsPerPage)
            .Select((merchants, index) => new ReportPage(currency, merchants,
                index == (currency.SubMerchantCount - 1) / MerchantsPerPage))).ToArray();
        var pageCount = Math.Max(1, pages.Length);

        for (var index = 0; index < pageCount; index++)
        {
            var page = document.AddPage();
            page.Size = PageSize.A4;
            page.Orientation = PageOrientation.Landscape;
            using var graphics = XGraphics.FromPdfPage(page);
            var width = page.Width.Point - 2 * Margin;
            Header(graphics, report, width, pages.Length == 0 ? null : pages[index].Currency.CurrencyCode);
            if (pages.Length == 0)
            {
                graphics.DrawRoundedRectangle(Brush(PaleTeal), new XRect(Margin, 134, width, 100), new XSize(8, 8));
                Text(graphics, "No payments existed before the end of this reporting date.",
                    Margin + 18, 177, width - 36, 12, bold: true);
                Text(graphics, "Choose a date with payment activity to see currency totals and merchant balances.",
                    Margin + 18, 201, width - 36, 9, color: Muted);
            }
            else
            {
                var content = pages[index];
                CurrencyCards(graphics, content.Currency, width);
                Table(graphics, content, width);
            }
            Footer(graphics, report, width, page.Height.Point, index + 1, pageCount);
        }

        using var output = new MemoryStream();
        document.Save(output, false);
        return output.ToArray();
    }

    private static void Header(XGraphics graphics, DailyPaymentReport report, double width, string? currency)
    {
        graphics.DrawRectangle(Brush(Navy), 0, 0, width + 2 * Margin, 14);
        Text(graphics, "DAILY PAYMENT SUMMARY", Margin, 54, width - 100, 21, bold: true);
        Text(graphics, report.Date.ToString("dddd, d MMMM yyyy", CultureInfo.InvariantCulture)
            + "  /  South African Standard Time (UTC+02:00)", Margin, 75, width, 9, color: Muted);
        if (currency is not null)
        {
            graphics.DrawRoundedRectangle(Brush(PaleTeal), new XRect(Margin + width - 88, 43, 88, 26), new XSize(8, 8));
            Text(graphics, currency, Margin + width - 88, 60, 88, 10, bold: true, color: Teal, centered: true);
        }
        graphics.DrawLine(new XPen(Rule, 0.5), Margin, 90, Margin + width, 90);
    }

    private static void CurrencyCards(XGraphics graphics, CurrencyPaymentSummary totals, double width)
    {
        Text(graphics, totals.CurrencyCode + " TOTALS", Margin, 112, 95, 9, bold: true, color: Teal);
        Text(graphics, "Across " + Quantity(totals.SubMerchantCount, "sub-merchant"), Margin + 98, 112, 220, 8, color: Muted);
        Text(graphics, totals.CurrencyCode == "ZAR" ? "Amounts in rand" : "Amounts in minor units",
            Margin + width - 160, 112, 160, 8, color: Muted, right: true);
        (string Label, string Value, string Detail)[] cards =
        [
            ("REQUESTED", Money(totals.TotalRequested, totals.CurrencyCode), Quantity(totals.PaymentsRequested, "payment")),
            ("APPROVED", Money(totals.TotalApproved, totals.CurrencyCode), Quantity(totals.PaymentsApproved, "payment")),
            ("REVERSED", Money(totals.TotalReversed, totals.CurrencyCode), Quantity(totals.Reversals, "reversal")),
            ("REFUNDED", Money(totals.TotalRefunded, totals.CurrencyCode), Quantity(totals.Refunds, "refund")),
            ("REMAINING VALUE", Remaining(totals, totals.CurrencyCode), totals.UnknownRemainingPayments > 0
                ? Quantity(totals.UnknownRemainingPayments, "unknown payment") : "as at date end")
        ];
        var cardWidth = (width - 4 * 9) / 5;
        for (var index = 0; index < cards.Length; index++)
        {
            var x = Margin + index * (cardWidth + 9);
            var last = index == cards.Length - 1;
            graphics.DrawRoundedRectangle(new XPen(last ? PaleTeal : Rule, 0.6),
                Brush(last ? PaleTeal : XColor.FromArgb(245, 248, 250)),
                new XRect(x, 125, cardWidth, 69), new XSize(8, 8));
            Text(graphics, cards[index].Label, x + 12, 145, cardWidth - 24, 7.5, bold: true,
                color: last ? Teal : Navy);
            Text(graphics, cards[index].Value, x + 12, 172, cardWidth - 24, 17, bold: true,
                color: last ? Teal : Navy);
            Text(graphics, cards[index].Detail, x + 12, 185, cardWidth - 24, 7, color: Muted);
        }
    }

    private static void Table(XGraphics graphics, ReportPage page, double width)
    {
        Text(graphics, "MERCHANT / CURRENCY BREAKDOWN", Margin, 220, 310, 10, bold: true);
        Text(graphics, "Activity on reporting date  |  Balances as at end of date",
            Margin + 310, 220, width - 310, 7.5, color: Muted, right: true);
        graphics.DrawRoundedRectangle(Brush(Navy), new XRect(Margin, TableTop, width, 27), new XSize(7, 7));
        graphics.DrawRectangle(Brush(Navy), Margin, TableTop + 12, width, 8);
        Text(graphics, "IDENTITY", X(0), TableTop + 13, Span(0, 2), 7, bold: true, color: XColors.White, centered: true);
        Text(graphics, "DAILY ACTIVITY   (amount / count)", X(2), TableTop + 13, Span(2, 5), 7,
            bold: true, color: XColors.White, centered: true);
        Text(graphics, "END-OF-DAY BALANCE", X(7), TableTop + 13, Span(7, 2), 7,
            bold: true, color: XColors.White, centered: true);
        Text(graphics, "FLAG", X(9), TableTop + 13, ColumnWidths[9], 7, bold: true, color: XColors.White, centered: true);
        graphics.DrawRectangle(Brush(XColor.FromArgb(231, 239, 244)), Margin, TableTop + 18, width, 30);
        string[] labels = ["Sub-merchant", "CCY", "Requested", "Approved", "Declined", "Reversed", "Refunded", "Remaining", "Pending appr.", "Later"];
        for (var column = 0; column < labels.Length; column++)
            Text(graphics, labels[column], X(column) + (column == 0 ? 10 : 3), TableTop + 37,
                ColumnWidths[column] - (column == 0 ? 16 : 6), 7.5, bold: true, centered: column != 0);

        var y = TableTop + 48;
        for (var row = 0; row < page.Merchants.Length; row++)
        {
            var merchant = page.Merchants[row];
            if (row % 2 == 1)
                graphics.DrawRectangle(Brush(XColor.FromArgb(249, 251, 253)), Margin, y, width, RowHeight);
            Identity(graphics, merchant.SubMerchantName, merchant.SubMerchantId, merchant.CurrencyCode, y);
            Values(graphics, merchant, merchant.CurrencyCode, y);
            if (merchant.HasSubsequentActivity)
            {
                graphics.DrawRoundedRectangle(Brush(XColor.FromArgb(255, 241, 215)),
                    new XRect(X(9) + 12, y + 16, ColumnWidths[9] - 24, 19), new XSize(7, 7));
                Text(graphics, "YES", X(9) + 12, y + 29, ColumnWidths[9] - 24, 7.5,
                    bold: true, color: XColor.FromArgb(157, 98, 0), centered: true);
            }
            else
                Text(graphics, "No", X(9), y + 29, ColumnWidths[9], 7.5, color: Muted, centered: true);
            y += RowHeight;
        }
        if (page.LastForCurrency)
        {
            var currency = page.Currency;
            graphics.DrawRectangle(Brush(PaleTeal), Margin, y, width, RowHeight);
            Identity(graphics, currency.CurrencyCode + " subtotal", Quantity(currency.SubMerchantCount, "sub-merchant"),
                currency.CurrencyCode, y, subtotal: true);
            Values(graphics, currency, currency.CurrencyCode, y, subtotal: true);
            Text(graphics, $"{currency.FlaggedSubMerchantCount} / {currency.SubMerchantCount}",
                X(9), y + 24, ColumnWidths[9], 7.5, bold: true, color: Teal, centered: true);
            Text(graphics, "flagged", X(9), y + 40, ColumnWidths[9], 6.5, color: Muted, centered: true);
            graphics.DrawLine(new XPen(Teal, 0.8), Margin, y + RowHeight, Margin + width, y + RowHeight);
        }
        else
        {
            graphics.DrawLine(new XPen(Rule, 0.5), Margin, y, Margin + width, y);
            Text(graphics, "Continued on the next page. Currency subtotal follows the final merchant row.",
                Margin + 10, y + 18, width - 20, 7.5, color: Muted);
        }
    }

    private static void Identity(XGraphics graphics, string name, string detail, string currency, double y, bool subtotal = false)
    {
        Text(graphics, name, Margin + 10, y + 22, ColumnWidths[0] - 20, 8.5, bold: true,
            color: subtotal ? Teal : Navy, ellipsis: true);
        Text(graphics, detail, Margin + 10, y + 39, ColumnWidths[0] - 20, 6.8, color: Muted, ellipsis: true);
        Text(graphics, currency, X(1), y + 29, ColumnWidths[1], 7.5, bold: true, centered: true);
    }

    private static void Values(XGraphics graphics, PaymentSummaryTotals totals, string currency, double y, bool subtotal = false)
    {
        (decimal Amount, long Count, string Unit)[] activity =
        [
            (totals.TotalRequested, totals.PaymentsRequested, "payment"),
            (totals.TotalApproved, totals.PaymentsApproved, "payment"),
            (totals.TotalDeclined, totals.PaymentsDeclined, "payment"),
            (totals.TotalReversed, totals.Reversals, "event"),
            (totals.TotalRefunded, totals.Refunds, "event")
        ];
        for (var index = 0; index < activity.Length; index++)
        {
            var column = index + 2;
            Text(graphics, Money(activity[index].Amount, currency), X(column) + 5, y + 23,
                ColumnWidths[column] - 10, 8.5, bold: subtotal, color: subtotal ? Teal : Navy, centered: true);
            Text(graphics, Quantity(activity[index].Count, activity[index].Unit), X(column) + 5, y + 39,
                ColumnWidths[column] - 10, 6.5, color: Muted, centered: true);
        }
        Text(graphics, Remaining(totals, currency), X(7) + 5, y + (totals.UnknownRemainingPayments > 0 ? 20 : 29),
            ColumnWidths[7] - 10, 9, bold: true, color: subtotal ? Teal : Navy, centered: true);
        if (totals.UnknownRemainingPayments > 0)
        {
            Text(graphics, "Known: " + Money(totals.RemainingPaymentValue, currency), X(7) + 4, y + 34,
                ColumnWidths[7] - 8, 6, color: Muted, centered: true);
            Text(graphics, Quantity(totals.UnknownRemainingPayments, "unknown payment"), X(7) + 4, y + 44,
                ColumnWidths[7] - 8, 6, color: Muted, centered: true);
        }
        Text(graphics, Money(totals.PendingApprovalValue, currency), X(8) + 5, y + 29,
            ColumnWidths[8] - 10, 8.5, centered: true);
    }

    private static void Footer(XGraphics graphics, DailyPaymentReport report, double width, double height, int page, int pages)
    {
        Text(graphics, "HOW TO READ THIS REPORT", Margin, height - 90, width, 8, bold: true, color: Teal);
        Text(graphics, "Each row is one sub-merchant + currency. Totals are kept separate for each currency; no conversion is applied.",
            Margin, height - 74, width, 7.5, color: Muted);
        Text(graphics, "Activity reflects the report date; Remaining payment value and Pending approval value are historical end-of-day balances.",
            Margin, height - 60, width, 7.5, color: Muted);
        Text(graphics, "Later = a financial state change after the report date; subsequent amounts are intentionally not shown.",
            Margin, height - 46, width, 7.5, color: Muted);
        graphics.DrawLine(new XPen(Rule, 0.5), Margin, height - 35, Margin + width, height - 35);
        Text(graphics, $"Generated {TimeZoneInfo.ConvertTime(report.GeneratedAt, ReportingPeriod.TimeZone):yyyy-MM-dd HH:mm} SAST",
            Margin, height - 22, width - 100, 7, color: Muted);
        Text(graphics, $"PAGE {page} OF {pages}", Margin + width - 100, height - 22, 100, 7,
            bold: true, color: Muted, right: true);
    }

    private sealed record ReportPage(CurrencyPaymentSummary Currency, MerchantPaymentSummary[] Merchants, bool LastForCurrency);
    private static XSolidBrush Brush(XColor color) => new(color);
    private static double X(int column) => Margin + ColumnWidths.Take(column).Sum();
    private static double Span(int start, int count) => ColumnWidths.Skip(start).Take(count).Sum();
    private static string Quantity(long count, string unit) => count.ToString("N0", CultureInfo.InvariantCulture)
        + " " + unit + (count == 1 ? "" : "s");
    private static string Money(decimal minorUnits, string currency) => currency == "ZAR"
        ? "R" + (minorUnits / 100m).ToString("N2", CultureInfo.InvariantCulture)
        : minorUnits.ToString("N0", CultureInfo.InvariantCulture);
    private static string Remaining(PaymentSummaryTotals totals, string currency) =>
        totals.UnknownRemainingPayments > 0 ? "Unknown" : Money(totals.RemainingPaymentValue, currency);

    // Constrain every string to its cell, including large aggregates and long merchant names.
    private static void Text(XGraphics graphics, string value, double x, double baseline, double width, double size,
        bool bold = false, XColor? color = null, bool centered = false, bool right = false, bool ellipsis = false)
    {
        var style = bold ? XFontStyleEx.Bold : XFontStyleEx.Regular;
        var font = new XFont("ReportSans", size, style);
        var measured = graphics.MeasureString(value, font).Width;
        if (measured > width && ellipsis)
        {
            var length = value.Length;
            while (length > 0 && graphics.MeasureString(value[..length] + "...", font).Width > width)
                length--;
            value = value[..length] + "...";
        }
        else if (measured > width)
            font = new XFont("ReportSans", size * width / measured, style);
        var format = centered ? XStringFormats.BaseLineCenter : right ? XStringFormats.BaseLineRight : XStringFormats.BaseLineLeft;
        graphics.DrawString(value, font, Brush(color ?? Navy), new XRect(x, baseline, width, 0), format);
    }
}
