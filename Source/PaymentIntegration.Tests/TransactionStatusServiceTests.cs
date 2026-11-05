using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PaymentIntegration.Payments;
using Xunit;

namespace PaymentIntegration.Tests;

public sealed class TransactionStatusServiceTests
{
    private static readonly Guid PaymentId = Guid.Parse("2e0e1b85-bf91-45a9-b038-282e00036919");
    private const string Rrn = "ORDER1234567";

    [Theory]
    [InlineData("Refund", 500L, 750L, "PartiallyRefunded")]
    [InlineData("Refund", 1250L, 0L, "Refunded")]
    [InlineData("Reversal", 1250L, 0L, "Reversed")]
    public async Task Confirmed_adjustments_use_the_locked_authorisation_and_expose_the_lifecycle_state(
        string type, long amount, long remaining, string state)
    {
        var flow = new Flow();

        await flow.Receive(type, amount);

        Assert.Equal(state, flow.Payment!.State);
        Assert.Equal(2000, flow.Payment.RequestedAmount);
        Assert.Equal(1250, flow.Payment.ApprovedAmount);
        Assert.Equal(remaining, flow.Payment.RemainingAmount);
        Assert.True(PaymentResult.FromPayment(flow.Payment).IsFullyReconciled);
        var receipt = Assert.Single(flow.Receipts);
        Assert.Equal("Applied", receipt.ProcessingState);
        Assert.NotNull(receipt.ProcessedAt);
        Assert.True(flow.Calls.IndexOf("lock") < flow.Calls.IndexOf("insert"));
        Assert.True(flow.Calls.IndexOf("insert") < flow.Calls.IndexOf("pending"));
        Assert.True(flow.Calls.IndexOf("pending") < flow.Calls.IndexOf("history"));
        Assert.True(flow.Calls.IndexOf("save_event") < flow.Calls.IndexOf("commit"));
        flow.Repository.Verify(value => value.BeginProcessingByRrnAsync(Rrn, It.IsAny<Guid>(),
            It.IsAny<CancellationToken>()), Times.Once);
        flow.Repository.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Refund", 1300L)]
    [InlineData("Refund", 0L)]
    [InlineData("Refund", -1L)]
    [InlineData("Reversal", 500L)]
    public async Task Inapplicable_amounts_are_audited_without_reducing_value(string type, long amount)
    {
        var flow = new Flow();

        await flow.Receive(type, amount);

        Assert.Equal(1250, flow.Payment!.RemainingAmount);
        var receipt = Assert.Single(flow.Receipts);
        Assert.Equal("Ignored", receipt.ProcessingState);
        Assert.False(string.IsNullOrWhiteSpace(receipt.ProcessingComment));
    }

    [Fact]
    public async Task A_replay_preserves_both_receipts_and_unknown_fields_but_reduces_value_once()
    {
        var flow = new Flow();
        await flow.Receive("Refund", 500);
        var replay = Fields("Refund", 500);
        replay["FutureSwitchField"] = "retained";

        await flow.Service.ReceiveAsync(JsonSerializer.SerializeToElement(replay));

        Assert.Equal(750, flow.Payment!.RemainingAmount);
        Assert.Equal(2, flow.Receipts.Count);
        Assert.Single(flow.Receipts, item => item.ProcessingState == "Applied");
        Assert.Single(flow.Receipts, item => item.ProcessingState == "Ignored");
        Assert.Equal(2, flow.Receipts.Select(item => item.PaymentEventId).Distinct().Count());
        Assert.Equal("retained", flow.Receipts[1].Payload!.Value.GetProperty("FutureSwitchField").GetString());
    }

    [Fact]
    public async Task Distinct_events_with_the_same_amount_are_separate_refunds()
    {
        var flow = new Flow();

        await flow.Receive("Refund", 500, "refund-a");
        await flow.Receive("Refund", 500, "refund-b");

        Assert.Equal(250, flow.Payment!.RemainingAmount);
        Assert.All(flow.Receipts, item => Assert.Equal("Applied", item.ProcessingState));
    }

    [Theory]
    [InlineData("AB")]
    [InlineData("BA")]
    public async Task Multiple_refunds_produce_the_same_value_in_either_delivery_order(string order)
    {
        var flow = new Flow();
        foreach (var item in order)
            await flow.Receive("Refund", item == 'A' ? 500 : 200, $"refund-{item}");

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
    public async Task A_full_reversal_supersedes_refunds_in_every_delivery_order(string order)
    {
        var flow = new Flow();
        foreach (var item in order)
            await flow.Receive(item == 'R' ? "Reversal" : "Refund", item == 'R' ? 1250 : 200, $"event-{item}");

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
    public async Task Refunds_stop_at_the_first_excess_in_switch_order_regardless_of_delivery_order(string order)
    {
        var flow = new Flow();
        foreach (var item in order)
            await flow.Receive("Refund", item == 'A' ? 800 : 700, $"refund-{item}");

        Assert.Equal(1250, flow.Payment!.ApprovedAmount);
        Assert.Equal(450, flow.Payment.RemainingAmount);
        Assert.Equal("PartiallyRefunded", flow.Payment.State);
        Assert.False(flow.Payment.HasPendingEvents);
        Assert.True(PaymentResult.FromPayment(flow.Payment).IsFullyReconciled);
        Assert.Equal("Applied", flow.Receipts.Single(item => item.Payload!.Value.GetProperty("transactionUUID").GetString() == "refund-A").ProcessingState);
        Assert.Equal("Ignored", flow.Receipts.Single(item => item.Payload!.Value.GetProperty("transactionUUID").GetString() == "refund-B").ProcessingState);

        await flow.Receive("Reversal", 1250, "reversal");

        Assert.Equal(0, flow.Payment.RemainingAmount);
        Assert.Equal("Reversed", flow.Payment.State);
        Assert.False(flow.Payment.HasPendingEvents);
        Assert.Equal(2, flow.Receipts.Count(item => item.ProcessingState == "Ignored"));
    }

    [Fact]
    public async Task Excessive_combined_refunds_do_not_overflow_the_minor_unit_integer()
    {
        var flow = new Flow();
        flow.Payment = flow.Payment! with
        {
            RequestedAmount = long.MaxValue, ApprovedAmount = long.MaxValue, RemainingAmount = long.MaxValue
        };

        await flow.Receive("Refund", 9223372036854775806, "refund-a");
        await flow.Receive("Refund", 2, "refund-b");

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
    public async Task Refunds_after_the_first_excess_stay_ignored_even_when_a_later_smaller_refund_would_fit(string order)
    {
        var flow = new Flow();
        foreach (var item in order)
        {
            var fields = Fields("Refund", item == 'A' ? 800 : item == 'B' ? 700 : 100,
                item == 'A' ? "refund-z" : item == 'B' ? "refund-a" : "refund-m");
            fields["transactionDateTime"] = item == 'A' ? "2026-03-04T08:02:11.417"
                : item == 'B' ? "2026-03-05T08:02:11.417" : "2026-03-06T08:02:11.417";
            await flow.Service.ReceiveAsync(JsonSerializer.SerializeToElement(fields));
        }

        Assert.Equal(450, flow.Payment!.RemainingAmount);
        Assert.Equal("PartiallyRefunded", flow.Payment.State);
        var applied = Assert.Single(flow.Receipts, item => item.ProcessingState == "Applied");
        Assert.Equal("refund-z", applied.Payload!.Value.GetProperty("transactionUUID").GetString());
        Assert.Equal(2, flow.Receipts.Count(item => item.ProcessingState == "Ignored"));

        var replay = Fields("Refund", 100, "refund-m");
        replay["transactionDateTime"] = "2026-03-06T08:02:11.417";
        await flow.Service.ReceiveAsync(JsonSerializer.SerializeToElement(replay));

        Assert.Equal(450, flow.Payment.RemainingAmount);
        Assert.Single(flow.Receipts, item => item.ProcessingState == "Applied");
        Assert.Equal(3, flow.Receipts.Count(item => item.ProcessingState == "Ignored"));
    }

    [Theory]
    [InlineData("AB")]
    [InlineData("BA")]
    public async Task An_individually_excessive_refund_also_stops_later_refunds(string order)
    {
        var flow = new Flow();
        foreach (var item in order)
            await flow.Receive("Refund", item == 'A' ? 1300 : 100, $"refund-{item}");

        Assert.Equal("Approved", flow.Payment!.State);
        Assert.Equal(1250, flow.Payment.RemainingAmount);
        Assert.All(flow.Receipts, item => Assert.Equal("Ignored", item.ProcessingState));
    }

    [Theory]
    [InlineData(500L, 400L, "PartiallyRefunded", 750L)]
    [InlineData(400L, 500L, "PartiallyRefunded", 850L)]
    [InlineData(1250L, 500L, "Refunded", 0L)]
    public async Task Conflicting_UUID_receipts_preserve_an_already_applied_refund(
        long first, long second, string state, long remaining)
    {
        var flow = new Flow();
        await flow.Receive("Refund", first);
        var applied = Assert.Single(flow.Receipts);

        await flow.Receive("Refund", second);

        Assert.Equal(state, flow.Payment!.State);
        Assert.Equal(1250, flow.Payment.ApprovedAmount);
        Assert.Equal(remaining, flow.Payment.RemainingAmount);
        Assert.True(flow.Payment.HasPendingEvents);
        Assert.False(PaymentResult.FromPayment(flow.Payment).IsFullyReconciled);
        Assert.Equal(applied, flow.Receipts[0]);
        AssertPendingConflict(flow.Receipts[1]);
        Assert.Equal(second, flow.Receipts[1].Payload!.Value.GetProperty("transactionAmount").GetInt64());
        Assert.NotEqual(applied.PaymentEventId, flow.Receipts[1].PaymentEventId);
        flow.Session.Verify(value => value.CommitAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData("transactionAmount")]
    [InlineData("transactionType")]
    [InlineData("transactionDateTime")]
    [InlineData("txResultCode")]
    [InlineData("isApproved")]
    public async Task Conflicting_UUID_details_preserve_a_full_reversal_and_ignored_audit_records(string changed)
    {
        var flow = new Flow();
        await flow.Receive("Reversal", 1250, "reversal");
        await flow.Receive("Refund", 500, "ignored-refund");
        var originalReceipts = flow.Receipts.ToArray();
        var fields = Fields("Reversal", 1250, "reversal");
        fields[changed] = changed switch
        {
            "transactionAmount" => 500L,
            "transactionType" => "Refund",
            "transactionDateTime" => "2026-03-05T08:02:11.417",
            "txResultCode" => "05",
            _ => false
        };
        fields["FutureSwitchField"] = "retain for investigation";
        var payload = JsonSerializer.SerializeToElement(fields);

        await flow.Service.ReceiveAsync(payload);

        Assert.Equal("Reversed", flow.Payment!.State);
        Assert.Equal(1250, flow.Payment.ApprovedAmount);
        Assert.Equal(0, flow.Payment.RemainingAmount);
        Assert.True(flow.Payment.HasPendingEvents);
        Assert.Equal(originalReceipts, flow.Receipts.Take(2));
        var conflict = flow.Receipts[2];
        AssertPendingConflict(conflict);
        Assert.Equal(PaymentId, conflict.PaymentId);
        Assert.Equal(payload.GetRawText(), conflict.Payload!.Value.GetRawText());
        Assert.Equal(3, flow.Receipts.Select(item => item.PaymentEventId).Distinct().Count());
        flow.Session.Verify(value => value.CommitAsync(It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task A_conflict_with_an_ignored_UUID_preserves_the_previous_terminal_receipts()
    {
        var flow = new Flow();
        await flow.Receive("Reversal", 1250, "reversal");
        await flow.Receive("Refund", 500, "ignored-refund");
        var originalReceipts = flow.Receipts.ToArray();

        await flow.Receive("Refund", 400, "ignored-refund");

        Assert.Equal("Reversed", flow.Payment!.State);
        Assert.Equal(0, flow.Payment.RemainingAmount);
        Assert.Equal(originalReceipts, flow.Receipts.Take(2));
        AssertPendingConflict(flow.Receipts[2]);
    }

    [Fact]
    public async Task A_UUID_conflict_logs_a_warning_with_the_payment_and_conflicting_receipt_identifiers()
    {
        var flow = new Flow();
        await flow.Receive("Reversal", 1250);

        await flow.Receive("Reversal", 500);

        AssertConflictWarning(flow, flow.Receipts[1]);
    }

    [Theory]
    [InlineData("Refund", 500L, "PartiallyRefunded", 750L)]
    [InlineData("Refund", 1250L, "Refunded", 0L)]
    [InlineData("Reversal", 1250L, "Reversed", 0L)]
    public async Task An_exact_replay_preserves_the_applied_receipt_and_does_not_apply_twice(
        string type, long amount, string state, long remaining)
    {
        var flow = new Flow();
        await flow.Receive(type, amount);
        var applied = Assert.Single(flow.Receipts);

        await flow.Receive(type, amount);

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
    public async Task A_replay_after_a_UUID_conflict_cannot_apply_another_adjustment(
        string type, long amount, string state, long remaining)
    {
        var flow = new Flow();
        await flow.Receive(type, amount);
        var applied = Assert.Single(flow.Receipts);
        await flow.Receive(type, 400);

        await flow.Receive(type, amount);

        Assert.Equal(state, flow.Payment!.State);
        Assert.Equal(remaining, flow.Payment.RemainingAmount);
        Assert.Equal(3, flow.Receipts.Count);
        Assert.Equal(applied, flow.Receipts[0]);
        AssertPendingConflict(flow.Receipts[1]);
        Assert.Single(flow.Receipts, item => item.ProcessingState == "Applied");
    }

    [Fact]
    public async Task Authoritative_approval_does_not_apply_any_previously_conflicting_pending_UUID()
    {
        var flow = await TimedOutPayment();
        await flow.Receive("Refund", 500);
        await flow.Receive("Refund", 400);

        await flow.BankApproval("PaymentResponse");

        Assert.Equal("Approved", flow.Payment!.State);
        Assert.Equal(1250, flow.Payment.ApprovedAmount);
        Assert.Equal(1250, flow.Payment.RemainingAmount);
        Assert.True(flow.Payment.HasPendingEvents);
        Assert.All(flow.Receipts.Where(item => item.Source == "TransactionStatus"), AssertPendingConflict);
    }

    [Theory]
    [InlineData("Refunded", "FQ")]
    [InlineData("Refunded", "QF")]
    [InlineData("Refunded", "ABQ")]
    [InlineData("Refunded", "AQB")]
    [InlineData("Refunded", "QAB")]
    [InlineData("Refunded", "BAQ")]
    [InlineData("Refunded", "BQA")]
    [InlineData("Refunded", "QBA")]
    [InlineData("Reversed", "RQ")]
    [InlineData("Reversed", "QR")]
    public async Task Adjusted_RTTQ_and_matching_webhooks_resolve_a_lost_response_in_either_delivery_order(
        string state, string order)
    {
        var flow = await TimedOutPayment();
        foreach (var item in order)
        {
            if (item == 'Q')
                await flow.Query(QueryFields(state));
            else
                await flow.Receive(item == 'R' ? "Reversal" : "Refund",
                    item == 'A' ? 500 : item == 'B' ? 750 : 1250, $"adjustment-{item}");
        }

        Assert.Equal(state, flow.Payment!.State);
        Assert.Equal(2000, flow.Payment.RequestedAmount);
        Assert.Equal(1250, flow.Payment.ApprovedAmount);
        Assert.Equal(0, flow.Payment.RemainingAmount);
        Assert.True(PaymentResult.FromPayment(flow.Payment).IsFullyReconciled);
        Assert.Equal(order.Length + 1, flow.Receipts.Count);
        Assert.DoesNotContain(flow.Receipts, item => item.ProcessingState == "Pending");
        Assert.All(flow.Receipts.Where(item => item.Source == "TransactionStatus"),
            item => Assert.Equal("Applied", item.ProcessingState));
        var query = Assert.Single(flow.Receipts, item => item.Source == "TransactionQuery");
        Assert.Equal(state, query.Payload!.Value.GetProperty("PaymentResponse").GetProperty("TransactionStatus").GetString());
        flow.Session.Verify(value => value.CommitAsync(It.IsAny<CancellationToken>()), Times.Exactly(order.Length + 1));
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
    public async Task Matching_webhooks_cannot_replace_a_missing_or_invalid_RTTQ_AuthAmount(string state, string invalid)
    {
        var flow = await TimedOutPayment();
        await flow.Receive(state == "Reversed" ? "Reversal" : "Refund", 1250);
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

        await flow.Query(fields);

        Assert.Equal("Unresolved", flow.Payment!.State);
        Assert.Null(flow.Payment.ApprovedAmount);
        Assert.Null(flow.Payment.RemainingAmount);
        Assert.False(PaymentResult.FromPayment(flow.Payment).IsFullyReconciled);
        Assert.Equal(3, flow.Receipts.Count);
        Assert.All(flow.Receipts, item => Assert.Equal("Pending", item.ProcessingState));
    }

    [Theory]
    [InlineData("Refunded", "Q")]
    [InlineData("Refunded", "AQ")]
    [InlineData("Refunded", "QA")]
    [InlineData("Reversed", "Q")]
    public async Task An_adjusted_RTTQ_with_incomplete_history_does_not_infer_a_remaining_balance(string state, string order)
    {
        var flow = await TimedOutPayment();
        foreach (var item in order)
        {
            if (item == 'Q')
                await flow.Query(QueryFields(state));
            else
                await flow.Receive("Refund", 500);
        }

        Assert.Equal("Unresolved", flow.Payment!.State);
        Assert.Null(flow.Payment.RemainingAmount);
        Assert.False(PaymentResult.FromPayment(flow.Payment).IsFullyReconciled);
        Assert.All(flow.Receipts, item => Assert.Equal("Pending", item.ProcessingState));
        Assert.False(string.IsNullOrWhiteSpace(flow.Receipts.Single(item => item.Source == "TransactionQuery").ProcessingComment));
    }

    [Theory]
    [InlineData("declined")]
    [InlineData("currency")]
    [InlineData("outcome")]
    [InlineData("zero")]
    public async Task Inapplicable_refund_receipts_do_not_satisfy_a_Refunded_RTTQ(string invalid)
    {
        var flow = await TimedOutPayment();
        await flow.Query(QueryFields("Refunded"));
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

        await flow.Service.ReceiveAsync(JsonSerializer.SerializeToElement(fields));

        Assert.Equal("Unresolved", flow.Payment!.State);
        Assert.Null(flow.Payment.RemainingAmount);
        Assert.False(PaymentResult.FromPayment(flow.Payment).IsFullyReconciled);
        Assert.Equal(3, flow.Receipts.Count);
        Assert.DoesNotContain(flow.Receipts, item => item.ProcessingState == "Applied");
        Assert.Equal("Pending", flow.Receipts.Single(item => item.Source == "TransactionQuery").ProcessingState);
    }

    [Theory]
    [InlineData("QAA")]
    [InlineData("AQA")]
    [InlineData("AAQ")]
    public async Task Replayed_refund_UUIDs_count_once_when_reconciling_a_Refunded_RTTQ(string order)
    {
        var flow = await TimedOutPayment();
        foreach (var item in order)
        {
            if (item == 'Q')
                await flow.Query(QueryFields("Refunded"));
            else
                await flow.Receive("Refund", 625, "refund-a");
        }
        Assert.Equal("Unresolved", flow.Payment!.State);
        Assert.Null(flow.Payment.RemainingAmount);
        Assert.DoesNotContain(flow.Receipts, item => item.ProcessingState == "Applied");

        await flow.Receive("Refund", 625, "refund-b");

        Assert.Equal("Refunded", flow.Payment.State);
        Assert.Equal(1250, flow.Payment.ApprovedAmount);
        Assert.Equal(0, flow.Payment.RemainingAmount);
        Assert.True(PaymentResult.FromPayment(flow.Payment).IsFullyReconciled);
        Assert.Equal(5, flow.Receipts.Count);
        var refunds = flow.Receipts.Where(item => item.Source == "TransactionStatus").ToArray();
        Assert.Equal(2, refunds.Count(item => item.ProcessingState == "Applied"));
        Assert.Equal("Ignored", Assert.Single(refunds, item => item.ProcessingState != "Applied").ProcessingState);
    }

    [Theory]
    [InlineData("ABQ")]
    [InlineData("AQB")]
    [InlineData("QAB")]
    [InlineData("BAQ")]
    [InlineData("BQA")]
    [InlineData("QBA")]
    public async Task Conflicting_pending_UUIDs_cannot_satisfy_a_Refunded_RTTQ(string order)
    {
        var flow = await TimedOutPayment();
        foreach (var item in order)
        {
            if (item == 'Q')
                await flow.Query(QueryFields("Refunded"));
            else
                await flow.Receive("Refund", item == 'A' ? 500 : 750);
        }

        Assert.Equal("Unresolved", flow.Payment!.State);
        Assert.Null(flow.Payment.RemainingAmount);
        Assert.False(PaymentResult.FromPayment(flow.Payment).IsFullyReconciled);
        Assert.Equal(4, flow.Receipts.Count);
        Assert.All(flow.Receipts.Where(item => item.Source == "TransactionStatus"), AssertPendingConflict);
        var query = Assert.Single(flow.Receipts, item => item.Source == "TransactionQuery");
        Assert.Equal("Pending", query.ProcessingState);
        Assert.False(string.IsNullOrWhiteSpace(query.ProcessingComment));
        AssertConflictWarning(flow, flow.Receipts[^1]);
    }

    [Theory]
    [InlineData("Refunded", "Reversal", "AQ")]
    [InlineData("Refunded", "Reversal", "QA")]
    [InlineData("Reversed", "Refund", "AQ")]
    [InlineData("Reversed", "Refund", "QA")]
    public async Task Contradictory_RTTQ_and_adjustment_histories_remain_unresolved_and_warn(
        string state, string type, string order)
    {
        var flow = await TimedOutPayment();
        foreach (var item in order)
        {
            if (item == 'Q')
                await flow.Query(QueryFields(state));
            else
                await flow.Receive(type, 1250);
        }

        Assert.Equal("Unresolved", flow.Payment!.State);
        Assert.Null(flow.Payment.RemainingAmount);
        Assert.False(PaymentResult.FromPayment(flow.Payment).IsFullyReconciled);
        Assert.Equal(3, flow.Receipts.Count);
        var query = Assert.Single(flow.Receipts, item => item.Source == "TransactionQuery");
        Assert.Equal("Pending", query.ProcessingState);
        Assert.False(string.IsNullOrWhiteSpace(query.ProcessingComment));
        Assert.DoesNotContain(flow.Receipts, item => item.ProcessingState == "Applied");
        AssertConflictWarning(flow, flow.Receipts[^1]);
    }

    [Theory]
    [InlineData("PaymentResponse", "Refund", 500L, 750L, "PartiallyRefunded")]
    [InlineData("TransactionQuery", "Refund", 500L, 750L, "PartiallyRefunded")]
    [InlineData("PaymentResponse", "Reversal", 1250L, 0L, "Reversed")]
    [InlineData("TransactionQuery", "Reversal", 1250L, 0L, "Reversed")]
    public async Task An_adjustment_before_approval_is_reconsidered_when_AuthAmount_arrives(
        string source, string type, long amount, long remaining, string state)
    {
        var flow = new Flow();
        flow.Payment = flow.Payment! with { State = "Unresolved", ApprovedAmount = null, RemainingAmount = null };
        await flow.Receive(type, amount);
        Assert.Equal("Pending", Assert.Single(flow.Receipts).ProcessingState);
        Assert.Null(flow.Payment.RemainingAmount);

        await flow.BankApproval(source);

        Assert.Equal(state, flow.Payment.State);
        Assert.Equal(1250, flow.Payment.ApprovedAmount);
        Assert.Equal(remaining, flow.Payment.RemainingAmount);
        Assert.False(flow.Payment.HasPendingEvents);
        Assert.All(flow.Receipts, item => Assert.Equal("Applied", item.ProcessingState));
    }

    [Theory]
    [InlineData("early", "late")]
    [InlineData("late", "early")]
    public async Task Distinct_full_reversals_have_one_representative_independent_of_delivery_order(string first, string second)
    {
        var flow = new Flow();
        foreach (var identifier in new[] { first, second })
        {
            var fields = Fields("Reversal", 1250, identifier);
            fields["transactionDateTime"] = identifier == "early" ? "2026-03-04T08:02:11.417" : "2026-03-05T08:02:11.417";
            await flow.Service.ReceiveAsync(JsonSerializer.SerializeToElement(fields));
        }

        Assert.Equal(0, flow.Payment!.RemainingAmount);
        var applied = Assert.Single(flow.Receipts, item => item.ProcessingState == "Applied");
        Assert.Equal("early", applied.Payload!.Value.GetProperty("transactionUUID").GetString());
        Assert.Single(flow.Receipts, item => item.ProcessingState == "Ignored");
    }

    [Theory]
    [InlineData("Refund", 500L, "PartiallyRefunded", 750L)]
    [InlineData("Refund", 1250L, "Refunded", 0L)]
    [InlineData("Reversal", 1250L, "Reversed", 0L)]
    public async Task Late_bank_responses_and_Payment_Advice_preserve_an_adjusted_balance(
        string type, long amount, string state, long remaining)
    {
        var flow = new Flow();
        await flow.Receive(type, amount);

        await flow.BankApproval("PaymentResponse");
        var advice = new PaymentAdviceService(new(Guid.Empty, "unused", "unused"), flow.Events,
            TimeProvider.System, NullLogger<PaymentAdviceService>.Instance);
        await advice.ReceiveAsync(JsonSerializer.SerializeToElement(new
        {
            MerchantID = Guid.Empty, TransactionID = PaymentId, RequestType = "Payment", OrderNumber = Rrn,
            RetrievalReferenceNumber = Rrn, SystemsTraceAuditNumber = "7543", Amount = 2000,
            CurrencyCode = "ZAR", PaymentService = "CardNotPresent", TransactionStatus = "Success",
            ResponseSource = "Acquirer", ResponseCode = "00", CommProtocol = "REST"
        }));

        Assert.Equal(remaining, flow.Payment!.RemainingAmount);
        Assert.Equal(state, flow.Payment.State);
        Assert.False(flow.Payment.HasPendingEvents);
        Assert.All(flow.Receipts.Where(item => item.Source != "TransactionStatus"),
            item => Assert.Equal("Ignored", item.ProcessingState));
    }

    [Theory]
    [InlineData("00", false, "Pending")]
    [InlineData("05", true, "Pending")]
    [InlineData("05", false, "Ignored")]
    public async Task Switch_outcome_indicators_control_whether_an_adjustment_can_apply(
        string code, bool approved, string expectedState)
    {
        var flow = new Flow();
        var fields = Fields("Refund", 500);
        fields["txResultCode"] = code;
        fields["isApproved"] = approved;

        await flow.Service.ReceiveAsync(JsonSerializer.SerializeToElement(fields));

        Assert.Equal(expectedState, Assert.Single(flow.Receipts).ProcessingState);
        if (expectedState == "Pending")
            Assert.Null(flow.Payment!.RemainingAmount);
        else
            Assert.Equal(1250, flow.Payment!.RemainingAmount);
    }

    [Theory]
    [InlineData("Declined")]
    [InlineData("Failed")]
    public async Task A_payment_without_approval_cannot_be_adjusted(string state)
    {
        var flow = new Flow();
        flow.Payment = flow.Payment! with { State = state, ApprovedAmount = null, RemainingAmount = null };

        await flow.Receive("Refund", 500);

        Assert.Equal(state, flow.Payment.State);
        Assert.Null(flow.Payment.RemainingAmount);
        Assert.Equal("Ignored", Assert.Single(flow.Receipts).ProcessingState);
    }

    [Fact]
    public async Task Currency_mismatch_cannot_change_a_locked_remaining_value()
    {
        var flow = new Flow();
        flow.Payment = flow.Payment! with { RemainingAmount = 500 };
        var fields = Fields("Refund", 200);
        fields["currencyCode"] = "USD";

        await flow.Service.ReceiveAsync(JsonSerializer.SerializeToElement(fields));

        Assert.Equal(500, flow.Payment.RemainingAmount);
        Assert.Equal("Ignored", Assert.Single(flow.Receipts).ProcessingState);
    }

    [Fact]
    public async Task Omitted_currency_uses_Acmes_ZAR_default()
    {
        var flow = new Flow();
        var fields = Fields("Refund", 500);
        fields.Remove("currencyCode");

        await flow.Service.ReceiveAsync(JsonSerializer.SerializeToElement(fields));

        Assert.Equal(750, flow.Payment!.RemainingAmount);
    }

    [Fact]
    public async Task An_unknown_RRN_is_committed_as_an_unlinked_pending_receipt()
    {
        var flow = new Flow { Payment = null };

        await flow.Receive("Refund", 500);

        var receipt = Assert.Single(flow.Receipts);
        Assert.Null(receipt.PaymentId);
        Assert.Equal("Pending", receipt.ProcessingState);
        Assert.Equal("UnknownPayment", receipt.ProcessingComment);
        flow.Session.Verify(value => value.SavePaymentAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()), Times.Never);
        flow.Session.Verify(value => value.GetPendingEventsAsync(It.IsAny<CancellationToken>()), Times.Never);
        flow.Session.Verify(value => value.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("insert")]
    [InlineData("history")]
    [InlineData("save_payment")]
    [InlineData("save_event")]
    public async Task Persistence_failure_disposes_the_session_without_committing(string operation)
    {
        var flow = new Flow();
        var failure = new TimeoutException("Database operation failed.");
        if (operation == "insert")
            flow.Session.Setup(value => value.InsertEventAsync(It.IsAny<PaymentEvent>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        else if (operation == "history")
            flow.Session.Setup(value => value.GetTransactionStatusEventsAsync(It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        else if (operation == "save_payment")
            flow.Session.Setup(value => value.SavePaymentAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        else
            flow.Session.Setup(value => value.SaveEventAsync(It.IsAny<PaymentEvent>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);

        await Assert.ThrowsAsync<TimeoutException>(() => flow.Receive("Refund", 500));

        flow.Session.Verify(value => value.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
        flow.Session.Verify(value => value.DisposeAsync(), Times.Once);
    }

    private static void AssertPendingConflict(PaymentEvent receipt)
    {
        Assert.Equal("Pending", receipt.ProcessingState);
        Assert.NotNull(receipt.ProcessingComment);
        Assert.Contains("conflict", receipt.ProcessingComment, StringComparison.OrdinalIgnoreCase);
        Assert.Null(receipt.ProcessedAt);
    }

    private static void AssertConflictWarning(Flow flow, PaymentEvent receipt) =>
        flow.Logger.Verify(logger => logger.Log(LogLevel.Warning, It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains(PaymentId.ToString())
                && state.ToString()!.Contains(receipt.PaymentEventId.ToString())),
            It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.AtLeastOnce);

    private static async Task<Flow> TimedOutPayment()
    {
        var flow = new Flow();
        flow.Payment = flow.Payment! with { State = "Unresolved", ApprovedAmount = null, RemainingAmount = null };
        var id = Guid.NewGuid();
        await flow.Events.ProcessAsync(PaymentId, id,
            new(id, PaymentId, "PaymentResponse", null, null, DateTimeOffset.UtcNow));
        return flow;
    }

    private static Dictionary<string, object?> QueryFields(string state) => new()
    {
        ["TransactionID"] = PaymentId, ["OrderNumber"] = Rrn, ["TransactionStatus"] = state,
        ["ResponseDetail"] = new { Source = "Acquirer", Code = "00" }, ["AuthAmount"] = 1250L
    };

    private static Dictionary<string, object?> Fields(string type, long amount, string identifier = "event-1") => new()
    {
        ["transactionUUID"] = identifier, ["transactionType"] = type,
        ["transactionDateTime"] = "2026-03-04T08:02:11.417", ["rrn"] = Rrn,
        ["transactionAmount"] = amount, ["currencyCode"] = "ZAR", ["txResultCode"] = "00", ["isApproved"] = true
    };

    private sealed class Flow
    {
        public Payment? Payment { get; set; } = new(PaymentId, "app-payment", "customer", Guid.NewGuid(),
            "shop", Rrn, "ZAR", 2000, "Approved", DateTimeOffset.UtcNow, 1250, 1250);
        public List<PaymentEvent> Receipts { get; } = [];
        public List<string> Calls { get; } = [];
        public Mock<IPaymentRepository> Repository { get; } = new(MockBehavior.Strict);
        public Mock<IPaymentProcessingSession> Session { get; } = new(MockBehavior.Strict);
        public Mock<ILogger<PaymentEventService>> Logger { get; } = new();
        public PaymentEventService Events { get; }
        public TransactionStatusService Service { get; }

        public Flow()
        {
            Repository.Setup(value => value.BeginProcessingByRrnAsync(Rrn, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Callback(() => Calls.Add("lock")).ReturnsAsync(Session.Object);
            Repository.Setup(value => value.BeginProcessingAsync(PaymentId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Callback(() => Calls.Add("lock")).ReturnsAsync(Session.Object);
            Session.SetupGet(value => value.Payment).Returns(() => Payment);
            Session.Setup(value => value.InsertEventAsync(It.IsAny<PaymentEvent>(), It.IsAny<CancellationToken>()))
                .Callback((PaymentEvent receipt, CancellationToken _) => { Calls.Add("insert"); Receipts.Add(receipt); })
                .Returns(Task.CompletedTask);
            Session.Setup(value => value.GetPendingEventsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => { Calls.Add("pending"); return Receipts.Where(item => item.ProcessingState == "Pending").ToArray(); });
            Session.Setup(value => value.GetTransactionStatusEventsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => { Calls.Add("history"); return Receipts.Where(item => item.Source == "TransactionStatus").ToArray(); });
            Session.Setup(value => value.SavePaymentAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()))
                .Callback((Payment payment, CancellationToken _) => { Calls.Add("save_payment"); Payment = payment; })
                .Returns(Task.CompletedTask);
            Session.Setup(value => value.SaveEventAsync(It.IsAny<PaymentEvent>(), It.IsAny<CancellationToken>()))
                .Callback((PaymentEvent receipt, CancellationToken _) =>
                {
                    Calls.Add("save_event");
                    Receipts[Receipts.FindIndex(item => item.PaymentEventId == receipt.PaymentEventId)] = receipt;
                }).Returns(Task.CompletedTask);
            Session.Setup(value => value.CommitAsync(It.IsAny<CancellationToken>()))
                .Callback(() => Calls.Add("commit")).Returns(Task.CompletedTask);
            Session.Setup(value => value.DisposeAsync()).Returns(ValueTask.CompletedTask);
            Events = new(Repository.Object, new PaymentResponseProcessor(), new TransactionStatusProcessor(),
                TimeProvider.System, Logger.Object);
            Service = new(Events, TimeProvider.System, NullLogger<TransactionStatusService>.Instance);
        }

        public Task Receive(string type, long amount, string identifier = "event-1") =>
            Service.ReceiveAsync(JsonSerializer.SerializeToElement(Fields(type, amount, identifier)));

        public Task<Payment?> Query(Dictionary<string, object?> response)
        {
            var id = Guid.NewGuid();
            return Events.ProcessAsync(PaymentId, id, new(id, PaymentId, "TransactionQuery",
                JsonSerializer.SerializeToElement(new { PaymentResponse = response }), 200, DateTimeOffset.UtcNow));
        }

        public Task<Payment?> BankApproval(string source)
        {
            var id = Guid.NewGuid();
            return Events.ProcessAsync(PaymentId, id, new(id, PaymentId, source,
                JsonSerializer.SerializeToElement(new
                {
                    PaymentResponse = new
                    {
                        TransactionID = PaymentId, OrderNumber = Rrn, TransactionStatus = "Success",
                        ResponseDetail = new { Source = "Acquirer", Code = "00" }, AuthAmount = 1250
                    }
                }), 200, DateTimeOffset.UtcNow));
        }
    }
}
