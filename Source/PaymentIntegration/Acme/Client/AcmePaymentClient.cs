using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.RateLimiting;
using PaymentIntegration.Acme.Contracts;

namespace PaymentIntegration.Acme.Client;

public sealed class AcmePaymentClient(HttpClient httpClient, AcmeSettings settings,
    ConcurrencyLimiter limiter, ILogger<AcmePaymentClient> logger) : IAcmePaymentClient
{
    // Acme's wire format uses PascalCase and is independent of the public API's JSON options.
    private static readonly JsonSerializerOptions WireJson = new();

    public async Task<AcmeReply> SubmitAsync(AcmeEnvelope request, CancellationToken cancellationToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(settings.BaseUrl, "/api/payment"))
        {
            Content = JsonContent.Create(request, options: WireJson)
        };
        return await SendAsync(message, request.PaymentRequest.TransactionID, "submit", cancellationToken);
    }

    public async Task<AcmeReply> QueryAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        var path = $"/apirttq/query/Merchant/{Uri.EscapeDataString(settings.MerchantId)}?TransactionID={paymentId:D}";
        using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(settings.BaseUrl, path));
        return await SendAsync(message, paymentId.ToString(), "query", cancellationToken);
    }

    private async Task<AcmeReply> SendAsync(HttpRequestMessage message, string paymentId, string operation,
        CancellationToken cancellationToken)
    {
        var requestAttempted = false;
        try
        {
            using var lease = await limiter.AcquireAsync(1, cancellationToken);
            if (!lease.IsAcquired)
            {
                logger.LogWarning("Acme capacity unavailable for operation {Operation}, payment {PaymentId}", operation, paymentId);
                return new(null, null, "Acme outbound capacity unavailable.", RequestAttempted: false);
            }

            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
            logger.LogInformation("Acme operation {Operation} for payment {PaymentId}", operation, paymentId);
            // The service owns retries so every response is audited before waiting.
            cancellationToken.ThrowIfCancellationRequested();
            requestAttempted = true;
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
            logger.LogInformation("Acme operation {Operation} returned HTTP {HttpStatusCode} for payment {PaymentId}",
                operation, (int)response.StatusCode, paymentId);
            // E29 retries apply to payment submission only, never to RTTQ recovery.
            var retryDelay = operation == "submit" ? GetRetryDelay(response, payload, paymentId) : null;
            return new((short)response.StatusCode, payload, RetryDelay: retryDelay);
        }
        catch (OperationCanceledException exception)
        {
            logger.LogWarning(exception, "Acme operation {Operation} timed out or was cancelled for payment {PaymentId}",
                operation, paymentId);
            return new(null, null, "Acme request timed out or was cancelled; outcome unknown.", RequestAttempted: requestAttempted);
        }
        catch (HttpRequestException exception)
        {
            logger.LogError(exception, "Acme operation {Operation} failed for payment {PaymentId}", operation, paymentId);
            return new(null, null, "Acme connection failed; outcome unknown.", RequestAttempted: requestAttempted);
        }
    }

    private TimeSpan? GetRetryDelay(
        HttpResponseMessage response,
        JsonElement? payload,
        string paymentId)
    {
        if ((int)response.StatusCode != 429
            || payload is not { ValueKind: JsonValueKind.Object } body
            || !body.TryGetProperty("PaymentResponse", out var paymentResponse)
            || paymentResponse.ValueKind != JsonValueKind.Object
            || !paymentResponse.TryGetProperty("ResponseDetail", out var detail)
            || detail.ValueKind != JsonValueKind.Object
            || !detail.TryGetProperty("Code", out var code)
            || code.ValueKind != JsonValueKind.String
            || code.GetString() != "E29")
        {
            return null;
        }

        var retryAfter = response.Headers.RetryAfter?.Delta;

        if (retryAfter is null
            || retryAfter.Value < TimeSpan.Zero
            || retryAfter.Value.Ticks > TimeSpan.MaxValue.Ticks / 2)
        {
            logger.LogWarning(
                "Acme E29 has no usable Retry-After seconds for payment {PaymentId}",
                paymentId);
            return null;
        }

        var bufferedTicks = Math.Max(
            TimeSpan.FromSeconds(3).Ticks,
            retryAfter.Value.Ticks * 2);

        return TimeSpan.FromTicks(bufferedTicks);
    }
}
