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
using PaymentIntegration.Acme.Client;
using PaymentIntegration.Payments;
using PaymentIntegration.Payments.Models;
using Xunit;

namespace PaymentIntegration.Tests;

public sealed class PaymentEndpointTests
{
    [Fact]
    public Task Status_reads_stored_amounts_and_pending_flag_without_Acme_configuration() => WithApi(async (client, repository, gateway) =>
    {
        var payment = Existing(Request()) with
        {
            State = "Approved", ApprovedAmount = 1250, RemainingAmount = 850
        };
        repository.Setup(value => value.GetByAppIdAsync("app", It.IsAny<CancellationToken>()))
            .ReturnsAsync(payment);

        using var response = await client.GetAsync("/payments?appPaymentId=app");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var result = body.RootElement;
        Assert.Equal(payment.PaymentId, result.GetProperty("paymentId").GetGuid());
        Assert.Equal("app", result.GetProperty("appPaymentId").GetString());
        Assert.Equal("Approved", result.GetProperty("state").GetString());
        Assert.Equal("ZAR", result.GetProperty("currencyCode").GetString());
        Assert.Equal(1999, result.GetProperty("requestedAmount").GetInt64());
        Assert.Equal(1250, result.GetProperty("approvedAmount").GetInt64());
        Assert.Equal(850, result.GetProperty("remainingAmount").GetInt64());
        Assert.False(result.GetProperty("isFullyReconciled").GetBoolean());
        Assert.False(result.TryGetProperty("hasPendingEvents", out _));
        repository.Verify(value => value.GetByAppIdAsync("app", It.IsAny<CancellationToken>()), Times.Once);
        repository.VerifyNoOtherCalls();
        gateway.VerifyNoOtherCalls();
    }, configureAcme: false);

    [Fact]
    public Task Status_returns_ok_for_an_unresolved_payment_with_unknown_amounts() => WithApi(async (client, repository, gateway) =>
    {
        repository.Setup(value => value.GetByAppIdAsync("app", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Existing(Request()) with { HasPendingEvents = false });

        using var response = await client.GetAsync("/payments?appPaymentId=app");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Unresolved", body.RootElement.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("approvedAmount").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("remainingAmount").ValueKind);
        Assert.False(body.RootElement.GetProperty("isFullyReconciled").GetBoolean());
        repository.Verify(value => value.GetByAppIdAsync("app", It.IsAny<CancellationToken>()), Times.Once);
        repository.VerifyNoOtherCalls();
        gateway.VerifyNoOtherCalls();
    }, configureAcme: false);

    [Fact]
    public Task Status_returns_not_found_for_an_unknown_app_identifier() => WithApi(async (client, repository, gateway) =>
    {
        repository.Setup(value => value.GetByAppIdAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((Payment?)null);

        using var response = await client.GetAsync("/payments?appPaymentId=missing");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("payment_not_found", body.RootElement.GetProperty("title").GetString());
        gateway.VerifyNoOtherCalls();
    }, configureAcme: false);

    [Theory]
    [InlineData("/payments")]
    [InlineData("/payments?appPaymentId=")]
    [InlineData("/payments?appPaymentId=%20")]
    public Task Status_requires_an_app_identifier_before_reading(string url) => WithApi(async (client, repository, gateway) =>
    {
        using var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        repository.VerifyNoOtherCalls();
        gateway.VerifyNoOtherCalls();
    }, configureAcme: false);

    [Fact]
    public Task Status_database_failure_returns_a_generic_error_without_contacting_Acme() => WithApi(async (client, repository, gateway) =>
    {
        repository.Setup(value => value.GetByAppIdAsync("app", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Private database diagnostics."));

        using var response = await client.GetAsync("/payments?appPaymentId=app");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("Private database diagnostics", await response.Content.ReadAsStringAsync());
        gateway.VerifyNoOtherCalls();
    }, configureAcme: false);

    [Fact]
    public Task Status_preserves_app_identifiers_containing_URL_reserved_characters() => WithApi(async (client, repository, gateway) =>
    {
        const string appPaymentId = "order/part?ref=1+2&tag=#%2F";
        repository.Setup(value => value.GetByAppIdAsync(appPaymentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Existing(Request()) with { AppPaymentId = appPaymentId });

        using var response = await client.GetAsync($"/payments?appPaymentId={Uri.EscapeDataString(appPaymentId)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        repository.Verify(value => value.GetByAppIdAsync(appPaymentId, It.IsAny<CancellationToken>()), Times.Once);
        repository.VerifyNoOtherCalls();
        gateway.VerifyNoOtherCalls();
    }, configureAcme: false);

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

    private static async Task WithApi(Func<HttpClient, Mock<IPaymentRepository>, Mock<IAcmePaymentClient>, Task> check,
        bool configureAcme = true)
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
            if (configureAcme)
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
