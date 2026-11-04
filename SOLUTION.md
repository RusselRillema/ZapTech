# Solution

## Database deployment

PostgreSQL 18 is permitted by the assignment and fits containerised deployment. One psql setup script serves native and Docker operation. Native users supply their existing server and administrative connection; Docker creates its server first. The SQL creates the database outside a transaction, then configures application access transactionally. Existing database/login names are rejected before creation; setup never resets credentials or deletes existing objects. Removal is explicit and refuses to force active connections closed.

The database name and application credentials are hard-coded development values, documented in the README. SQL deployment reads no environment files. Docker administrator settings and Acme runtime settings remain environment-based, with optional local `.env` files. This development shortcut is not production secret management; that remains part of the unfinished AWS design.

The connected administrator owns the database and will own its objects. A separate non-admin application login receives `CONNECT`, schema `USAGE` and procedure `EXECUTE`, without direct table/sequence grants, creation privileges or administrator membership. Public database/schema access and default public routine execution are revoked. Password verifiers use SCRAM-SHA-256.

The shared setup includes catalogue, payment and event tables, seed data and payment procedures in its existing transaction. Customer and sub-merchant primary keys are Zapper's supplied identifiers; card friendly names are unique per customer. Five synthetic customers, three sub-merchants and eight cards demonstrate the catalogue. Procedures use `SECURITY DEFINER`, a fixed trusted search path, explicit schema qualification and bound named arguments; the caller controls transactions. Existing databases need explicit schema deployment rather than replaying initial provisioning.

Single-record reads return explicit typed output columns. The small pending-event collection returns an array of the explicit `payment_event_result` type, mapped by Npgsql. Only Acme's variable audit payload remains JSONB. Every DAL command is a named procedure call; no cursor commands are needed. Permissions are configured centrally in `setup.sql`.

Compose has independent Acme and database services with loopback publishing. A persistent volume retains PostgreSQL data; a thin first-start initializer invokes the shared setup file. Removal SQL is mounted outside the automatic initialization directory. The authenticated application-login health check proves connectivity and credentials, not future procedure correctness.

## Payment submission

`POST /payments` validates an explicit currency, positive minor-unit amount and selected stored card owned by the customer. The payment is committed before Acme is called. A unique app payment identifier coordinates creation across instances: matching repeats return the current stored outcome, and changed details return 409. Payment ID is Acme's TransactionID; a separately generated alphanumeric order reference has its first 12 characters protected by a database unique index.

Acme submission responses, RTTQ responses and Payment Advice share `PaymentEventService`: it begins a transaction, acquires the payment row lock and reads the latest persisted state. It inserts a Pending event, loads pending events, interprets them through an injected `PaymentResponseProcessor` instance, reconsiders them after an application, and atomically saves the payment and event states. Approved value comes only from AuthAmount, including partial authorisations; an acquirer decline is determined by its response code even when TransactionStatus says Success. The confirmed states distinguish Approved (acquirer approval), Declined (acquirer decline), Failed (Acme explicitly rejected the request with 400/401/409 or a final 429) and Unresolved (timeouts, connection failures or 500–504). A gateway 409 rejects this request without adopting the other transaction's ID. Unknown outcomes retain unknown amounts and require later reconciliation. Failed database transactions roll back and release their resources. Service tests verify orchestration, not database locking or commit semantics.

`POST /notifications/payment-advice` authenticates Acme's Basic credentials before parsing the body or resolving the DAL. It validates the account and payload, preserves unknown fields in the audit payload, and acknowledges only after commit. Duplicate receipts are retained but do not reapply an outcome; currency/reference mismatches are Ignored and conflicting outcomes remain Pending. Unknown transactions are recorded with a NULL payment link and Pending state for investigation, with no automatic linking mechanism in this step. Authorize/Capture advice is Ignored with `UnsupportedRequestType` in ProcessingComment. Approval advice never supplies ApprovedAmount from Advice.Amount; it remains Pending when that amount is unknown. If submission remains unresolved, the service makes one RTTQ call using the same transaction ID, allowing up to 10 seconds within the 60-second submission window. Its response reconsiders pending advice using the locked persisted state; inconclusive outcomes require later reconciliation.

The Acme client reuses pooled HTTP connections, bounds concurrent calls to 20 with 40 queued requests per instance, uses a 35-second HTTP timeout and returns each response to the service for audit. Only HTTP 429/E29 with valid Retry-After seconds is automatically retried, using the same persisted payment identifiers and an asynchronous delay of max(3 seconds, twice Retry-After). Each response is recorded before waiting; no payment transaction or client concurrency lease is held during the delay. Retries share a 60-second submission window that continues if the caller disconnects. Received responses are persisted under a separate 30-second deadline, including after the submission window expires. Structured `ILogger<T>` messages carry operation and payment/event identifiers, excluding credentials, card tokens and raw payloads. FNB descriptor rules live behind `ICardAcceptorNameFormatter`; another bank can supply an implementation through DI without changing payment orchestration. FNB uses the confirmed ZAP* prefix and rejects oversized merchant/city fields.

## Payment status

`GET /payments?appPaymentId=...` uses the existing read-only `payments_get_by_app_id` procedure, which returns stored payment state and pending-event existence from one database snapshot. A query parameter accommodates the same identifiers accepted by submission, including URL-encoded slashes. The read service depends only on the repository and requires no Acme configuration. Submission and status share the response projection: `IsFullyReconciled` requires Approved, Declined or Failed state and no pending events. Found payments return 200 even when unresolved; unknown app identifiers return 404. Reads neither process pending events nor query Acme; reconciliation remains separate.

## Acme design and configuration

The .NET 8 Minimal API runs natively or in a non-root container. In-memory storage keeps the supporting stub small: concurrent submissions replay the original outcome for 24 hours, RRNs remain reserved until restart, and queries reflect reversals/refunds. Valid payments are approved; optional controls support full reversals/refunds. Partial outcomes and further simulations are deferred.

A bounded callback queue and one worker reuse HTTP connections. Attempts time out after ten seconds; failures receive three retries, one second apart, preserving bytes and event identity. Shutdown drains accepted work until the host deadline; restart loses transactions and queued callbacks.

DotNetEnv loads an optional repository-root `.env` before native host creation without replacing existing variables. Compose maps only each service's settings, also preserving shell precedence; required values are checked when that service starts. AWS can supply runtime variables directly. Native/container examples account for listening addresses and callback routing.

## Validation and AI workflow

The current Release suite passes on Linux, including service tests with mocked DAL/Acme dependencies, HTTP contract/error tests and the existing Acme tests. These verify application behavior without a database. The new procedure deployment, output mapping, privileges, row locking and database transactions remain unverified against PostgreSQL; full end-to-end verification is deferred to step 6. No database fixtures or substitute database harnesses are used.

Earlier bootstrap work verified Docker startup and the actual catalogue setup/seed scripts on Linux. That historical verification does not cover the current payment tables and procedures. Native deployment, removal SQL, restart persistence and Windows/macOS execution remain unverified.

Codex used the assignment, repository, related startup discussion and official documentation. The user selected shared SQL, hard-coded development credentials and procedure-only access, avoiding an extra launcher. Static checks and documentation review support this update; actual database verification must use these deployment scripts without copied schemas, database test harnesses or fabricated fixtures.

## Remaining deliverables

Reversal/refund processing, further reconciliation, daily reporting, AWS deployment reasoning, production operability/secrets, the full AI workflow record and the 5–10 minute walkthrough remain unfinished. No AWS deployment is required. Deferred stub scenarios are listed in [Docs/TODO.md](Docs/TODO.md).
