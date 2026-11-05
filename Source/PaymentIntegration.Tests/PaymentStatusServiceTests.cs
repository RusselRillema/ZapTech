using Moq;
using PaymentIntegration.Payments;
using Xunit;

namespace PaymentIntegration.Tests;

public sealed class PaymentStatusServiceTests
{
    [Theory]
    [InlineData("Approved", 1250L, 850L, false, true)]
    [InlineData("Approved", 1250L, 850L, true, false)]
    [InlineData("PartiallyRefunded", 1250L, 850L, false, true)]
    [InlineData("PartiallyRefunded", 1250L, 850L, true, false)]
    [InlineData("Refunded", 1250L, 0L, false, true)]
    [InlineData("Refunded", 1250L, 0L, true, false)]
    [InlineData("Reversed", 1250L, 0L, false, true)]
    [InlineData("Reversed", 1250L, 0L, true, false)]
    [InlineData("Declined", null, null, false, true)]
    [InlineData("Declined", null, null, true, false)]
    [InlineData("Failed", null, null, false, true)]
    [InlineData("Failed", null, null, true, false)]
    [InlineData("Unresolved", null, null, false, false)]
    [InlineData("Unresolved", null, null, true, false)]
    public async Task Reconciliation_requires_a_known_outcome_and_no_pending_events(
        string state, long? approvedAmount, long? remainingAmount, bool hasPendingEvents, bool reconciled)
    {
        var repository = new Mock<IPaymentRepository>(MockBehavior.Strict);
        using var caller = new CancellationTokenSource();
        var payment = new Payment(Guid.NewGuid(), "app-payment", "customer", Guid.NewGuid(),
            "shop", "ORDER1234567", "ZAR", 1999, state, DateTimeOffset.UtcNow,
            approvedAmount, remainingAmount, hasPendingEvents);
        repository.Setup(value => value.GetByAppIdAsync("app-payment", caller.Token)).ReturnsAsync(payment);
        var service = new PaymentStatusService(repository.Object);

        var result = await service.GetAsync("app-payment", caller.Token);

        Assert.Equal(reconciled, result.IsFullyReconciled);
        repository.Verify(value => value.GetByAppIdAsync("app-payment", caller.Token), Times.Once);
        repository.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Missing_app_identifier_is_rejected_before_accessing_the_repository(string? appPaymentId)
    {
        var repository = new Mock<IPaymentRepository>(MockBehavior.Strict);
        var service = new PaymentStatusService(repository.Object);

        var exception = await Assert.ThrowsAsync<PaymentRequestException>(() =>
            service.GetAsync(appPaymentId, CancellationToken.None));

        Assert.Equal(400, exception.StatusCode);
        Assert.Equal("invalid_payment", exception.Code);
        repository.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Unknown_app_identifier_returns_not_found_without_creating_a_payment()
    {
        var repository = new Mock<IPaymentRepository>(MockBehavior.Strict);
        repository.Setup(value => value.GetByAppIdAsync("missing", CancellationToken.None))
            .ReturnsAsync((Payment?)null);
        var service = new PaymentStatusService(repository.Object);

        var exception = await Assert.ThrowsAsync<PaymentRequestException>(() =>
            service.GetAsync("missing", CancellationToken.None));

        Assert.Equal(404, exception.StatusCode);
        Assert.Equal("payment_not_found", exception.Code);
        repository.Verify(value => value.GetByAppIdAsync("missing", CancellationToken.None), Times.Once);
        repository.VerifyNoOtherCalls();
    }
}
