using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AcmePay.Stub;

public static class StubApplication
{
    private static readonly JsonSerializerOptions ControlJson = new(Wire.Json) { PropertyNameCaseInsensitive = true };

    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        configure?.Invoke(builder);
        var settings = StubSettings.Read(builder.Configuration);
        builder.WebHost.ConfigureKestrel(options => options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(60));
        builder.Services.AddSingleton(settings);
        builder.Services.TryAddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<PaymentStore>();
        builder.Services.AddSingleton<CallbackDispatcher>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<CallbackDispatcher>());
        builder.Services.AddHttpClient("callbacks", client => client.Timeout = TimeSpan.FromSeconds(10))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            });
        builder.Services.AddSingleton(_ => new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = 20,
            Window = TimeSpan.FromSeconds(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));

        var app = builder.Build();
        var clock = app.Services.GetRequiredService<TimeProvider>();
        var gateway = app.MapGroup("");
        gateway.AddEndpointFilter(new BearerFilter(settings, clock));
        gateway.AddEndpointFilter(new GatewayLimit(app.Services.GetRequiredService<FixedWindowRateLimiter>(), clock));
        gateway.MapPost("/api/payment", SubmitAsync);
        gateway.MapGet("/apirttq/query/Merchant/{merchantId}",
            (string merchantId, HttpContext context, PaymentStore store) =>
            {
                var transactionId = context.Request.Query["TransactionID"].ToString();
                var orderNumber = context.Request.Query["OrderNumber"].ToString();
                if (string.IsNullOrEmpty(transactionId) == string.IsNullOrEmpty(orderNumber))
                    return Wire.Error(400, "E01", "Pass either TransactionID or OrderNumber.", clock);
                return store.Query(merchantId, transactionId, orderNumber);
            });

        if (settings.EnableControls)
        {
            var controls = app.MapGroup("/__stub/transactions");
            controls.AddEndpointFilter(new BearerFilter(settings, clock));
            controls.MapPost("/{transactionId}/reversal", (string transactionId, HttpContext context, PaymentStore store) =>
                SendStatusAsync(transactionId, "Reversal", context, store, clock));
            controls.MapPost("/{transactionId}/refund", (string transactionId, HttpContext context, PaymentStore store) =>
                SendStatusAsync(transactionId, "Refund", context, store, clock));
        }
        app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
        return app;
    }

    private static async Task<IResult> SubmitAsync(HttpContext context, PaymentStore store,
        StubSettings settings, TimeProvider clock)
    {
        PaymentEnvelope? envelope;
        try
        {
            envelope = await JsonSerializer.DeserializeAsync<PaymentEnvelope>(context.Request.Body, Wire.Json, context.RequestAborted);
        }
        catch (JsonException)
        {
            return Wire.Error(400, "E01", "The body could not be parsed.", clock);
        }
        var error = PaymentValidation.Error(envelope, settings);
        return error is null ? store.Submit(envelope!) : Wire.Error(400, "E01", error, clock);
    }

    private static async Task<IResult> SendStatusAsync(string transactionId, string type,
        HttpContext context, PaymentStore store, TimeProvider clock)
    {
        try
        {
            var request = await JsonSerializer.DeserializeAsync<StatusRequest>(context.Request.Body, ControlJson, context.RequestAborted);
            return request?.Amount is not null
                ? store.SendStatus(transactionId, type, request.Amount.Value)
                : Wire.Error(400, "E01", "Amount is required in minor units.", clock);
        }
        catch (JsonException)
        {
            return Wire.Error(400, "E01", "The body could not be parsed.", clock);
        }
        catch (NotImplementedException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status501NotImplemented);
        }
    }

    private sealed class BearerFilter(StubSettings settings, TimeProvider clock) : IEndpointFilter
    {
        public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            if (!AuthenticationHeaderValue.TryParse(context.HttpContext.Request.Headers.Authorization, out var header)
                || !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
                || header.Parameter is null || !CryptographicOperations.FixedTimeEquals(
                    SHA256.HashData(Encoding.UTF8.GetBytes(header.Parameter)),
                    SHA256.HashData(Encoding.UTF8.GetBytes(settings.ApiKey))))
            {
                context.HttpContext.Response.Headers.WWWAuthenticate = "Bearer";
                return ValueTask.FromResult<object?>(Wire.Error(401, "E02", "The API key is missing or not valid.", clock));
            }
            return next(context);
        }
    }

    private sealed class GatewayLimit(FixedWindowRateLimiter limiter, TimeProvider clock) : IEndpointFilter
    {
        public ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            using var lease = limiter.AttemptAcquire();
            if (!lease.IsAcquired)
            {
                context.HttpContext.Response.Headers.RetryAfter = "1";
                return ValueTask.FromResult<object?>(Wire.Error(429, "E29", "You have exceeded 20 requests per second.", clock));
            }
            return next(context);
        }
    }
}
