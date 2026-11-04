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

        // Once persisted, finish the bounded submission flow even if the caller disconnects.
        var processingWindow = TimeSpan.FromSeconds(60);
        var startedAt = clock.GetTimestamp();
        using var processingTimeout = new CancellationTokenSource(processingWindow, clock);
        var token = processingTimeout.Token;
        var eventId = Guid.Empty;
        var operation = "Acme submission";
        try
        {
            while (true)
            {
                eventId = Guid.NewGuid();
                operation = "Acme submission";
                var response = await acme.SubmitAsync(envelope, token);
                var receipt = new PaymentEvent(
                    eventId,
                    payment.PaymentId,
                    "PaymentResponse",
                    response.Payload,
                    response.HttpStatusCode,
                    clock.GetUtcNow(),
                    ProcessingComment: response.Failure);

                var remaining = processingWindow - clock.GetElapsedTime(startedAt);
                var retryDelay = response.RetryDelay;
                var retrying = retryDelay is { } delay
                    && delay < remaining
                    && !token.IsCancellationRequested;

                if (retryDelay is not null && !retrying)
                {
                    logger.LogWarning(
                        "Acme E29 retry cannot fit the remaining processing window for payment {PaymentId}, event {PaymentEventId}",
                        payment.PaymentId,
                        eventId);
                }

                operation = "payment event transaction";
                payment = await ProcessEventAsync(payment.PaymentId, eventId, receipt, retrying);

                if (!retrying
                    || payment.State != "Unresolved"
                    || retryDelay is not { } wait)
                {
                    remaining = processingWindow - clock.GetElapsedTime(startedAt);
                    if (payment.State == "Unresolved" && response.RequestAttempted
                        && remaining > TimeSpan.Zero && !token.IsCancellationRequested)
                    {
                        // The previous processing session has committed and disposed before network recovery.
                        // Neither the shared deadline nor the original payment identity is reset for RTTQ.
                        var queryWindow = remaining < TimeSpan.FromSeconds(10) ? remaining : TimeSpan.FromSeconds(10);
                        using var queryTimeout = new CancellationTokenSource(queryWindow, clock);
                        using var queryCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, queryTimeout.Token);
                        eventId = Guid.NewGuid();
                        operation = "Acme transaction query";
                        var query = await acme.QueryAsync(payment.PaymentId, queryCancellation.Token);
                        var queryReceipt = new PaymentEvent(eventId, payment.PaymentId, "TransactionQuery",
                            query.Payload, query.HttpStatusCode, clock.GetUtcNow(), ProcessingComment: query.Failure);

                        operation = "transaction query event transaction";
                        // Reload the locked persisted payment; callbacks may have changed it during RTTQ.
                        payment = await ProcessEventAsync(payment.PaymentId, eventId, queryReceipt, false);
                    }

                    return PaymentResult.FromPayment(payment);
                }

                remaining = processingWindow - clock.GetElapsedTime(startedAt);

                if (wait >= remaining || token.IsCancellationRequested)
                {
                    operation = "E29 deadline finalization";
                    payment = await ProcessEventAsync(payment.PaymentId, eventId, null, false);
                    return PaymentResult.FromPayment(payment);
                }

                logger.LogWarning(
                    "Acme E29 for payment {PaymentId}, event {PaymentEventId}; retrying after {RetryDelaySeconds} seconds",
                    payment.PaymentId,
                    eventId,
                    wait.TotalSeconds);

                operation = "E29 retry delay";
                try
                {
                    await Task.Delay(wait, clock, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    operation = "E29 deadline finalization";
                    payment = await ProcessEventAsync(payment.PaymentId, eventId, null, false);
                    return PaymentResult.FromPayment(payment);
                }

                if (token.IsCancellationRequested || clock.GetElapsedTime(startedAt) >= processingWindow)
                {
                    operation = "E29 deadline finalization";
                    payment = await ProcessEventAsync(payment.PaymentId, eventId, null, false);
                    return PaymentResult.FromPayment(payment);
                }

                // A retry is a new message for the same persisted payment attempt.
                envelope = requestFactory.Create(payment, details);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Operation {Operation} failed for payment {PaymentId}, event {PaymentEventId}",
                operation, payment.PaymentId, eventId);
            throw;
        }
    }

    private async Task<Payment> ProcessEventAsync(
        Guid paymentId,
        Guid eventId,
        PaymentEvent? receipt,
        bool retryingRateLimit)
    {
        // Audit received responses even when the HTTP/retry deadline has expired.
        using var persistenceTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
        var token = persistenceTimeout.Token;

        await using var session = await repository.BeginProcessingAsync(paymentId, eventId, token);
        var payment = session.Payment;

        if (receipt is not null)
        {
            await session.InsertEventAsync(receipt, token);
        }

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

                // Earlier E29 receipts await the current attempt's outcome.
                var deferRateLimitFailure = pending[index].PaymentEventId != eventId || retryingRateLimit;
                var result = responseProcessor.Process(
                    payment,
                    pending[index],
                    clock.GetUtcNow(),
                    deferRateLimitFailure);

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

        logger.LogInformation(
            "Payment {PaymentId}, event {PaymentEventId} processed; state {PaymentState}, pending {HasPendingEvents}",
            payment.PaymentId,
            eventId,
            payment.State,
            payment.HasPendingEvents);

        return payment;
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
        return PaymentResult.FromPayment(payment);
    }
}
