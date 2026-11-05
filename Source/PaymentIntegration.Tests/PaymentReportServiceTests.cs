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
    public async Task Currency_totals_combine_daily_activity_balances_and_flags_without_mixing_currencies()
    {
        var refunded = ReportTestData.Payment();
        var approved = ReportTestData.Payment() with { SubMerchantId = "merchant-b", RequestedAmount = 6000,
            CreatedAt = DateTimeOffset.Parse("2026-11-05T08:00:00Z") };
        var declined = approved with { PaymentId = Guid.NewGuid(), RequestedAmount = 4200 };
        var pending = approved with { PaymentId = Guid.NewGuid(), SubMerchantId = "merchant-c", RequestedAmount = 17000 };
        var reversed = ReportTestData.Payment() with { SubMerchantId = "merchant-c", RequestedAmount = 50000 };
        var usd = approved with { PaymentId = Guid.NewGuid(), SubMerchantId = "merchant-a", CurrencyCode = "USD", RequestedAmount = 2500 };
        var (service, _) = ReportTestData.CreateService([
            new(refunded, "Merchant A", [ReportTestData.Outcome(refunded), ReportTestData.Adjustment(refunded),
                ReportTestData.Adjustment(refunded, "Reversal", 100000, "2026-11-06T10:00:00.000", "2026-11-06T08:00:00Z", "later-reversal")]),
            new(approved, "Merchant B", [ReportTestData.Outcome(approved, "2026-11-05T10:01:00", "2026-11-05T08:01:00Z", amount: 6000)]),
            new(declined, "Merchant B", [ReportTestData.Outcome(declined, "2026-11-05T10:01:00", "2026-11-05T08:01:00Z", "05", null)]),
            new(pending, "Merchant C", []),
            new(reversed, "Merchant C", [ReportTestData.Outcome(reversed, amount: 50000),
                ReportTestData.Adjustment(reversed, "Reversal", 50000)]),
            new(usd, "Merchant A", [ReportTestData.Outcome(usd, "2026-11-05T10:01:00", "2026-11-05T08:01:00Z", amount: 2500)])
        ]);

        var report = await service.GetDailyAsync(new(2026, 11, 5), CancellationToken.None);

        Assert.Equal(2, report.CurrencySummaries.Count);
        var zar = Assert.Single(report.CurrencySummaries, item => item.CurrencyCode == "ZAR");
        Assert.Equal(3, zar.SubMerchantCount);
        Assert.Equal(1, zar.FlaggedSubMerchantCount);
        Assert.Equal(3, zar.PaymentsRequested);
        Assert.Equal(27200, zar.TotalRequested);
        Assert.Equal(1, zar.PaymentsApproved);
        Assert.Equal(6000, zar.TotalApproved);
        Assert.Equal(1, zar.PaymentsDeclined);
        Assert.Equal(4200, zar.TotalDeclined);
        Assert.Equal(1, zar.Reversals);
        Assert.Equal(50000, zar.TotalReversed);
        Assert.Equal(1, zar.Refunds);
        Assert.Equal(30000, zar.TotalRefunded);
        Assert.Equal(76000, zar.RemainingPaymentValue);
        Assert.Equal(17000, zar.PendingApprovalValue);
        var dollars = Assert.Single(report.CurrencySummaries, item => item.CurrencyCode == "USD");
        Assert.Equal(1, dollars.SubMerchantCount);
        Assert.Equal(2500, dollars.TotalApproved);
        Assert.Equal(2500, dollars.RemainingPaymentValue);
        Assert.Equal(0, dollars.FlaggedSubMerchantCount);
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
