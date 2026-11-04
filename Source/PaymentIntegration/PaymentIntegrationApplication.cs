using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics;
using Npgsql;
using PaymentIntegration.Acme;
using PaymentIntegration.Payments;

namespace PaymentIntegration;

public static class PaymentIntegrationApplication
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton(TimeProvider.System);
        // Resolve each operation's external configuration lazily; health remains independent.
        builder.Services.AddSingleton(provider => AcmeSettings.Read(provider.GetRequiredService<IConfiguration>()));
        builder.Services.AddSingleton(provider => AcmeAdviceSettings.Read(provider.GetRequiredService<IConfiguration>()));
        builder.Services.AddSingleton(provider =>
        {
            var configuration = provider.GetRequiredService<IConfiguration>();
            var connectionString = configuration["PAYMENT_DATABASE_CONNECTION_STRING"];
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException("Set the PAYMENT_DATABASE_CONNECTION_STRING environment variable.");
            var dataSource = new NpgsqlDataSourceBuilder(connectionString);
            dataSource.ConnectionStringBuilder.IncludeErrorDetail = false;
            dataSource.MapComposite<PaymentEventRow>("public.payment_event_result");
            return dataSource.Build();
        });
        builder.Services.AddSingleton(_ => new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = 20, QueueLimit = 40, QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        }));
        builder.Services.AddHttpClient<IAcmePaymentClient, AcmePaymentClient>(client => client.Timeout = TimeSpan.FromSeconds(35))
            .RedactLoggedHeaders(["Authorization"])
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                MaxConnectionsPerServer = 20,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                AllowAutoRedirect = false
            });
        builder.Services.AddSingleton<IPaymentRepository, PaymentRepository>();
        builder.Services.AddSingleton<FnbCardAcceptorNameFormatter>();
        builder.Services.AddSingleton<CardAcceptorNameFormatterFactory>();
        builder.Services.AddSingleton<AcmeRequestFactory>();
        builder.Services.AddScoped<PaymentResponseProcessor>();
        builder.Services.AddScoped<PaymentEventService>();
        builder.Services.AddScoped<PaymentAdviceService>();
        builder.Services.AddScoped<PaymentService>();
        builder.Services.AddScoped<PaymentStatusService>();
        configure?.Invoke(builder);

        var app = builder.Build();
        app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
        {
            var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
            context.Response.StatusCode = exception is BadHttpRequestException badRequest ? badRequest.StatusCode : 503;
            await Results.Problem(statusCode: context.Response.StatusCode,
                title: "Payment request could not be completed",
                detail: "Retry using the same AppPaymentId.").ExecuteAsync(context);
        }));
        app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
        app.MapGet("/payments", async (string appPaymentId, PaymentStatusService service, CancellationToken cancellationToken) =>
        {
            try
            {
                return Results.Ok(await service.GetAsync(appPaymentId, cancellationToken));
            }
            catch (PaymentRequestException exception)
            {
                return Results.Problem(statusCode: exception.StatusCode, title: exception.Code, detail: exception.Message);
            }
        });
        app.MapPost("/notifications/payment-advice", ReceiveAdviceAsync);
        app.MapPost("/payments", async (NewPaymentRequest request, PaymentService service, CancellationToken cancellationToken) =>
        {
            try
            {
                var result = await service.SubmitAsync(request, cancellationToken);
                return Results.Json(result, statusCode: result.IsFullyReconciled ? 200 : 202);
            }
            catch (PaymentRequestException exception)
            {
                return Results.Problem(statusCode: exception.StatusCode, title: exception.Code, detail: exception.Message);
            }
        });
        return app;
    }

    private static async Task<IResult> ReceiveAdviceAsync(HttpContext context, AcmeAdviceSettings settings)
    {
        // Authenticate before parsing the body or resolving database dependencies.
        if (!settings.Authenticates(context.Request.Headers.Authorization.ToString()))
        {
            context.Response.Headers.WWWAuthenticate = "Basic realm=\"Acme Payment Advice\", charset=\"UTF-8\"";
            return Results.Unauthorized();
        }

        string? transactionId = null;
        try
        {
            if (!context.Request.HasJsonContentType())
                return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
            using var body = await JsonDocument.ParseAsync(context.Request.Body,
                cancellationToken: context.RequestAborted);
            if (body.RootElement.ValueKind == JsonValueKind.Object
                && body.RootElement.TryGetProperty("TransactionID", out var id) && id.ValueKind == JsonValueKind.String)
                transactionId = id.GetString();
            var service = context.RequestServices.GetRequiredService<PaymentAdviceService>();
            await service.ReceiveAsync(body.RootElement);
            return Results.Ok();
        }
        catch (PaymentRequestException exception)
        {
            return Results.Problem(statusCode: exception.StatusCode, title: exception.Code, detail: exception.Message);
        }
        catch (JsonException)
        {
            return Results.Problem(statusCode: 400, title: "Invalid Payment Advice JSON");
        }
        catch (BadHttpRequestException exception)
        {
            return Results.Problem(statusCode: exception.StatusCode, title: "Invalid Payment Advice request");
        }
        catch (Exception exception)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("PaymentAdviceEndpoint")
                .LogError(exception, "Operation {Operation} failed for Acme transaction {AcmeTransactionId}",
                    "receive Payment Advice", transactionId);
            return Results.Problem(statusCode: 503, title: "Payment Advice could not be recorded",
                detail: "The notification was not acknowledged. Retry the delivery.");
        }
    }
}
