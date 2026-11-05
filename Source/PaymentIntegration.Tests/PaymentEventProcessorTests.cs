using System.Text.Json;
using PaymentIntegration.Payments.Models;
using PaymentIntegration.Payments.Processing;
using Xunit;
using static PaymentIntegration.Tests.PaymentEventTestData;

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

    [Theory]
    [InlineData("Refund", 1300L)]
    [InlineData("Refund", 0L)]
    [InlineData("Refund", -1L)]
    [InlineData("Reversal", 500L)]
    public void Inapplicable_amounts_are_audited_without_reducing_value(string type, long amount)
    {
        var flow = new AdjustmentScenario();

        flow.Receive(type, amount);

        Assert.Equal(1250, flow.Payment!.RemainingAmount);
        var receipt = Assert.Single(flow.Receipts);
        Assert.Equal("Ignored", receipt.ProcessingState);
        Assert.False(string.IsNullOrWhiteSpace(receipt.ProcessingComment));
    }

    [Fact]
    public void Distinct_events_with_the_same_amount_are_separate_refunds()
    {
        var flow = new AdjustmentScenario();

        flow.Receive("Refund", 500, "refund-a");
        flow.Receive("Refund", 500, "refund-b");

        Assert.Equal(250, flow.Payment!.RemainingAmount);
        Assert.All(flow.Receipts, item => Assert.Equal("Applied", item.ProcessingState));
    }

    [Theory]
    [InlineData("AB")]
    [InlineData("BA")]
    public void Multiple_refunds_produce_the_same_value_in_either_delivery_order(string order)
    {
        var flow = new AdjustmentScenario();
        foreach (var item in order)
            flow.Receive("Refund", item == 'A' ? 500 : 200, $"refund-{item}");

        Assert.Equal(550, flow.Payment!.RemainingAmount);
        Assert.False(flow.Payment.HasPendingEvents);
        Assert.Equal(2, flow.Receipts.Count(item => item.ProcessingState == "Applied"));
    }

    [Theory]
    [InlineData("RAB")]
    [InlineData("RBA")]
    [InlineData("ARB")]
    [InlineData("ABR")]
    [InlineData("BRA")]
    [InlineData("BAR")]
    public void A_full_reversal_supersedes_refunds_in_every_delivery_order(string order)
    {
        var flow = new AdjustmentScenario();
        foreach (var item in order)
            flow.Receive(item == 'R' ? "Reversal" : "Refund", item == 'R' ? 1250 : 200, $"event-{item}");

        Assert.Equal(0, flow.Payment!.RemainingAmount);
        Assert.Equal("Reversed", flow.Payment.State);
        Assert.False(flow.Payment.HasPendingEvents);
        var applied = Assert.Single(flow.Receipts, item => item.ProcessingState == "Applied");
        Assert.Equal("Reversal", applied.Payload!.Value.GetProperty("transactionType").GetString());
        Assert.Equal(2, flow.Receipts.Count(item => item.ProcessingState == "Ignored"));
    }

    [Theory]
    [InlineData("AB")]
    [InlineData("BA")]
    public void Refunds_stop_at_the_first_excess_in_switch_order_regardless_of_delivery_order(string order)
    {
        var flow = new AdjustmentScenario();
        foreach (var item in order)
            flow.Receive("Refund", item == 'A' ? 800 : 700, $"refund-{item}");

        Assert.Equal(1250, flow.Payment!.ApprovedAmount);
        Assert.Equal(450, flow.Payment.RemainingAmount);
        Assert.Equal("PartiallyRefunded", flow.Payment.State);
        Assert.False(flow.Payment.HasPendingEvents);
        Assert.True(PaymentResult.FromPayment(flow.Payment).IsFullyReconciled);
        Assert.Equal("Applied", flow.Receipts.Single(item => item.Payload!.Value.GetProperty("transactionUUID").GetString() == "refund-A").ProcessingState);
        Assert.Equal("Ignored", flow.Receipts.Single(item => item.Payload!.Value.GetProperty("transactionUUID").GetString() == "refund-B").ProcessingState);

        flow.Receive("Reversal", 1250, "reversal");

        Assert.Equal(0, flow.Payment.RemainingAmount);
        Assert.Equal("Reversed", flow.Payment.State);
        Assert.False(flow.Payment.HasPendingEvents);
        Assert.Equal(2, flow.Receipts.Count(item => item.ProcessingState == "Ignored"));
    }

    [Fact]
    public void Excessive_combined_refunds_do_not_overflow_the_minor_unit_integer()
    {
        var flow = new AdjustmentScenario();
        flow.Payment = flow.Payment! with
        {
            RequestedAmount = long.MaxValue, ApprovedAmount = long.MaxValue, RemainingAmount = long.MaxValue
        };

        flow.Receive("Refund", 9223372036854775806, "refund-a");
        flow.Receive("Refund", 2, "refund-b");

        Assert.Equal(1, flow.Payment.RemainingAmount);
        Assert.Equal("PartiallyRefunded", flow.Payment.State);
        Assert.Single(flow.Receipts, item => item.ProcessingState == "Applied");
        Assert.Single(flow.Receipts, item => item.ProcessingState == "Ignored");
    }

    [Theory]
    [InlineData("ABC")]
    [InlineData("ACB")]
    [InlineData("BAC")]
    [InlineData("BCA")]
    [InlineData("CAB")]
    [InlineData("CBA")]
    public void Refunds_after_the_first_excess_stay_ignored_even_when_a_later_smaller_refund_would_fit(string order)
    {
        var flow = new AdjustmentScenario();
        foreach (var item in order)
        {
            var fields = Fields("Refund", item == 'A' ? 800 : item == 'B' ? 700 : 100,
                item == 'A' ? "refund-z" : item == 'B' ? "refund-a" : "refund-m");
            fields["transactionDateTime"] = item == 'A' ? "2026-03-04T08:02:11.417"
                : item == 'B' ? "2026-03-05T08:02:11.417" : "2026-03-06T08:02:11.417";
            flow.Receive(JsonSerializer.SerializeToElement(fields));
        }

        Assert.Equal(450, flow.Payment!.RemainingAmount);
        Assert.Equal("PartiallyRefunded", flow.Payment.State);
        var applied = Assert.Single(flow.Receipts, item => item.ProcessingState == "Applied");
        Assert.Equal("refund-z", applied.Payload!.Value.GetProperty("transactionUUID").GetString());
        Assert.Equal(2, flow.Receipts.Count(item => item.ProcessingState == "Ignored"));

        var replay = Fields("Refund", 100, "refund-m");
        replay["transactionDateTime"] = "2026-03-06T08:02:11.417";
        flow.Receive(JsonSerializer.SerializeToElement(replay));

        Assert.Equal(450, flow.Payment.RemainingAmount);
        Assert.Single(flow.Receipts, item => item.ProcessingState == "Applied");
        Assert.Equal(3, flow.Receipts.Count(item => item.ProcessingState == "Ignored"));
    }

    [Theory]
    [InlineData("AB")]
    [InlineData("BA")]
    public void An_individually_excessive_refund_also_stops_later_refunds(string order)
    {
        var flow = new AdjustmentScenario();
        foreach (var item in order)
            flow.Receive("Refund", item == 'A' ? 1300 : 100, $"refund-{item}");

        Assert.Equal("Approved", flow.Payment!.State);
        Assert.Equal(1250, flow.Payment.RemainingAmount);
        Assert.All(flow.Receipts, item => Assert.Equal("Ignored", item.ProcessingState));
    }

    [Theory]
    [InlineData("Refund", 500L, "PartiallyRefunded", 750L)]
    [InlineData("Refund", 1250L, "Refunded", 0L)]
    [InlineData("Reversal", 1250L, "Reversed", 0L)]
    public void An_exact_replay_preserves_the_applied_receipt_and_does_not_apply_twice(
        string type, long amount, string state, long remaining)
    {
        var flow = new AdjustmentScenario();
        flow.Receive(type, amount);
        var applied = Assert.Single(flow.Receipts);

        flow.Receive(type, amount);

        Assert.Equal(state, flow.Payment!.State);
        Assert.Equal(remaining, flow.Payment.RemainingAmount);
        Assert.False(flow.Payment.HasPendingEvents);
        Assert.Equal(applied, flow.Receipts[0]);
        Assert.Equal("Ignored", flow.Receipts[1].ProcessingState);
        Assert.Single(flow.Receipts, item => item.ProcessingState == "Applied");
    }

    [Theory]
    [InlineData("Refund", 500L, "PartiallyRefunded", 750L)]
    [InlineData("Reversal", 1250L, "Reversed", 0L)]
    public void A_replay_after_a_UUID_conflict_cannot_apply_another_adjustment(
        string type, long amount, string state, long remaining)
    {
        var flow = new AdjustmentScenario();
        flow.Receive(type, amount);
        var applied = Assert.Single(flow.Receipts);
        flow.Receive(type, 400);

        flow.Receive(type, amount);

        Assert.Equal(state, flow.Payment!.State);
        Assert.Equal(remaining, flow.Payment.RemainingAmount);
        Assert.Equal(3, flow.Receipts.Count);
        Assert.Equal(applied, flow.Receipts[0]);
        AssertPendingConflict(flow.Receipts[1]);
        Assert.Single(flow.Receipts, item => item.ProcessingState == "Applied");
    }

    [Fact]
    public void A_conflict_with_an_ignored_over_limit_refund_preserves_its_barrier_to_later_refunds()
    {
        var flow = new AdjustmentScenario();
        flow.Receive("Refund", 1300, "refund-a");
        var ignored = Assert.Single(flow.Receipts);
        Assert.Equal("Ignored", ignored.ProcessingState);
        flow.Receive("Refund", 1400, "refund-a");

        flow.Receive("Refund", 100, "refund-b");

        Assert.Equal("Approved", flow.Payment!.State);
        Assert.Equal(1250, flow.Payment.ApprovedAmount);
        Assert.Equal(1250, flow.Payment.RemainingAmount);
        Assert.True(flow.Payment.HasPendingEvents);
        Assert.Equal(ignored, flow.Receipts[0]);
        AssertPendingConflict(flow.Receipts[1]);
        Assert.Equal("Ignored", flow.Receipts[2].ProcessingState);
        Assert.DoesNotContain(flow.Receipts, item => item.ProcessingState == "Applied");
    }

    [Theory]
    [InlineData("early", "late")]
    [InlineData("late", "early")]
    public void Distinct_full_reversals_have_one_representative_independent_of_delivery_order(string first, string second)
    {
        var flow = new AdjustmentScenario();
        foreach (var identifier in new[] { first, second })
        {
            var fields = Fields("Reversal", 1250, identifier);
            fields["transactionDateTime"] = identifier == "early" ? "2026-03-04T08:02:11.417" : "2026-03-05T08:02:11.417";
            flow.Receive(JsonSerializer.SerializeToElement(fields));
        }

        Assert.Equal(0, flow.Payment!.RemainingAmount);
        var applied = Assert.Single(flow.Receipts, item => item.ProcessingState == "Applied");
        Assert.Equal("early", applied.Payload!.Value.GetProperty("transactionUUID").GetString());
        Assert.Single(flow.Receipts, item => item.ProcessingState == "Ignored");
    }

    [Theory]
    [InlineData("00", false, "Pending")]
    [InlineData("05", true, "Pending")]
    [InlineData("05", false, "Ignored")]
    public void Switch_outcome_indicators_control_whether_an_adjustment_can_apply(
        string code, bool approved, string expectedState)
    {
        var flow = new AdjustmentScenario();
        var fields = Fields("Refund", 500);
        fields["txResultCode"] = code;
        fields["isApproved"] = approved;

        flow.Receive(JsonSerializer.SerializeToElement(fields));

        Assert.Equal(expectedState, Assert.Single(flow.Receipts).ProcessingState);
        if (expectedState == "Pending")
            Assert.Null(flow.Payment!.RemainingAmount);
        else
            Assert.Equal(1250, flow.Payment!.RemainingAmount);
    }

    [Theory]
    [InlineData("Declined")]
    [InlineData("Failed")]
    public void A_payment_without_approval_cannot_be_adjusted(string state)
    {
        var flow = new AdjustmentScenario();
        flow.Payment = flow.Payment! with { State = state, ApprovedAmount = null, RemainingAmount = null };

        flow.Receive("Refund", 500);

        Assert.Equal(state, flow.Payment.State);
        Assert.Null(flow.Payment.RemainingAmount);
        Assert.Equal("Ignored", Assert.Single(flow.Receipts).ProcessingState);
    }

    [Fact]
    public void Currency_mismatch_cannot_change_the_remaining_value()
    {
        var flow = new AdjustmentScenario();
        flow.Payment = flow.Payment! with { RemainingAmount = 500 };
        var fields = Fields("Refund", 200);
        fields["currencyCode"] = "USD";

        flow.Receive(JsonSerializer.SerializeToElement(fields));

        Assert.Equal(500, flow.Payment.RemainingAmount);
        Assert.Equal("Ignored", Assert.Single(flow.Receipts).ProcessingState);
    }

    [Fact]
    public void Omitted_currency_uses_Acmes_ZAR_default()
    {
        var flow = new AdjustmentScenario();
        var fields = Fields("Refund", 500);
        fields.Remove("currencyCode");

        flow.Receive(JsonSerializer.SerializeToElement(fields));

        Assert.Equal(750, flow.Payment!.RemainingAmount);
    }

    [Theory]
    [InlineData("Refunded", "missing")]
    [InlineData("Refunded", "null")]
    [InlineData("Refunded", "negative")]
    [InlineData("Refunded", "string")]
    [InlineData("Refunded", "fractional")]
    [InlineData("Refunded", "overflow")]
    [InlineData("Reversed", "missing")]
    [InlineData("Reversed", "null")]
    [InlineData("Reversed", "negative")]
    [InlineData("Reversed", "string")]
    [InlineData("Reversed", "fractional")]
    [InlineData("Reversed", "overflow")]
    public void Matching_webhooks_cannot_replace_a_missing_or_invalid_RTTQ_AuthAmount(string state, string invalid)
    {
        var payment = NewPayment() with { PaymentId = PaymentId };
        var fields = QueryFields(state);
        if (invalid == "missing")
            fields.Remove("AuthAmount");
        else
            fields["AuthAmount"] = invalid switch
            {
                "negative" => -1L,
                "string" => "1250",
                "fractional" => 1250.5m,
                "overflow" => 9223372036854775808m,
                _ => null
            };
        var timeout = Timeout(payment);
        var webhook = StatusReceipt(payment, Fields(state == "Reversed" ? "Reversal" : "Refund", 1250));
        var query = QueryReceipt(payment, fields);

        var result = processor.Process(payment, [timeout, webhook, query], [webhook], query.PaymentEventId, false, ProcessedAt);

        Assert.Equal("Unresolved", result.Payment.State);
        Assert.Null(result.Payment.ApprovedAmount);
        Assert.Null(result.Payment.RemainingAmount);
        Assert.True(result.Payment.HasPendingEvents);
        Assert.False(PaymentResult.FromPayment(result.Payment).IsFullyReconciled);
        Assert.Equal(3, result.Events.Count);
        Assert.All(result.Events, item => Assert.Equal("Pending", item.ProcessingState));
    }

    [Theory]
    [InlineData("Refunded", "Q")]
    [InlineData("Refunded", "AQ")]
    [InlineData("Refunded", "QA")]
    [InlineData("Refunded", "AAQ")]
    [InlineData("Refunded", "QAA")]
    [InlineData("Reversed", "Q")]
    public void Adjusted_queries_with_incomplete_history_do_not_infer_a_balance(string state, string order)
    {
        var payment = NewPayment() with { PaymentId = PaymentId };
        var query = QueryReceipt(payment, QueryFields(state));
        var events = new List<PaymentEvent> { Timeout(payment) };
        var history = new List<PaymentEvent>();
        foreach (var item in order)
        {
            if (item == 'Q')
                events.Add(query);
            else
            {
                var receipt = StatusReceipt(payment, Fields("Refund", 750, "refund-a"));
                events.Add(receipt);
                history.Add(receipt);
            }
        }

        var result = processor.Process(payment, events, history, events[^1].PaymentEventId, false, ProcessedAt);

        Assert.Equal("Unresolved", result.Payment.State);
        Assert.Null(result.Payment.RemainingAmount);
        Assert.True(result.Payment.HasPendingEvents);
        Assert.False(PaymentResult.FromPayment(result.Payment).IsFullyReconciled);
        Assert.DoesNotContain(result.Events, item => item.ProcessingState == "Applied");
        var updatedQuery = Assert.Single(result.Events, item => item.Source == "TransactionQuery");
        Assert.Equal("Pending", updatedQuery.ProcessingState);
        Assert.False(string.IsNullOrWhiteSpace(updatedQuery.ProcessingComment));
        if (order.Count(item => item == 'A') < 2)
            Assert.All(result.Events, item => Assert.Equal("Pending", item.ProcessingState));
    }

    [Theory]
    [InlineData("declined")]
    [InlineData("currency")]
    [InlineData("outcome")]
    [InlineData("zero")]
    public void Inapplicable_refund_receipts_do_not_satisfy_a_Refunded_RTTQ(string invalid)
    {
        var payment = NewPayment() with { PaymentId = PaymentId };
        var fields = Fields("Refund", 1250);
        if (invalid == "declined")
        {
            fields["txResultCode"] = "05";
            fields["isApproved"] = false;
        }
        else if (invalid == "currency")
            fields["currencyCode"] = "USD";
        else if (invalid == "outcome")
            fields["isApproved"] = false;
        else
            fields["transactionAmount"] = 0;
        var timeout = Timeout(payment);
        var query = QueryReceipt(payment, QueryFields("Refunded"));
        var webhook = StatusReceipt(payment, fields);

        var result = processor.Process(payment, [timeout, query, webhook], [webhook], webhook.PaymentEventId, false, ProcessedAt);

        Assert.Equal("Unresolved", result.Payment.State);
        Assert.Null(result.Payment.RemainingAmount);
        Assert.False(PaymentResult.FromPayment(result.Payment).IsFullyReconciled);
        Assert.Equal(3, result.Events.Count);
        Assert.DoesNotContain(result.Events, item => item.ProcessingState == "Applied");
        Assert.Equal("Pending", result.Events.Single(item => item.Source == "TransactionQuery").ProcessingState);
    }

    private static PaymentEvent Timeout(Payment payment) =>
        new(Guid.NewGuid(), payment.PaymentId, "PaymentResponse", null, null, ReceivedAt);

    private static PaymentEvent QueryReceipt(Payment payment, Dictionary<string, object?> fields) =>
        new(Guid.NewGuid(), payment.PaymentId, "TransactionQuery",
            JsonSerializer.SerializeToElement(new { PaymentResponse = fields }), 200, ReceivedAt);

    private static PaymentEvent StatusReceipt(Payment payment, Dictionary<string, object?> fields) =>
        new(Guid.NewGuid(), payment.PaymentId, "TransactionStatus", JsonSerializer.SerializeToElement(fields), null, ReceivedAt);

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

    private static void AssertPendingConflict(PaymentEvent receipt)
    {
        Assert.Equal("Pending", receipt.ProcessingState);
        Assert.NotNull(receipt.ProcessingComment);
        Assert.Contains("conflict", receipt.ProcessingComment, StringComparison.OrdinalIgnoreCase);
        Assert.Null(receipt.ProcessedAt);
    }

    private sealed class AdjustmentScenario
    {
        private static readonly DateTimeOffset ReceivedAt = new(2026, 3, 4, 6, 3, 0, TimeSpan.Zero);
        private readonly PaymentEventProcessor processor;
        private int deliveries;

        public AdjustmentScenario()
        {
            var status = new TransactionStatusProcessor();
            processor = new(new PaymentResponseProcessor(status), status);
        }

        public Payment Payment { get; set; } = new(PaymentId, "app-payment", "customer", Guid.NewGuid(),
            "shop", Rrn, "ZAR", 2000, "Approved", ReceivedAt, 1250, 1250);
        public IReadOnlyList<PaymentEvent> Receipts { get; private set; } = [];

        public void Receive(string type, long amount, string identifier = "event-1") =>
            Receive(JsonSerializer.SerializeToElement(Fields(type, amount, identifier)));

        public void Receive(JsonElement payload)
        {
            var receipt = new PaymentEvent(Guid.NewGuid(), Payment.PaymentId, "TransactionStatus", payload.Clone(),
                null, ReceivedAt.AddTicks(++deliveries));
            var result = processor.Process(Payment, [receipt], [.. Receipts, receipt], receipt.PaymentEventId, false, ReceivedAt.AddSeconds(1));
            Payment = result.Payment;
            Receipts = result.Events;
        }
    }
}
