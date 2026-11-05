using System.Net;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace AcmePay.Stub;

public sealed record CallbackDelivery(byte[] Body, bool IsAdvice, string Identifier);
public sealed record CallbackStatistics(int CompletedAttempts = 0, int Acknowledgements = 0, int? LastHttpStatusCode = null);

public sealed class CallbackDispatcher(
    IHttpClientFactory clients,
    StubSettings settings,
    ILogger<CallbackDispatcher> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<CallbackDelivery, CallbackStatistics> _statistics = new();
    private readonly Channel<CallbackDelivery> _deliveries = Channel.CreateBounded<CallbackDelivery>(
        new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    public bool TryEnqueue(CallbackDelivery delivery) => _deliveries.Writer.TryWrite(delivery);

    public CallbackStatistics GetStatistics(CallbackDelivery delivery) =>
        _statistics.TryGetValue(delivery, out var statistics) ? statistics : new();

    private void RecordAttempt(CallbackDelivery delivery, int? status)
    {
        var acknowledged = status == 200 ? 1 : 0;
        _statistics.AddOrUpdate(delivery, new CallbackStatistics(1, acknowledged, status), (_, previous) =>
            new(previous.CompletedAttempts + 1, previous.Acknowledgements + acknowledged, status));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var delivery in _deliveries.Reader.ReadAllAsync(stoppingToken))
                await SendAsync(delivery, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task SendAsync(CallbackDelivery delivery, CancellationToken cancellationToken)
    {
        var client = clients.CreateClient("callbacks");
        // One initial delivery followed by at most three retries, always using the same bytes.
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post,
                delivery.IsAdvice ? settings.AdviceUrl : settings.StatusWebhookUrl);
            request.Content = new ByteArrayContent(delivery.Body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            if (delivery.IsAdvice)
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(
                    Encoding.UTF8.GetBytes($"{settings.AdviceUsername}:{settings.AdvicePassword}")));
            else
                request.Headers.Add("x-signature", Convert.ToBase64String(
                    HMACSHA256.HashData(Encoding.UTF8.GetBytes(settings.WebhookSecret), delivery.Body)));

            try
            {
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                RecordAttempt(delivery, (int)response.StatusCode);
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    logger.LogInformation("Callback {Identifier} acknowledged on attempt {Attempt}", delivery.Identifier, attempt);
                    return;
                }
                logger.LogWarning("Callback {Identifier} received HTTP {Status} on attempt {Attempt}",
                    delivery.Identifier, (int)response.StatusCode, attempt);
            }
            catch (Exception exception) when (exception is HttpRequestException
                || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                RecordAttempt(delivery, null);
                // Log the type only: exception messages can contain callback URLs or other private data.
                logger.LogWarning("Callback {Identifier} failed with {Failure} on attempt {Attempt}",
                    delivery.Identifier, exception.GetType().Name, attempt);
            }
            if (attempt < 4)
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }
        logger.LogError("Callback {Identifier} exhausted its three retries", delivery.Identifier);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _deliveries.Writer.TryComplete();
        try
        {
            if (ExecuteTask is not null)
                await ExecuteTask.WaitAsync(cancellationToken);
        }
        finally
        {
            await base.StopAsync(cancellationToken);
        }
    }
}
