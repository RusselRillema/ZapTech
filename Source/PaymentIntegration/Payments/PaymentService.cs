using System.Security.Cryptography;
using PaymentIntegration.Acme;

namespace PaymentIntegration.Payments;

public sealed class PaymentService(IPaymentRepository repository, IAcmePaymentClient acme,
    AcmeRequestFactory requestFactory, PaymentResponseProcessor responseProcessor,
    TimeProvider clock, ILogger<PaymentService> logger)
{
    public async Task<PaymentResult> SubmitAsync(NewPaymentRequest request, CancellationToken cancellationToken)
    {
        try
        {
            PaymentValidation.Validate(request);
        }
        catch (PaymentRequestException exception)
        {
            logger.LogWarning("Payment validation failed: {Reason}", exception.Message);
            throw;
        }

        logger.LogInformation("Received app payment {AppPaymentId}", request.AppPaymentId);
        var existing = await repository.GetByAppIdAsync(request.AppPaymentId!, cancellationToken);
        if (existing is not null)
        {
            return Existing(existing, request);
        }

        var details = await repository.GetDetailsAsync(request.CustomerId!, request.CustomerCardId,
            request.SubMerchantId!, cancellationToken);
        if (details is null)
        {
            logger.LogWarning("Payment lookup failed for app payment {AppPaymentId}", request.AppPaymentId);
            throw new PaymentRequestException(400, "invalid_payment_details",
                "The customer, their selected stored card, or sub-merchant could not be found.");
        }

        var payment = new Payment(Guid.NewGuid(), request.AppPaymentId!, request.CustomerId!,
            request.CustomerCardId, request.SubMerchantId!, Convert.ToHexString(RandomNumberGenerator.GetBytes(6)),
            request.CurrencyCode!, request.Amount, "Unresolved", clock.GetUtcNow());
        AcmeEnvelope envelope;
        try
        {
            envelope = requestFactory.Create(payment, details);
        }
        catch (PaymentRequestException exception)
        {
            logger.LogWarning("Acme request validation failed for app payment {AppPaymentId}: {Reason}", request.AppPaymentId, exception.Message);
            throw;
        }
        var creation = await repository.CreateAsync(payment, cancellationToken);
        if (!creation.Created)
        {
            return Existing(creation.Payment, request);
        }

        using var scope = logger.BeginScope(new Dictionary<string, object> { ["PaymentId"] = payment.PaymentId });
        logger.LogInformation("Created payment {PaymentId} for app payment {AppPaymentId}", payment.PaymentId, payment.AppPaymentId);

        // Once persisted, finish the bounded submission/audit flow even if the caller disconnects.
        using var processingTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var token = processingTimeout.Token;
        var eventId = Guid.NewGuid();
        var operation = "Acme submission";
        try
        {
            var response = await acme.SubmitAsync(envelope, token);
            var receipt = new PaymentEvent(eventId, payment.PaymentId, "PaymentResponse", response.Payload,
                response.HttpStatusCode, clock.GetUtcNow(), ProcessingComment: response.Failure);

            operation = "payment event transaction";
            await using var session = await repository.BeginProcessingAsync(payment.PaymentId, eventId, token);
            payment = session.Payment;
            await session.InsertEventAsync(receipt, token);
            var pending = (await session.GetPendingEventsAsync(token)).ToArray();
            bool applied;
            do
            {
                applied = false;
                for (var index = 0; index < pending.Length; index++)
                {
                    if (pending[index].ProcessingState != "Pending")
                    {
                        continue;
                    }

                    var result = responseProcessor.Process(payment, pending[index], clock.GetUtcNow());
                    payment = result.Payment;
                    pending[index] = result.Event;
                    applied |= pending[index].ProcessingState == "Applied";
                }
            } while (applied);

            payment = payment with
            {
                HasPendingEvents = pending.Any(item => item.ProcessingState == "Pending")
            };
            await session.SavePaymentAsync(payment, token);
            foreach (var item in pending)
            {
                await session.SaveEventAsync(item, token);
            }
            await session.CommitAsync(token);
            logger.LogInformation("Payment {PaymentId}, event {PaymentEventId} processed; state {PaymentState}, pending {HasPendingEvents}",
                payment.PaymentId, eventId, payment.State, payment.HasPendingEvents);
            return Result(payment);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Operation {Operation} failed for payment {PaymentId}, event {PaymentEventId}",
                operation, payment.PaymentId, eventId);
            throw;
        }
    }

    private PaymentResult Existing(Payment payment, NewPaymentRequest request)
    {
        if (payment.CustomerId != request.CustomerId || payment.CustomerCardId != request.CustomerCardId
            || payment.SubMerchantId != request.SubMerchantId || payment.RequestedAmount != request.Amount
            || payment.CurrencyCode != request.CurrencyCode)
        {
            logger.LogWarning("Conflicting duplicate submission for payment {PaymentId}, app payment {AppPaymentId}",
                payment.PaymentId, payment.AppPaymentId);
            throw new PaymentRequestException(409, "payment_conflict",
                "AppPaymentId is already associated with different payment details.");
        }
        logger.LogInformation("Returning existing payment {PaymentId}, app payment {AppPaymentId}", payment.PaymentId, payment.AppPaymentId);
        return Result(payment);
    }

    private static PaymentResult Result(Payment payment) => new(payment.PaymentId, payment.AppPaymentId,
        payment.State, payment.CurrencyCode, payment.RequestedAmount, payment.ApprovedAmount, payment.RemainingAmount,
        payment.State is "Approved" or "Declined" or "Failed" && !payment.HasPendingEvents);
}
