# Solution

## Stub design

`Source/AcmePay.Stub` is a standalone .NET 8 ASP.NET Core Minimal API using System.Text.Json. It runs locally or in a non-root Docker container.

- In-memory storage keeps the supporting stub small. Concurrent submissions replay the original outcome for 24 hours; RRNs remain reserved until restart. Queries reflect subsequent reversals or refunds.
- A bounded callback queue and one background worker use pooled HTTP connections. Each attempt has a ten-second timeout; failed deliveries receive three retries, one second apart, preserving body bytes and event identity.
- Shutdown drains accepted callbacks until the host deadline. Remaining work is lost when the process exits.
- All valid payments receive synthetic approval. Optional local controls support full reversals/refunds; partial outcomes and additional simulations are deferred.

Setup, endpoints and authentication are documented in [README.md](README.md).

## Configuration

Configuration comes from environment variables, as required by the assignment. For native development, DotNetEnv loads an optional repository-root `.env` before the host is created, preserving existing variables. The native run working directory is set to the repository root. Compose reads the same file and passes the settings to its single Acme container; existing shell variables take precedence. The examples account for native and container listening addresses and callback routing. Local `.env` files are excluded from Git and Docker builds. AWS can supply the variables directly without a file.

## Validation

The Release build passed with zero warnings and all 24 existing xUnit cases passed, covering live HTTP validation, authentication, queries, concurrency and callbacks. Validation ran on Linux with .NET 8 SDK 8.0.427 installed separately under `/tmp`. Native startup checks verified repository-root `.env` loading, literal `$`/`#` values, environment overrides, environment-only startup, health responses, authentication, shutdown and missing-setting rejection. Compose configuration checks verified the single service, loopback publishing, callback addresses, environment precedence and required settings.

Docker is installed, but daemon access is denied even outside the sandbox; image builds, Compose startup and the SDK-container test command remain unverified. Windows and macOS execution was not exercised. The payment integration service and database are not implemented.

## Remaining work

Payment integration, SQL persistence/reporting, AWS deployment notes, AI workflow documentation and the video walkthrough are unfinished. Deferred stub scenarios are listed in [Docs/TODO.md](Docs/TODO.md).
