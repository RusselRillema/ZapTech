using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics;
using Npgsql;
using PaymentIntegration.Acme;
using PaymentIntegration.Acme.Client;
using PaymentIntegration.Database.Repositories;
using PaymentIntegration.Endpoints;
using PaymentIntegration.Payments;
using PaymentIntegration.Payments.Processing;
using PaymentIntegration.Payments.Services;
using PaymentIntegration.Reports;

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
        builder.Services.AddSingleton(provider => AcmeWebhookSettings.Read(provider.GetRequiredService<IConfiguration>()));
        builder.Services.AddSingleton(provider =>
        {
            var configuration = provider.GetRequiredService<IConfiguration>();
            var connectionString = configuration["PAYMENT_DATABASE_CONNECTION_STRING"];
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException("Set the PAYMENT_DATABASE_CONNECTION_STRING environment variable.");
            var dataSource = new NpgsqlDataSourceBuilder(connectionString);
            dataSource.ConnectionStringBuilder.IncludeErrorDetail = false;
            dataSource.MapComposite<PaymentEventRow>("public.payment_event_result");
            dataSource.MapComposite<PaymentHistoryRow>("public.payment_history_result");
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
        builder.Services.AddSingleton<IPaymentReportRepository, PaymentReportRepository>();
        builder.Services.AddScoped<PaymentHistoryReconstructor>();
        builder.Services.AddScoped<PaymentReportService>();
        builder.Services.AddSingleton<PaymentReportPdfRenderer>();
        builder.Services.AddSingleton<FnbCardAcceptorNameFormatter>();
        builder.Services.AddSingleton<CardAcceptorNameFormatterFactory>();
        builder.Services.AddSingleton<AcmeRequestFactory>();
        builder.Services.AddScoped<PaymentResponseProcessor>();
        builder.Services.AddScoped<TransactionStatusProcessor>();
        builder.Services.AddScoped<PaymentEventProcessor>();
        builder.Services.AddScoped<PaymentEventService>();
        builder.Services.AddScoped<PaymentAdviceService>();
        builder.Services.AddScoped<TransactionStatusService>();
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
        app.MapHealthEndpoints();
        app.MapPaymentReports();
        app.MapPaymentEndpoints();
        app.MapNotificationEndpoints();
        return app;
    }
}
