using System.Text.Json;
using Moq;
using PaymentIntegration.Payments.Models;
using Xunit;
using static PaymentIntegration.Tests.PaymentAdviceTestContext;

namespace PaymentIntegration.Tests;

public sealed class PaymentAdviceServiceTests
{
    [Fact]
    public async Task Acquirer_decline_overrides_gateway_success_and_reconsiders_a_pending_timeout()
    {
        var flow = new PaymentAdviceTestContext();
        flow.Events.Add(new(Guid.NewGuid(), PaymentId, "PaymentResponse", null, null, DateTimeOffset.UtcNow));

        await flow.Service.ReceiveAsync(Advice());

        Assert.Equal("Declined", flow.SavedPayment!.State);
        Assert.Null(flow.SavedPayment.ApprovedAmount);
        Assert.False(flow.SavedPayment.HasPendingEvents);
        Assert.Equal("Ignored", flow.Events.Single(item => item.Source == "PaymentResponse").ProcessingState);
        Assert.Equal("Applied", flow.Events.Single(item => item.Source == "PaymentAdvice").ProcessingState);
        Assert.True(flow.Calls.IndexOf("lock") < flow.Calls.IndexOf("insert"));
        Assert.True(flow.Calls.IndexOf("insert") < flow.Calls.IndexOf("read_pending"));
        Assert.True(flow.Calls.IndexOf("save_event") < flow.Calls.IndexOf("commit"));
    }

    [Fact]
    public async Task Approval_advice_leaves_unknown_authorised_amount_pending()
    {
        var flow = new PaymentAdviceTestContext();

        await flow.Service.ReceiveAsync(Advice(code: "00"));

        Assert.Equal("Unresolved", flow.SavedPayment!.State);
        Assert.Null(flow.SavedPayment.ApprovedAmount);
        Assert.Null(flow.SavedPayment.RemainingAmount);
        Assert.True(flow.SavedPayment.HasPendingEvents);
        var advice = Assert.Single(flow.Events);
        Assert.Equal("Pending", advice.ProcessingState);
        Assert.Contains("AuthAmount", advice.ProcessingComment);
        Assert.Null(advice.ProcessedAt);
        Assert.Equal("preserved", advice.Payload!.Value.GetProperty("FutureAcmeField").GetString());
    }

    [Fact]
    public async Task An_approved_state_without_an_authorised_amount_still_keeps_advice_pending()
    {
        var flow = new PaymentAdviceTestContext();
        flow.InitialPayment = flow.InitialPayment! with { State = "Approved" };

        await flow.Service.ReceiveAsync(Advice(code: "00"));

        Assert.Null(flow.SavedPayment!.ApprovedAmount);
        Assert.Null(flow.SavedPayment.RemainingAmount);
        Assert.True(flow.SavedPayment.HasPendingEvents);
        Assert.Equal("Pending", Assert.Single(flow.Events).ProcessingState);
    }

    [Fact]
    public async Task Approval_advice_preserves_the_locked_partial_authorisation_and_remaining_value()
    {
        var flow = new PaymentAdviceTestContext();
        flow.InitialPayment = flow.InitialPayment! with
        {
            State = "Approved", ApprovedAmount = 1250, RemainingAmount = 500
        };

        await flow.Service.ReceiveAsync(Advice(code: "00"));

        Assert.Equal(1250, flow.SavedPayment!.ApprovedAmount);
        Assert.Equal(500, flow.SavedPayment.RemainingAmount);
        Assert.Equal("Ignored", Assert.Single(flow.Events).ProcessingState);
    }

    [Fact]
    public async Task Conflicting_advice_cannot_overwrite_a_locked_established_outcome()
    {
        var flow = new PaymentAdviceTestContext();
        flow.InitialPayment = flow.InitialPayment! with
        {
            State = "Approved", ApprovedAmount = 1250, RemainingAmount = 500
        };

        await flow.Service.ReceiveAsync(Advice());

        Assert.Equal("Approved", flow.SavedPayment!.State);
        Assert.Equal(500, flow.SavedPayment.RemainingAmount);
        Assert.True(flow.SavedPayment.HasPendingEvents);
        Assert.Equal("Pending", Assert.Single(flow.Events).ProcessingState);
    }

