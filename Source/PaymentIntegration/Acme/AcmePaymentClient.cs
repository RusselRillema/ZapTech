using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.RateLimiting;

namespace PaymentIntegration.Acme;

public sealed class AcmePaymentClient(HttpClient httpClient, AcmeSettings settings,
    ConcurrencyLimiter limiter, ILogger<AcmePaymentClient> logger) : IAcmePaymentClient
{
    // Acme's wire format uses PascalCase and is independent of the public API's JSON options.
    private static readonly JsonSerializerOptions WireJson = new();

    public async Task<AcmeReply> SubmitAsync(AcmeEnvelope request, CancellationToken cancellationToken)
    {
        var paymentId = request.PaymentRequest.TransactionID;
        try
        {
            using var lease = await limiter.AcquireAsync(1, cancellationToken);
            if (!lease.IsAcquired)
            {
                logger.LogWarning("Acme capacity unavailable for payment {PaymentId}", paymentId);
                return new(null, null, "Acme submission capacity unavailable.");
            }

            using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(settings.BaseUrl, "/api/payment"))
            {
                Content = JsonContent.Create(request, options: WireJson)
            };
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
            logger.LogInformation("Submitting payment {PaymentId} to Acme", paymentId);
            // No automatic retry: an interrupted request may already have reached the acquirer.
            using var response = await httpClient.SendAsync(message, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            JsonElement? payload = null;
            if (body.Length != 0)
            {
                try
                {
                    using var document = JsonDocument.Parse(body);
                    payload = document.RootElement.Clone();
                }
                catch (JsonException)
                {
                    // Preserve an invalid JSON body losslessly as a JSON string for audit.
                    payload = JsonSerializer.SerializeToElement(body);
                }
            }
            logger.LogInformation("Acme returned HTTP {HttpStatusCode} for payment {PaymentId}",
                (int)response.StatusCode, paymentId);
            return new((short)response.StatusCode, payload);
        }
        catch (OperationCanceledException exception)
        {
            logger.LogWarning(exception, "Acme operation {Operation} timed out or was cancelled for payment {PaymentId}",
                "submit", paymentId);
            return new(null, null, "Acme request timed out or was cancelled; outcome unknown.");
        }
        catch (HttpRequestException exception)
        {
            logger.LogError(exception, "Acme operation {Operation} failed for payment {PaymentId}", "submit", paymentId);
            return new(null, null, "Acme connection failed; outcome unknown.");
        }
    }
}
