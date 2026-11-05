using System.Text.Json;
using Microsoft.Extensions.Logging;
using Moq;
using PaymentIntegration.Payments;
using PaymentIntegration.Payments.Models;
using PaymentIntegration.Payments.Processing;
using PaymentIntegration.Payments.Services;
using static PaymentIntegration.Tests.PaymentEventTestData;

namespace PaymentIntegration.Tests;

internal sealed class PaymentEventServiceTestContext
{
    public Payment? Payment { get; set; } = new(PaymentId, "app-payment", "customer", Guid.NewGuid(),
        "shop", Rrn, "ZAR", 2000, "Approved", DateTimeOffset.UtcNow, 1250, 1250);
    public List<PaymentEvent> Receipts { get; } = [];
    public List<string> Calls { get; } = [];
    public Mock<IPaymentRepository> Repository { get; } = new(MockBehavior.Strict);
    public Mock<IPaymentProcessingSession> Session { get; } = new(MockBehavior.Strict);
    public Mock<ILogger<PaymentEventService>> Logger { get; } = new();
    public PaymentEventService Service { get; }

    public PaymentEventServiceTestContext()
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
        var statusProcessor = new TransactionStatusProcessor();
        var responseProcessor = new PaymentResponseProcessor(statusProcessor);
        Service = new(Repository.Object, responseProcessor, new PaymentEventProcessor(responseProcessor, statusProcessor),
            TimeProvider.System, Logger.Object);
    }

    public Task<Payment?> Receive(string type, long amount, string identifier = "event-1") =>
        Receive(JsonSerializer.SerializeToElement(Fields(type, amount, identifier)));

    public Task<Payment?> Receive(JsonElement payload)
    {
        var receipt = new PaymentEvent(Guid.NewGuid(), null, "TransactionStatus",
            payload.Clone(), null, DateTimeOffset.UtcNow);
        return Service.ProcessStatusAsync(payload.GetProperty("rrn").GetString()!, receipt.PaymentEventId, receipt);
    }

    public Task<Payment?> Query(Dictionary<string, object?> response)
    {
        var id = Guid.NewGuid();
        return Service.ProcessAsync(PaymentId, id, new(id, PaymentId, "TransactionQuery",
            JsonSerializer.SerializeToElement(new { PaymentResponse = response }), 200, DateTimeOffset.UtcNow));
    }

    public Task<Payment?> BankApproval(string source)
    {
        var id = Guid.NewGuid();
        return Service.ProcessAsync(PaymentId, id, new(id, PaymentId, source,
            JsonSerializer.SerializeToElement(new
            {
                PaymentResponse = new
                {
                    TransactionID = PaymentId, OrderNumber = Rrn, TransactionStatus = "Success",
                    ResponseDetail = new { Source = "Acquirer", Code = "00" }, AuthAmount = 1250
                }
            }), 200, DateTimeOffset.UtcNow));
    }

    public Task<Payment?> Query(string state, string code = "00")
    {
        var response = QueryFields(state);
        response["ResponseDetail"] = new { Source = "Acquirer", Code = code };
        return Query(response);
    }
}
