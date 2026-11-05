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

        Assert.Equal(3, pdf.NumberOfPages);
        var text = string.Join("\n", pdf.GetPages().Select(page => ContentOrderTextExtractor.GetText(page)));
        for (var index = 0; index < 5; index++)
            Assert.Contains($"Merchant {index}", text);
        foreach (var page in pdf.GetPages())
        {
            var pageText = ContentOrderTextExtractor.GetText(page);
            Assert.Contains("Payments approved", pageText);
            Assert.Contains("Reversals", pageText);
            Assert.Contains("Refunds", pageText);
            Assert.Contains("Remaining payment value", pageText);
            Assert.All(page.Letters, letter =>
            {
                Assert.InRange(Convert.ToDouble(letter.BoundingBox.Left), 0, Convert.ToDouble(page.Width));
                Assert.InRange(Convert.ToDouble(letter.BoundingBox.Right), 0, Convert.ToDouble(page.Width));
                Assert.InRange(Convert.ToDouble(letter.BoundingBox.Bottom), 0, Convert.ToDouble(page.Height));
                Assert.InRange(Convert.ToDouble(letter.BoundingBox.Top), 0, Convert.ToDouble(page.Height));
            });
        }
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
        Assert.Contains("Unknown remaining payments", text);
        Assert.Contains("Known remaining portion", text);
    }
}
