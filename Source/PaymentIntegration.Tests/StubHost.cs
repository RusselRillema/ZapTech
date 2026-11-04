using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AcmePay.Stub;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace PaymentIntegration.Tests;

internal sealed record CapturedCallback(string Path, byte[] Body, string Authorization, string Signature);

internal sealed class TestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 3, 1, 11, 56, 36, 533, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}

internal sealed class StubHost : IAsyncDisposable
{
    public const string ApiKey = "sk_test_local_application_tests";
    public const string MerchantId = "8B67BF88-BB8D-4EB5-ADFC-0F5C97C7ED67";
    public const string Username = "local-advice";
    public const string Password = "local-advice-password";
    public const string Secret = "local-webhook-secret";

    private readonly WebApplication _receiver;
    private WebApplication? _stub;
    private readonly Channel<CapturedCallback> _callbacks = Channel.CreateUnbounded<CapturedCallback>();
    public ConcurrentQueue<CapturedCallback> Deliveries { get; } = new();
    public ConcurrentQueue<int> CallbackResponses { get; } = new();
    public TestClock Clock { get; } = new();
    public HttpClient Client { get; private set; } = null!;

    private StubHost()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _receiver = builder.Build();
        _receiver.MapPost("/{kind}", async context =>
        {
            using var stream = new MemoryStream();
            await context.Request.Body.CopyToAsync(stream, context.RequestAborted);
            var captured = new CapturedCallback(context.Request.Path.Value!, stream.ToArray(),
                context.Request.Headers.Authorization.ToString(), context.Request.Headers["x-signature"].ToString());
            context.Response.StatusCode = CallbackResponses.TryDequeue(out var status) ? status : 200;
            Deliveries.Enqueue(captured);
            _callbacks.Writer.TryWrite(captured);
        });
    }

    public static async Task<StubHost> StartAsync(bool enableControls = true)
    {
        var host = new StubHost();
        try
        {
            await host._receiver.StartAsync();
            var receiverUrl = host._receiver.Urls.Single();
            host._stub = StubApplication.Build([], builder =>
            {
                builder.WebHost.UseUrls("http://127.0.0.1:0");
                builder.Logging.ClearProviders();
                builder.Configuration.Sources.Clear();
                builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ACME_API_KEY"] = ApiKey,
                    ["ACME_MERCHANT_ID"] = MerchantId,
                    ["ACME_ADVICE_URL"] = receiverUrl + "/advice",
                    ["ACME_ADVICE_USERNAME"] = Username,
                    ["ACME_ADVICE_PASSWORD"] = Password,
                    ["ACME_STATUS_WEBHOOK_URL"] = receiverUrl + "/status",
                    ["ACME_WEBHOOK_SECRET"] = Secret,
                    ["ACME_ENABLE_CONTROLS"] = enableControls.ToString()
                });
                builder.Services.AddSingleton<TimeProvider>(host.Clock);
            });
            await host._stub.StartAsync();
            host.Client = new HttpClient { BaseAddress = new Uri(host._stub.Urls.Single()) };
            host.Client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
            return host;
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }
    }

    public static JsonObject Payment(string transactionId = "attempt-1", string orderNumber = "MR43851259") => new()
    {
        ["MessageHeader"] = new JsonObject
        {
            ["MessageDateTime"] = "2026-03-01T13:56:36.533",
            ["MessageID"] = Guid.NewGuid().ToString(),
            ["Channel"] = "App"
        },
        ["PaymentRequest"] = new JsonObject
        {
            ["MerchantID"] = MerchantId,
            ["TransactionID"] = transactionId,
            ["OrderNumber"] = orderNumber,
            ["Amount"] = 1999,
            ["PaymentService"] = "CardNotPresent",
            ["Card"] = new JsonObject { ["Token"] = "stored-card-token" },
            ["CardOnFileScenario"] = "CIT",
            ["SubMerchantID"] = "ZAP0004182",
            ["MerchantCategoryCode"] = "5812",
            ["CardAcceptorName"] = "ZAP*CAFE DEL SOL".PadRight(23) + "CAPE TOWN".PadRight(13) + "WCZA"
        }
    };

    public Task<HttpResponseMessage> SubmitAsync(JsonObject? request = null) =>
        Client.PostAsJsonAsync("/api/payment", request ?? Payment());

    public Task<HttpResponseMessage> QueryAsync(string query = "TransactionID=attempt-1") =>
        Client.GetAsync($"/apirttq/query/Merchant/{MerchantId}?{query}");

    public async Task<CapturedCallback> NextCallbackAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await _callbacks.Reader.ReadAsync(timeout.Token);
    }

    public async Task StopStubAsync()
    {
        if (_stub is null)
            return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await _stub.StopAsync(timeout.Token);
        await _stub.DisposeAsync();
        _stub = null;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopStubAsync();
        }
        finally
        {
            Client?.Dispose();
            await _receiver.StopAsync();
            await _receiver.DisposeAsync();
        }
    }
}
