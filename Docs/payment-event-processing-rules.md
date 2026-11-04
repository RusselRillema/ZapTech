# Payment Event Processing Rules

- A payment is stored before the request is sent to Acme.

## Payment State

- **Approved** — the acquirer approved the payment.
- **Declined** — the acquirer explicitly declined the payment. Acme's `TransactionStatus=Success` does not override a decline response code such as `05` or `51`.
- **Failed** — Acme explicitly rejected the request: HTTP `400`, `401`, `409` or `429`.
- **Unresolved** — the outcome is not yet known, including before an Acme outcome is received, timeouts, connection failures and HTTP `500`–`504`.

- A repeated `AppPaymentId` with identical payment details returns the existing local payment without creating or submitting another payment. Reusing that identifier with different details returns HTTP `409`.

## Payment Events and Amounts

- All monetary values in the database are stored in the currency's lowest monetary unit, e.g., cents for ZAR: R19.99 is stored as 1999.

- Every meaningful response or notification received from Acme is stored as a payment event, even if it cannot currently be applied.

- All notifications for a specific payment must use the payment's currency. A notification in a different currency is stored as a payment event and marked **Ignored**; it must not change the payment's state or value.

- Events may arrive more than once or out of order. Arrival order must not determine the payment's final state.

- An event has one of three processing states:
  - **Pending** — received, but cannot yet be applied because required earlier lifecycle information is not known.
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
