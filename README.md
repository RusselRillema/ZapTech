# Zapper Assessment

.NET 8 payment submission, status and Payment Advice service, Acme Pay stub and PostgreSQL 18 schema for [the assignment](Docs/Zapper%20-%20Team_Lead%20Assignment.pdf). Reversal/refund callbacks, further reconciliation and reporting remain unfinished.

## Choose a startup mode

Run commands from the repository root. Native database deployment uses your existing PostgreSQL server and administrator connection; Acme and its tests need no database.

| Product / mode | Quick prerequisites | Start |
| --- | --- | --- |
| Native database | `psql --version` (18.x); existing privileged connection | Run the setup script below. |
| Native Acme | `dotnet --list-sdks`, `dotnet --list-runtimes`; .NET 8 SDK and runtimes | `dotnet run --project Source/AcmePay.Stub` |
| Native PaymentIntegration | .NET 8 SDK and runtimes | `dotnet run --project Source/PaymentIntegration -- --urls http://127.0.0.1:5080` |
| Compose | `docker --version`, `docker compose version`, `docker info` | `docker compose up --build -d` |
| Existing tests | .NET 8 SDK, or Docker | Commands below; no running services or credentials needed. |

Install missing software using the official [PostgreSQL](https://www.postgresql.org/download/), [.NET](https://dotnet.microsoft.com/download/dotnet/8.0) or [Docker](https://docs.docker.com/get-started/get-docker/) instructions. Container startup needs no host PostgreSQL or .NET installation.

Copy [env.local.example](env.local.example) (native) or [.env.docker.example](.env.docker.example) (containers) to root `.env` using your editor/file manager. Preserve customised files; replace example Acme credentials and callback URLs. PaymentIntegration also needs `ACME_BASE_URL` and `PAYMENT_DATABASE_CONNECTION_STRING`. For Docker's database, set `POSTGRES_PASSWORD`; `POSTGRES_USER` defaults to `postgres` and `POSTGRES_PORT` to `5432`.

Both native applications load optional `.env` settings without overriding existing environment variables; Compose gives shell variables precedence too. Single-quote literal `$`/`#` values. Keep `.env` private: it contains plaintext secrets and is excluded from Git and Docker builds. Required settings are checked by the service being started.

PaymentIntegration exposes [http://127.0.0.1:5080/health](http://127.0.0.1:5080/health) without external dependencies; payment submissions require the database and Acme configuration, while status reads require only the database configuration. Its native command selects port 5080, overriding the shared `.env` setting for Acme's port 5081. Compose currently runs only Acme and the database; run PaymentIntegration natively with the native environment settings.

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
docker compose up --build -d
docker compose ps
docker compose logs --tail=100 db acme
```

For one service, use `docker compose up -d db` or `docker compose up --build -d acme`; each can start without the other's credentials. Database access defaults to `127.0.0.1:5432` on the host and `db:5432` inside Compose. Docker's initializer runs the same setup SQL after creating the server. Its authenticated health check runs inside the database container and reads no application tables.

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

Both modes default to [http://localhost:5081/health](http://localhost:5081/health). Native listening uses `ASPNETCORE_URLS`; Docker's example listens on port 8080 internally and publishes `ACME_HTTP_PORT` (default 5081). Stop native Acme with Ctrl+C. Restart loses its in-memory transactions and queued callbacks.

Native callbacks use `localhost`; container callbacks use `host.docker.internal` with Compose's host-gateway mapping. Receivers must be reachable from the container; Linux containers cannot reach a host listener bound only to loopback. PaymentIntegration receives Payment Advice; the Transaction Status receiver remains deferred.

Both API routes require `Authorization: Bearer <ACME_API_KEY>`:

- `POST /api/payment`
- `GET /apirttq/query/Merchant/{MerchantID}?TransactionID={id}` (or `OrderNumber={reference}`)

With `ACME_ENABLE_CONTROLS=true`, bearer-protected `POST /__stub/transactions/{id}/reversal` and `POST /__stub/transactions/{id}/refund` send full-amount status callbacks using a body such as `{"Amount":1999}`. Payment Advice uses Basic authentication; status callbacks use Base64 HMAC-SHA256 over the exact body bytes in `x-signature`. Valid payments are approved; partial events are unsupported.

## Tests and verification

```text
dotnet test ZenTech.sln --configuration Release
```

Or run the same suite in a temporary SDK container, without a local .NET installation:

```text
docker run --rm --mount type=bind,source=.,target=/src,readonly --workdir /src mcr.microsoft.com/dotnet/sdk:8.0 dotnet test ZenTech.sln --configuration Release --artifacts-path /tmp/test-artifacts --results-directory /tmp/test-results
```

The payment service tests mock the DAL and Acme client; HTTP client tests use an in-memory handler. The new procedures, their privileges and database locking have not been verified against PostgreSQL. Full end-to-end testing belongs to step 6. See [SOLUTION.md](SOLUTION.md) for design and verification limits, and [Docs/TODO.md](Docs/TODO.md) for deferred stub scenarios.
