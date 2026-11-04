using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Logging;
using PaymentIntegration.Acme;
using Xunit;

namespace PaymentIntegration.Tests;

public sealed class AcmePaymentClientTests
{
    [Fact]
    public async Task Sends_the_PascalCase_contract_with_bearer_auth_without_logging_secrets()
    {
        var logger = new CapturingLogger<AcmePaymentClient>();
        var calls = 0;
        using var handler = new Handler(async message =>
        {
            calls++;
            Assert.Equal(HttpMethod.Post, message.Method);
            Assert.Equal("/api/payment", message.RequestUri!.AbsolutePath);
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme);
            Assert.Equal("sensitive-api-key", message.Headers.Authorization.Parameter);
            using var body = JsonDocument.Parse(await message.Content!.ReadAsStringAsync());
            Assert.Equal("App", body.RootElement.GetProperty("MessageHeader").GetProperty("Channel").GetString());
            var payment = body.RootElement.GetProperty("PaymentRequest");
            Assert.Equal("sensitive-card-token", payment.GetProperty("Card").GetProperty("Token").GetString());
            Assert.Equal(1999, payment.GetProperty("Amount").GetInt64());
            Assert.False(body.RootElement.TryGetProperty("paymentRequest", out _));
            return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("Gateway offline") };
        });
        using var http = new HttpClient(handler);
        using var limiter = Limiter();
        var client = Client(http, limiter, logger);

        await client.SubmitAsync(Request(), CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.DoesNotContain("sensitive-api-key", logger.Text);
        Assert.DoesNotContain("sensitive-card-token", logger.Text);
        Assert.Contains("payment-transaction", logger.Text);
    }

    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task Does_not_retry_when_gateway_rejects_or_fails(int statusCode)
    {
        var calls = 0;
        using var handler = new Handler(_ =>
        {
            calls++;
            var response = new HttpResponseMessage((HttpStatusCode)statusCode) { Content = new StringContent("{}") };
            response.Headers.TryAddWithoutValidation("Retry-After", "1");
            return Task.FromResult(response);
        });
        using var http = new HttpClient(handler);
        using var limiter = Limiter();

        await Client(http, limiter).SubmitAsync(Request(), CancellationToken.None);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Timeout_returns_an_unknown_outcome_and_releases_the_outbound_slot()
    {
        var calls = 0;
        using var handler = new Handler(_ =>
        {
            calls++;
            throw new TaskCanceledException("Simulated HTTP timeout.");
        });
        using var http = new HttpClient(handler);
        using var limiter = Limiter();

        var result = await Client(http, limiter).SubmitAsync(Request(), CancellationToken.None);

        Assert.Null(result.HttpStatusCode);
        Assert.Null(result.Payload);
        Assert.Contains("outcome unknown", result.Failure);
        Assert.Equal(1, calls);
        using var available = limiter.AttemptAcquire();
        Assert.True(available.IsAcquired);
    }

    [Fact]
    public async Task Full_outbound_queue_does_not_start_an_HTTP_request()
    {
        using var limiter = Limiter();
        using var occupied = limiter.AttemptAcquire();
        using var handler = new Handler(_ => throw new InvalidOperationException("HTTP must not be called."));
        using var http = new HttpClient(handler);

        var result = await Client(http, limiter).SubmitAsync(Request(), CancellationToken.None);

        Assert.Contains("capacity", result.Failure);
    }

    private static ConcurrencyLimiter Limiter() => new(new() { PermitLimit = 1, QueueLimit = 0 });
    private static AcmePaymentClient Client(HttpClient http, ConcurrencyLimiter limiter, ILogger<AcmePaymentClient>? logger = null) =>
        new(http, new(new Uri("http://localhost:5081"), "merchant", "sensitive-api-key"), limiter,
            logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AcmePaymentClient>.Instance);
    private static AcmeEnvelope Request() => new(new("2026-11-04T12:00:00.000", "message-id", "App"),
        new("merchant", "payment-transaction", "ORDER1234567", 1999, "ZAR", "CardNotPresent",
            new("sensitive-card-token"), "CIT", "shop", "5812", "ZAP*COFFEE             CAPE TOWN    WCZA", "customer@example.com"));

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<string> entries = [];
        public string Text => string.Join('\n', entries);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => entries.Add(formatter(state, exception));
    }
}
