using System.Text.Json;
using PaymentIntegration.Reports;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using Xunit;

namespace PaymentIntegration.Tests;

public sealed class PaymentReportPdfTests
{
    [Fact]
    public async Task More_merchants_continue_onto_new_pages_with_all_required_metrics()
    {
        var histories = Enumerable.Range(0, 5).Select(index =>
        {
            var payment = ReportTestData.Payment() with { SubMerchantId = $"merchant-{index}" };
            return new PaymentReportHistory(payment, $"Merchant {index}", [ReportTestData.Outcome(payment)]);
        }).ToArray();
        var (service, _) = ReportTestData.CreateService(histories);
        var report = await service.GetDailyAsync(new(2026, 11, 4), CancellationToken.None);

        using var pdf = PdfDocument.Open(new PaymentReportPdfRenderer().Render(report));

        Assert.Equal(2, pdf.NumberOfPages);
        var text = string.Join("\n", pdf.GetPages().Select(page => ContentOrderTextExtractor.GetText(page)));
        for (var index = 0; index < 5; index++)
            Assert.Contains($"Merchant {index}", text);
        foreach (var page in pdf.GetPages())
        {
            var pageText = ContentOrderTextExtractor.GetText(page);
            foreach (var label in new[] { "Requested", "Approved", "Declined", "Reversed", "Refunded", "Remaining", "Pending appr.", "Later" })
                Assert.Contains(label, pageText);
            Assert.All(page.Letters, letter =>
            {
                Assert.InRange(Convert.ToDouble(letter.BoundingBox.Left), 0, Convert.ToDouble(page.Width));
                Assert.InRange(Convert.ToDouble(letter.BoundingBox.Right), 0, Convert.ToDouble(page.Width));
                Assert.InRange(Convert.ToDouble(letter.BoundingBox.Bottom), 0, Convert.ToDouble(page.Height));
                Assert.InRange(Convert.ToDouble(letter.BoundingBox.Top), 0, Convert.ToDouble(page.Height));
            });
        }
        Assert.DoesNotContain("ZAR subtotal", ContentOrderTextExtractor.GetText(pdf.GetPage(1)));
        Assert.Contains("ZAR subtotal", ContentOrderTextExtractor.GetText(pdf.GetPage(2)));
    }

    [Fact]
    public async Task Each_currency_has_its_own_totals_and_merchant_rows_with_explicit_units()
    {
        var zar = ReportTestData.Payment();
        var usd = ReportTestData.Payment() with { CurrencyCode = "USD", RequestedAmount = 2500 };
        var (service, _) = ReportTestData.CreateService([
            new(zar, "Rand merchant", [ReportTestData.Outcome(zar)]),
            new(usd, "Dollar merchant", [ReportTestData.Outcome(usd, amount: 2500)])
        ]);
        var report = await service.GetDailyAsync(new(2026, 11, 4), CancellationToken.None);

        using var pdf = PdfDocument.Open(new PaymentReportPdfRenderer().Render(report));

        Assert.Equal(2, pdf.NumberOfPages);
        var randPage = ContentOrderTextExtractor.GetText(pdf.GetPage(1));
        var dollarPage = ContentOrderTextExtractor.GetText(pdf.GetPage(2));
        Assert.Contains("ZAR TOTALS", randPage);
        Assert.Contains("ZAR subtotal", randPage);
        Assert.Contains("Rand merchant", randPage);
        Assert.Contains("Amounts in rand", randPage);
        Assert.Contains("R1,000.00", randPage);
        Assert.DoesNotContain("Dollar merchant", randPage);
        Assert.Contains("USD TOTALS", dollarPage);
        Assert.Contains("USD subtotal", dollarPage);
        Assert.Contains("Dollar merchant", dollarPage);
        Assert.Contains("Amounts in minor units", dollarPage);
        Assert.Contains("2,500", dollarPage);
        Assert.DoesNotContain("Rand merchant", dollarPage);
        Assert.DoesNotContain("R1,000.00", dollarPage);
    }

    [Fact]
    public async Task Unknown_balances_are_identified_instead_of_rendered_as_a_known_zero()
    {
        var payment = ReportTestData.Payment();
        var conflict = ReportTestData.Adjustment(payment) with
        {
            Payload = JsonSerializer.SerializeToElement(new
            {
                transactionUUID = "conflicting-outcome", transactionType = "Refund",
                transactionDateTime = "2026-11-05T10:00:00.000", rrn = payment.OrderNumber,
                transactionAmount = 30000, currencyCode = "ZAR", txResultCode = "00", isApproved = false
            })
        };
        var (service, _) = ReportTestData.CreateService([
            new(payment, "Merchant A", [ReportTestData.Outcome(payment), conflict])
        ]);
        var report = await service.GetDailyAsync(new(2026, 11, 5), CancellationToken.None);

        Assert.Equal(1, Assert.Single(report.Summaries).UnknownRemainingPayments);
        using var pdf = PdfDocument.Open(new PaymentReportPdfRenderer().Render(report));
        var text = ContentOrderTextExtractor.GetText(pdf.GetPage(1));
        Assert.Contains("Unknown", text);
        Assert.Contains("1 unknown payment", text);
        Assert.Contains("Known: R0.00", text);
    }
}
