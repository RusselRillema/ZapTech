using System.Text.Json;
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
    [InlineData(500L, 400L)]
    [InlineData(400L, 500L)]
    public async Task Changed_details_under_one_switch_identity_require_reconciliation(long first, long second)
    {
        var flow = new Flow();

        await flow.Receive("Refund", first);
        await flow.Receive("Refund", second);

        Assert.Null(flow.Payment!.RemainingAmount);
        Assert.True(flow.Payment.HasPendingEvents);
        Assert.All(flow.Receipts, item =>
        {
            Assert.Equal("Pending", item.ProcessingState);
            Assert.Contains("conflicting details", item.ProcessingComment);
            Assert.Null(item.ProcessedAt);
        });
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
                TimeProvider.System, NullLogger<PaymentEventService>.Instance);
            Service = new(Events, TimeProvider.System, NullLogger<TransactionStatusService>.Instance);
        }

        public Task Receive(string type, long amount, string identifier = "event-1") =>
            Service.ReceiveAsync(JsonSerializer.SerializeToElement(Fields(type, amount, identifier)));

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
