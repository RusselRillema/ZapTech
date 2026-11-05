using Moq;
using PaymentIntegration.Reports;
using Xunit;

namespace PaymentIntegration.Tests;

public sealed class PaymentReportServiceTests
{
    [Theory]
    [InlineData(3, 1, 0, 0, 0, 100000, true)]
    [InlineData(4, 0, 1, 0, 100000, 0, true)]
    [InlineData(5, 0, 0, 1, 70000, 0, false)]
    public async Task Daily_activity_is_separate_from_cumulative_historical_balances(
        int day, long requested, long approved, long refunds, decimal remaining, decimal pending, bool subsequent)
    {
        var payment = ReportTestData.Payment();
        var history = new PaymentReportHistory(payment, "Merchant A",
            [ReportTestData.Outcome(payment), ReportTestData.Adjustment(payment)]);
        var (service, repository) = ReportTestData.CreateService([history]);

        var report = await service.GetDailyAsync(new(2026, 11, day), CancellationToken.None);

        var summary = Assert.Single(report.Summaries);
        Assert.Equal(requested, summary.PaymentsRequested);
        Assert.Equal(day == 3 ? 100000 : 0, summary.TotalRequested);
        Assert.Equal(approved, summary.PaymentsApproved);
        Assert.Equal(day == 4 ? 100000 : 0, summary.TotalApproved);
        Assert.Equal(refunds, summary.Refunds);
        Assert.Equal(day == 5 ? 30000 : 0, summary.TotalRefunded);
        Assert.Equal(remaining, summary.RemainingPaymentValue);
        Assert.Equal(pending, summary.PendingApprovalValue);
        Assert.Equal(subsequent, summary.HasSubsequentActivity);
        repository.Verify(value => value.GetHistoryAsync(ReportingPeriod.ForDate(report.Date).End,
            It.IsAny<CancellationToken>()), Times.Once);
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Groups_merchants_and_currencies_independently_and_excludes_future_payments()
    {
        var zar = ReportTestData.Payment();
        var usd = ReportTestData.Payment() with { CurrencyCode = "USD", RequestedAmount = 2500 };
        var other = ReportTestData.Payment() with { SubMerchantId = "merchant-b", RequestedAmount = 4000 };
        var future = ReportTestData.Payment() with { CreatedAt = DateTimeOffset.Parse("2026-11-05T00:00:00Z") };
        var (service, _) = ReportTestData.CreateService([
            new(zar, "Merchant A", [ReportTestData.Outcome(zar)]),
            new(usd, "Merchant A", [ReportTestData.Outcome(usd, amount: 2500)]),
            new(other, "Merchant B", [ReportTestData.Outcome(other, amount: 2000)]),
            new(future, "Future merchant", [])
        ]);

        var report = await service.GetDailyAsync(new(2026, 11, 4), CancellationToken.None);

        Assert.Equal(3, report.Summaries.Count);
        Assert.Equal(100000, Assert.Single(report.Summaries, value => value.SubMerchantId == "merchant-a" && value.CurrencyCode == "ZAR").TotalApproved);
        Assert.Equal(2500, Assert.Single(report.Summaries, value => value.CurrencyCode == "USD").TotalApproved);
        Assert.Equal(2000, Assert.Single(report.Summaries, value => value.SubMerchantId == "merchant-b").TotalApproved);
    }

    [Fact]
    public async Task Declined_values_use_requested_amounts_and_exclude_gateway_failures()
    {
        var declined = ReportTestData.Payment() with { RequestedAmount = 4250 };
        var failed = ReportTestData.Payment() with { RequestedAmount = 9000 };
        var rejection = ReportTestData.Outcome(failed) with { HttpStatusCode = 400 };
        var (service, _) = ReportTestData.CreateService([
            new(declined, "Merchant A", [ReportTestData.Outcome(declined, code: "05", amount: null)]),
            new(failed, "Merchant A", [rejection])
        ]);

        var report = await service.GetDailyAsync(new(2026, 11, 4), CancellationToken.None);

        var summary = Assert.Single(report.Summaries);
        Assert.Equal(1, summary.PaymentsDeclined);
        Assert.Equal(4250, summary.TotalDeclined);
        Assert.Equal(0, summary.PaymentsApproved);
        Assert.Equal(0, summary.PendingApprovalValue);
    }

    [Fact]
    public async Task Replayed_refunds_are_counted_once_using_the_actual_event_processor()
    {
        var payment = ReportTestData.Payment();
        var refund = ReportTestData.Adjustment(payment);
        var duplicate = refund with { PaymentEventId = Guid.NewGuid(), ReceivedAt = refund.ReceivedAt.AddSeconds(1) };
        var (service, _) = ReportTestData.CreateService([new(payment, "Merchant A", [ReportTestData.Outcome(payment), refund, duplicate])]);

        var report = await service.GetDailyAsync(new(2026, 11, 5), CancellationToken.None);

        var summary = Assert.Single(report.Summaries);
        Assert.Equal(1, summary.Refunds);
        Assert.Equal(30000, summary.TotalRefunded);
        Assert.Equal(70000, summary.RemainingPaymentValue);
    }

    [Fact]
    public async Task Totals_can_exceed_a_single_payments_int64_amount_without_overflow()
    {
        var first = ReportTestData.Payment() with { RequestedAmount = long.MaxValue };
        var second = ReportTestData.Payment() with { RequestedAmount = long.MaxValue };
        var (service, _) = ReportTestData.CreateService([
            new(first, "Merchant A", [ReportTestData.Outcome(first, amount: long.MaxValue)]),
            new(second, "Merchant A", [ReportTestData.Outcome(second, amount: long.MaxValue)])
        ]);

        var report = await service.GetDailyAsync(new(2026, 11, 4), CancellationToken.None);

        Assert.Equal(18446744073709551614m, Assert.Single(report.Summaries).TotalApproved);
    }
}
