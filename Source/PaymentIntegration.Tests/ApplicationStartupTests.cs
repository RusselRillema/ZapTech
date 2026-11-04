using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace PaymentIntegration.Tests;

public sealed class ApplicationStartupTests
{
    [Fact]
    public async Task Application_starts_and_serves_health_without_external_configuration()
    {
        await using var app = PaymentIntegrationApplication.Build([], builder =>
        {
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
        });

        using var startupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await app.StartAsync(startupTimeout.Token);
            using var client = new HttpClient
            {
                BaseAddress = new Uri(app.Urls.Single()),
                Timeout = TimeSpan.FromSeconds(10)
            };
            using var response = await client.GetAsync("/health");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
            Assert.Equal("healthy", body.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            using var shutdownTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await app.StopAsync(shutdownTimeout.Token);
        }
    }
}
