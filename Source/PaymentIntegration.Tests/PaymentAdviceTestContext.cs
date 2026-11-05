using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PaymentIntegration.Payments;
using PaymentIntegration.Payments.Models;
using PaymentIntegration.Payments.Processing;
using PaymentIntegration.Payments.Services;

namespace PaymentIntegration.Tests;

internal sealed class PaymentAdviceTestContext
{
    public static readonly Guid MerchantId = Guid.Parse("8b67bf88-bb8d-4eb5-adfc-0f5c97c7ed67");
    public static readonly Guid PaymentId = Guid.Parse("2e0e1b85-bf91-45a9-b038-282e00036919");

    public static JsonElement Advice(string code = "05", string requestType = "Payment") =>
        JsonSerializer.SerializeToElement(AdviceFields(code, requestType));

    public static Dictionary<string, object?> AdviceFields(string code = "05", string requestType = "Payment") => new()
    {
        ["RequestType"] = requestType,
        ["MerchantID"] = MerchantId.ToString(),
        ["TransactionID"] = PaymentId.ToString(),
        ["OrderNumber"] = "ORDER1234567",
        ["RetrievalReferenceNumber"] = "ORDER1234567",
        ["SystemsTraceAuditNumber"] = "7543",
        ["Amount"] = 2000,
        ["CurrencyCode"] = "ZAR",
        ["PaymentService"] = "CardNotPresent",
        ["TransactionStatus"] = "Success",
        ["ResponseSource"] = "Acquirer",
        ["ResponseCode"] = code,
        ["CommProtocol"] = "REST",
        ["FutureAcmeField"] = "preserved"
    };

    public Payment? InitialPayment { get; set; } = new(PaymentId, "app-payment", "customer", Guid.NewGuid(),
        "shop", "ORDER1234567", "ZAR", 2000, "Unresolved", DateTimeOffset.UtcNow);
    public Payment? SavedPayment { get; private set; }
    public Mock<IPaymentRepository> Repository { get; } = new(MockBehavior.Strict);
    public Mock<IPaymentProcessingSession> Session { get; } = new(MockBehavior.Strict);
    public List<PaymentEvent> Events { get; } = [];
    public List<string> Calls { get; } = [];
    public PaymentEventService EventService { get; }
    public PaymentAdviceService Service { get; }

    public PaymentAdviceTestContext()
    {
        Repository.Setup(value => value.BeginProcessingAsync(It.IsAny<Guid?>(), It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .Callback(() => Calls.Add("lock")).ReturnsAsync(Session.Object);
        Session.SetupGet(value => value.Payment).Returns(() => SavedPayment ?? InitialPayment);
        Session.Setup(value => value.InsertEventAsync(It.IsAny<PaymentEvent>(), It.IsAny<CancellationToken>()))
            .Callback((PaymentEvent receipt, CancellationToken _) =>
            {
                Calls.Add("insert");
                Events.Add(receipt);
            }).Returns(Task.CompletedTask);
        Session.Setup(value => value.GetPendingEventsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Calls.Add("read_pending");
                return Events.Where(item => item.ProcessingState == "Pending").ToArray();
            });
        Session.Setup(value => value.SavePaymentAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()))
            .Callback((Payment payment, CancellationToken _) =>
            {
                Calls.Add("save_payment");
                SavedPayment = payment;
            }).Returns(Task.CompletedTask);
        Session.Setup(value => value.SaveEventAsync(It.IsAny<PaymentEvent>(), It.IsAny<CancellationToken>()))
            .Callback((PaymentEvent receipt, CancellationToken _) =>
            {
                Calls.Add("save_event");
                Events[Events.FindIndex(item => item.PaymentEventId == receipt.PaymentEventId)] = receipt;
            }).Returns(Task.CompletedTask);
        Session.Setup(value => value.CommitAsync(It.IsAny<CancellationToken>()))
            .Callback(() => Calls.Add("commit")).Returns(Task.CompletedTask);
        Session.Setup(value => value.DisposeAsync()).Returns(ValueTask.CompletedTask);
        var statusProcessor = new TransactionStatusProcessor();
        var responseProcessor = new PaymentResponseProcessor(statusProcessor);
        EventService = new(Repository.Object, responseProcessor, new PaymentEventProcessor(responseProcessor, statusProcessor),
            TimeProvider.System, NullLogger<PaymentEventService>.Instance);
        Service = new(new(MerchantId, "acme", "callback-password"), EventService,
            TimeProvider.System, NullLogger<PaymentAdviceService>.Instance);
    }
}
