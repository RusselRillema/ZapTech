using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using PaymentIntegration.Payments.Models;
using Xunit;
using static PaymentIntegration.Tests.PaymentEventTestData;

namespace PaymentIntegration.Tests;

public sealed class PaymentEventServiceTests
{
    [Theory]
    [InlineData(500L, 400L, "PartiallyRefunded", 750L)]
    [InlineData(400L, 500L, "PartiallyRefunded", 850L)]
    [InlineData(1250L, 500L, "Refunded", 0L)]
    public async Task Conflicting_UUID_receipts_preserve_an_already_applied_refund(
        long first, long second, string state, long remaining)
    {
        var flow = new PaymentEventServiceTestContext();
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
        AssertConflictWarning(flow, flow.Receipts[1]);
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
        var flow = new PaymentEventServiceTestContext();
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

        await flow.Receive(payload);

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
        AssertConflictWarning(flow, conflict);
        flow.Session.Verify(value => value.CommitAsync(It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Theory]
    [InlineData("Refund", 500L, 400L)]
    [InlineData("Reversal", 500L, 600L)]
    public async Task A_conflict_with_an_ignored_UUID_preserves_the_previous_terminal_receipts(
        string type, long originalAmount, long conflictingAmount)
    {
        var flow = new PaymentEventServiceTestContext();
        await flow.Receive("Reversal", 1250, "reversal");
        await flow.Receive(type, originalAmount, "ignored-adjustment");
        var originalReceipts = flow.Receipts.ToArray();
        Assert.Equal("Applied", originalReceipts[0].ProcessingState);
        Assert.Equal("Ignored", originalReceipts[1].ProcessingState);
        var fields = Fields(type, conflictingAmount, "ignored-adjustment");
        fields["FutureSwitchField"] = "retain for investigation";
        var payload = JsonSerializer.SerializeToElement(fields);

        await flow.Receive(payload);

        Assert.Equal("Reversed", flow.Payment!.State);
        Assert.Equal(1250, flow.Payment.ApprovedAmount);
        Assert.Equal(0, flow.Payment.RemainingAmount);
        Assert.True(flow.Payment.HasPendingEvents);
        Assert.False(PaymentResult.FromPayment(flow.Payment).IsFullyReconciled);
        Assert.Equal(3, flow.Receipts.Count);
        Assert.Equal(originalReceipts, flow.Receipts.Take(2));
        var conflict = flow.Receipts[2];
        AssertPendingConflict(conflict);
        Assert.Equal(PaymentId, conflict.PaymentId);
        Assert.Equal(payload.GetRawText(), conflict.Payload!.Value.GetRawText());
        Assert.Equal(3, flow.Receipts.Select(item => item.PaymentEventId).Distinct().Count());
        AssertConflictWarning(flow, conflict);
        flow.Session.Verify(value => value.CommitAsync(It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Fact]
    public async Task A_UUID_conflict_logs_a_warning_with_the_payment_and_conflicting_receipt_identifiers()
    {
        var flow = new PaymentEventServiceTestContext();
        await flow.Receive("Reversal", 1250);

        await flow.Receive("Reversal", 500);

        AssertConflictWarning(flow, flow.Receipts[1]);
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
    [InlineData(true, false, 550L)]
    [InlineData(false, false, 1050L)]
    [InlineData(false, true, 1050L)]
    public async Task A_UUID_conflict_does_not_block_a_distinct_valid_refund(
        bool alreadyApplied, bool replayBeforeConflict, long remaining)
    {
        var flow = alreadyApplied ? new PaymentEventServiceTestContext() : await TimedOutPayment();
        await flow.Receive("Refund", 500, "refund-a");
        var original = flow.Receipts.Single(item => item.Source == "TransactionStatus");
        if (replayBeforeConflict)
            await flow.Receive("Refund", 500, "refund-a");
        await flow.Receive("Refund", 400, "refund-a");
        var conflictId = flow.Receipts[^1].PaymentEventId;
        if (!alreadyApplied)
            await flow.BankApproval("PaymentResponse");

        await flow.Receive("Refund", 200, "refund-b");

        Assert.Equal("PartiallyRefunded", flow.Payment!.State);
        Assert.Equal(1250, flow.Payment.ApprovedAmount);
        Assert.Equal(remaining, flow.Payment.RemainingAmount);
        Assert.True(flow.Payment.HasPendingEvents);
        Assert.False(PaymentResult.FromPayment(flow.Payment).IsFullyReconciled);
        AssertPendingConflict(flow.Receipts.Single(item => item.PaymentEventId == conflictId));
        Assert.Equal("Applied", flow.Receipts[^1].ProcessingState);
        if (alreadyApplied)
            Assert.Equal(original, flow.Receipts.Single(item => item.PaymentEventId == original.PaymentEventId));
        else
            AssertPendingConflict(flow.Receipts.Single(item => item.PaymentEventId == original.PaymentEventId));
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
        Assert.Null(query.ProcessedAt);
        Assert.Contains("investigation", query.ProcessingComment, StringComparison.OrdinalIgnoreCase);
        AssertConflictWarning(flow, query);
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
        var flow = new PaymentEventServiceTestContext();
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

    [Fact]
    public async Task Refunded_query_uses_recorded_history_even_when_no_webhook_is_pending()
    {
        var flow = new PaymentEventServiceTestContext();
        await flow.Receive("Refund", 1250);
        Assert.False(flow.Payment!.HasPendingEvents);

        await flow.Query("Refunded");

        Assert.Equal("Refunded", flow.Payment.State);
        Assert.Equal(0, flow.Payment.RemainingAmount);
        Assert.True(PaymentResult.FromPayment(flow.Payment).IsFullyReconciled);
        Assert.All(flow.Receipts, item => Assert.Equal("Applied", item.ProcessingState));
        flow.Session.Verify(value => value.GetTransactionStatusEventsAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData("Refund", 500L, "PartiallyRefunded", 750L)]
    [InlineData("Refund", 1250L, "Refunded", 0L)]
    [InlineData("Reversal", 1250L, "Reversed", 0L)]
    public async Task Late_bank_responses_and_Payment_Advice_preserve_an_adjusted_balance(
        string type, long amount, string state, long remaining)
    {
        var flow = new PaymentEventServiceTestContext();
        await flow.Receive(type, amount);

        await flow.BankApproval("PaymentResponse");
        var adviceId = Guid.NewGuid();
        await flow.Service.ProcessAsync(PaymentId, adviceId, new(adviceId, PaymentId, "PaymentAdvice",
            JsonSerializer.SerializeToElement(new
            {
                MerchantID = Guid.Empty, TransactionID = PaymentId, RequestType = "Payment", OrderNumber = Rrn,
                RetrievalReferenceNumber = Rrn, SystemsTraceAuditNumber = "7543", Amount = 2000,
                CurrencyCode = "ZAR", PaymentService = "CardNotPresent", TransactionStatus = "Success",
                ResponseSource = "Acquirer", ResponseCode = "00", CommProtocol = "REST"
            }), null, DateTimeOffset.UtcNow));

        Assert.Equal(remaining, flow.Payment!.RemainingAmount);
        Assert.Equal(state, flow.Payment.State);
        Assert.False(flow.Payment.HasPendingEvents);
        Assert.All(flow.Receipts.Where(item => item.Source != "TransactionStatus"),
            item => Assert.Equal("Ignored", item.ProcessingState));
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
    [InlineData("Refunded", "AaBQ")]
    [InlineData("Refunded", "QAaB")]
    [InlineData("Refunded", "ABQa")]
    [InlineData("Refunded", "AaQB")]
    [InlineData("Reversed", "RQ")]
    [InlineData("Reversed", "QR")]
    public async Task Adjusted_queries_load_matching_history_and_persist_each_delivery(string state, string order)
    {
        var flow = await TimedOutPayment();
        var delivered = new HashSet<char>();
        foreach (var item in order)
        {
            if (item == 'Q')
                await flow.Query(QueryFields(state));
            else
                await flow.Receive(item == 'R' ? "Reversal" : "Refund",
                    item is 'A' or 'a' ? 500 : item == 'B' ? 750 : 1250,
                    $"adjustment-{char.ToUpperInvariant(item)}");
            delivered.Add(char.ToUpperInvariant(item));
            var required = order.Contains('A') ? new[] { 'Q', 'A', 'B' }
                : state == "Reversed" ? new[] { 'Q', 'R' } : new[] { 'Q', 'F' };
            if (!delivered.IsSupersetOf(required))
                AssertUnresolved(flow);
        }

        Assert.Equal(state, flow.Payment!.State);
        Assert.Equal(2000, flow.Payment.RequestedAmount);
        Assert.Equal(1250, flow.Payment.ApprovedAmount);
        Assert.Equal(0, flow.Payment.RemainingAmount);
        Assert.True(PaymentResult.FromPayment(flow.Payment).IsFullyReconciled);
        Assert.Equal(order.Length + 1, flow.Receipts.Count);
        Assert.DoesNotContain(flow.Receipts, item => item.ProcessingState == "Pending");
        Assert.Equal("Ignored", Assert.Single(flow.Receipts, item => item.Source == "PaymentResponse").ProcessingState);
        var query = Assert.Single(flow.Receipts, item => item.Source == "TransactionQuery");
        Assert.Equal(state, query.Payload!.Value.GetProperty("PaymentResponse").GetProperty("TransactionStatus").GetString());
        Assert.Equal("Applied", query.ProcessingState);
        Assert.All(flow.Receipts.Where(item => item.ProcessingState == "Applied"), item => Assert.NotNull(item.ProcessedAt));
        var refunds = flow.Receipts.Where(item => item.Source == "TransactionStatus").ToArray();
        Assert.Equal(order.Contains('A') ? 2 : 1, refunds.Count(item => item.ProcessingState == "Applied"));
        Assert.Equal(order.Count(item => item == 'a'), refunds.Count(item => item.ProcessingState == "Ignored"));
        flow.Session.Verify(value => value.CommitAsync(It.IsAny<CancellationToken>()), Times.Exactly(order.Length + 1));
    }

    [Theory]
    [InlineData("Refunded", "outcome", false)]
    [InlineData("Refunded", "outcome", true)]
    [InlineData("Refunded", "reversal", false)]
    [InlineData("Refunded", "reversal", true)]
    [InlineData("Refunded", "excess", false)]
    [InlineData("Refunded", "excess", true)]
    [InlineData("Refunded", "decline", false)]
    [InlineData("Refunded", "decline", true)]
    [InlineData("Reversed", "refund", false)]
    [InlineData("Reversed", "refund", true)]
    public async Task Adjusted_queries_with_conflicting_history_are_persisted_for_investigation(
        string state, string conflict, bool queryFirst)
    {
        var flow = await TimedOutPayment();
        if (queryFirst)
            await flow.Query(state, code: conflict == "decline" ? "05" : "00");
        var fields = Fields(conflict == "reversal" ? "Reversal" : "Refund", conflict == "excess" ? 1300 : 1250);
        if (conflict == "outcome")
            fields["isApproved"] = false;
        await flow.Receive(JsonSerializer.SerializeToElement(fields));
        if (!queryFirst)
            await flow.Query(state, code: conflict == "decline" ? "05" : "00");

        AssertUnresolved(flow);
        Assert.Equal(3, flow.Receipts.Count);
        Assert.DoesNotContain(flow.Receipts, item => item.ProcessingState == "Applied");
        var query = Assert.Single(flow.Receipts, item => item.Source == "TransactionQuery");
        Assert.Equal("Pending", query.ProcessingState);
        Assert.Null(query.ProcessedAt);
        Assert.Contains("investigation", query.ProcessingComment, StringComparison.OrdinalIgnoreCase);
        AssertConflictWarning(flow, query);
        AssertConflictWarning(flow, flow.Receipts[^1]);
    }

    [Theory]
    [InlineData("PaymentResponse")]
    [InlineData("TransactionQuery")]
    public async Task A_later_payment_or_query_response_reconsiders_earlier_approval_advice_using_AuthAmount(string source)
    {
        var flow = new PaymentAdviceTestContext();
        await flow.Service.ReceiveAsync(PaymentAdviceTestContext.Advice(code: "00"));
        var response = new PaymentEvent(Guid.NewGuid(), PaymentId, source,
            JsonSerializer.SerializeToElement(new
            {
                PaymentResponse = new
                {
                    TransactionID = PaymentId,
                    OrderNumber = "ORDER1234567",
                    TransactionStatus = "Success",
                    ResponseDetail = new { Source = "Acquirer", Code = "00" },
                    AuthAmount = 1250
                }
            }), 200, DateTimeOffset.UtcNow);

        await flow.EventService.ProcessAsync(PaymentId, response.PaymentEventId, response);

        Assert.Equal("Approved", flow.SavedPayment!.State);
        Assert.Equal(1250, flow.SavedPayment.ApprovedAmount);
        Assert.Equal(1250, flow.SavedPayment.RemainingAmount);
        Assert.False(flow.SavedPayment.HasPendingEvents);
        Assert.Equal("Ignored", flow.Events.Single(item => item.Source == "PaymentAdvice").ProcessingState);
        Assert.Equal("Applied", flow.Events.Single(item => item.Source == source).ProcessingState);
    }

    private static async Task<PaymentEventServiceTestContext> TimedOutPayment()
    {
        var flow = new PaymentEventServiceTestContext();
        flow.Payment = flow.Payment! with { State = "Unresolved", ApprovedAmount = null, RemainingAmount = null };
        var id = Guid.NewGuid();
        await flow.Service.ProcessAsync(PaymentId, id, new(id, PaymentId, "PaymentResponse", null, null, DateTimeOffset.UtcNow));
        return flow;
    }

    private static void AssertUnresolved(PaymentEventServiceTestContext flow)
    {
        Assert.Equal("Unresolved", flow.Payment!.State);
        Assert.Null(flow.Payment.RemainingAmount);
        Assert.True(flow.Payment.HasPendingEvents);
        Assert.False(PaymentResult.FromPayment(flow.Payment).IsFullyReconciled);
    }

    private static void AssertPendingConflict(PaymentEvent receipt)
    {
        Assert.Equal("Pending", receipt.ProcessingState);
        Assert.NotNull(receipt.ProcessingComment);
        Assert.Contains("conflict", receipt.ProcessingComment, StringComparison.OrdinalIgnoreCase);
        Assert.Null(receipt.ProcessedAt);
    }

    private static void AssertConflictWarning(PaymentEventServiceTestContext flow, PaymentEvent receipt) =>
        flow.Logger.Verify(logger => logger.Log(LogLevel.Warning, It.IsAny<EventId>(),
            It.Is<It.IsAnyType>((state, _) => state.ToString()!.Contains(PaymentId.ToString())
                && state.ToString()!.Contains(receipt.PaymentEventId.ToString())
                && state.ToString()!.Contains("investigation", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<Exception?>(), It.IsAny<Func<It.IsAnyType, Exception?, string>>()), Times.AtLeastOnce);
}