    [Fact]
    public async Task Repeated_advice_retains_each_receipt_but_applies_the_outcome_once()
    {
        var flow = new PaymentAdviceTestContext();

        await flow.Service.ReceiveAsync(Advice());
        await flow.Service.ReceiveAsync(Advice());

        Assert.Equal("Declined", flow.SavedPayment!.State);
        Assert.Equal(2, flow.Events.Count);
        Assert.Single(flow.Events, item => item.ProcessingState == "Applied");
        Assert.Single(flow.Events, item => item.ProcessingState == "Ignored");
        Assert.Equal(2, flow.Events.Select(item => item.PaymentEventId).Distinct().Count());
        flow.Repository.Verify(value => value.BeginProcessingAsync(PaymentId, It.IsAny<Guid>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData("CurrencyCode", "USD")]
    [InlineData("OrderNumber", "ANOTHERORDER")]
    [InlineData("RetrievalReferenceNumber", "ANOTHERRRN")]
    public async Task Inconsistent_advice_is_audited_without_changing_payment_state(string field, string value)
    {
        var flow = new PaymentAdviceTestContext();
        var body = AdviceFields();
        body[field] = value;

        await flow.Service.ReceiveAsync(JsonSerializer.SerializeToElement(body));

        Assert.Equal("Unresolved", flow.SavedPayment!.State);
        Assert.Null(flow.SavedPayment.ApprovedAmount);
        var ignored = Assert.Single(flow.Events);
        Assert.Equal("Ignored", ignored.ProcessingState);
        Assert.False(string.IsNullOrWhiteSpace(ignored.ProcessingComment));
    }

    [Theory]
    [InlineData("Authorize")]
    [InlineData("Capture")]
    public async Task Unsupported_request_types_are_ignored_with_the_confirmed_reason(string requestType)
    {
        var flow = new PaymentAdviceTestContext();

        await flow.Service.ReceiveAsync(Advice(requestType: requestType));

        Assert.Equal("Unresolved", flow.SavedPayment!.State);
        var ignored = Assert.Single(flow.Events);
        Assert.Equal("Ignored", ignored.ProcessingState);
        Assert.Equal("UnsupportedRequestType", ignored.ProcessingComment);
        Assert.NotNull(ignored.ProcessedAt);
    }

    [Theory]
    [InlineData("2e0e1b85-bf91-45a9-b038-282e00036919", "Payment", "Pending", "UnknownPayment")]
    [InlineData("external-transaction", "Payment", "Pending", "UnknownPayment")]
    [InlineData("external-transaction", "Capture", "Ignored", "UnsupportedRequestType")]
    public async Task Unknown_transactions_are_audited_without_a_payment_link(
        string transactionId, string requestType, string state, string reason)
    {
        var flow = new PaymentAdviceTestContext { InitialPayment = null };
        var fields = AdviceFields(requestType: requestType);
        fields["TransactionID"] = transactionId;

        await flow.Service.ReceiveAsync(JsonSerializer.SerializeToElement(fields));

        var receipt = Assert.Single(flow.Events);
        Assert.Null(receipt.PaymentId);
        Assert.Equal(state, receipt.ProcessingState);
        Assert.Equal(reason, receipt.ProcessingComment);
        Assert.Equal(transactionId, receipt.Payload!.Value.GetProperty("TransactionID").GetString());
        Assert.Null(flow.SavedPayment);
        flow.Session.Verify(value => value.GetPendingEventsAsync(It.IsAny<CancellationToken>()), Times.Never);
        flow.Session.Verify(value => value.SavePaymentAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()), Times.Never);
        flow.Session.Verify(value => value.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Omitted_currency_uses_the_Acme_ZAR_default()
    {
        var flow = new PaymentAdviceTestContext();
        var fields = AdviceFields();
        fields.Remove("CurrencyCode");

        await flow.Service.ReceiveAsync(JsonSerializer.SerializeToElement(fields));

        Assert.Equal("Declined", flow.SavedPayment!.State);
        Assert.Equal("Applied", Assert.Single(flow.Events).ProcessingState);
    }

    [Theory]
    [InlineData("MerchantID", "another-account")]
    [InlineData("PaymentService", "invalid-service")]
    [InlineData("TransactionStatus", "invalid-status")]
    [InlineData("Amount", "1999")]
    [InlineData("ResponseCode", null)]
    public async Task Invalid_advice_is_rejected_before_database_processing(string field, string? value)
    {
        var flow = new PaymentAdviceTestContext();
        var fields = AdviceFields();
        fields[field] = value;

        var exception = await Assert.ThrowsAsync<PaymentRequestException>(() =>
            flow.Service.ReceiveAsync(JsonSerializer.SerializeToElement(fields)));

        Assert.Equal(400, exception.StatusCode);
        flow.Repository.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("insert")]
    [InlineData("save_payment")]
    [InlineData("save_event")]
    public async Task Persistence_failure_disposes_the_session_without_committing(string operation)
    {
        var flow = new PaymentAdviceTestContext();
        var failure = new InvalidOperationException("Database operation failed.");
        if (operation == "insert")
            flow.Session.Setup(value => value.InsertEventAsync(It.IsAny<PaymentEvent>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(failure);
        else if (operation == "save_payment")
            flow.Session.Setup(value => value.SavePaymentAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(failure);
        else
            flow.Session.Setup(value => value.SaveEventAsync(It.IsAny<PaymentEvent>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(failure);

        await Assert.ThrowsAsync<InvalidOperationException>(() => flow.Service.ReceiveAsync(Advice()));

        flow.Session.Verify(value => value.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
        flow.Session.Verify(value => value.DisposeAsync(), Times.Once);
    }
}
