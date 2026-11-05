using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PaymentIntegration.Payments.Models;
using PaymentIntegration.Payments.Services;
using Xunit;
using static PaymentIntegration.Tests.PaymentEventTestData;

namespace PaymentIntegration.Tests;

public sealed class TransactionStatusServiceTests
{
    [Theory]
    [InlineData("Refund", 500L, 750L, "PartiallyRefunded")]
    [InlineData("Refund", 1250L, 0L, "Refunded")]
    [InlineData("Reversal", 1250L, 0L, "Reversed")]
    public async Task Confirmed_adjustments_use_the_locked_authorisation_and_expose_the_lifecycle_state(
        string type, long amount, long remaining, string state)
    {
        var flow = new PaymentEventServiceTestContext();
        var service = CreateService(flow);

        await service.ReceiveAsync(JsonSerializer.SerializeToElement(Fields(type, amount)));

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

    [Fact]
    public async Task A_replay_preserves_both_receipts_and_unknown_fields_but_reduces_value_once()
    {
        var flow = new PaymentEventServiceTestContext();
        var service = CreateService(flow);
        await service.ReceiveAsync(JsonSerializer.SerializeToElement(Fields("Refund", 500)));
        var replay = Fields("Refund", 500);
        replay["FutureSwitchField"] = "retained";

        await service.ReceiveAsync(JsonSerializer.SerializeToElement(replay));

        Assert.Equal(750, flow.Payment!.RemainingAmount);
        Assert.Equal(2, flow.Receipts.Count);
        Assert.Single(flow.Receipts, item => item.ProcessingState == "Applied");
        Assert.Single(flow.Receipts, item => item.ProcessingState == "Ignored");
        Assert.Equal(2, flow.Receipts.Select(item => item.PaymentEventId).Distinct().Count());
        Assert.Equal("retained", flow.Receipts[1].Payload!.Value.GetProperty("FutureSwitchField").GetString());
    }

    [Fact]
    public async Task An_unknown_RRN_is_committed_as_an_unlinked_pending_receipt()
    {
        var flow = new PaymentEventServiceTestContext { Payment = null };
        var service = CreateService(flow);

        await service.ReceiveAsync(JsonSerializer.SerializeToElement(Fields("Refund", 500)));

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
        var flow = new PaymentEventServiceTestContext();
        var service = CreateService(flow);
        var failure = new TimeoutException("Database operation failed.");
        if (operation == "insert")
            flow.Session.Setup(value => value.InsertEventAsync(It.IsAny<PaymentEvent>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        else if (operation == "history")
            flow.Session.Setup(value => value.GetTransactionStatusEventsAsync(It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        else if (operation == "save_payment")
            flow.Session.Setup(value => value.SavePaymentAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
        else
            flow.Session.Setup(value => value.SaveEventAsync(It.IsAny<PaymentEvent>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);

        await Assert.ThrowsAsync<TimeoutException>(() => service.ReceiveAsync(JsonSerializer.SerializeToElement(Fields("Refund", 500))));

        flow.Session.Verify(value => value.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
        flow.Session.Verify(value => value.DisposeAsync(), Times.Once);
    }

    private static TransactionStatusService CreateService(PaymentEventServiceTestContext flow) =>
        new(flow.Service, TimeProvider.System, NullLogger<TransactionStatusService>.Instance);
}
