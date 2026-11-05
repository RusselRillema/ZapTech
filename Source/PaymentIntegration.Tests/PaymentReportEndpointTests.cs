using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using PaymentIntegration.Acme;
using PaymentIntegration.Payments;
using PaymentIntegration.Reports;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using Xunit;

namespace PaymentIntegration.Tests;

public sealed class PaymentReportEndpointTests
{
    [Theory]
    [InlineData("/reports/payments/daily")]
    [InlineData("/reports/payments/daily?date=")]
    [InlineData("/reports/payments/daily?date=2026-02-29")]
    [InlineData("/reports/payments/daily?date=04-11-2026")]
    [InlineData("/reports/payments/daily?date=2026-1-4")]
    [InlineData("/reports/payments/daily?date=%202026-11-04%20")]
    [InlineData("/reports/payments/daily?date=2026-11-04T00%3A00%3A00Z")]
    [InlineData("/reports/payments/daily?date=2026-11-04&date=2026-11-05")]
    [InlineData("/reports/payments/daily?date=9999-12-31")]
    public Task Invalid_dates_are_rejected_before_reading_history(string url) => WithApi(async (client, repository) =>
    {
        using var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        repository.VerifyNoOtherCalls();
    });

    [Fact]
    public Task Returns_a_real_historical_PDF_without_later_financial_details_or_Acme_configuration() => WithApi(async (client, repository) =>
    {
        var payment = ReportTestData.Payment();
        repository.Setup(value => value.GetHistoryAsync(DateTimeOffset.Parse("2026-11-03T22:00:00Z"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentReportHistory[]
            {
                new(payment, "Merchant A", [ReportTestData.Outcome(payment), ReportTestData.Adjustment(payment)])
            });

        using var response = await client.GetAsync("/reports/payments/daily?date=2026-11-03");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("payment-summary-2026-11-03.pdf", response.Content.Headers.ContentDisposition?.ToString());
        using var pdf = PdfDocument.Open(await response.Content.ReadAsByteArrayAsync());
        var text = ContentOrderTextExtractor.GetText(pdf.GetPage(1));
        Assert.Contains("3 November 2026", text);
        Assert.Contains("Merchant A", text);
        Assert.Contains("R1,000.00", text);
        Assert.Contains("Pending approval value", text);
        Assert.Contains("Yes", text);
        Assert.DoesNotContain("R300.00", text);
        Assert.DoesNotContain("R700.00", text);
        repository.Verify(value => value.GetHistoryAsync(DateTimeOffset.Parse("2026-11-03T22:00:00Z"),
            It.IsAny<CancellationToken>()), Times.Once);
        repository.VerifyNoOtherCalls();
    });

    [Fact]
    public Task Database_failure_is_reported_without_exposing_private_diagnostics() => WithApi(async (client, repository) =>
    {
        repository.Setup(value => value.GetHistoryAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("private-database-diagnostics"));

        using var response = await client.GetAsync("/reports/payments/daily?date=2026-11-04");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("private-database-diagnostics", body);
        Assert.DoesNotContain("AppPaymentId", body);
        Assert.Contains("Payment report could not be generated", body);
    });

    [Fact]
    public Task A_date_without_payments_returns_an_explicit_empty_PDF() => WithApi(async (client, repository) =>
    {
        repository.Setup(value => value.GetHistoryAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PaymentReportHistory>());

        using var response = await client.GetAsync("/reports/payments/daily?date=2024-02-29");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var pdf = PdfDocument.Open(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, pdf.NumberOfPages);
        Assert.Contains("No payments existed", ContentOrderTextExtractor.GetText(pdf.GetPage(1)));
    });

    private static async Task WithApi(Func<HttpClient, Mock<IPaymentReportRepository>, Task> check)
    {
        var repository = new Mock<IPaymentReportRepository>(MockBehavior.Strict);
        var payments = new Mock<IPaymentRepository>(MockBehavior.Strict);
        var gateway = new Mock<IAcmePaymentClient>(MockBehavior.Strict);
        await using var app = PaymentIntegrationApplication.Build([], builder =>
        {
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(repository.Object);
            builder.Services.AddSingleton(payments.Object);
            builder.Services.AddSingleton(gateway.Object);
            builder.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(DateTimeOffset.Parse("2026-11-06T12:00:00Z")));
        });
        using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await app.StartAsync(startup.Token);
            using var client = new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) };
            await check(client, repository);
            payments.VerifyNoOtherCalls();
            gateway.VerifyNoOtherCalls();
        }
        finally
        {
            using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await app.StopAsync(shutdown.Token);
        }
    }
}
