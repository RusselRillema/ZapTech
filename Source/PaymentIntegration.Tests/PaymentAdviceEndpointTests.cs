using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using static PaymentIntegration.Tests.PaymentAdviceTestContext;

namespace PaymentIntegration.Tests;

public sealed class PaymentAdviceEndpointTests
{
    private const string Route = "/notifications/payment-advice";

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

    private static async Task WithApi(Func<HttpClient, PaymentAdviceTestContext, Task> check)
    {
        var flow = new PaymentAdviceTestContext();
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
}
