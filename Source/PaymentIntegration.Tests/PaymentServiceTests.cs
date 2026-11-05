using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using PaymentIntegration.Acme;
using PaymentIntegration.Payments;
using Xunit;

namespace PaymentIntegration.Tests;

public sealed class PaymentServiceTests
{
    [Theory]
    [InlineData("05")]
    [InlineData("51")]
    public async Task Decline_code_overrides_gateway_success_and_commits_the_audit_event(string code)
    {
        var flow = new Flow();
        flow.Reply = envelope => BankReply(envelope, code, null);

        var result = await flow.Service.SubmitAsync(flow.Request, CancellationToken.None);

        Assert.Equal("Declined", result.State);
        Assert.Null(result.ApprovedAmount);
        Assert.Null(result.RemainingAmount);
        Assert.True(result.IsFullyReconciled);
        Assert.Equal("Applied", Assert.Single(flow.SavedEvents).ProcessingState);
        flow.Session.Verify(session => session.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        flow.Client.Verify(client => client.SubmitAsync(It.IsAny<AcmeEnvelope>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.True(flow.Calls.IndexOf("create") < flow.Calls.IndexOf("send"));
        Assert.True(flow.Calls.IndexOf("lock") < flow.Calls.IndexOf("insert"));
        Assert.True(flow.Calls.IndexOf("insert") < flow.Calls.IndexOf("read_pending"));
        Assert.True(flow.Calls.IndexOf("save_payment") < flow.Calls.IndexOf("commit"));
        Assert.True(flow.Calls.IndexOf("save_event") < flow.Calls.IndexOf("commit"));
    }

    [Fact]
    public async Task Partial_authorisation_uses_bank_amount_and_does_not_assume_requested_value()
    {
        var flow = new Flow();
        flow.Reply = envelope => BankReply(envelope, "00", 1250);

        var result = await flow.Service.SubmitAsync(flow.Request, CancellationToken.None);

        Assert.Equal("Approved", result.State);
        Assert.Equal(1250, result.ApprovedAmount);
        Assert.Equal(1250, result.RemainingAmount);
        Assert.Equal(2000, result.RequestedAmount);
        Assert.True(result.IsFullyReconciled);
        Assert.Equal("Approved", flow.SavedPayment!.State);
        Assert.Equal("Applied", Assert.Single(flow.SavedEvents).ProcessingState);
    }

    [Fact]
    public async Task Approval_without_authorised_amount_stays_pending()
    {
        var flow = new Flow();
        flow.Reply = envelope => BankReply(envelope, "00", null);

        var result = await flow.Service.SubmitAsync(flow.Request, CancellationToken.None);

        Assert.Equal("Unresolved", result.State);
        Assert.Null(result.ApprovedAmount);
        Assert.False(result.IsFullyReconciled);
        Assert.Equal("Pending", Assert.Single(flow.Events, item => item.Source == "PaymentResponse").ProcessingState);
    }

    [Fact]
    public async Task Timeout_keeps_amount_unknown_and_does_not_resubmit()
    {
        var flow = new Flow();
        flow.Reply = _ => new(null, null, "Timed out.");

        var result = await flow.Service.SubmitAsync(flow.Request, CancellationToken.None);

        Assert.Equal("Unresolved", result.State);
        Assert.Null(result.ApprovedAmount);
        Assert.False(result.IsFullyReconciled);
        var receipt = Assert.Single(flow.Events, item => item.Source == "PaymentResponse");
        Assert.Equal("Pending", receipt.ProcessingState);
        Assert.Null(receipt.HttpStatusCode);
        flow.Client.Verify(client => client.SubmitAsync(It.IsAny<AcmeEnvelope>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Gateway_RRN_conflict_fails_this_payment_without_adopting_the_other_transaction()
    {
        var flow = new Flow();
        flow.Reply = _ => new(409, JsonSerializer.SerializeToElement(new
        {
            PaymentResponse = new { TransactionID = Guid.NewGuid(), ResponseDetail = new { Source = "Application", Code = "E09" } }
        }));

        var result = await flow.Service.SubmitAsync(flow.Request, CancellationToken.None);

        Assert.Equal("Failed", result.State);
        Assert.Equal(flow.Created!.PaymentId, result.PaymentId);
        Assert.Null(result.ApprovedAmount);
        Assert.True(result.IsFullyReconciled);
        var receipt = Assert.Single(flow.SavedEvents);
        Assert.Equal("Applied", receipt.ProcessingState);
        Assert.NotNull(receipt.ProcessedAt);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(409)]
    [InlineData(429)]
    public async Task Explicit_Acme_rejection_is_failed_rather_than_acquirer_declined(int httpStatusCode)
    {
        var flow = new Flow();
        flow.Reply = _ => new((short)httpStatusCode, null);

        var result = await flow.Service.SubmitAsync(flow.Request, CancellationToken.None);

        Assert.Equal("Failed", result.State);
        Assert.Null(result.ApprovedAmount);
        Assert.Null(result.RemainingAmount);
        Assert.True(result.IsFullyReconciled);
        Assert.Equal("Applied", Assert.Single(flow.SavedEvents).ProcessingState);
        flow.Client.Verify(client => client.QueryAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(501)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public async Task Gateway_server_failure_is_an_unknown_outcome(int httpStatusCode)
    {
        var flow = new Flow();
        flow.Reply = _ => new((short)httpStatusCode, null);

        var result = await flow.Service.SubmitAsync(flow.Request, CancellationToken.None);

        Assert.Equal("Unresolved", result.State);
        Assert.Null(result.ApprovedAmount);
        Assert.False(result.IsFullyReconciled);
        Assert.Equal("Pending", Assert.Single(flow.Events, item => item.Source == "PaymentResponse").ProcessingState);
    }

    [Fact]
    public async Task Foreign_successful_response_cannot_approve_this_payment()
    {
        var flow = new Flow();
        flow.Reply = _ => new(200, JsonSerializer.SerializeToElement(new
        {
            PaymentResponse = new
            {
                TransactionID = Guid.NewGuid(), TransactionStatus = "Success", AuthAmount = 2000,
                ResponseDetail = new { Source = "Acquirer", Code = "00" }
            }
        }));

        var result = await flow.Service.SubmitAsync(flow.Request, CancellationToken.None);

        Assert.Equal("Unresolved", result.State);
        Assert.Null(result.ApprovedAmount);
        var receipt = Assert.Single(flow.Events, item => item.Source == "PaymentResponse");
        Assert.Equal("Ignored", receipt.ProcessingState);
        Assert.Contains("different transaction", receipt.ProcessingComment);
    }

    [Theory]
    [InlineData("Unresolved", false)]
    [InlineData("Failed", true)]
    public async Task Matching_repeat_returns_existing_payment_without_looking_up_or_sending_again(string state, bool reconciled)
    {
        var flow = new Flow();
        flow.Repository.Setup(repository => repository.GetByAppIdAsync(flow.Request.AppPaymentId!, It.IsAny<CancellationToken>()))
            .ReturnsAsync(flow.ExistingPayment(state));

        var result = await flow.Service.SubmitAsync(flow.Request, CancellationToken.None);

        Assert.Equal(state, result.State);
        Assert.Equal(reconciled, result.IsFullyReconciled);
        flow.Client.VerifyNoOtherCalls();
        flow.Repository.Verify(repository => repository.CreateAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()), Times.Never);
        flow.Repository.Verify(repository => repository.GetDetailsAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("amount")]
    [InlineData("card")]
    [InlineData("customer")]
    [InlineData("merchant")]
    [InlineData("currency")]
    public async Task Reusing_app_identifier_with_changed_details_is_a_conflict(string changed)
    {
        var flow = new Flow();
        var existing = flow.ExistingPayment("Unresolved");
        flow.Repository.Setup(repository => repository.GetByAppIdAsync(flow.Request.AppPaymentId!, It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);
        var request = changed switch
        {
            "amount" => flow.Request with { Amount = 3000 },
            "card" => flow.Request with { CustomerCardId = Guid.NewGuid() },
            "customer" => flow.Request with { CustomerId = "another-customer" },
            "merchant" => flow.Request with { SubMerchantId = "another-shop" },
            _ => flow.Request with { CurrencyCode = "USD" }
        };

        var exception = await Assert.ThrowsAsync<PaymentRequestException>(() => flow.Service.SubmitAsync(request, CancellationToken.None));

        Assert.Equal(409, exception.StatusCode);
        flow.Client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Losing_a_concurrent_create_does_not_submit_a_second_payment()
    {
        var flow = new Flow();
        flow.Repository.Setup(repository => repository.CreateAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentCreation(false, flow.ExistingPayment("Unresolved")));

        var result = await flow.Service.SubmitAsync(flow.Request, CancellationToken.None);

        Assert.Equal("Unresolved", result.State);
        flow.Client.VerifyNoOtherCalls();
        flow.Repository.Verify(repository => repository.BeginProcessingAsync(It.IsAny<Guid?>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Failed_initial_persistence_prevents_an_Acme_call()
    {
        var flow = new Flow();
        flow.Repository.Setup(repository => repository.CreateAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Persistence failed."));

        await Assert.ThrowsAsync<InvalidOperationException>(() => flow.Service.SubmitAsync(flow.Request, CancellationToken.None));

        flow.Client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Failed_event_save_disposes_the_transaction_without_committing()
    {
        var flow = new Flow();
        flow.Session.Setup(session => session.SaveEventAsync(It.IsAny<PaymentEvent>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Event save failed."));

        await Assert.ThrowsAsync<InvalidOperationException>(() => flow.Service.SubmitAsync(flow.Request, CancellationToken.None));

        flow.Session.Verify(session => session.CommitAsync(It.IsAny<CancellationToken>()), Times.Never);
        flow.Session.Verify(session => session.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task Processing_uses_the_locked_persisted_state_instead_of_the_initial_snapshot()
    {
        var flow = new Flow();
        flow.Session.SetupGet(session => session.Payment).Returns(() => flow.Created! with
        {
            State = "Approved", ApprovedAmount = 1250, RemainingAmount = 1250
        });
        flow.Reply = envelope => BankReply(envelope, "00", 1250);

        await flow.Service.SubmitAsync(flow.Request, CancellationToken.None);

        var receipt = Assert.Single(flow.SavedEvents);
        Assert.Equal("Ignored", receipt.ProcessingState);
        Assert.Contains("already been applied", receipt.ProcessingComment);
    }

    [Fact]
    public async Task Applying_a_bank_outcome_reconsiders_an_earlier_pending_timeout_receipt()
    {
        var flow = new Flow();
        flow.Session.Setup(session => session.InsertEventAsync(It.IsAny<PaymentEvent>(), It.IsAny<CancellationToken>()))
            .Callback((PaymentEvent receipt, CancellationToken _) =>
            {
                flow.Events.Add(new(Guid.NewGuid(), receipt.PaymentId, "PaymentResponse", null, null,
                    receipt.ReceivedAt.AddMinutes(-1)));
                flow.Events.Add(receipt);
            }).Returns(Task.CompletedTask);

        var result = await flow.Service.SubmitAsync(flow.Request, CancellationToken.None);

        Assert.True(result.IsFullyReconciled);
        Assert.Equal(2, flow.SavedEvents.Count);
        Assert.Equal("Ignored", flow.SavedEvents[0].ProcessingState);
        Assert.Equal("Applied", flow.SavedEvents[1].ProcessingState);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Nonpositive_amount_is_rejected_before_accessing_dependencies(long amount)
    {
        var flow = new Flow();

        var exception = await Assert.ThrowsAsync<PaymentRequestException>(() =>
            flow.Service.SubmitAsync(flow.Request with { Amount = amount }, CancellationToken.None));

        Assert.Equal(400, exception.StatusCode);
        flow.Repository.VerifyNoOtherCalls();
        flow.Client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Card_not_owned_by_customer_prevents_creation_and_submission()
    {
        var flow = new Flow();
        flow.Repository.Setup(repository => repository.GetDetailsAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PaymentDetails?)null);

        var exception = await Assert.ThrowsAsync<PaymentRequestException>(() => flow.Service.SubmitAsync(flow.Request, CancellationToken.None));

        Assert.Equal(400, exception.StatusCode);
        flow.Repository.Verify(repository => repository.CreateAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()), Times.Never);
        flow.Client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Acme_request_uses_the_saved_payment_identity_and_the_fixed_FNB_positions()
    {
        var flow = new Flow();
        AcmeEnvelope? submitted = null;
        flow.Reply = envelope => { submitted = envelope; return BankReply(envelope, "00", 2000); };

        await flow.Service.SubmitAsync(flow.Request, CancellationToken.None);

        Assert.NotNull(submitted);
        Assert.Equal(flow.Created!.PaymentId.ToString(), submitted.PaymentRequest.TransactionID);
        Assert.Equal(flow.Created.OrderNumber, submitted.PaymentRequest.OrderNumber);
        Assert.Equal("ZAP*COFFEE             CAPE TOWN    WCZA", submitted.PaymentRequest.CardAcceptorName);
        Assert.Equal(40, submitted.PaymentRequest.CardAcceptorName.Length);
        Assert.Equal("CardNotPresent", submitted.PaymentRequest.PaymentService);
        Assert.Equal("CIT", submitted.PaymentRequest.CardOnFileScenario);
        flow.Repository.Verify(repository => repository.GetDetailsAsync(flow.Request.CustomerId!, flow.Request.CustomerCardId,
            flow.Request.SubMerchantId!, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("THIS MERCHANT IS TOO LONG", "CAPE TOWN")]
    [InlineData("COFFEE", "CITY NAME THAT IS TOO LONG")]
    public async Task Oversized_FNB_statement_fields_are_rejected_before_persistence_and_submission(string merchant, string city)
    {
        var flow = new Flow();
        flow.Repository.Setup(repository => repository.GetDetailsAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentDetails("token", "customer@example.com", merchant, city, "WC", "ZA", "5812"));

        var exception = await Assert.ThrowsAsync<PaymentRequestException>(() => flow.Service.SubmitAsync(flow.Request, CancellationToken.None));

        Assert.Equal(422, exception.StatusCode);
        flow.Repository.Verify(repository => repository.CreateAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()), Times.Never);
        flow.Client.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Caller_disconnect_after_creation_does_not_cancel_submission_and_audit_processing()
    {
        var flow = new Flow();
        using var caller = new CancellationTokenSource();
        flow.Repository.Setup(repository => repository.CreateAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()))
            .Callback(() => caller.Cancel())
            .ReturnsAsync((Payment payment, CancellationToken _) =>
            {
                flow.SetCreatedPayment(payment);
                return new(true, payment);
            });

        var result = await flow.Service.SubmitAsync(flow.Request, caller.Token);

        Assert.True(result.IsFullyReconciled);
        flow.Client.Verify(client => client.SubmitAsync(It.IsAny<AcmeEnvelope>(),
            It.Is<CancellationToken>(token => !token.IsCancellationRequested)), Times.Once);
        flow.Session.Verify(session => session.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(4)]
    public async Task E29_waits_for_the_buffered_delay_then_retries_the_same_persisted_payment(int delaySeconds)
    {
        var clock = new ObservedTimeProvider();
        var flow = new Flow(clock);
        var submitted = new List<AcmeEnvelope>();
        flow.Reply = envelope =>
        {
            submitted.Add(envelope);
            return submitted.Count == 1
                ? RateLimitedReply(envelope, delaySeconds)
                : BankReply(envelope, "00", 1250);
        };

        var submission = flow.Service.SubmitAsync(flow.Request, CancellationToken.None);
        var delay = TimeSpan.FromSeconds(delaySeconds);
        await clock.WaitForTimerAsync(delay);

        Assert.Single(submitted);
        Assert.Equal("Pending", Assert.Single(flow.Events).ProcessingState);
        flow.Session.Verify(session => session.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        flow.Session.Verify(session => session.DisposeAsync(), Times.Once);

        clock.Advance(delay - TimeSpan.FromMilliseconds(1));
        Assert.Single(submitted);
        clock.Advance(TimeSpan.FromMilliseconds(1));

        var result = await submission.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("Approved", result.State);
        Assert.Equal(1250, result.ApprovedAmount);
        Assert.True(result.IsFullyReconciled);
        Assert.Equal(2, submitted.Count);
        Assert.All(submitted, envelope =>
        {
            Assert.Equal(flow.Created!.PaymentId.ToString(), envelope.PaymentRequest.TransactionID);
            Assert.Equal(flow.Created.OrderNumber, envelope.PaymentRequest.OrderNumber);
        });
        Assert.Equal(2, submitted.Select(envelope => envelope.MessageHeader.MessageID).Distinct().Count());
        flow.Repository.Verify(repository => repository.CreateAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(2, flow.Events.Count);
        Assert.Equal("Ignored", flow.Events[0].ProcessingState);
        Assert.Equal("Applied", flow.Events[1].ProcessingState);
    }

    [Fact]
    public async Task Repeated_E29_responses_each_wait_and_remain_in_the_audit_history()
    {
        var clock = new ObservedTimeProvider();
        var flow = new Flow(clock);
        var calls = 0;
        flow.Reply = envelope => ++calls switch
        {
            1 => RateLimitedReply(envelope, 3),
            2 => RateLimitedReply(envelope, 4),
            _ => BankReply(envelope, "00", 2000)
        };

        var submission = flow.Service.SubmitAsync(flow.Request, CancellationToken.None);
        await clock.WaitForTimerAsync(TimeSpan.FromSeconds(3));
        clock.Advance(TimeSpan.FromSeconds(3));
        await clock.WaitForTimerAsync(TimeSpan.FromSeconds(4));

        Assert.Equal(2, calls);
        Assert.Equal(2, flow.Events.Count);
        clock.Advance(TimeSpan.FromSeconds(4));

        var result = await submission.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("Approved", result.State);
        Assert.True(result.IsFullyReconciled);
        Assert.Equal(3, calls);
        Assert.Equal(3, flow.Events.Count);
        Assert.Equal(new short?[] { 429, 429, 200 }, flow.Events.Select(receipt => receipt.HttpStatusCode));
        Assert.Equal(new[] { "Ignored", "Ignored", "Applied" }, flow.Events.Select(receipt => receipt.ProcessingState));
    }

    [Fact]
    public async Task An_E29_delay_that_cannot_fit_the_deadline_is_not_shortened_to_send_early()
    {
        var flow = new Flow(new ObservedTimeProvider());
        flow.Reply = envelope => RateLimitedReply(envelope, 62);

        var result = await flow.Service.SubmitAsync(flow.Request, CancellationToken.None);

        Assert.Equal("Failed", result.State);
        Assert.True(result.IsFullyReconciled);
        Assert.Equal("Applied", Assert.Single(flow.Events).ProcessingState);
        flow.Client.Verify(client => client.SubmitAsync(It.IsAny<AcmeEnvelope>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task E29_without_a_retry_delay_remains_a_failed_rejection()
    {
        var flow = new Flow();
        flow.Reply = envelope => RateLimitedReply(envelope, null);

        var result = await flow.Service.SubmitAsync(flow.Request, CancellationToken.None);

        Assert.Equal("Failed", result.State);
        Assert.True(result.IsFullyReconciled);
        Assert.Equal("Applied", Assert.Single(flow.Events).ProcessingState);
        flow.Client.Verify(client => client.SubmitAsync(It.IsAny<AcmeEnvelope>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task E29_does_not_retry_when_the_locked_payment_already_has_a_bank_outcome()
    {
        var flow = new Flow(new ObservedTimeProvider());
        flow.Reply = envelope => RateLimitedReply(envelope, 3);
        flow.Session.SetupGet(session => session.Payment).Returns(() => flow.Created! with
        {
            State = "Approved", ApprovedAmount = 1250, RemainingAmount = 1250
        });

        var result = await flow.Service.SubmitAsync(flow.Request, CancellationToken.None);

        Assert.Equal("Approved", result.State);
        Assert.Equal(1250, result.RemainingAmount);
        Assert.Equal("Ignored", Assert.Single(flow.Events).ProcessingState);
        flow.Client.Verify(client => client.SubmitAsync(It.IsAny<AcmeEnvelope>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Deadline_expiring_during_E29_wait_finalizes_the_saved_rejection_without_a_new_attempt()
    {
        var clock = new ObservedTimeProvider();
        var flow = new Flow(clock);
        flow.Reply = envelope => RateLimitedReply(envelope, 3);

        var submission = flow.Service.SubmitAsync(flow.Request, CancellationToken.None);
        await clock.WaitForTimerAsync(TimeSpan.FromSeconds(3));
        clock.Advance(TimeSpan.FromSeconds(60));

        var result = await submission.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("Failed", result.State);
        Assert.True(result.IsFullyReconciled);
        Assert.Equal("Applied", Assert.Single(flow.Events).ProcessingState);
        flow.Client.Verify(client => client.SubmitAsync(It.IsAny<AcmeEnvelope>(), It.IsAny<CancellationToken>()), Times.Once);
        flow.Session.Verify(session => session.DisposeAsync(), Times.Exactly(2));
    }

    [Fact]
    public async Task An_unknown_outcome_after_an_E29_retry_is_not_retried_again()
    {
        var clock = new ObservedTimeProvider();
        var flow = new Flow(clock);
        var calls = 0;
        flow.Reply = envelope => ++calls == 1
            ? RateLimitedReply(envelope, 3)
            : new(null, null, "Timed out.");

        var submission = flow.Service.SubmitAsync(flow.Request, CancellationToken.None);
        await clock.WaitForTimerAsync(TimeSpan.FromSeconds(3));
        clock.Advance(TimeSpan.FromSeconds(3));

        var result = await submission.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("Unresolved", result.State);
        Assert.False(result.IsFullyReconciled);
        Assert.Equal(2, calls);
        Assert.Equal(3, flow.Events.Count);
        Assert.All(flow.Events, receipt => Assert.Equal("Pending", receipt.ProcessingState));
        flow.Client.Verify(client => client.QueryAsync(flow.Created!.PaymentId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("connection")]
    [InlineData("500")]
    [InlineData("501")]
    [InlineData("502")]
    [InlineData("503")]
    [InlineData("504")]
    [InlineData("malformed")]
    [InlineData("missing_amount")]
    [InlineData("processing")]
    public async Task Inconclusive_submission_queries_the_saved_payment_and_applies_partial_authorisation(string trigger)
    {
        var flow = new Flow();
        flow.Reply = envelope => trigger switch
        {
            "timeout" => new(null, null, "Timed out."),
            "connection" => new(null, null, "Connection lost."),
            "malformed" => new(200, JsonSerializer.SerializeToElement("invalid JSON response")),
            "missing_amount" => BankReply(envelope, "00", null),
            "processing" => QueryOutcome(flow, status: "Processing"),
            _ => new(short.Parse(trigger), null)
        };
        flow.QueryReply = (paymentId, _) =>
        {
            Assert.Equal(flow.Created!.PaymentId, paymentId);
            Assert.Equal("dispose", flow.Calls[^2]); // Query starts after the first transaction has ended.
            flow.Session.Verify(session => session.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
            return Task.FromResult(QueryOutcome(flow));
        };

        var result = await flow.Service.SubmitAsync(flow.Request, CancellationToken.None);

        Assert.Equal("Approved", result.State);
        Assert.Equal(1250, result.ApprovedAmount);
        Assert.Equal(1250, result.RemainingAmount);
        Assert.Equal(2000, result.RequestedAmount);
        Assert.True(result.IsFullyReconciled);
        Assert.Equal(2, flow.Events.Count);
        Assert.Equal("Ignored", flow.Events[0].ProcessingState);
        var query = flow.Events[1];
        Assert.Equal("TransactionQuery", query.Source);
        Assert.Equal("Applied", query.ProcessingState);
        Assert.Equal("retained", query.Payload!.Value.GetProperty("PaymentResponse").GetProperty("FutureAcmeField").GetString());
        flow.Client.Verify(client => client.SubmitAsync(It.IsAny<AcmeEnvelope>(), It.IsAny<CancellationToken>()), Times.Once);
        flow.Client.Verify(client => client.QueryAsync(result.PaymentId, It.IsAny<CancellationToken>()), Times.Once);
        flow.Session.Verify(session => session.CommitAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        flow.Session.Verify(session => session.DisposeAsync(), Times.Exactly(2));
    }

    [Fact]
    public async Task Query_decline_code_overrides_gateway_success()
    {
        var flow = new Flow { Reply = _ => new(null, null, "Timed out.") };
        flow.QueryReply = (_, _) => Task.FromResult(QueryOutcome(flow, code: "05", amount: null));

        var result = await flow.Service.SubmitAsync(flow.Request, CancellationToken.None);

        Assert.Equal("Declined", result.State);
        Assert.Null(result.ApprovedAmount);
        Assert.Null(result.RemainingAmount);
        Assert.True(result.IsFullyReconciled);
        Assert.Equal("Applied", flow.Events[1].ProcessingState);
    }

    [Theory]
    [InlineData("Refund", "Refunded")]
    [InlineData("Reversal", "Reversed")]
    public async Task A_single_RTTQ_recovers_a_timed_out_submission_using_the_recorded_adjustment(
        string type, string state)
    {
        var flow = new Flow { Reply = _ => new(null, null, "Timed out.") };
        flow.Repository.Setup(repository => repository.BeginProcessingByRrnAsync(
                It.Is<string>(rrn => flow.Created != null && rrn == flow.Created.OrderNumber.Substring(0, 12)),
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback(() => flow.Calls.Add("lock")).ReturnsAsync(flow.Session.Object);
        flow.QueryReply = async (_, _) =>
        {
            var callbacks = new TransactionStatusService(flow.EventService, TimeProvider.System,
                NullLogger<TransactionStatusService>.Instance);
            await callbacks.ReceiveAsync(JsonSerializer.SerializeToElement(new
            {
                transactionUUID = "adjustment-after-lost-response", transactionType = type,
                transactionDateTime = "2026-03-04T08:02:11.417", rrn = flow.Created!.OrderNumber[..12],
                transactionAmount = 1250, currencyCode = "ZAR", txResultCode = "00", isApproved = true
            }));
            return QueryOutcome(flow, status: state);
        };

        var result = await flow.Service.SubmitAsync(flow.Request, CancellationToken.None);

        Assert.Equal(state, result.State);
        Assert.Equal(2000, result.RequestedAmount);
        Assert.Equal(1250, result.ApprovedAmount);
        Assert.Equal(0, result.RemainingAmount);
        Assert.True(result.IsFullyReconciled);
        Assert.Equal(3, flow.Events.Count);
        Assert.Equal("Ignored", Assert.Single(flow.Events, item => item.Source == "PaymentResponse").ProcessingState);
        Assert.Equal("Applied", Assert.Single(flow.Events, item => item.Source == "TransactionQuery").ProcessingState);
        Assert.Equal("Applied", Assert.Single(flow.Events, item => item.Source == "TransactionStatus").ProcessingState);
        flow.Client.Verify(client => client.SubmitAsync(It.IsAny<AcmeEnvelope>(), It.IsAny<CancellationToken>()), Times.Once);
        flow.Client.Verify(client => client.QueryAsync(result.PaymentId, It.IsAny<CancellationToken>()), Times.Once);
        flow.Session.Verify(session => session.CommitAsync(It.IsAny<CancellationToken>()), Times.Exactly(3));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(404)]
    [InlineData(409)]
    [InlineData(429)]
    [InlineData(500)]
    [InlineData(503)]
    public async Task Query_HTTP_errors_do_not_fail_or_approve_the_payment_and_are_not_retried(short status)
    {
        var flow = new Flow { Reply = _ => new(null, null, "Timed out.") };
        flow.QueryReply = (_, _) => Task.FromResult(QueryOutcome(flow) with
        {
            HttpStatusCode = status,
            RetryDelay = TimeSpan.FromSeconds(3)
        });

        var result = await flow.Service.SubmitAsync(flow.Request, CancellationToken.None);

        Assert.Equal("Unresolved", result.State);
        Assert.Null(result.ApprovedAmount);
        Assert.False(result.IsFullyReconciled);
        Assert.Equal("Pending", flow.Events[1].ProcessingState);
        flow.Client.Verify(client => client.QueryAsync(result.PaymentId, It.IsAny<CancellationToken>()), Times.Once);
        flow.Client.Verify(client => client.SubmitAsync(It.IsAny<AcmeEnvelope>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("Processing")]
    [InlineData("Reversed")]
    [InlineData("Refunded")]
    [InlineData("missing_amount")]
    [InlineData("malformed")]
    [InlineData("empty")]
    public async Task Inconclusive_query_remains_pending_without_inventing_an_outcome(string response)
    {
        var flow = new Flow { Reply = _ => new(null, null, "Timed out.") };
        flow.QueryReply = (_, _) => Task.FromResult(response switch
        {
            "missing_amount" => QueryOutcome(flow, amount: null),
            "malformed" => new AcmeReply(200, JsonSerializer.SerializeToElement("invalid body")),
            "empty" => new AcmeReply(200, null),
            _ => QueryOutcome(flow, status: response)
        });

        var result = await flow.Service.SubmitAsync(flow.Request, CancellationToken.None);

        Assert.Equal("Unresolved", result.State);
        Assert.Null(result.ApprovedAmount);
        Assert.Null(result.RemainingAmount);
        Assert.False(result.IsFullyReconciled);
        Assert.All(flow.Events, item => Assert.Equal("Pending", item.ProcessingState));
        flow.Client.Verify(client => client.QueryAsync(result.PaymentId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("transaction")]
    [InlineData("order")]
    public async Task Query_for_another_payment_is_audited_and_ignored(string mismatch)
    {
        var flow = new Flow { Reply = _ => new(null, null, "Timed out.") };
        flow.QueryReply = (_, _) => Task.FromResult(new AcmeReply(200, JsonSerializer.SerializeToElement(new
        {
            PaymentResponse = new
            {
                TransactionID = mismatch == "transaction" ? Guid.NewGuid() : flow.Created!.PaymentId,
                OrderNumber = mismatch == "order" ? "OTHERORDER" : flow.Created!.OrderNumber,
                ResponseDetail = new { Source = "Acquirer", Code = "00" }, AuthAmount = 1250
            }
        })));

        var result = await flow.Service.SubmitAsync(flow.Request, CancellationToken.None);

        Assert.Equal("Unresolved", result.State);
        Assert.Null(result.ApprovedAmount);
        Assert.Equal("Ignored", flow.Events[1].ProcessingState);
        Assert.Contains("different", flow.Events[1].ProcessingComment);
    }

    [Theory]
    [InlineData("Approved", 500)]
    [InlineData("Reversed", 0)]
    [InlineData("Refunded", 0)]
    public async Task Query_processing_reloads_persisted_state_and_preserves_changes_made_during_network_call(string state, long remaining)
    {
        var flow = new Flow { Reply = _ => new(null, null, "Timed out.") };
        flow.QueryReply = (_, _) =>
        {
            flow.Session.SetupGet(session => session.Payment).Returns(() => flow.Created! with
            {
                State = state, ApprovedAmount = 1250, RemainingAmount = remaining
            });
            return Task.FromResult(QueryOutcome(flow));
        };

        var result = await flow.Service.SubmitAsync(flow.Request, CancellationToken.None);

        Assert.Equal(state, result.State);
        Assert.Equal(1250, result.ApprovedAmount);
        Assert.Equal(remaining, result.RemainingAmount);
        Assert.Equal(remaining, flow.SavedPayment!.RemainingAmount);
        flow.Repository.Verify(repository => repository.BeginProcessingAsync(result.PaymentId,
            It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Theory]
    [InlineData("00", 1250L)]
    [InlineData("05", null)]
    public async Task Complete_submission_outcome_does_not_query(string code, long? amount)
    {
        var flow = new Flow();
        flow.Reply = envelope => BankReply(envelope, code, amount);

        await flow.Service.SubmitAsync(flow.Request, CancellationToken.None);

        flow.Client.Verify(client => client.QueryAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Callback_outcome_seen_while_processing_submission_skips_query()
    {
        var flow = new Flow { Reply = _ => new(null, null, "Timed out.") };
        flow.Session.SetupGet(session => session.Payment).Returns(() => flow.Created! with
        {
            State = "Approved", ApprovedAmount = 1250, RemainingAmount = 1250
        });

        var result = await flow.Service.SubmitAsync(flow.Request, CancellationToken.None);

        Assert.Equal("Approved", result.State);
        flow.Client.Verify(client => client.QueryAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Local_capacity_rejection_does_not_query_a_payment_that_was_never_sent()
    {
        var flow = new Flow { Reply = _ => new(null, null, "Capacity unavailable.", RequestAttempted: false) };

        var result = await flow.Service.SubmitAsync(flow.Request, CancellationToken.None);

        Assert.Equal("Unresolved", result.State);
        flow.Client.Verify(client => client.QueryAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(55, 5)]
    public async Task Query_timeout_is_capped_by_the_remaining_deadline_and_does_not_cancel_auditing(int elapsedSeconds, int querySeconds)
    {
        var clock = new ObservedTimeProvider();
        var flow = new Flow(clock);
        flow.Reply = _ =>
        {
            clock.Advance(TimeSpan.FromSeconds(elapsedSeconds));
            return new(null, null, "Connection failed.");
        };
        flow.QueryReply = QueryUntilCancelled;
        flow.Session.Setup(session => session.CommitAsync(It.IsAny<CancellationToken>()))
            .Callback((CancellationToken token) => Assert.False(token.IsCancellationRequested))
            .Returns(Task.CompletedTask);

        var submission = flow.Service.SubmitAsync(flow.Request, CancellationToken.None);
        await clock.WaitForTimerAsync(TimeSpan.FromSeconds(querySeconds));
        clock.Advance(TimeSpan.FromSeconds(querySeconds) - TimeSpan.FromMilliseconds(1));
        Assert.False(submission.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        var result = await submission.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("Unresolved", result.State);
        Assert.Equal(2, flow.Events.Count);
        Assert.Equal("TransactionQuery", flow.Events[1].Source);
        Assert.Equal("Pending", flow.Events[1].ProcessingState);
        flow.Session.Verify(session => session.CommitAsync(It.IsAny<CancellationToken>()), Times.Exactly(2));
        flow.Client.Verify(client => client.QueryAsync(result.PaymentId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Exhausted_submission_deadline_skips_query_but_still_audits_submission()
    {
        var clock = new ObservedTimeProvider();
        var flow = new Flow(clock);
        flow.Reply = _ =>
        {
            clock.Advance(TimeSpan.FromSeconds(60));
            return new(null, null, "Timed out.");
        };

        var result = await flow.Service.SubmitAsync(flow.Request, CancellationToken.None);

        Assert.Equal("Unresolved", result.State);
        Assert.Single(flow.Events);
        flow.Client.Verify(client => client.QueryAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        flow.Session.Verify(session => session.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task E29_wait_consumes_the_same_deadline_used_by_query_recovery()
    {
        var clock = new ObservedTimeProvider();
        var flow = new Flow(clock);
        var attempts = 0;
        flow.Reply = envelope => ++attempts == 1
            ? RateLimitedReply(envelope, 55)
            : new(null, null, "Timed out.");
        flow.QueryReply = QueryUntilCancelled;

        var submission = flow.Service.SubmitAsync(flow.Request, CancellationToken.None);
        await clock.WaitForTimerAsync(TimeSpan.FromSeconds(55));
        clock.Advance(TimeSpan.FromSeconds(55));
        await clock.WaitForTimerAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(5));
        var result = await submission.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("Unresolved", result.State);
        Assert.Equal(3, flow.Events.Count);
        flow.Client.Verify(client => client.SubmitAsync(It.IsAny<AcmeEnvelope>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        flow.Client.Verify(client => client.QueryAsync(result.PaymentId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Query_event_save_failure_disposes_its_session_without_committing_it()
    {
        var flow = new Flow { Reply = _ => new(null, null, "Timed out.") };
        flow.QueryReply = (_, _) => Task.FromResult(QueryOutcome(flow));
        flow.Session.Setup(session => session.SaveEventAsync(
                It.Is<PaymentEvent>(item => item.Source == "TransactionQuery"), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Query event save failed."));

        await Assert.ThrowsAsync<InvalidOperationException>(() => flow.Service.SubmitAsync(flow.Request, CancellationToken.None));

        flow.Session.Verify(session => session.CommitAsync(It.IsAny<CancellationToken>()), Times.Once);
        flow.Session.Verify(session => session.DisposeAsync(), Times.Exactly(2));
    }

    private static AcmeReply QueryOutcome(Flow flow, string code = "00", long? amount = 1250, string status = "Success") =>
        new(200, JsonSerializer.SerializeToElement(new
        {
            PaymentResponse = new
            {
                TransactionID = flow.Created!.PaymentId,
                flow.Created.OrderNumber,
                TransactionStatus = status,
                ResponseDetail = new { Source = "Acquirer", Code = code },
                AuthAmount = amount,
                FutureAcmeField = "retained"
            }
        }));

    private static async Task<AcmeReply> QueryUntilCancelled(Guid _, CancellationToken token)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("The query should be cancelled by its deadline.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return new(null, null, "Query timed out.");
        }
    }

    private static AcmeReply RateLimitedReply(AcmeEnvelope envelope, double? bufferedDelaySeconds)
    {
        return new AcmeReply(429, JsonSerializer.SerializeToElement(new
        {
            PaymentResponse = new
            {
                envelope.PaymentRequest.TransactionID,
                envelope.PaymentRequest.OrderNumber,
                TransactionStatus = "Failure",
                ResponseDetail = new { Source = "Application", Code = "E29" }
            }
        }), RetryDelay: bufferedDelaySeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null);
    }

    private static AcmeReply BankReply(AcmeEnvelope envelope, string code, long? authAmount) =>
        new(200, JsonSerializer.SerializeToElement(new
        {
            MessageHeader = new { MessageID = "independent-acme-message" },
            PaymentResponse = new
            {
                envelope.PaymentRequest.TransactionID,
                envelope.PaymentRequest.OrderNumber,
                TransactionStatus = "Success",
                ResponseDetail = new { Source = "Acquirer", Code = code },
                AuthAmount = authAmount,
                FutureAcmeField = "preserved"
            }
        }));

    private sealed class Flow
    {
        public NewPaymentRequest Request { get; } = new("app-payment", "customer", Guid.NewGuid(), "shop", 2000, "ZAR");
        public Mock<IPaymentRepository> Repository { get; } = new(MockBehavior.Strict);
        public Mock<IPaymentProcessingSession> Session { get; } = new(MockBehavior.Strict);
        public Mock<IAcmePaymentClient> Client { get; } = new(MockBehavior.Strict);
        public List<string> Calls { get; } = [];
        public List<PaymentEvent> Events { get; } = [];
        public List<PaymentEvent> SavedEvents { get; } = [];
        public Payment? Created { get; private set; }
        public Payment? SavedPayment { get; private set; }
        public Func<AcmeEnvelope, AcmeReply> Reply { get; set; } = envelope => BankReply(envelope, "00", 2000);
        public Func<Guid, CancellationToken, Task<AcmeReply>> QueryReply { get; set; } =
            (_, _) => Task.FromResult(new AcmeReply(null, null, "Query outcome unknown."));
        public PaymentService Service { get; }
        public PaymentEventService EventService { get; }

        public Flow(TimeProvider? timeProvider = null)
        {
            var clock = timeProvider ?? TimeProvider.System;
            Repository.Setup(repository => repository.GetByAppIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync((Payment?)null);
            Repository.Setup(repository => repository.GetDetailsAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new PaymentDetails("sensitive-card-token", "customer@example.com", "COFFEE", "CAPE TOWN", "WC", "ZA", "5812"));
            Repository.Setup(repository => repository.CreateAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Payment payment, CancellationToken _) =>
                {
                    Calls.Add("create");
                    Created = payment;
                    return new(true, payment);
                });
            Repository.Setup(repository => repository.BeginProcessingAsync(It.IsAny<Guid?>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    Calls.Add("lock");
                    return Session.Object;
                });
            Client.Setup(client => client.SubmitAsync(It.IsAny<AcmeEnvelope>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((AcmeEnvelope envelope, CancellationToken _) =>
                {
                    Calls.Add("send");
                    return Reply(envelope);
                });
            Client.Setup(client => client.QueryAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .Returns((Guid paymentId, CancellationToken token) =>
                {
                    Calls.Add("query");
                    return QueryReply(paymentId, token);
                });
            Session.SetupGet(session => session.Payment).Returns(() => SavedPayment ?? Created!);
            Session.Setup(session => session.InsertEventAsync(It.IsAny<PaymentEvent>(), It.IsAny<CancellationToken>()))
                .Callback((PaymentEvent receipt, CancellationToken _) =>
                {
                    Calls.Add("insert");
                    Events.Add(receipt);
                })
                .Returns(Task.CompletedTask);
            Session.Setup(session => session.GetPendingEventsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    Calls.Add("read_pending");
                    return Events.Where(receipt => receipt.ProcessingState == "Pending").ToArray();
                });
            Session.Setup(session => session.GetTransactionStatusEventsAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Events.Where(receipt => receipt.Source == "TransactionStatus").ToArray());
            Session.Setup(session => session.SavePaymentAsync(It.IsAny<Payment>(), It.IsAny<CancellationToken>()))
                .Callback((Payment payment, CancellationToken _) =>
                {
                    Calls.Add("save_payment");
                    SavedPayment = payment;
                })
                .Returns(Task.CompletedTask);
            Session.Setup(session => session.SaveEventAsync(It.IsAny<PaymentEvent>(), It.IsAny<CancellationToken>()))
                .Callback((PaymentEvent receipt, CancellationToken _) =>
                {
                    Calls.Add("save_event");
                    SavedEvents.Add(receipt);
                    var index = Events.FindIndex(item => item.PaymentEventId == receipt.PaymentEventId);
                    Events[index] = receipt;
                })
                .Returns(Task.CompletedTask);
            Session.Setup(session => session.CommitAsync(It.IsAny<CancellationToken>()))
                .Callback(() => Calls.Add("commit")).Returns(Task.CompletedTask);
            Session.Setup(session => session.DisposeAsync()).Callback(() => Calls.Add("dispose")).Returns(ValueTask.CompletedTask);
            var statusProcessor = new TransactionStatusProcessor();
            var responseProcessor = new PaymentResponseProcessor(statusProcessor);
            EventService = new(Repository.Object, responseProcessor, new PaymentEventProcessor(responseProcessor, statusProcessor), clock,
                NullLogger<PaymentEventService>.Instance);
            Service = new(Repository.Object, Client.Object,
                new AcmeRequestFactory(new(new Uri("http://localhost:5081"), Guid.NewGuid().ToString(), "sensitive-api-key"),
                    clock, new CardAcceptorNameFormatterFactory(new FnbCardAcceptorNameFormatter())),
                EventService,
                clock, NullLogger<PaymentService>.Instance);
        }

        public Payment ExistingPayment(string state) => new(Guid.NewGuid(), Request.AppPaymentId!, Request.CustomerId!,
            Request.CustomerCardId, Request.SubMerchantId!, "ORDER1234567", Request.CurrencyCode!, Request.Amount,
            state, DateTimeOffset.UtcNow);

        public void SetCreatedPayment(Payment payment) => Created = payment;
    }
}
