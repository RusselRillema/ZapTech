# Zapper Assessment

.NET 8 Acme Pay stub for [the assignment](Docs/Zapper%20-%20Team_Lead%20Assignment.pdf). The payment integration service, database and reporting are not implemented yet.

## Choose a startup mode

Run commands from the repository root. The commands below work in Windows PowerShell, macOS and Linux terminals; Docker uses Linux containers. Acme and its tests do not require PostgreSQL.

| Mode | Prerequisite checks | Configuration example |
| --- | --- | --- |
| Native | `dotnet --list-sdks` and `dotnet --list-runtimes`: confirm the .NET 8 SDK, `Microsoft.NETCore.App 8.x` and `Microsoft.AspNetCore.App 8.x`. | [env.local.example](env.local.example) |
| Docker Compose | `docker --version`, `docker compose version` and `docker info`: confirm the CLI, Compose plugin and accessible engine. | [.env.docker.example](.env.docker.example) |

Install missing prerequisites using the official [.NET](https://dotnet.microsoft.com/download/dotnet/8.0) or [Docker](https://docs.docker.com/get-started/get-docker/) instructions for your OS. Native startup needs no Docker; container startup needs no host .NET installation.

Using your editor or file manager, copy the selected example to `.env` in the repository root and replace the example credentials and callback URLs. Preserve your customised values when switching modes. Keep `.env` private; it is ignored by Git and Docker builds. Single-quote values containing literal `$` or `#`, such as `ACME_ADVICE_PASSWORD='local$example#value'`.

Acme loads `.env` before startup without replacing existing environment variables. Compose also gives existing shell variables precedence over `.env`. In AWS, supply the environment variables directly; the file is optional.

## Run natively

```text
dotnet run --project Source/AcmePay.Stub
```

`ASPNETCORE_URLS` in `.env` controls the listening address. Stop with Ctrl+C.

## Run with Docker Compose

```text
docker compose config --quiet
docker compose up --build -d acme
docker compose ps
docker compose logs --tail=100 acme
```

`ACME_HTTP_PORT` sets the host port (default 5081); keep the Docker example's internal listening port at 8080. Stop and remove the container and its network with:

```text
docker compose down
```

Both modes default to [http://localhost:5081/health](http://localhost:5081/health); open it to confirm the response is `{"status":"healthy"}`. Adjust the URL if you change the port. Restarting clears transactions and pending callbacks.

For callbacks to a receiver on your host, native mode uses `localhost`; Docker uses `host.docker.internal`. Compose includes the Linux host-gateway mapping. The receiver must listen on an address reachable from the container; Linux containers cannot reach a host listener bound only to loopback. A callback receiver is not supplied by this stub.

## API

Payment and query payloads follow Appendix A. Both require `Authorization: Bearer <ACME_API_KEY>`.

- `POST /api/payment`
- `GET /apirttq/query/Merchant/{MerchantID}?TransactionID={id}` (or `OrderNumber={reference}`)

With `ACME_ENABLE_CONTROLS=true`, these bearer-protected routes send full-amount status callbacks using a body such as `{"Amount":1999}`:

- `POST /__stub/transactions/{id}/reversal`
- `POST /__stub/transactions/{id}/refund`

Payment Advice uses the configured Basic credentials. Status webhooks carry a Base64 HMAC-SHA256 signature of the exact body bytes in `x-signature`.

Valid payments are approved. Partial events are unsupported.

## Tests

The existing tests start their own stub and HTTP callback receiver; they need no `.env` or separately running services. With the .NET 8 SDK:

```text
dotnet test ZenTech.sln --configuration Release
```

Alternatively, after the Docker prerequisite checks, run the same suite in a temporary SDK container:

```text
docker run --rm --mount type=bind,source=.,target=/src,readonly --workdir /src mcr.microsoft.com/dotnet/sdk:8.0 dotnet test ZenTech.sln --configuration Release --artifacts-path /tmp/test-artifacts --results-directory /tmp/test-results
```

Source is mounted read-only; build artifacts and test results stay inside the temporary container.

See [SOLUTION.md](SOLUTION.md) for design and verification limits, and [Docs/TODO.md](Docs/TODO.md) for deferred scenarios.
