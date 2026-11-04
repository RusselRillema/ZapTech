using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using PaymentIntegration.Payments;
using Xunit;

namespace PaymentIntegration.Tests;

public sealed class TransactionStatusTests
{
    private const string Route = "/notifications/transaction-status";
    // Published Acme specification example, independently of our signing helper.
    private const string Secret = "k7Qf2mZr9XpL4vNcA8sTgH3wYbE6dJ5u";
    private const string ExampleSignature = "KURAJlPrV3BWvWzsGBgr0EzClUSfu3itG62dM5Lqy/Y=";
    private const string ExampleBody = """
        {"transactionUUID":"7f3a9c12-4b8e-4d21-9f0a-6c5e2d81b4a7","transactionType":"Reversal","transactionDateTime":"2026-03-04T08:02:11.417","rrn":"MR43851259","transactionAmount":1999,"currencyCode":"ZAR","txResultCode":"00","isApproved":true}
        """;

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("AA==")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    public Task Unauthenticated_delivery_is_rejected_before_JSON_parsing_or_database_access(string? signature) =>
        WithApi(async (client, repository) =>
        {
            using var request = Request("{", signature);

            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            repository.VerifyNoOtherCalls();
        });

    [Theory]
    [InlineData(" ")]
    [InlineData("\n")]
    public Task Changing_only_body_whitespace_invalidates_the_published_signature(string suffix) =>
        WithApi(async (client, repository) =>
        {
            using var request = Request(ExampleBody + suffix, ExampleSignature);

            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            repository.VerifyNoOtherCalls();
        });

    [Fact]
    public Task Multiple_signature_headers_are_rejected() =>
        WithApi(async (client, repository) =>
        {
            using var request = Request(ExampleBody, ExampleSignature);
            request.Headers.TryAddWithoutValidation("x-signature", ExampleSignature);

            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            repository.VerifyNoOtherCalls();
        });

    [Fact]
    public Task The_published_signature_reaches_RRN_lookup_and_database_failure_is_not_acknowledged() =>
        WithApi(async (client, repository) =>
        {
            repository.Setup(value => value.BeginProcessingByRrnAsync("MR43851259", It.IsAny<Guid>(),
                    It.IsAny<CancellationToken>()))
                .ThrowsAsync(new TimeoutException("Private database diagnostics."));
            using var request = Request(ExampleBody, ExampleSignature);

            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.DoesNotContain("Private database diagnostics", await response.Content.ReadAsStringAsync());
            repository.Verify(value => value.BeginProcessingByRrnAsync("MR43851259", It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()), Times.Once);
            repository.VerifyNoOtherCalls();
        });

    [Fact]
    public Task Authenticated_malformed_JSON_is_rejected_without_database_access() =>
        WithApi(async (client, repository) =>
        {
            using var request = SignedRequest("{");

            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            repository.VerifyNoOtherCalls();
        });

    [Theory]
    [InlineData("transactionUUID", null)]
    [InlineData("transactionType", "Payment")]
    [InlineData("transactionType", "refund")]
    [InlineData("transactionDateTime", "not-a-timestamp")]
    [InlineData("rrn", "TOO-LONG-REFERENCE")]
    [InlineData("transactionAmount", "1999")]
    [InlineData("currencyCode", "zar")]
    [InlineData("txResultCode", null)]
    [InlineData("isApproved", "true")]
    public Task Invalid_authenticated_payload_is_rejected_before_database_access(string field, string? value) =>
        WithApi(async (client, repository) =>
        {
            var fields = JsonSerializer.Deserialize<Dictionary<string, object?>>(ExampleBody)!;
            fields[field] = value;
            using var request = SignedRequest(JsonSerializer.Serialize(fields));

            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("invalid_transaction_status", body.RootElement.GetProperty("title").GetString());
            repository.VerifyNoOtherCalls();
        });

