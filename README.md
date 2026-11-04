# Zapper Assessment

.NET 8 Acme Pay stub for [the assignment](Docs/Zapper%20-%20Team_Lead%20Assignment.pdf). The payment integration service, database and reporting are not implemented yet.

## Run locally

Requires the .NET 8 SDK. Copy `.env.example` to `.env` and set credentials and callback URLs before running:

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
