using System.Globalization;

namespace PaymentIntegration.Reports;

public static class PaymentReportEndpoints
{
    public static void MapPaymentReports(this WebApplication app)
    {
        app.MapGet("/reports/payments/daily", async (HttpContext context) =>
        {
            var values = context.Request.Query["date"];
            if (values.Count != 1 || !DateOnly.TryParseExact(values[0], "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var date) || date == DateOnly.MaxValue)
            {
                return Results.Problem(statusCode: 400, title: "invalid_report_date",
                    detail: "Supply a date in yyyy-MM-dd format with a representable following day.");
            }

            context.Response.Headers.CacheControl = "no-store";
            try
            {
                var service = context.RequestServices.GetRequiredService<PaymentReportService>();
                var report = await service.GetDailyAsync(date, context.RequestAborted);
                var renderer = context.RequestServices.GetRequiredService<PaymentReportPdfRenderer>();
                return Results.File(renderer.Render(report), "application/pdf", $"payment-summary-{date:yyyy-MM-dd}.pdf");
            }
            catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("PaymentReportEndpoint")
                    .LogError(exception, "Operation {Operation} failed for report date {ReportDate}", "generate daily payment PDF", date);
                return Results.Problem(statusCode: 503, title: "Payment report could not be generated",
                    detail: "Retry the report request later.");
            }
        });
    }
}
