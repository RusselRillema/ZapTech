# Payment Event Processing Rules

- A payment is stored before the request is sent to Acme.

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

- An approval establishes the amount that can subsequently be reversed or refunded.

- A reversal can only be applied against an approved payment. Once the full approved value has been reversed, later refunds cannot reduce the payment further and are ignored.

- A refund can only be applied against an approved value that has not already been refunded or reversed.

- Multiple refunds may be applied as long as their combined value does not exceed the remaining refundable value.

- A refund that would exceed the remaining refundable value is ignored in full. The system must not partially apply an event or invent a different amount from the one reported by Acme.

- A refund larger than the approved payment amount is therefore ignored.

- The payment record represents the current interpreted state of the payment. Payment events remain the audit history showing how that state was reached.

- When a payment request times out and the outcome is unknown, the payment remains unresolved until Acme's transaction query or a later notification establishes the outcome.

## Reading Payment State

- Reading a payment's current state should not apply or reconcile events. State queries should remain side-effect free.

- Event processing is responsible for applying newly received events and then reconsidering any pending events for that payment.

- A separate reconciliation process should handle payments that remain unresolved, especially timeout cases that require Acme's transaction query.

- A payment state response should return the current stored state together with an indication of whether unresolved events still exist.

- Prefer exposing this as a business-facing concept such as **IsFullyReconciled** rather than exposing internal processing details directly.
