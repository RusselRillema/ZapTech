using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PaymentIntegration.Acme;
using PaymentIntegration.Payments;
using Xunit;

namespace PaymentIntegration.Tests;

public sealed class PaymentAdviceTests
{
    private static readonly Guid MerchantId = Guid.Parse("8b67bf88-bb8d-4eb5-adfc-0f5c97c7ed67");
    private static readonly Guid PaymentId = Guid.Parse("2e0e1b85-bf91-45a9-b038-282e00036919");
    private const string Route = "/notifications/payment-advice";

    [Fact]
    public async Task Acquirer_decline_overrides_gateway_success_and_reconsiders_a_pending_timeout()
    {
        var flow = new Flow();
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
        var flow = new Flow();

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
        var flow = new Flow();
        flow.InitialPayment = flow.InitialPayment! with { State = "Approved" };

        await flow.Service.ReceiveAsync(Advice(code: "00"));

        Assert.Null(flow.SavedPayment!.ApprovedAmount);
        Assert.Null(flow.SavedPayment.RemainingAmount);
        Assert.True(flow.SavedPayment.HasPendingEvents);
        Assert.Equal("Pending", Assert.Single(flow.Events).ProcessingState);
    }

    [Fact]
    public async Task A_later_payment_response_reconsiders_earlier_approval_advice_using_AuthAmount()
    {
        var flow = new Flow();
        await flow.Service.ReceiveAsync(Advice(code: "00"));
        var response = new PaymentEvent(Guid.NewGuid(), PaymentId, "PaymentResponse",
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
        Assert.Equal("Applied", flow.Events.Single(item => item.Source == "PaymentResponse").ProcessingState);
    }

    [Fact]
    public async Task Approval_advice_preserves_the_locked_partial_authorisation_and_remaining_value()
    {
        var flow = new Flow();
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
        var flow = new Flow();
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
        var flow = new Flow();

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
        var flow = new Flow();
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
        var flow = new Flow();

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
        var flow = new Flow { InitialPayment = null };
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
        var flow = new Flow();
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
        var flow = new Flow();
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
        var flow = new Flow();
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

    [Theory]
    [InlineData(null)]
    [InlineData("Bearer a-token")]
    [InlineData("Basic invalid-base64")]
    [InlineData("Basic YWNtZTppbmNvcnJlY3Q=")]
    public Task Authentication_failure_precedes_body_parsing_and_database_access(string? authorization) =>
        WithApi(async (client, flow) =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Route);
            if (authorization is not null)
                request.Headers.TryAddWithoutValidation("Authorization", authorization);
            request.Content = new StringContent("{", Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains(response.Headers.WwwAuthenticate, value => value.Scheme == "Basic");
            flow.Repository.VerifyNoOtherCalls();
        });

    [Fact]
    public Task Authenticated_malformed_JSON_is_rejected_without_database_processing() =>
        WithApi(async (client, flow) =>
        {
            using var request = Request(Advice());
            request.Content = new StringContent("{", Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            flow.Repository.VerifyNoOtherCalls();
        });

    [Theory]
    [InlineData("invalid-service")]
    [InlineData("cardnotpresent")]
    [InlineData(" CardNotPresent ")]
    public Task Invalid_payment_service_returns_400_without_database_processing(string paymentService) =>
        WithApi(async (client, flow) =>
        {
            var fields = AdviceFields();
            fields["PaymentService"] = paymentService;
            using var request = Request(JsonSerializer.SerializeToElement(fields));

            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("invalid_payment_advice", body.RootElement.GetProperty("title").GetString());
            flow.Repository.VerifyNoOtherCalls();
        });

    [Theory]
    [InlineData("CardNotPresent")]
    [InlineData("CardNotPresentRecurring")]
    [InlineData("AcmeEft")]
    [InlineData("GooglePay")]
    [InlineData("ApplePay")]
    public Task Valid_payment_services_reach_the_real_service_and_only_acknowledge_after_commit(string paymentService) =>
        WithApi(async (client, flow) =>
        {
            var committing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            flow.Session.Setup(value => value.CommitAsync(It.IsAny<CancellationToken>()))
                .Returns(async () =>
                {
                    committing.SetResult();
                    await releaseCommit.Task;
                });
            var fields = AdviceFields();
            fields["PaymentService"] = paymentService;
            using var request = Request(JsonSerializer.SerializeToElement(fields));
            var pending = client.SendAsync(request);
            try
            {
                await committing.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(pending.IsCompleted);
            }
            finally
            {
                releaseCommit.TrySetResult();
            }

            using var response = await pending;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("Declined", flow.SavedPayment!.State);
        });

    [Fact]
    public Task Unknown_payment_is_acknowledged_after_the_unlinked_audit_is_saved() =>
        WithApi(async (client, flow) =>
        {
            flow.InitialPayment = null;
            using var request = Request(Advice());

            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Null(Assert.Single(flow.Events).PaymentId);
            Assert.Equal("Pending", flow.Events[0].ProcessingState);
            Assert.Contains("commit", flow.Calls);
        });

    [Theory]
    [InlineData("lock")]
    [InlineData("commit")]
    public Task Failed_lock_or_commit_is_not_acknowledged_and_does_not_leak_diagnostics(string operation) =>
        WithApi(async (client, flow) =>
        {
            var failure = new TimeoutException("Private database diagnostics.");
            if (operation == "lock")
                flow.Repository.Setup(value => value.BeginProcessingAsync(It.IsAny<Guid?>(), It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>())).ThrowsAsync(failure);
            else
                flow.Session.Setup(value => value.CommitAsync(It.IsAny<CancellationToken>())).ThrowsAsync(failure);
            using var request = Request(Advice());

            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.DoesNotContain("Private database diagnostics", await response.Content.ReadAsStringAsync());
            if (operation == "commit")
                flow.Session.Verify(value => value.DisposeAsync(), Times.Once);
        });

    private static HttpRequestMessage Request(JsonElement advice) => new(HttpMethod.Post, Route)
    {
        Headers = { Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes("acme:callback-password"))) },
        Content = new StringContent(advice.GetRawText(), Encoding.UTF8, "application/json")
    };

