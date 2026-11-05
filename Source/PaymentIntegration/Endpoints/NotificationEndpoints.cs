using System.Text.Json;
using PaymentIntegration.Acme;
using PaymentIntegration.Payments.Models;
using PaymentIntegration.Payments.Services;

namespace PaymentIntegration.Endpoints;

internal static class NotificationEndpoints
{
    public static void MapNotificationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/notifications/payment-advice", ReceiveAdviceAsync);
        endpoints.MapPost("/notifications/transaction-status", (Delegate)ReceiveStatusAsync);
    }

    private static async Task<IResult> ReceiveStatusAsync(HttpContext context)
    {
        string? rrn = null;
        string? acmeEventId = null;
        try
        {
            var settings = context.RequestServices.GetRequiredService<AcmeWebhookSettings>();
            var signatures = context.Request.Headers["x-signature"];
            if (signatures.Count != 1 || string.IsNullOrWhiteSpace(signatures[0]))
                return Results.Unauthorized();

            // Verify the exact delivered bytes, before parsing JSON or resolving the DAL.
            using var bytes = new MemoryStream();
            await context.Request.Body.CopyToAsync(bytes, context.RequestAborted);
            if (!settings.Authenticates(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length)), signatures[0]!))
                return Results.Unauthorized();
            if (!context.Request.HasJsonContentType())
                return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);

            using var body = JsonDocument.Parse(bytes.GetBuffer().AsMemory(0, checked((int)bytes.Length)));
            if (body.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (body.RootElement.TryGetProperty("rrn", out var reference) && reference.ValueKind == JsonValueKind.String)
                    rrn = reference.GetString();
                if (body.RootElement.TryGetProperty("transactionUUID", out var identifier) && identifier.ValueKind == JsonValueKind.String)
                    acmeEventId = identifier.GetString();
            }
            await context.RequestServices.GetRequiredService<TransactionStatusService>().ReceiveAsync(body.RootElement);
            return Results.Ok();
        }
        catch (PaymentRequestException exception)
        {
            return Results.Problem(statusCode: exception.StatusCode, title: exception.Code, detail: exception.Message);
        }
        catch (JsonException)
        {
            return Results.Problem(statusCode: 400, title: "Invalid Transaction Status JSON");
        }
        catch (BadHttpRequestException exception)
        {
            return Results.Problem(statusCode: exception.StatusCode, title: "Invalid Transaction Status request");
        }
        catch (Exception exception)
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("TransactionStatusEndpoint")
                .LogError(exception, "Operation {Operation} failed for RRN {RetrievalReferenceNumber}, Acme event {AcmeEventId}",
                    "receive Transaction Status", rrn, acmeEventId);
            return Results.Problem(statusCode: 503, title: "Transaction Status could not be recorded",
                detail: "The notification was not acknowledged. Retry the delivery.");
        }
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
