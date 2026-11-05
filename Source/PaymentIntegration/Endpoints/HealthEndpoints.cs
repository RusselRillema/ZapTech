namespace PaymentIntegration.Endpoints;

internal static class HealthEndpoints
{
    public static void MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
    }
}
