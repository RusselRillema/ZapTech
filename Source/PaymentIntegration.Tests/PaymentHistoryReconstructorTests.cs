using PaymentIntegration.Reports;
using Xunit;

namespace PaymentIntegration.Tests;

public sealed class PaymentHistoryReconstructorTests
{
    [Theory]
    [InlineData(3, "Unresolved", null, true)]
    [InlineData(4, "Approved", 100000L, true)]
    [InlineData(5, "PartiallyRefunded", 70000L, false)]
    public void Rebuilds_the_cross_midnight_history_without_using_current_state(
        int day, string state, long? remaining, bool subsequent)
    {
        var payment = ReportTestData.Payment() with
        {
            State = "PartiallyRefunded", ApprovedAmount = 100000, RemainingAmount = 70000
        };
        var approval = ReportTestData.Outcome(payment) with
        {
            ProcessingState = "Applied", ProcessedAt = DateTimeOffset.Parse("2026-11-03T22:01:00Z")
        };
        var refund = ReportTestData.Adjustment(payment) with
        {
            ProcessingState = "Applied", ProcessedAt = DateTimeOffset.Parse("2026-11-05T08:00:00Z")
        };

        var result = ReportTestData.Reconstructor().Reconstruct(new(payment, "Merchant A", [approval, refund]),
            ReportingPeriod.ForDate(new(2026, 11, day)), CancellationToken.None);

        Assert.Equal(state, result.Payment.State);
        Assert.Equal(remaining, result.Payment.RemainingAmount);
        Assert.Equal(subsequent, result.HasSubsequentActivity);
        Assert.Equal("PartiallyRefunded", payment.State);
        Assert.Equal(70000, payment.RemainingAmount);
        if (day == 3)
        {
            Assert.Null(result.Payment.ApprovedAmount);
            Assert.Null(result.OutcomeAt);
            Assert.Empty(result.Events);
        }
        else
        {
            Assert.Equal(100000, result.Payment.ApprovedAmount);
            Assert.Equal(DateTimeOffset.Parse("2026-11-03T22:01:00Z"), result.OutcomeAt);
        }
    }

    [Fact]
    public void Later_reversal_does_not_remove_a_refund_from_the_historical_balance()
    {
        var payment = ReportTestData.Payment() with { State = "Reversed", ApprovedAmount = 100000, RemainingAmount = 0 };
        var approval = ReportTestData.Outcome(payment);
        var refund = ReportTestData.Adjustment(payment, timestamp: "2026-11-04T10:00:00.000",
            receivedAt: "2026-11-04T08:00:00Z") with
        {
            ProcessingState = "Ignored", ProcessingComment = "A later full reversal superseded this refund.",
            ProcessedAt = DateTimeOffset.Parse("2026-11-05T08:00:00Z")
        };
        var reversal = ReportTestData.Adjustment(payment, "Reversal", 100000, identifier: "reversal-1");

        var result = ReportTestData.Reconstructor().Reconstruct(new(payment, "Merchant A", [approval, refund, reversal]),
            ReportingPeriod.ForDate(new(2026, 11, 4)), CancellationToken.None);

        Assert.Equal("PartiallyRefunded", result.Payment.State);
        Assert.Equal(70000, result.Payment.RemainingAmount);
        Assert.True(result.HasSubsequentActivity);
        Assert.Equal("Applied", Assert.Single(result.Events, item => item.Source == "TransactionStatus").ProcessingState);
        Assert.Equal("Ignored", refund.ProcessingState);
        Assert.DoesNotContain(result.Events, item => item.PaymentEventId == reversal.PaymentEventId);
    }

    [Fact]
    public void A_later_duplicate_has_no_financial_effect_and_does_not_set_the_warning()
    {
        var payment = ReportTestData.Payment();
        var approval = ReportTestData.Outcome(payment);
        var duplicate = approval with
        {
            PaymentEventId = Guid.NewGuid(), ReceivedAt = DateTimeOffset.Parse("2026-11-05T08:00:00Z"),
            Payload = ReportTestData.Outcome(payment, "2026-11-05T10:00:00.000").Payload
        };

        var result = ReportTestData.Reconstructor().Reconstruct(new(payment, "Merchant A", [approval, duplicate]),
            ReportingPeriod.ForDate(new(2026, 11, 4)), CancellationToken.None);

        Assert.Equal("Approved", result.Payment.State);
        Assert.Equal(100000, result.Payment.RemainingAmount);
        Assert.False(result.HasSubsequentActivity);
    }

    [Fact]
    public void Delayed_notifications_use_the_transaction_date_instead_of_the_receipt_date()
    {
        var payment = ReportTestData.Payment();
        var approval = ReportTestData.Outcome(payment, receivedAt: "2026-11-06T12:00:00Z");

        var result = ReportTestData.Reconstructor().Reconstruct(new(payment, "Merchant A", [approval]),
            ReportingPeriod.ForDate(new(2026, 11, 4)), CancellationToken.None);

        Assert.Equal("Approved", result.Payment.State);
        Assert.Equal(100000, result.Payment.RemainingAmount);
        Assert.False(result.HasSubsequentActivity);
    }

    [Theory]
    [InlineData("2026-11-04T21:59:59.999Z", "Approved")]
    [InlineData("2026-11-04T22:00:00Z", "Unresolved")]
    public void Missing_transaction_times_fall_back_to_receipt_time_at_the_SAST_boundary(string receivedAt, string state)
    {
        var payment = ReportTestData.Payment();
        var approval = ReportTestData.Outcome(payment, transactionTime: null, receivedAt: receivedAt);

        var result = ReportTestData.Reconstructor().Reconstruct(new(payment, "Merchant A", [approval]),
            ReportingPeriod.ForDate(new(2026, 11, 4)), CancellationToken.None);

        Assert.Equal(state, result.Payment.State);
    }

    [Theory]
    [InlineData("2026-11-04T23:30:00.000+02:00", "Approved")]
    [InlineData("2026-11-04T23:30:00.000Z", "Unresolved")]
    public void Explicit_offsets_are_respected(string transactionTime, string state)
    {
        var payment = ReportTestData.Payment();
        var approval = ReportTestData.Outcome(payment, transactionTime);

        var result = ReportTestData.Reconstructor().Reconstruct(new(payment, "Merchant A", [approval]),
            ReportingPeriod.ForDate(new(2026, 11, 4)), CancellationToken.None);

        Assert.Equal(state, result.Payment.State);
    }

    [Fact]
    public void A_malformed_supplied_timestamp_does_not_silently_change_reporting_dates()
    {
        var payment = ReportTestData.Payment();
        var approval = ReportTestData.Outcome(payment, "invalid-time");

        Assert.Throws<FormatException>(() => ReportTestData.Reconstructor().Reconstruct(new(payment, "Merchant A", [approval]),
            ReportingPeriod.ForDate(new(2026, 11, 4)), CancellationToken.None));
    }
}
