using System.Net;
using System.Threading.RateLimiting;
using Xunit;

namespace PaymentIntegration.Tests;

public sealed partial class AcmePaymentClientTests
{
    [Fact]
    public async Task Query_sends_one_authenticated_GET_for_the_transaction_without_payment_details()
    {
        var paymentId = Guid.NewGuid();
        var calls = 0;
        var logger = new CapturingLogger<PaymentIntegration.Acme.AcmePaymentClient>();
        using var handler = new Handler(message =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, message.Method);
            Assert.Equal("/apirttq/query/Merchant/merchant", message.RequestUri!.AbsolutePath);
            Assert.Equal($"?TransactionID={paymentId:D}", message.RequestUri.Query);
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme);
            Assert.Equal("sensitive-api-key", message.Headers.Authorization.Parameter);
            Assert.Null(message.Content);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        using var http = new HttpClient(handler);
        using var limiter = Limiter();

        await Client(http, limiter, logger).QueryAsync(paymentId, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.Contains(paymentId.ToString(), logger.Text);
        Assert.Contains("query", logger.Text);
        Assert.DoesNotContain("sensitive-api-key", logger.Text);
    }

    [Fact]
    public async Task Query_rate_limit_does_not_enable_payment_retry_policy()
    {
        var calls = 0;
        using var handler = new Handler(_ =>
        {
            calls++;
            var response = RateLimitedResponse();
            response.Headers.TryAddWithoutValidation("Retry-After", "1");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var limiter = Limiter();

        var reply = await Client(http, limiter).QueryAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(reply.RetryDelay);
        Assert.Equal(1, calls);
        using var available = limiter.AttemptAcquire();
        Assert.True(available.IsAcquired);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Query_transport_failure_returns_unknown_and_releases_capacity(bool cancelled)
    {
        using var handler = new Handler(_ => cancelled
            ? throw new TaskCanceledException("Timed out.")
            : throw new HttpRequestException("Connection failed."));
        using var http = new HttpClient(handler);
        using var limiter = Limiter();

        var reply = await Client(http, limiter).QueryAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.Null(reply.HttpStatusCode);
        Assert.Null(reply.Payload);
        Assert.True(reply.RequestAttempted);
        Assert.Contains("outcome unknown", reply.Failure);
        using var available = limiter.AttemptAcquire();
        Assert.True(available.IsAcquired);
    }

    [Fact]
    public async Task Query_cancelled_while_waiting_for_capacity_does_not_send_HTTP_or_leak_the_queue_slot()
    {
        using var limiter = new ConcurrencyLimiter(new() { PermitLimit = 1, QueueLimit = 1 });
        using var occupied = limiter.AttemptAcquire();
        using var handler = new Handler(_ => throw new InvalidOperationException("HTTP must not be sent."));
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();

        var query = Client(http, limiter).QueryAsync(Guid.NewGuid(), cancellation.Token);
        Assert.False(query.IsCompleted);
        cancellation.Cancel();
        var reply = await query.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(reply.RequestAttempted);
        Assert.Null(reply.HttpStatusCode);
        occupied.Dispose();
        using var available = limiter.AttemptAcquire();
        Assert.True(available.IsAcquired);
    }
}
