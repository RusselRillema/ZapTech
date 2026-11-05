using PaymentIntegration.Payments.Models;
using PaymentIntegration.Payments.Services;

namespace PaymentIntegration.Endpoints;

internal static class PaymentEndpoints
{
    public static void MapPaymentEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/payments", async (string appPaymentId, PaymentStatusService service, CancellationToken cancellationToken) =>
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
        endpoints.MapPost("/payments", async (NewPaymentRequest request, PaymentService service, CancellationToken cancellationToken) =>
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
    }
}
