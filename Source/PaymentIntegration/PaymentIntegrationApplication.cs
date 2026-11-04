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
        // Resolve external configuration only when a payment is submitted; health remains independent.
        builder.Services.AddSingleton(provider => AcmeSettings.Read(provider.GetRequiredService<IConfiguration>()));
        builder.Services.AddSingleton(provider =>
        {
            var configuration = provider.GetRequiredService<IConfiguration>();
            var connectionString = configuration["PAYMENT_DATABASE_CONNECTION_STRING"];
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException("Set the PAYMENT_DATABASE_CONNECTION_STRING environment variable.");
            var dataSource = new NpgsqlDataSourceBuilder(connectionString);
            dataSource.ConnectionStringBuilder.IncludeErrorDetail = false;
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
        builder.Services.AddSingleton<ICardAcceptorNameFormatter, FnbCardAcceptorNameFormatter>();
        builder.Services.AddSingleton<AcmeRequestFactory>();
        builder.Services.AddScoped<PaymentService>();
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
}
