# Zapper Assessment

Run the payment API, Acme Pay stub and PostgreSQL locally using either option below. Commands use Bash (Linux, macOS or Windows via WSL/Git Bash), from the repository root. The copy commands ask before replacing an existing `.env`.

## Run with Docker

Requires Docker with Compose and a running Docker engine. The example includes all settings and development credentials; no editing or local .NET/PostgreSQL installation needed.

```bash
docker --version
docker compose version
docker info
cp -i .env.docker.example .env
docker compose config --quiet
docker compose up --build --wait
docker compose ps
```

Stop with `docker compose down`; database data is retained. **Adding `--volumes` deletes the database data.** Port 5432 must be free, or change `POSTGRES_PORT` in `.env`. HTTP ports default to 5080/5081; change `PAYMENT_HTTP_PORT` / `ACME_HTTP_PORT` if needed.

## Run natively

Requires the .NET 8 SDK, PostgreSQL 18 running locally, and PostgreSQL command-line tools. Check dependencies and server availability:

```bash
dotnet --list-sdks
dotnet --list-runtimes
psql --version
pg_isready -h 127.0.0.1 -p 5432
cp -i env.local.example .env
```

Edit `.env`: replace the example Acme credentials. The database connection string matches the setup below; adjust its host/port if your PostgreSQL server differs.

Create the database **once**, using your PostgreSQL administrator account (replace `postgres` if needed). This creates the schema, procedures and sample customers/cards/merchants:

```bash
psql -h 127.0.0.1 -p 5432 -U postgres -d postgres -f Source/PaymentIntegration/Database/setup.sql
```

Start Acme in one terminal:

```bash
dotnet run --project Source/AcmePay.Stub -- --urls http://127.0.0.1:5081
```

Start the payment API in a second terminal, also from the repository root:

```bash
dotnet run --project Source/PaymentIntegration -- --urls http://127.0.0.1:5080
```

Stop each with Ctrl+C. The database persists; restarting Acme clears its in-memory transactions and callbacks.

## Check it is running

For either mode, run in another terminal (`curl` required):

```bash
curl -fS http://127.0.0.1:5080/health
curl -fS http://127.0.0.1:5081/health
```

Both should return `{"status":"healthy"}`. These are liveness checks; use the approval demo below to check the payment flow. For Docker troubleshooting: `docker compose logs --tail=100 db acme payments`.

Both native apps load `.env`; existing shell variables take precedence in both modes. Use the matching environment template when switching modes: native URLs use the host, Docker URLs use service names. Keep `.env` private. The development database login is `payment_integration_app`, password `payment_integration_dev_only`, database `payment_integration`.

Database setup only supports a fresh database/login and Docker only initializes empty storage. Existing databases need explicit schema deployment; restarting does not upgrade or repair them. Setup/removal details: [database scripts](Source/PaymentIntegration/Database/).

## Demos and tests

With the stack running and `ACME_ENABLE_CONTROLS=true` (the template default), run an approval demo:

```bash
# Docker
docker compose build demo
docker compose run --rm --no-deps -T demo --list
docker compose run --rm --no-deps -T demo approval

# Native (use instead of the Docker commands)
dotnet run --project Source/PaymentIntegration.Demo -- approval
```

Run scenarios sequentially against a dedicated local stack; they retain payments for audit. With Docker and Bash, `bash Scripts/run-demos.sh --include-adjustments` starts the stack and runs all twelve scenarios, including refunds/reversals. Use `bash Scripts/run-demos.sh --help` for options.

Native demos default to ports 5080/5081; override `DEMO_PAYMENT_BASE_URL` / `DEMO_ACME_BASE_URL` for other ports. Optional `DEMO_CUSTOMER_ID`, `DEMO_CUSTOMER_CARD_ID`, `DEMO_SUB_MERCHANT_ID`, `DEMO_AMOUNT` and `DEMO_CURRENCY_CODE` select existing records and values (defaults match the API example).

Run application tests with the .NET SDK; no running services or credentials needed:

```bash
dotnet test ZenTech.sln --configuration Release
```

## Call the API

Submit a payment using the seeded records (amount is in minor units: `1999` = R19.99):

```bash
curl -sS http://127.0.0.1:5080/payments \
  -H 'Content-Type: application/json' \
  -d '{"appPaymentId":"app-payment-001","customerId":"860e4537-28e4-4b83-a399-d4bd13f63a26","customerCardId":"8ee3f008-84f5-4152-80a5-21821abdd045","subMerchantId":"ZAP9193145856","amount":1999,"currencyCode":"ZAR"}'

curl -sS 'http://127.0.0.1:5080/payments?appPaymentId=app-payment-001'
curl -fS 'http://127.0.0.1:5080/reports/payments/daily?date=2026-11-05' -o daily-payments.pdf
```

Keep the same `appPaymentId` when retrying: identical details return the stored payment; changed details return 409. Reports accept `yyyy-MM-dd` and group daily counts/totals by sub-merchant and currency.

The environment templates configure Acme callbacks to `POST /notifications/payment-advice` (Basic authentication) and `POST /notifications/transaction-status` (HMAC signature). API callers need no authentication.

See [SOLUTION.md](SOLUTION.md) for design, simulated Acme behaviour, verification limits and unfinished work (including further reconciliation), and the [assignment PDF](Docs/Zapper%20-%20Team_Lead%20Assignment.pdf) for requirements.
