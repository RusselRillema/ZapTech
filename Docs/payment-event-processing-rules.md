# Payment Event Processing Rules

- A payment is stored before the request is sent to Acme.

## Payment State

- **Approved** — the acquirer approved the payment.
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

- After processing an inconclusive submission result, make at most one Real Time Transaction Query (RTTQ) for the same persisted `TransactionID`, provided the outcome remains unknown and submission time remains. This includes timeouts, uncertain network failures, HTTP `500`–`504`, and unusable or incomplete responses such as approval without a valid `AuthAmount`.
- Keep the existing `35`-second payment HTTP timeout and `60`-second submission deadline, including E29 waits. RTTQ has a `10`-second limit, including waiting for outbound capacity, capped by the remaining submission time. Do not reset or extend the deadline for RTTQ.
- Make RTTQ calls outside database transactions. Record and process the result through the same locked event-processing flow, reloading the latest persisted payment state before applying it.
- Do not query after a complete outcome is known or after an explicit payment rejection. Repeating an app request still returns the existing payment without making another Acme call.
- Do not repeat RTTQ during submission. A query returning `Processing`, `404`, an error, or an incomplete result does not establish a payment failure; keep an unknown outcome unresolved for later callbacks or separate reconciliation.
- Audit and persist received results using the separate bounded database timeout even if the submission deadline has expired. The submission deadline is not a guarantee of the total HTTP response time.

## Payment Events and Amounts

- All monetary values in the database are stored in the currency's lowest monetary unit, e.g., cents for ZAR: R19.99 is stored as 1999.

- Every meaningful response or notification received from Acme is stored as a payment event, even if it cannot currently be applied.

- All notifications for a specific payment must use the payment's currency. A notification in a different currency is stored as a payment event and marked **Ignored**; it must not change the payment's state or value.

- Events may arrive more than once or out of order. Arrival order must not determine the payment's final state.

- An event has one of three processing states:
  - **Pending** — received, but cannot yet be applied because required lifecycle information or an E29 retry outcome is not known.
  - **Applied** — successfully used to update the current payment state or value.
  - **Ignored** — recorded for audit purposes but will never be applied because it conflicts with the established payment lifecycle.

- Whenever an event is applied, any pending events for that payment should be reconsidered because they may now be applicable.

- Ignored events should include a short comment explaining why they were ignored, e.g., "Refund is greater than the current payment value."

- A refund larger than the approved payment amount is therefore ignored.

- An approval establishes the amount that can subsequently be reversed or refunded.

- `ApprovedAmount` must come from `PaymentResponse.AuthAmount` in either the response to the payment request or the Real Time Transaction Query response. Do not assume that `Advice.Amount` is an approved amount.

- A reversal can only be applied against an approved payment. Once the full approved value has been reversed, later refunds cannot reduce the payment further and are ignored.

- Multiple refunds may be applied as long as their combined value does not exceed the remaining refundable value.

- The payment record represents the current interpreted state of the payment. Payment events remain the audit history showing how that state was reached.

- When a payment request times out and the outcome is unknown, the payment remains unresolved until Acme's transaction query or a later notification establishes the outcome.

## Reading Payment State

- Reading a payment's current state should not apply or reconcile events. State queries should remain side-effect free.

- Event processing is responsible for applying newly received events and then reconsidering any pending events for that payment.

- A separate reconciliation process should handle payments that remain unresolved, especially timeout cases that require Acme's transaction query.

- A payment state response should return the current stored state together with an indication of whether unresolved events still exist.

- Prefer exposing this as a business-facing concept such as **IsFullyReconciled** rather than exposing internal processing details directly.
