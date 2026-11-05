using System.Text.Json;
using PaymentIntegration.Payments;
using Xunit;

namespace PaymentIntegration.Tests;

public sealed class PaymentEventProcessorTests
{
    private static readonly DateTimeOffset ReceivedAt = new(2026, 11, 3, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ProcessedAt = ReceivedAt.AddMinutes(1);
    private readonly PaymentEventProcessor processor;

    public PaymentEventProcessorTests()
    {
        var statusProcessor = new TransactionStatusProcessor();
        processor = new(new PaymentResponseProcessor(statusProcessor), statusProcessor);
    }

    [Fact]
    public void Approval_reconsiders_an_earlier_timeout_without_changing_the_input_records()
    {
        var payment = NewPayment();
        var timeout = new PaymentEvent(Guid.NewGuid(), payment.PaymentId, "PaymentResponse", null, null, ReceivedAt);
        var approval = BankResponse(payment);
        PaymentEvent[] events = [timeout, approval];

        var result = processor.Process(payment, events, [], approval.PaymentEventId, false, ProcessedAt);

        Assert.Equal("Approved", result.Payment.State);
        Assert.Equal(1250, result.Payment.ApprovedAmount);
        Assert.Equal(1250, result.Payment.RemainingAmount);
        Assert.False(result.Payment.HasPendingEvents);
        Assert.Equal("Ignored", result.Events[0].ProcessingState);
        Assert.Equal("Applied", result.Events[1].ProcessingState);
        Assert.All(result.Events, item => Assert.Equal(ProcessedAt, item.ProcessedAt));

        // Historical reconstruction must be able to reuse the same stored inputs.
        Assert.Equal("Unresolved", payment.State);
        Assert.Null(payment.ApprovedAmount);
        Assert.Null(payment.RemainingAmount);
        Assert.All(events, item =>
        {
            Assert.Equal("Pending", item.ProcessingState);
            Assert.Null(item.ProcessedAt);
        });
    }

    [Fact]
    public void Duplicate_approval_is_ignored_after_the_first_approval_is_applied()
    {
        var payment = NewPayment();
        var approval = BankResponse(payment);
        var duplicate = BankResponse(payment);

        var result = processor.Process(payment, [approval, duplicate], [], duplicate.PaymentEventId, false, ProcessedAt);

        Assert.Equal("Approved", result.Payment.State);
        Assert.Equal(1250, result.Payment.RemainingAmount);
        Assert.False(result.Payment.HasPendingEvents);
        Assert.Equal("Applied", result.Events[0].ProcessingState);
        Assert.Equal("Ignored", result.Events[1].ProcessingState);
        Assert.False(string.IsNullOrWhiteSpace(result.Events[1].ProcessingComment));
    }

    [Fact]
    public void An_unknown_outcome_leaves_the_payment_unresolved_with_a_pending_event()
    {
        var payment = NewPayment();
        var timeout = new PaymentEvent(Guid.NewGuid(), payment.PaymentId, "PaymentResponse", null, null, ReceivedAt);

        var result = processor.Process(payment, [timeout], [], timeout.PaymentEventId, false, ProcessedAt);

        Assert.Equal("Unresolved", result.Payment.State);
        Assert.Null(result.Payment.ApprovedAmount);
        Assert.Null(result.Payment.RemainingAmount);
        Assert.True(result.Payment.HasPendingEvents);
        var receipt = Assert.Single(result.Events);
        Assert.Equal("Pending", receipt.ProcessingState);
        Assert.Null(receipt.ProcessedAt);
    }

    [Theory]
    [InlineData(true, "Unresolved", "Pending")]
    [InlineData(false, "Failed", "Applied")]
    public void Current_E29_uses_the_callers_retry_decision(
        bool retrying, string expectedPaymentState, string expectedEventState)
    {
        var payment = NewPayment();
        var receipt = new PaymentEvent(Guid.NewGuid(), payment.PaymentId, "PaymentResponse",
            JsonSerializer.SerializeToElement(new
            {
                PaymentResponse = new
                {
                    ResponseDetail = new { Source = "Application", Code = "E29" }
                }
            }), 429, ReceivedAt);

        var result = processor.Process(payment, [receipt], [], receipt.PaymentEventId, retrying, ProcessedAt);

        Assert.Equal(expectedPaymentState, result.Payment.State);
        Assert.Equal(expectedEventState, Assert.Single(result.Events).ProcessingState);
        Assert.Equal(retrying, result.Payment.HasPendingEvents);
    }

    [Fact]
    public void Already_ignored_receipts_cannot_establish_a_payment_outcome()
    {
        var payment = NewPayment();
        var ignored = BankResponse(payment) with
        {
            ProcessingState = "Ignored", ProcessingComment = "Previously rejected receipt.", ProcessedAt = ProcessedAt
        };

        var result = processor.Process(payment, [ignored], [], ignored.PaymentEventId, false, ProcessedAt);

        Assert.Equal("Unresolved", result.Payment.State);
        Assert.Null(result.Payment.ApprovedAmount);
        Assert.Null(result.Payment.RemainingAmount);
        Assert.False(result.Payment.HasPendingEvents);
    }

    [Fact]
    public void Pending_refund_is_applied_after_approval_using_the_complete_history()
    {
        var payment = NewPayment();
        var refund = Refund(payment);
        var approval = BankResponse(payment);

        var result = processor.Process(payment, [refund, approval], [refund], approval.PaymentEventId, false, ProcessedAt);

        Assert.Equal("PartiallyRefunded", result.Payment.State);
        Assert.Equal(1250, result.Payment.ApprovedAmount);
        Assert.Equal(750, result.Payment.RemainingAmount);
        Assert.False(result.Payment.HasPendingEvents);
        Assert.Equal(2, result.Events.Count);
        Assert.All(result.Events, item => Assert.Equal("Applied", item.ProcessingState));
        Assert.Equal("Pending", refund.ProcessingState);
        Assert.Null(refund.ProcessedAt);
    }

    [Fact]
    public void Replayed_refund_does_not_reduce_the_already_adjusted_balance_again()
    {
        var payment = NewPayment() with
        {
            State = "PartiallyRefunded", ApprovedAmount = 1250, RemainingAmount = 750
        };
        var applied = Refund(payment) with { ProcessingState = "Applied", ProcessedAt = ProcessedAt };
        var duplicate = applied with
        {
            PaymentEventId = Guid.NewGuid(), ProcessingState = "Pending", ProcessedAt = null,
            ReceivedAt = ReceivedAt.AddSeconds(2)
        };

        var result = processor.Process(payment, [duplicate], [applied, duplicate], duplicate.PaymentEventId, false, ProcessedAt);

        Assert.Equal("PartiallyRefunded", result.Payment.State);
        Assert.Equal(750, result.Payment.RemainingAmount);
        Assert.False(result.Payment.HasPendingEvents);
        Assert.Single(result.Events, item => item.ProcessingState == "Applied");
        Assert.Single(result.Events, item => item.ProcessingState == "Ignored");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Events_from_another_payment_are_rejected(bool inAdjustmentHistory)
    {
        var payment = NewPayment();
        var receipt = BankResponse(payment) with { PaymentId = Guid.NewGuid() };

        var exception = Assert.Throws<ArgumentException>(() =>
            processor.Process(payment, inAdjustmentHistory ? [] : [receipt], inAdjustmentHistory ? [receipt] : [],
                receipt.PaymentEventId, false, ProcessedAt));

        Assert.Equal(inAdjustmentHistory ? "transactionStatusHistory" : "events", exception.ParamName);
    }

    private static Payment NewPayment() => new(Guid.NewGuid(), "app-payment", "customer", Guid.NewGuid(),
        "shop", "ORDER1234567", "ZAR", 2000, "Unresolved", ReceivedAt.AddMinutes(-1));

    private static PaymentEvent BankResponse(Payment payment) => new(Guid.NewGuid(), payment.PaymentId,
        "PaymentResponse", JsonSerializer.SerializeToElement(new
        {
            PaymentResponse = new
            {
                TransactionID = payment.PaymentId.ToString(),
                payment.OrderNumber,
                ResponseDetail = new { Source = "Acquirer", Code = "00" },
                AuthAmount = 1250
            }
        }), 200, ReceivedAt.AddSeconds(1));

    private static PaymentEvent Refund(Payment payment) => new(Guid.NewGuid(), payment.PaymentId,
        "TransactionStatus", JsonSerializer.SerializeToElement(new
        {
            transactionUUID = "refund-1",
            transactionType = "Refund",
            transactionDateTime = "2026-11-03T12:00:00.000",
            rrn = payment.OrderNumber,
            transactionAmount = 500,
            currencyCode = "ZAR",
            txResultCode = "00",
            isApproved = true
        }), null, ReceivedAt);
}
