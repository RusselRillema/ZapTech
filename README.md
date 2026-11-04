# Zapper Assessment

.NET 8 payment submission, status and Payment Advice service, Acme Pay stub and PostgreSQL 18 schema for [the assignment](Docs/Zapper%20-%20Team_Lead%20Assignment.pdf). Reversal/refund callbacks, further reconciliation and reporting remain unfinished.

## Choose a startup mode

Run commands from the repository root. Native database deployment uses your existing PostgreSQL server and administrator connection; Acme and its tests need no database.

| Product / mode | Quick prerequisites | Start |
| --- | --- | --- |
| Native database | `psql --version` (18.x); existing privileged connection | Run the setup script below. |
| Native Acme | `dotnet --list-sdks`, `dotnet --list-runtimes`; .NET 8 SDK and runtimes | `dotnet run --project Source/AcmePay.Stub` |
| Native PaymentIntegration | .NET 8 SDK and runtimes | `dotnet run --project Source/PaymentIntegration -- --urls http://127.0.0.1:5080` |
| Compose (all three services) | `docker --version`, `docker compose version`, `docker info` | `docker compose up --build --wait` |
| Existing tests | .NET 8 SDK, or Docker | Commands below; no running services or credentials needed. |

Install missing software using the official [PostgreSQL](https://www.postgresql.org/download/), [.NET](https://dotnet.microsoft.com/download/dotnet/8.0) or [Docker](https://docs.docker.com/get-started/get-docker/) instructions. Container startup needs no host PostgreSQL or .NET installation.

Copy [env.local.example](env.local.example) (native) or [.env.docker.example](.env.docker.example) (containers) to root `.env` using your editor/file manager. Preserve customised files; replace example Acme credentials and callback URLs. PaymentIntegration also needs `ACME_BASE_URL` and `PAYMENT_DATABASE_CONNECTION_STRING`. For Docker's database, set `POSTGRES_PASSWORD`; `POSTGRES_USER` defaults to `postgres` and `POSTGRES_PORT` to `5432`.

Both native applications load optional `.env` settings without overriding existing environment variables; Compose gives shell variables precedence too. Single-quote literal `$`/`#` values. Keep `.env` private: it contains plaintext secrets and is excluded from Git and Docker builds. Required settings are checked by the service being started.

PaymentIntegration exposes [http://127.0.0.1:5080/health](http://127.0.0.1:5080/health) without external dependencies; payment submissions require the database and Acme configuration, while status reads require only the database configuration. Its native command selects port 5080, overriding the shared `.env` setting for Acme's port 5081. Compose starts `payments`, `acme` and `db` together; host HTTP ports default to 5080 and 5081.

## Database setup and removal

Using your existing privileged `psql` connection, outside a transaction, run:

```text
\i Source/PaymentIntegration/Database/setup.sql
```

The script reads no `.env` files. It creates `payment_integration`, owned by the connected role, and the non-admin login `payment_integration_app` with password `payment_integration_dev_only`. It creates catalogue, payment and event tables plus payment procedures, and seeds five synthetic customers, three sub-merchants and eight tokenised cards. These are fixed development credentials; use the same values when configuring a database client. Existing target databases/logins are rejected before creation, without resetting passwords or deleting data.

For removal, stop application connections first, then explicitly run:

```text
\i Source/PaymentIntegration/Database/remove.sql
```

Only the application database and login are removed; the server and other databases remain. The scripts use psql commands and work from Windows, macOS and Linux terminals with an already configured connection.

## Docker Compose

```text
docker compose config --quiet
docker compose up --build --wait
docker compose ps
docker compose logs --tail=100 db acme payments
```

For one service, use `docker compose up -d db` or `docker compose up --build -d acme`; each can start without the other's credentials. Database access defaults to `127.0.0.1:5432` on the host and `db:5432` inside Compose. Docker's initializer runs the same setup SQL after creating the server. Its authenticated health check runs inside the database container and reads no application tables.

PaymentIntegration waits for the database's authenticated health check and Acme's HTTP health check. Both application containers run as the non-root .NET `app` user on port 8080; their `--health-check` command probes the existing liveness endpoint. Liveness does not prove payment processing or schema compatibility. Compose callbacks use `http://payments:8080`, outgoing Acme calls use `http://acme:8080`, and database calls use `db:5432`. Use the Docker environment example when all services run in containers; native host addresses are different. The Transaction Status URL is configured for step 6, whose receiver remains unfinished.

Stop/remove containers with `docker compose down`; the database volume persists. **`docker compose down --volumes` deletes the project database volume.** Initialization runs only on empty storage. A failed setup may leave partial initialization: inspect logs and repair as administrator, or explicitly remove a disposable volume. Restart does not resume setup. Existing databases require explicit future schema deployment; these scripts are not migrations.

## Submit a payment

Send JSON to `POST http://127.0.0.1:5080/payments`:

```json
{
  "appPaymentId": "app-payment-001",
  "customerId": "860e4537-28e4-4b83-a399-d4bd13f63a26",
  "customerCardId": "8ee3f008-84f5-4152-80a5-21821abdd045",
  "subMerchantId": "ZAP9193145856",
  "amount": 1999,
  "currencyCode": "ZAR"
}
```

Amount is a positive integer in the currency's lowest unit; currency and the customer's selected stored-card ID are required. The response includes the payment ID, current state, requested/approved/remaining amounts and `isFullyReconciled`. Approved, Declined and Failed outcomes return 200 when fully reconciled; unresolved outcomes return 202. Acme 400/401/409 and final 429 responses mean Failed. E29 responses with valid Retry-After seconds retry the same payment after max(3 seconds, twice Retry-After), within a 60-second submission window.

Timeouts, connection failures and 500–504 leave the outcome unknown. If submission remains unresolved and time remains, the service makes one Real Time Transaction Query (RTTQ) using the same transaction ID, allowing up to 10 seconds within that 60-second window. RTTQ can establish an Approved or Declined outcome during the same request; inconclusive results remain Unresolved for later reconciliation. Identical repeats return the saved payment without another Acme call; changed details under the same app payment ID return 409. Keep the same identifier after a timeout. Further reconciliation is deferred.

Read the stored status with `GET http://127.0.0.1:5080/payments?appPaymentId=app-payment-001`, URL-encoding the app's original payment identifier. Found payments return 200 with the same response fields as submission; unknown identifiers return 404. `isFullyReconciled` is true only for Approved, Declined or Failed payments with no pending events. Unknown amounts remain null. Reads do not apply events, change payment state or contact Acme.

Payment Advice is received at `POST /notifications/payment-advice`, matching the example `ACME_ADVICE_URL`. Configure `ACME_MERCHANT_ID`, `ACME_ADVICE_USERNAME` and `ACME_ADVICE_PASSWORD`; the receiver uses HTTP Basic authentication and returns 200 after committing the audit event and any applicable state changes. Unknown transactions are retained as unlinked Pending events for investigation. Authorize/Capture advice is Ignored with reason `UnsupportedRequestType`. Approval advice lacking an authoritative approved amount remains Pending until a submission or RTTQ response supplies it; otherwise later reconciliation is required.

## Acme operation

Both modes default to [http://localhost:5081/health](http://localhost:5081/health). Native listening uses `ASPNETCORE_URLS`; Compose fixes application listeners to port 8080 internally and publishes `ACME_HTTP_PORT` (default 5081) and `PAYMENT_HTTP_PORT` (default 5080). Stop native Acme with Ctrl+C. Restart loses its in-memory transactions, scenario configuration and callbacks.

Native callbacks use `localhost`; callbacks between Compose services use the `payments` service name. For a mixed setup with a host receiver, explicitly use `host.docker.internal` and bind that receiver to an address reachable from containers. PaymentIntegration receives Payment Advice; the Transaction Status receiver remains deferred.

Both API routes require `Authorization: Bearer <ACME_API_KEY>`:

- `POST /api/payment`
- `GET /apirttq/query/Merchant/{MerchantID}?TransactionID={id}` (or `OrderNumber={reference}`)

Payment Advice uses Basic authentication; status callbacks use Base64 HMAC-SHA256 over the exact body bytes in `x-signature`. Valid payments are approved by default; partial events are unsupported.

With `ACME_ENABLE_CONTROLS=true`, the following demo controls require the same bearer key. Use a dedicated stub instance and run scenarios sequentially: `POST /__stub/scenario` replaces defaults for all newly accepted payments, while each existing transaction retains its outcome and callback settings. `GET /__stub/scenario` shows the current defaults.

```json
{"paymentOutcome":"Declined","responseDelaySeconds":0,"holdCallbacks":false}
```

`paymentOutcome` is `Approved` (default) or `Declined` (acquirer code `05`, HTTP 200). `responseDelaySeconds` is 0–60 (default 0); `holdCallbacks` defaults to false. Send `{}` to restore defaults. Invalid updates return 400 without changing configuration.

| Control | Behaviour |
| --- | --- |
| `POST /__stub/transactions/{id}/reversal` or `/refund` | Generate a full-amount event using `{"Amount":1999}`; queue it unless this transaction holds callbacks. Returns its `transactionUUID`. |
| `GET /__stub/transactions/{id}/callbacks` | List generated callback `identifier`, `kind` and `isHeld`; reading does not send anything. |
| `GET /__stub/transactions/{id}` | Observe accepted/replayed submission count, configured reply delay and whether a delayed reply was interrupted. |
| `POST /__stub/transactions/{id}/callbacks/{identifier}/send` | Queue a held callback or replay one using its original bytes and identity; returns 202, or 503 if the queue is full. |

`isHeld` means manual release is still required. Callback lists also expose `completedAttempts`, `acknowledgements` (HTTP 200 only) and `lastHttpStatusCode` from actual HTTP deliveries. Reads do not send callbacks or process events. A 202 from the send control only means queued; wait for an increased acknowledgement count to confirm receipt.

For a timeout demonstration, configure `{"responseDelaySeconds":40,"holdCallbacks":true}` before submission: the reply exceeds PaymentIntegration's 35-second Acme timeout, but Acme retains the accepted outcome for RTTQ. The service can recover that outcome through its submission-time RTTQ call, so a timed-out Acme request can still produce a final Approved response. Observe the timeout and query in service logs; status reads during the delay show the stored Unresolved state. To demonstrate ordering, queue selected callbacks in the required order; to demonstrate duplicates, send the same identifier again. Replays do not apply a reversal/refund again. Service reversal/refund handling remains unfinished.

## Console demonstrations

Start the stack with the Docker environment settings and `ACME_ENABLE_CONTROLS=true`, then run one scenario in a disposable runner container:

```text
docker compose build demo
docker compose run --rm --no-deps -T demo --list
docker compose run --rm --no-deps -T demo approval
```

The six scenarios are `approval`, `decline`, `duplicate-submission`, `duplicate-notification`, `out-of-order` and `timeout`. The runner uses HTTP APIs only and prints actual service responses, payment identifiers and acknowledged callback counts. Exit codes are 0 for pass, 1 for a failed scenario/setup and 2 for invalid arguments. Reversal/refund demonstrations are excluded until Task 1 Step 6 is ready.

Use a dedicated demo stack and run scenarios sequentially. Each run creates a new app payment identifier using the existing seeded customer/card/sub-merchant from the submission example, retains those payments for audit, and restores the previous stub scenario settings. To use other existing catalogue records, set `DEMO_CUSTOMER_ID`, `DEMO_CUSTOMER_CARD_ID` and `DEMO_SUB_MERCHANT_ID`; `DEMO_AMOUNT` and `DEMO_CURRENCY_CODE` default to 1999 and ZAR. No data or schemas are created by the runner.

`out-of-order` delivers Payment Advice before the delayed submission response, observes an Unresolved payment without an authoritative approved amount, and waits for the response to reconcile it. `timeout` verifies Acme observed its delayed reply being interrupted, then checks the service recovered approval before any advice was released. This exercises submission-time RTTQ recovery. Ordering between reversal/refund notifications remains deferred with step 6.

For native execution, use `dotnet run --project Source/PaymentIntegration.Demo -- approval` from the repository root. It loads optional `.env` values, requires `ACME_API_KEY`, and defaults to host ports 5080/5081. Override `DEMO_PAYMENT_BASE_URL` and `DEMO_ACME_BASE_URL` for different host ports. The Compose runner uses internal service DNS automatically and needs no host .NET SDK.

## Tests and verification

```text
dotnet test ZenTech.sln --configuration Release
```

Or run the same suite in a temporary SDK container, without a local .NET installation:

```text
docker run --rm --mount type=bind,source=.,target=/src,readonly --workdir /src mcr.microsoft.com/dotnet/sdk:8.0 dotnet test ZenTech.sln --configuration Release --artifacts-path /tmp/test-artifacts --results-directory /tmp/test-results
```

The payment service tests mock the DAL and Acme client; HTTP client tests use an in-memory handler. Stub tests exercise its HTTP API and actual callback deliveries; a controlled clock avoids waiting through simulated response delays. The new procedures, their privileges and database locking have not been verified against PostgreSQL. Full end-to-end testing belongs to step 6. See [SOLUTION.md](SOLUTION.md) for design and verification limits.
