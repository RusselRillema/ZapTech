# Solution

## Database bootstrap

PostgreSQL 18 is the selected SQL database. The assignment explicitly permits it and requires a containerised local setup with environment-based configuration. The official image supplies the server, client and first-start initialization mechanism, keeping the host dependencies limited to Docker and Compose for this phase.

This increment provides an empty application database and its access configuration only. Application code, tables, stored procedures and synthetic seed data remain deferred. The completed solution will still need the assignment's schema and small demonstration dataset.

## Database access

The database has two configured logins. The administrator owns the database and will own its objects; it is available for local development and deployment. The application uses its own non-admin login. No separate owner login is introduced.

Application data access is exclusively through stored procedures with named arguments and bound values. The application receives `CONNECT` on its database, `USAGE` on `public`, and `EXECUTE` on application procedures; it receives no direct table, sequence, schema creation or temporary-table privileges. Public database and schema permissions are revoked so they do not bypass this boundary.

Future table-accessing procedures must use `SECURITY DEFINER` with administrator ownership and a fixed trusted search path, such as `pg_catalog, public, pg_temp`. The application must not be able to create objects in that path. Transactions belong to the caller because security-definer procedures cannot issue transaction-control statements. Procedure deployments must grant execution explicitly after creating procedures: PostgreSQL default routine grants cover functions and procedures together, so automatic execution grants would be broader than requested. The bootstrap revokes default `PUBLIC` execution for routines created by the administrator and grants application execution on any procedures already present in `public`.

## Local database operation

The local Compose service uses `postgres:18`, a named volume at `/var/lib/postgresql`, and a read-only initialization directory. The official entrypoint creates the configured database on an empty volume; the access script configures the application login and permissions in one transaction. Existing volumes bypass initialization. First-start scripts are not a migration mechanism, and initialization failure must be investigated before manually repairing or deliberately recreating a disposable volume. Container restart does not resume a failed initialization.

Both passwords are supplied from an ignored local environment file through Compose environment interpolation. The official entrypoint creates the administrator; the access script reads the application password inside `psql`, quotes values and identifiers, and suppresses password-bearing statement logging for its session. Password verifiers use SCRAM-SHA-256. The local environment file and container environment retain plaintext secrets, so they must not be committed or included in shared diagnostics. Changing environment values does not rotate existing database passwords. Future application containers receive only application credentials.

Host access is bound to loopback. A Docker health check runs inside the database container, authenticates over TCP as the application login, and executes `SELECT 1`. This confirms connectivity and credentials without table access; it does not validate application schema or procedure behaviour. There is no automatic restart policy. Compose allows 30 seconds for shutdown, and ordinary shutdown preserves the volume. See the README for local operation and prerequisites.

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

## Database validation

Database validation is paused at the user's request until Docker is installed. No initialization, container health, authentication, permissions or persistence checks have been executed. The real Compose setup must be checked for first-start creation, missing-password rejection, application access restrictions, absence of application tables, preservation across restart and container recreation, and rejection of incorrect credentials. Verify permissions through PostgreSQL catalogs rather than introducing copied schemas, database test harnesses or synthetic test records.

## AI workflow for the database bootstrap

Codex inspected the repository, assignment PDF and local host, and consulted the official PostgreSQL, Docker and Microsoft documentation before implementing this bootstrap. The user selected environment-file passwords and two logins, then specified procedure-only application access. No business calculations, application schema or mock data were inferred. Validation was explicitly deferred until Docker is installed.

## Remaining work

Payment integration, SQL persistence/reporting, AWS deployment notes, AI workflow documentation and the video walkthrough are unfinished. Deferred stub scenarios are listed in [Docs/TODO.md](Docs/TODO.md).
