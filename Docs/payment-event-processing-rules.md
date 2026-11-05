# Payment Event Processing Rules

- A payment is stored before the request is sent to Acme.

## Payment State

- **Approved** — the acquirer approved the payment.
- **PartiallyRefunded** — some of the approved value has been refunded.
- **Refunded** — the full approved value has been refunded.
- **Reversed** — the full approved value has been reversed. A full reversal takes precedence over refunds.
- **Declined** — the acquirer explicitly declined the payment. Acme's `TransactionStatus=Success` does not override a decline response code such as `05` or `51`.
- **Failed** — Acme explicitly rejected the request: HTTP `400`, `401`, `409` or a final `429`. The E29 retry exception is described below.
- **Unresolved** — the outcome is not yet known, including before an Acme outcome is received, timeouts, connection failures and HTTP `500`–`504`.

- A repeated `AppPaymentId` with identical payment details returns the existing local payment without creating or submitting another payment. Reusing that identifier with different details returns HTTP `409`.

## E29 Rate-Limit Retries

- HTTP `429` with `ResponseDetail.Code=E29` and valid `Retry-After` seconds retries the same payment using its existing `TransactionID` and `OrderNumber`. Each retry has a new message header. This internal retry remains part of the original submission; repeating the app request still returns the existing local payment.
- Wait for `max(3 seconds, 2 × Retry-After)` before retrying. A value of `1` means a `3`-second wait; a value of `2` means a `4`-second wait.
- Record every E29 response before waiting. Its event remains **Pending** while awaiting a retry outcome. Reconsider it when a later response establishes the outcome.
- Keep retries within the existing `60`-second submission window. Do not shorten the required wait to fit that window. If another attempt cannot start within the window, finalize the last E29 rejection as **Failed**.
- A missing, invalid or unrepresentable `Retry-After` value does not cause a guessed delay or an automatic retry; the rejection is **Failed**.
- Timeouts, connection failures and HTTP `500`–`504` remain **Unresolved** and are not automatically retried.

## RTTQ Recovery During Submission

- If submission leaves the outcome unknown, query Acme once using the same transaction ID. Allow up to 10 seconds within the existing 60-second submission window.
- Process the query as a payment event. Inconclusive results remain unresolved for later recovery.
- An RTTQ `Refunded` snapshot without a valid `AuthAmount`, or without complete matching refund history, leaves an unknown payment **Unresolved**. Do not infer the original authorised amount from webhook amounts or infer a remaining balance from an incomplete history.
- Resolve a `Refunded` snapshot automatically only when its authoritative `AuthAmount` and distinct matching successful refund webhooks account for the full approved value. Replayed webhook identities count once. Set **Refunded** with the approved amount retained and a remaining amount of zero.
- A `Reversed` snapshot likewise requires authoritative `AuthAmount` and a matching successful full reversal.
- Conflicting snapshot and recorded event evidence remains **Unresolved** for an unknown payment. Keep an explanatory **Pending** event comment requiring investigation and log a warning with the payment and event identifiers. Preserve previously established bank outcomes when a conflicting query cannot be applied.

## Payment Events and Amounts

- All monetary values in the database are stored in the currency's lowest monetary unit, e.g., cents for ZAR: R19.99 is stored as 1999.

- Every meaningful response or notification received from Acme is stored as a payment event, even if it cannot currently be applied.

- All notifications for a specific payment must use the payment's currency. A notification in a different currency is stored as a payment event and marked **Ignored**; it must not change the payment's state or value.

- Events may arrive more than once or out of order. Normal valid adjustment histories, including exact replays, must produce the same final payment state and value regardless of arrival order. Conflicting duplicates follow the preserve-established-state policy confirmed in [Issue #34](https://github.com/RusselRillema/ZapTech/issues/34): they cannot replace an established financial effect, so different delivery orders of contradictory contents can retain different established balances pending investigation.

- An event has one of three processing states:
  - **Pending** — received, but cannot yet be applied because required lifecycle information or an E29 retry outcome is not known, or retained for investigation because adjustment contents conflict.
  - **Applied** — successfully used to update the current payment state or value.
  - **Ignored** — recorded for audit purposes but will never be applied because it conflicts with the established payment lifecycle.

- A delivery that reuses an existing `transactionUUID` with changed adjustment contents is retained as a separate **Pending** audit receipt with a conflict explanation and logged for investigation with payment and incoming event identifiers. It is not applied as a new financial event and must not change the payment's established State, ApprovedAmount or RemainingAmount, or previously Applied and Ignored receipts. If an identity has only conflicting Pending receipts, none of them can establish an adjustment when approval later arrives.

- Whenever an event is applied, any pending events for that payment should be reconsidered because they may now be applicable.

- Ignored events should include a short comment explaining why they were ignored, e.g., "Refund is greater than the current payment value."

- A refund larger than the approved payment amount is therefore ignored.

- An approval establishes the amount that can subsequently be reversed or refunded.

- `ApprovedAmount` must come from `PaymentResponse.AuthAmount` in either the response to the payment request or the Real Time Transaction Query response. Do not assume that `Advice.Amount` is an approved amount.

- A reversal can only be applied against an approved payment. Once the full approved value has been reversed, later refunds cannot reduce the payment further and are ignored.
- Partial reversals are rejected: record them as **Ignored** with a reason. A full reversal must equal the authoritative approved amount and supersedes refunds, including refunds already applied.

- Multiple refunds may be applied as long as their combined value does not exceed the remaining refundable value.
- Process refunds until the next refund would exceed the remaining value, then ignore that refund and all subsequent refunds. Use the switch transaction timestamp, then the ordinal switch event identifier, to define a stable order independent of delivery order. This ordering is an implementation default.
- Derive the current adjustment state and remaining value from the complete event history. A late event can change which earlier Applied events belong to the accepted refund sequence. Ignored events are never reapplied.

- The payment record represents the current interpreted state of the payment. Payment events remain the audit history showing how that state was reached.

- When a payment request times out and the outcome is unknown, the payment remains unresolved until Acme's transaction query or a later notification establishes the outcome.

## Reading Payment State

- Reading a payment's current state should not apply or reconcile events. State queries should remain side-effect free.

- Event processing is responsible for applying newly received events and then reconsidering any pending events for that payment.

- A separate reconciliation process should handle payments that remain unresolved, especially timeout cases that require Acme's transaction query.

- A payment state response should return the current stored state together with an indication of whether unresolved events still exist.

- Prefer exposing this as a business-facing concept such as **IsFullyReconciled** rather than exposing internal processing details directly.
