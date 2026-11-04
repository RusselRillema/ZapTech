# Solution

## Stub design

`Source/AcmePay.Stub` is a standalone .NET 8 ASP.NET Core Minimal API using System.Text.Json. It runs locally or in a non-root Docker container.

- In-memory storage keeps the supporting stub small. Concurrent submissions replay the original outcome for 24 hours; RRNs remain reserved until restart. Queries reflect subsequent reversals or refunds.
- A bounded callback queue and one background worker use pooled HTTP connections. Each attempt has a ten-second timeout; failed deliveries receive three retries, one second apart, preserving body bytes and event identity.
- Shutdown drains accepted callbacks until the host deadline. Remaining work is lost when the process exits.
- All valid payments receive synthetic approval. Optional local controls support full reversals/refunds; partial outcomes and additional simulations are deferred.

Setup, endpoints and authentication are documented in [README.md](README.md).

## Validation

The Release build passed with zero warnings. All 24 xUnit cases passed on .NET 8.0.24, exercising the running stub and an HTTP callback receiver: validation, bearer authentication, queries, concurrent replay, RRN conflicts, callback credentials/signatures, status changes and retries.

Docker was unavailable, so the image has not been built or run. The payment integration service and database remain unverified.

## Remaining work

Payment integration, SQL persistence/reporting, AWS deployment notes, AI workflow documentation and the video walkthrough are unfinished. Deferred stub scenarios are listed in [Docs/TODO.md](Docs/TODO.md).
