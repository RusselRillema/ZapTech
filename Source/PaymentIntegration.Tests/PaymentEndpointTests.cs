using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using PaymentIntegration.Acme;
using PaymentIntegration.Payments;
using Xunit;

namespace PaymentIntegration.Tests;

public sealed class PaymentEndpointTests
{
    [Fact]
    public Task Rejects_fractional_minor_units_before_accessing_the_repository() => WithApi(async (client, repository, gateway) =>
    {
        using var body = new StringContent("""
            {"appPaymentId":"app","customerId":"customer","customerCardId":"8ee3f008-84f5-4152-80a5-21821abdd045",
             "subMerchantId":"shop","amount":19.99,"currencyCode":"ZAR"}
            """, Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/payments", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        repository.VerifyNoOtherCalls();
        gateway.VerifyNoOtherCalls();
    });

    [Fact]
    public Task Requires_an_explicit_currency_before_accessing_the_repository() => WithApi(async (client, repository, gateway) =>
    {
        using var response = await client.PostAsJsonAsync("/payments", Request() with { CurrencyCode = null });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        repository.VerifyNoOtherCalls();
        gateway.VerifyNoOtherCalls();
    });

    [Fact]
    public Task Returns_accepted_and_reconciliation_flag_for_a_repeated_unresolved_payment() => WithApi(async (client, repository, gateway) =>
    {
        var request = Request();
        repository.Setup(value => value.GetByAppIdAsync(request.AppPaymentId!, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Existing(request));

        using var response = await client.PostAsJsonAsync("/payments", request);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(body.RootElement.GetProperty("isFullyReconciled").GetBoolean());
        Assert.Equal("Unresolved", body.RootElement.GetProperty("state").GetString());
        Assert.DoesNotContain("CardToken", await response.Content.ReadAsStringAsync());
        gateway.VerifyNoOtherCalls();
    });

    [Fact]
    public Task Returns_conflict_when_the_existing_app_identifier_has_different_payment_details() => WithApi(async (client, repository, gateway) =>
    {
        var request = Request();
        repository.Setup(value => value.GetByAppIdAsync(request.AppPaymentId!, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Existing(request) with { RequestedAmount = 5000 });

        using var response = await client.PostAsJsonAsync("/payments", request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        gateway.VerifyNoOtherCalls();
    });

    [Fact]
    public Task Database_failure_returns_a_generic_error_without_submitting_to_Acme() => WithApi(async (client, repository, gateway) =>
    {
        repository.Setup(value => value.GetByAppIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Private database diagnostics."));

        using var response = await client.PostAsJsonAsync("/payments", Request());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("Private database diagnostics", await response.Content.ReadAsStringAsync());
        gateway.VerifyNoOtherCalls();
    });

    private static NewPaymentRequest Request() => new("app", "customer",
        Guid.Parse("8ee3f008-84f5-4152-80a5-21821abdd045"), "shop", 1999, "ZAR");
    private static Payment Existing(NewPaymentRequest request) => new(Guid.NewGuid(), request.AppPaymentId!,
        request.CustomerId!, request.CustomerCardId, request.SubMerchantId!, "ORDER1234567", request.CurrencyCode!,
        request.Amount, "Unresolved", DateTimeOffset.UtcNow, HasPendingEvents: true);

    private static async Task WithApi(Func<HttpClient, Mock<IPaymentRepository>, Mock<IAcmePaymentClient>, Task> check)
    {
        var repository = new Mock<IPaymentRepository>(MockBehavior.Strict);
        var gateway = new Mock<IAcmePaymentClient>(MockBehavior.Strict);
        await using var app = PaymentIntegrationApplication.Build([], builder =>
        {
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(repository.Object);
            builder.Services.AddSingleton(gateway.Object);
            builder.Services.AddSingleton(new AcmeSettings(new Uri("http://localhost:5081"), Guid.NewGuid().ToString(), "test-api-key"));
        });
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await app.StartAsync(startup.Token);
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
            await check(client, repository, gateway);
        }
        finally
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await app.StopAsync(shutdown.Token);
        }
    }
}
