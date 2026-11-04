# Zapper Assessment

.NET 8 Acme Pay stub and PostgreSQL 18 bootstrap for [the assignment](Docs/Zapper%20-%20Team_Lead%20Assignment.pdf). The payment integration service, application schema, seed data and reporting are not implemented yet.

## Setup

Use Docker Engine with Compose v2 for the database, and the .NET 8 SDK to run the stub or tests locally. PostgreSQL clients are included in the database image.

On Linux Mint 22.3, follow [Docker's Ubuntu instructions](https://docs.docker.com/engine/install/ubuntu/) using the `noble` repository. Required packages: `docker-ce`, `docker-ce-cli`, `containerd.io`, `docker-buildx-plugin` and `docker-compose-plugin`. Docker does not officially support Mint derivatives.

On first setup only (keep an existing `.env`):

```bash
cp .env.example .env
chmod 600 .env
```

Set `POSTGRES_PASSWORD`, `PAYMENT_DB_PASSWORD` and the Acme settings in `.env`; see [.env.example](.env.example) for names and defaults. Use different database passwords and single-quote values containing `$` or `#`. This ignored file stores plaintext secrets; do not commit it or share expanded container configuration.

## Database

From the repository root:

```bash
docker compose --env-file .env up -d --wait db
docker compose --env-file .env ps
docker compose --env-file .env logs --tail=100 db
```

Defaults are database `payment_integration`, administrator `postgres` and application login `payment_integration_app`. Connect from the host at `127.0.0.1:5432` (`POSTGRES_PORT` can change the host port), or from Compose services at `db:5432`.

First startup creates the administrator-owned database and a separate application login with procedure execution access. No application tables, procedures or seed records are created. Docker checks database health inside the container using the application login.

Stop with `docker compose --env-file .env down`; the named volume retains the database. Adding `--volumes` deletes it. Initialization runs only on an empty volume: adding scripts or changing passwords in `.env` does not update an existing database. After a bootstrap failure, inspect the logs and repair as administrator; restarting does not resume initialization.

## Acme stub

Set the Acme credentials and callback URLs in `.env`, then run locally:

```bash
set -a
. ./.env
set +a
dotnet run --project Source/AcmePay.Stub --urls http://localhost:5081
```

Alternatively, build and run its container. For callback receivers on the host, use `host.docker.internal` in their URLs:

```bash
docker build -f Source/AcmePay.Stub/Dockerfile -t acmepay-stub .
docker run --rm --name acmepay-stub --env-file .env \
  --add-host host.docker.internal:host-gateway \
  -p 127.0.0.1:5081:8080 acmepay-stub
```

Health: `GET /health`. Both API routes require `Authorization: Bearer <ACME_API_KEY>`:

- `POST /api/payment`
- `GET /apirttq/query/Merchant/{MerchantID}?TransactionID={id}` (or `OrderNumber={reference}`)

With `ACME_ENABLE_CONTROLS=true`, these bearer-protected routes send full-amount status callbacks using a body such as `{"Amount":1999}`:

- `POST /__stub/transactions/{id}/reversal`
- `POST /__stub/transactions/{id}/refund`

Payment Advice uses Basic authentication; status callbacks use an HMAC signature.

## Validation

```bash
dotnet test ZenTech.sln --configuration Release
```

Database validation is paused until Docker is installed; the bootstrap has not been run. See [SOLUTION.md](SOLUTION.md) for design and verification limits, and [Docs/TODO.md](Docs/TODO.md) for deferred stub scenarios.
