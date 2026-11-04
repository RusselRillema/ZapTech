using System.Text.Json;
using Moq;
using PaymentIntegration.Acme;
using PaymentIntegration.Payments;
using Xunit;

namespace PaymentIntegration.Tests;

public sealed partial class PaymentServiceTests
{
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
}
