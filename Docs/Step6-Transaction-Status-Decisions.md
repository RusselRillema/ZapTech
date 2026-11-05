# Step 6 Transaction Status decisions for review

The Transaction Status callback handles Acme Switch reversals and refunds through the shared payment event transaction. The user confirmed the lifecycle states, reversal precedence, rejection of partial reversals and refund limit handling. [Issue #34](https://github.com/RusselRillema/ZapTech/issues/34) is the authoritative decision for conflicting duplicate webhooks and supersedes the former conflict-precedence default. The remaining choices fill unspecified cases and are implemented defaults for review.

## Requirements carried forward

The [assignment](Zapper%20-%20Team_Lead%20Assignment.pdf), especially Appendix A pages 14 to 16, defines the separate webhook, signature over the exact body bytes, event identifier, original payment RRN and HTTP 200 acknowledgement. The [payment event rules](payment-event-processing-rules.md) require authoritative `AuthAmount`, currency matching, duplicate and out-of-order handling, bounded refunds, audit receipts and reconsideration of Pending events. [AGENTS.md](../AGENTS.md) requires C# calculations and named procedure calls within one connection, transaction and payment row lock.

## Confirmed rules and implementation defaults

| Case | Decision | Basis |
| --- | --- | --- |
| Payment state after an adjustment | Use `PartiallyRefunded`, `Refunded` or `Reversed`; retain `ApprovedAmount` and update `RemainingAmount`. | User confirmed these states. |
| Reversal amount | Support a full reversal equal to the authoritative approved amount. Record a partial reversal as Ignored with an explanation. Acknowledge the recorded receipt with 200, without applying the adjustment. | User rejected partial reversals; acknowledgement follows Acme's callback contract. |
| Reversal and refunds for one payment | A confirmed full reversal sets the remaining value to zero and supersedes refunds, including refunds received or processed earlier. Those refund receipts become Ignored. | User confirmed reversal precedence. |
| Distinct full reversals | Apply one logical full reversal. Choose the earliest switch timestamp, then the ordinal event identifier. Ignore the others. | Keeps the interpreted reversal count and value independent of delivery order. |
| Multiple partial refunds | Apply distinct confirmed refunds in switch transaction time order, breaking ties by ordinal event identifier. Compute remaining value from the full history. | User confirmed processing until the value would be exceeded. Timestamp and identifier ordering are chosen defaults to remove delivery-order effects. |
| Individually excessive refund | Ignore a refund larger than the approved amount, as required by the event rules. | It can never fit that payment's approval. |
| Collectively excessive refunds | Ignore the first refund that would exceed the remaining value and every refund after it in switch order. Retain the amount from the accepted refund sequence. An over-limit refund remains a barrier to later refunds even if a smaller later refund would fit. | User confirmed that refunds are processed until the amount would be exceeded, then ignored. |
| Unknown RRN | Commit an unlinked Pending receipt with `UnknownPayment`, then acknowledge it. | Matches the existing Payment Advice audit approach. Automatic linking remains separate reconciliation work. |
| Switch outcome | Apply only when `txResultCode=00` and `isApproved=true`. Both indicators negative means Ignored. Conflicting indicators mean Pending. | Both fields describe whether the switch applied the event. |
| Reused event identifier with changed business fields | Retain the conflicting delivery as a separate Pending audit receipt with a conflict explanation and log it for investigation. Preserve the payment's established State, ApprovedAmount and RemainingAmount, and all previously Applied or Ignored receipts. Do not apply the conflicting delivery as a new financial event. | User-confirmed rule in [Issue #34](https://github.com/RusselRillema/ZapTech/issues/34); supersedes the former conflict-precedence default. |
| Currency or RRN mismatch | Ignore the receipt without changing the payment's value. | Currency matching is required; mismatched references cannot adjust this payment. |
| Zero or negative amount | Keep an Ignored audit receipt. | These notifications do not describe a positive refund or reversal. |

An exact replay has the same event identifier, type, timestamp, amount and outcome indicators. JSON whitespace, property order and additional unknown fields do not create a new adjustment. Identifiers are compared as case-sensitive strings. A missing or null currency uses the specification's ZAR default; an explicit currency must match the payment.

A conflicting duplicate cannot invalidate an established financial effect. For example, after an approved amount of 1250, a refund of 500 followed by a conflicting delivery of 400 under the same UUID retains the remaining value of 750. Reversing those deliveries retains 850. Each conflicting delivery remains Pending with an explanatory comment and a warning identifying the payment and incoming receipt. Previously Applied and Ignored receipts remain unchanged. When an identity has only conflicting Pending receipts, none of those deliveries can establish an adjustment when approval later arrives.

Delivery-order independence applies to normal valid adjustment histories and exact replays. It does not require conflicting duplicates to converge to the same balance: the preserve-established-state policy retains the financial effect established before the conflict and flags the conflicting delivery for investigation. Currency and RRN mismatches remain excluded from adjustment calculations. A receipt with conflicting switch outcome indicators remains Pending for investigation.

In plain terms, an unknown RRN is a notification whose payment reference cannot be found locally. A negative switch outcome means Acme says the refund or reversal failed. A contradiction means, for example, one field says success while another says failure, or the same event identifier describes a refund of 500 in one delivery and 600 in another. Unknown references are retained for investigation; confirmed failures have no financial effect; contradictions remain visible as unreconciled events.

Before approval or before authoritative `AuthAmount` is known, a successful adjustment remains Pending. A successful adjustment against a Declined or Failed payment is Ignored. Later payment responses and Payment Advice must preserve an already-adjusted remaining value.

The payment is a current interpretation of retained receipts. An Applied adjustment may become Ignored when a valid earlier switch event changes the accepted refund sequence or a full reversal supersedes it. A conflicting duplicate cannot demote a previously Applied receipt or change an Ignored receipt. Ignored receipts are never reapplied. Exact same-identity replays retain a single representative receipt; all deliveries remain in the audit history.

## Callback and persistence behaviour

`POST /notifications/transaction-status` verifies Base64 HMAC-SHA256 using `ACME_WEBHOOK_SECRET` before parsing JSON or resolving database dependencies. Invalid authentication returns 401, malformed or invalid JSON returns 400, and authenticated non-JSON content returns 415. Unknown JSON fields are accepted and retained.

For a matching RRN, the receiver locks the payment, inserts the Pending receipt, retrieves pending events and the complete Transaction Status history, processes events in C#, saves the payment and event classifications, and commits. Submission responses, RTTQ responses and Payment Advice use the same orchestration and reconsider pending adjustments. HTTP 200 follows a successful commit, including when a receipt is Pending or Ignored. Failures roll back and return 503 so Acme can retry.

The existing unique index protects applied switch identifiers across payments. A collision involving different payments fails atomically and remains unacknowledged for investigation; this implementation does not acquire another payment's lock to rewrite its history. Payment state reads stay side-effect free. Further reconciliation, including inconclusive RTTQ lifecycle snapshots, remains separate work.

## Verification and remaining scope

Service and HTTP tests use mocked repository/session dependencies and the actual C# processors. They cover amounts, event reconsideration, delivery permutations, duplicates, authentication, validation and transaction failure handling. They do not prove PostgreSQL locks, procedure deployment, output mapping, permissions or commits.

Full end-to-end verification remains a step 6 responsibility. It must use the actual application, real deployment SQL and Acme stub in an existing supported integration environment. In this worktree no connection string or `.env` is configured, PostgreSQL does not respond on the documented local port, and Docker access is denied even outside the sandbox. No substitute database or database test harness is created. The current stub can deliver full reversals and refunds, including held and repeated callbacks; partial-refund delivery is still unsupported there.

Task 1 Step 7 has been asked to extend its existing demonstration and Compose/CI checks in a separate PR using the confirmed rules above. Its existing stack has verified other scenarios; these callback changes must be included in the tested stack before the new checks can establish end-to-end results.

Existing installations need an explicit deployment of the new procedures with the established application execute permissions; the initial `setup.sql` intentionally refuses to recreate an existing database. Task 1 demonstrations remain step 7. Daily reporting, further stub scenarios and broader reconciliation remain separate deliverables.
