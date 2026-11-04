# Solution

## Database design

PostgreSQL 18 is permitted by the assignment and fits the containerised setup. Compose uses the official image, persistent storage and environment-based configuration. First startup creates an empty application database; tables, procedures and synthetic seeds are deferred. Initialization scripts do not upgrade existing databases, so schema evolution will need an explicit deployment process.

The administrator owns the database and will own its objects. A separate application login receives only database `CONNECT`, schema `USAGE` and procedure `EXECUTE`, with no direct table or sequence grants, creation privileges or administrator membership. Public access is revoked. Future operations will use schema-qualified procedure calls with named arguments and bound values.

Table-accessing procedures must use `SECURITY DEFINER` with a trusted search path such as `pg_catalog, public, pg_temp`; transactions belong to the caller. Future procedure deployments must explicitly grant application execution. Default public routine execution is revoked; default application grants are avoided because PostgreSQL cannot distinguish functions from procedures in those grants.

Passwords come from an ignored local environment file, with SCRAM-SHA-256 verifiers stored in PostgreSQL. Changing the file does not rotate existing passwords. Host access is limited to loopback. An authenticated `SELECT 1` health check verifies connectivity without accessing tables or proving schema correctness.

## Stub design and trade-offs

The standalone .NET 8 ASP.NET Core Minimal API uses System.Text.Json and runs locally or in a non-root container.

- In-memory storage keeps the supporting stub small. Concurrent submissions replay the original outcome for 24 hours; RRNs stay reserved until restart. Queries reflect reversals and refunds.
- A bounded callback queue and one worker reuse HTTP connections. Attempts time out after ten seconds; failures receive three retries, one second apart, preserving body bytes and event identity.
- Shutdown drains callbacks until the host deadline. Restart loses transactions and pending callbacks.
- Valid payments receive synthetic approval. Optional controls support full reversals/refunds; partial outcomes and further simulations are deferred.

## Validation and AI workflow

Prior stub validation recorded a Release build with zero warnings and 24 passing xUnit cases on .NET 8.0.24, covering live HTTP validation, authentication, queries, concurrency and callbacks. Docker images remain unverified. Database validation is paused until Docker is installed; initialization, permissions, credentials and persistence have not been exercised. Check these against the actual Compose setup without copied schemas or database fixtures.

Codex used the assignment, repository, host inspection and official documentation to implement the bootstrap. The user chose environment-file passwords, two logins and procedure-only access. Documentation was reviewed against the assignment and shortened to separate run instructions from design notes; runtime verification remains pending.

## Remaining deliverables

Payment processing, notification handling, schema, synthetic seeds and daily reporting remain unfinished. AWS deployment reasoning, production secrets/scaling/diagnostics, the full AI workflow record and the 5–10 minute walkthrough are still required. No AWS deployment is required. Deferred stub scenarios are listed in [Docs/TODO.md](Docs/TODO.md).