    [Fact]
    public Task Authenticated_non_JSON_content_is_rejected() =>
        WithApi(async (client, repository) =>
        {
            using var request = SignedRequest(ExampleBody);
            request.Content = new StringContent(ExampleBody, Encoding.UTF8, "text/plain");

            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
            repository.VerifyNoOtherCalls();
        });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task Valid_callbacks_are_acknowledged_only_after_the_service_commits(bool knownPayment) =>
        WithApi(async (client, repository) =>
        {
            var committing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var session = new Mock<IPaymentProcessingSession>(MockBehavior.Strict);
            var receipts = new List<PaymentEvent>();
            Payment? saved = null;
            session.SetupGet(value => value.Payment).Returns(knownPayment
                ? new Payment(Guid.NewGuid(), "app-payment", "customer", Guid.NewGuid(), "shop", "MR43851259", "ZAR",
                    2000, "Approved", DateTimeOffset.UtcNow, 1999, 1999)
                : null);
            repository.Setup(value => value.BeginProcessingByRrnAsync("MR43851259", It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(session.Object);
            session.Setup(value => value.InsertEventAsync(It.IsAny<PaymentEvent>(), It.IsAny<CancellationToken>()))
                .Callback((PaymentEvent receipt, CancellationToken _) => receipts.Add(receipt)).Returns(Task.CompletedTask);
            session.Setup(value => value.GetPendingEventsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => receipts.ToArray());
            session.Setup(value => value.GetTransactionStatusEventsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => receipts.ToArray());
            session.Setup(value => value.SavePaymentAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()))
                .Callback((Payment payment, CancellationToken _) => saved = payment).Returns(Task.CompletedTask);
            session.Setup(value => value.SaveEventAsync(It.IsAny<PaymentEvent>(), It.IsAny<CancellationToken>()))
                .Callback((PaymentEvent receipt, CancellationToken _) => receipts[0] = receipt).Returns(Task.CompletedTask);
            session.Setup(value => value.CommitAsync(It.IsAny<CancellationToken>())).Returns(async () =>
            {
                committing.SetResult();
                await releaseCommit.Task;
            });
            session.Setup(value => value.DisposeAsync()).Returns(ValueTask.CompletedTask);
            using var request = Request(ExampleBody, ExampleSignature);

            var delivery = client.SendAsync(request);
            try
            {
                await committing.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(delivery.IsCompleted);
            }
            finally
            {
                releaseCommit.TrySetResult();
            }
            using var response = await delivery;

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            if (knownPayment)
            {
                Assert.Equal(0, saved!.RemainingAmount);
                Assert.Equal("Applied", Assert.Single(receipts).ProcessingState);
            }
            else
            {
                Assert.Null(saved);
                Assert.Null(Assert.Single(receipts).PaymentId);
                Assert.Equal("UnknownPayment", receipts[0].ProcessingComment);
            }
        });

    [Theory]
    [InlineData("save_event")]
    [InlineData("commit")]
    public Task Failed_persistence_is_not_acknowledged_and_disposes_the_session(string operation) =>
        WithApi(async (client, repository) =>
        {
            var session = new Mock<IPaymentProcessingSession>(MockBehavior.Strict);
            session.SetupGet(value => value.Payment).Returns((Payment?)null);
            repository.Setup(value => value.BeginProcessingByRrnAsync("MR43851259", It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(session.Object);
            session.Setup(value => value.InsertEventAsync(It.IsAny<PaymentEvent>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            session.Setup(value => value.SaveEventAsync(It.IsAny<PaymentEvent>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            session.Setup(value => value.CommitAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
            session.Setup(value => value.DisposeAsync()).Returns(ValueTask.CompletedTask);
            var failure = new TimeoutException("Private database diagnostics.");
            if (operation == "save_event")
                session.Setup(value => value.SaveEventAsync(It.IsAny<PaymentEvent>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
            else
                session.Setup(value => value.CommitAsync(It.IsAny<CancellationToken>())).ThrowsAsync(failure);
            using var request = Request(ExampleBody, ExampleSignature);

            using var response = await client.SendAsync(request);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.DoesNotContain("Private database diagnostics", await response.Content.ReadAsStringAsync());
            session.Verify(value => value.DisposeAsync(), Times.Once);
            if (operation == "save_event")
                session.Verify(value => value.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
        });

    private static HttpRequestMessage SignedRequest(string body) => Request(body,
        Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(body))));

    private static HttpRequestMessage Request(string body, string? signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Route)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (signature is not null)
            request.Headers.TryAddWithoutValidation("x-signature", signature);
        return request;
    }

    private static async Task WithApi(Func<HttpClient, Mock<IPaymentRepository>, Task> check)
    {
        var repository = new Mock<IPaymentRepository>(MockBehavior.Strict);
        await using var app = PaymentIntegrationApplication.Build([], builder =>
        {
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ACME_WEBHOOK_SECRET"] = Secret
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(repository.Object);
        });
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await app.StartAsync(startup.Token);
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
            await check(client, repository);
        }
        finally
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await app.StopAsync(shutdown.Token);
        }
    }
}
