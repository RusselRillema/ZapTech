# Zapper Assessment

.NET 8 PaymentIntegration host, Acme Pay stub and PostgreSQL 18 bootstrap with seeded customer, sub-merchant and card tables for [the assignment](Docs/Zapper%20-%20Team_Lead%20Assignment.pdf). Payment processing, payment lifecycle tables, procedures and reporting remain unfinished.

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

For Acme, copy [env.local.example](env.local.example) (native) or [.env.docker.example](.env.docker.example) (containers) to root `.env` using your editor/file manager. Preserve customised files; replace example Acme credentials and callback URLs. For Docker's database, also set `POSTGRES_PASSWORD`; `POSTGRES_USER` defaults to `postgres` and `POSTGRES_PORT` to `5432`.

Both native applications load optional `.env` settings without overriding existing environment variables; Compose gives shell variables precedence too. Single-quote literal `$`/`#` values. Keep `.env` private: it contains plaintext secrets and is excluded from Git and Docker builds. Required settings are checked by the service being started.

PaymentIntegration currently exposes only [http://127.0.0.1:5080/health](http://127.0.0.1:5080/health) and needs no database, Acme service or credentials. Its native command explicitly selects port 5080, overriding the shared `.env` setting for Acme's port 5081. Compose currently runs Acme and the database.

## Database setup and removal

Using your existing privileged `psql` connection, outside a transaction, run:

```text
\i Source/PaymentIntegration/Database/setup.sql
```

The script reads no `.env` files. It creates `payment_integration`, owned by the connected role, and the non-admin login `payment_integration_app` with password `payment_integration_dev_only`. It includes separate table and seed scripts, loading five synthetic customers, three sub-merchants and eight tokenised cards. These are fixed development credentials; use the same values when configuring a database client. Existing target databases/logins are rejected before creation, without resetting passwords or deleting data.

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

## Acme operation

Both modes default to [http://localhost:5081/health](http://localhost:5081/health). Native listening uses `ASPNETCORE_URLS`; Docker's example listens on port 8080 internally and publishes `ACME_HTTP_PORT` (default 5081). Stop native Acme with Ctrl+C. Restart loses its in-memory transactions and queued callbacks.

Native callbacks use `localhost`; container callbacks use `host.docker.internal` with Compose's host-gateway mapping. Receivers must be reachable from the container; Linux containers cannot reach a host listener bound only to loopback. No receiver is supplied.

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

Docker startup, initial table creation and sample seeding were verified on Linux. See [SOLUTION.md](SOLUTION.md) for design and verification limits, and [Docs/TODO.md](Docs/TODO.md) for deferred stub scenarios.
