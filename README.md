# Zapper Assessment

.NET 8 Acme Pay stub and initial PostgreSQL database setup for [the assignment](Docs/Zapper%20-%20Team_Lead%20Assignment.pdf). The payment integration service, application schema and reporting are not implemented yet.

## Database prerequisites and configuration

The database runs in the official `postgres:18` image with Docker Compose. No PostgreSQL server, client or .NET installation is required on the host to run the database; `psql` is included in the image. The application will require .NET 8 when implemented.

The local machine inspected on 4 November 2026 runs Linux Mint 22.3 (Ubuntu `noble`, x86-64). Docker and Compose were absent, port 5432 was free, and .NET SDKs 9 and 10 were installed without .NET 8.

Install the following Docker packages using [Docker's Ubuntu installation instructions](https://docs.docker.com/engine/install/ubuntu/): `docker-ce`, `docker-ce-cli`, `containerd.io`, `docker-buildx-plugin` and `docker-compose-plugin`. Use the Ubuntu `noble` repository for this machine; Docker does not officially support Mint derivatives. `ca-certificates` and `curl` are already installed. Install the .NET 8 SDK alongside the existing user-managed SDKs when application development starts; see [Microsoft's installation instructions](https://learn.microsoft.com/en-us/dotnet/core/install/linux).

On first setup, create the local environment file below. If it already exists, edit it instead of overwriting it:

```bash
cp .env.example .env
chmod 600 .env
```

Set both passwords in that file before starting Compose. Use different values. Single-quoted values preserve literal `$` and `#` characters; escape a single quote inside a value as `\'`. The committed example intentionally leaves passwords empty.

| Variable | Default | Purpose |
| --- | --- | --- |
| `POSTGRES_USER` | `postgres` | Administrator; owns the database and performs initialization and future schema deployment. |
| `POSTGRES_DB` | `payment_integration` | Application database created by the official entrypoint. |
| `POSTGRES_PASSWORD` | Required | Administrator password. |
| `PAYMENT_DB_USER` | `payment_integration_app` | Separate application login; must differ from the administrator. |
| `PAYMENT_DB_PASSWORD` | Required | Application login password. |
| `POSTGRES_PORT` | `5432` | Host port, bound only to `127.0.0.1`. |

Compose reads the selected environment file and passes these database settings to the container; shell environment values take precedence. The official entrypoint reads `POSTGRES_PASSWORD` to create the administrator, and the initialization script reads `PAYMENT_DB_PASSWORD` to create the application login. Passwords are not embedded in SQL files or command-line arguments. The environment file stores plaintext passwords locally and is ignored by Git and Docker build contexts. Container environment settings are also visible to users with Docker access. Do not share expanded Compose configuration or container environment output containing credentials.

PostgreSQL stores SCRAM-SHA-256 password verifiers. These credentials initialize an empty data directory only: editing the environment file does not change existing database passwords. Password rotation requires an explicit administrator operation and corresponding environment updates. The future application container must receive only its own credentials.

## Run the database locally

Run the following commands from the repository root once Docker is installed and usable by your account:

```bash
docker compose --env-file .env up -d --wait db
docker compose --env-file .env ps
docker compose --env-file .env logs --tail=100 db
```

Use `--env-file` with another local file if needed. Empty or missing passwords cause Compose to stop with an error before starting the database.

The official entrypoint creates the database on an empty data volume, then runs the scripts mounted read-only at `/docker-entrypoint-initdb.d` in filename order. The initial script creates the application login and its permissions in one transaction. It creates no application tables, procedures or seed records.

There are exactly two configured logins: administrator and application. The application receives database `CONNECT`, schema `USAGE`, and `EXECUTE` on application procedures in `public`. It has no direct table or sequence grants, schema creation privileges, temporary-table privileges or administrator membership. PostgreSQL's built-in roles and system catalogs remain part of the engine.

Future data access will use schema-qualified stored procedure calls with named arguments and bound values. Procedures that access tables must use `SECURITY DEFINER`, be owned by the administrator, and fix a trusted search path such as `pg_catalog, public, pg_temp`. Control their transactions in the caller: security-definer procedures cannot issue `COMMIT` or `ROLLBACK`. Initialization revokes default `PUBLIC` execution of new routines created by the administrator. Each future procedure deployment must grant the application `EXECUTE` after creation; default execution grants cannot distinguish procedures from functions. Do not add direct table grants.

The database is reachable from the host at `127.0.0.1:<POSTGRES_PORT>` and from future Compose services at `db:5432`. Use `POSTGRES_DB` and the appropriate login for either connection. For an interactive application connection using the image's client:

```bash
docker compose --env-file .env exec db sh -c 'PGPASSWORD="$PAYMENT_DB_PASSWORD" exec psql --no-psqlrc --no-password --host=127.0.0.1 --username="$PAYMENT_DB_USER" --dbname="$POSTGRES_DB"'
```

For development administration, use the administrator login over TCP and enter `POSTGRES_PASSWORD` at the prompt:

```bash
docker compose --env-file .env exec db sh -c 'exec psql --no-psqlrc --host=127.0.0.1 --username="$POSTGRES_USER" --dbname="$POSTGRES_DB" --password'
```

Docker runs the health check inside the database container every 10 seconds. It connects over TCP as the application login and executes `SELECT 1`, which reads no tables. This verifies the engine, application database and application password; it does not verify a future application schema. Repeated failures mark the container unhealthy. No automatic restart policy is configured, so a bootstrap failure remains visible.

To stop and remove the container while keeping its database:

```bash
docker compose --env-file .env down
```

The named `paymentintegration_data` volume is mounted at `/var/lib/postgresql`, the persistence location for PostgreSQL 18 images. Normal shutdown, restart and container recreation preserve it. Do not add `--volumes` to shutdown unless you deliberately intend to delete the local database.

Initialization scripts run only on an empty data directory. Adding future schema or seed scripts will affect new databases, not existing volumes. Existing databases will need an explicit schema deployment process. A failed initialization can leave an initialized volume even though the access-configuration transaction rolled back; a restart will skip the scripts. Inspect the logs and repair as administrator, or deliberately recreate the volume only when its contents are disposable. No automatic deletion or reinitialization is provided. See the [official image documentation](https://hub.docker.com/_/postgres).

TODO: implement the payment integration application, then add build commands, service URLs, schema and a small synthetic dataset.

## Run the Acme stub locally

Requires the .NET 8 SDK. Use the local `.env` file configured above and set its Acme credentials and callback URLs before running:

```bash
set -a
. ./.env
set +a
dotnet run --project Source/AcmePay.Stub --urls http://localhost:5081
```

Health check: `GET http://localhost:5081/health`.

## Docker

If callback receivers run on your host, use `host.docker.internal` in their URLs in `.env`.

```bash
docker build -f Source/AcmePay.Stub/Dockerfile -t acmepay-stub .
docker run --rm --name acmepay-stub --env-file .env \
  --add-host host.docker.internal:host-gateway \
  -p 127.0.0.1:5081:8080 acmepay-stub
```

## API

Payment and query payloads follow Appendix A. Both require `Authorization: Bearer <ACME_API_KEY>`.

- `POST /api/payment`
- `GET /apirttq/query/Merchant/{MerchantID}?TransactionID={id}` (or `OrderNumber={reference}`)

With `ACME_ENABLE_CONTROLS=true`, these bearer-protected routes send full-amount status callbacks using a body such as `{"Amount":1999}`:

- `POST /__stub/transactions/{id}/reversal`
- `POST /__stub/transactions/{id}/refund`

Payment Advice uses the configured Basic credentials. Status webhooks carry a Base64 HMAC-SHA256 signature of the exact body bytes in `x-signature`.

Valid payments are approved. Partial events are unsupported. Transactions and pending callbacks are lost on restart.

## Tests

```bash
dotnet test ZenTech.sln --configuration Release
```

See [SOLUTION.md](SOLUTION.md) for design and verification limits, and [Docs/TODO.md](Docs/TODO.md) for deferred scenarios.

## Database validation

Database validation is paused at the user's request until Docker is installed. The bootstrap has not been run. After installation, validate the real Compose setup: missing-password rejection, first-start database creation, application authentication and permissions, absence of application tables, persistence across restart/container recreation, and incorrect-password rejection. Use catalog inspection without creating database test fixtures or seed records.