    private static JsonElement Advice(string code = "05", string requestType = "Payment") =>
        JsonSerializer.SerializeToElement(AdviceFields(code, requestType));

    private static Dictionary<string, object?> AdviceFields(string code = "05", string requestType = "Payment") => new()
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

    private static async Task WithApi(Func<HttpClient, Flow, Task> check)
    {
        var flow = new Flow();
        await using var app = PaymentIntegrationApplication.Build([], builder =>
        {
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ACME_MERCHANT_ID"] = MerchantId.ToString(),
                ["ACME_ADVICE_USERNAME"] = "acme",
                ["ACME_ADVICE_PASSWORD"] = "callback-password"
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(flow.Repository.Object);
        });
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await app.StartAsync(startup.Token);
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
            await check(client, flow);
        }
        finally
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await app.StopAsync(shutdown.Token);
        }
    }

    private sealed class Flow
    {
        public Payment? InitialPayment { get; set; } = new(PaymentId, "app-payment", "customer", Guid.NewGuid(),
            "shop", "ORDER1234567", "ZAR", 2000, "Unresolved", DateTimeOffset.UtcNow);
        public Payment? SavedPayment { get; private set; }
        public Mock<IPaymentRepository> Repository { get; } = new(MockBehavior.Strict);
        public Mock<IPaymentProcessingSession> Session { get; } = new(MockBehavior.Strict);
        public List<PaymentEvent> Events { get; } = [];
        public List<string> Calls { get; } = [];
        public PaymentEventService EventService { get; }
        public PaymentAdviceService Service { get; }

        public Flow()
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
            EventService = new(Repository.Object, new PaymentResponseProcessor(),
                TimeProvider.System, NullLogger<PaymentEventService>.Instance);
            Service = new(new(MerchantId, "acme", "callback-password"), EventService,
                TimeProvider.System, NullLogger<PaymentAdviceService>.Instance);
        }
    }
}
