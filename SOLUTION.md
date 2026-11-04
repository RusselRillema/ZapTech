# Solution

## Database deployment

PostgreSQL 18 is permitted by the assignment and fits containerised deployment. One psql setup script serves native and Docker operation. Native users supply their existing server and administrative connection; Docker creates its server first. The SQL creates the database outside a transaction, then configures application access transactionally. Existing database/login names are rejected before creation; setup never resets credentials or deletes existing objects. Removal is explicit and refuses to force active connections closed.

The database name and application credentials are hard-coded development values, documented in the README. SQL deployment reads no environment files. Docker administrator settings and Acme runtime settings remain environment-based, with optional local `.env` files. This development shortcut is not production secret management; that remains part of the unfinished AWS design.

The connected administrator owns the database and will own its objects. A separate non-admin application login receives `CONNECT`, schema `USAGE` and procedure `EXECUTE`, without direct table/sequence grants, creation privileges or administrator membership. Public database/schema access and default public routine execution are revoked. Password verifiers use SCRAM-SHA-256.

The shared setup includes separate table and seed scripts for `Customers`, `SubMerchants` and `CustomerCards` in its existing transaction. Customer and sub-merchant primary keys are Zapper's supplied identifiers; card friendly names are unique per customer. Five synthetic customers, three sub-merchants and eight cards demonstrate the catalogue. Procedures and payment lifecycle tables remain unfinished. Future procedures belong before execution grants and must use `SECURITY DEFINER` with a trusted search path and named arguments with bound values; the caller controls transactions. Existing databases need explicit schema deployment rather than replaying initial provisioning.

Compose has independent Acme and database services with loopback publishing. A persistent volume retains PostgreSQL data; a thin first-start initializer invokes the shared setup file. Removal SQL is mounted outside the automatic initialization directory. The authenticated application-login health check proves connectivity and credentials, not future procedure correctness.

## Acme design and configuration

The .NET 8 Minimal API runs natively or in a non-root container. In-memory storage keeps the supporting stub small: concurrent submissions replay the original outcome for 24 hours, RRNs remain reserved until restart, and queries reflect reversals/refunds. Valid payments are approved; optional controls support full reversals/refunds. Partial outcomes and further simulations are deferred.

A bounded callback queue and one worker reuse HTTP connections. Attempts time out after ten seconds; failures receive three retries, one second apart, preserving bytes and event identity. Shutdown drains accepted work until the host deadline; restart loses transactions and queued callbacks.

DotNetEnv loads an optional repository-root `.env` before native host creation without replacing existing variables. Compose maps only each service's settings, also preserving shell precedence; required values are checked when that service starts. AWS can supply runtime variables directly. Native/container examples account for listening addresses and callback routing.

## Validation and AI workflow

The Acme Release build and all 24 existing xUnit cases passed on Linux. Shell syntax, Compose configuration, Docker build and startup passed using the Docker environment template. A separate Compose project with a new volume ran the actual setup and included SQL files: queries confirmed five customers, three sub-merchants and eight cards. The authenticated application-login database health check and Acme HTTP health endpoint passed. Rerunning the actual seed file preserved the counts. The temporary test containers and volume were removed. No tests or fixtures changed from main. Native database deployment, removal SQL, detailed role permissions, restart persistence and Windows/macOS execution remain unverified.

Codex used the assignment, repository, related startup discussion and official documentation. The user selected shared SQL, hard-coded development credentials and procedure-only access, avoiding an extra launcher. Static checks and documentation review support this update; actual database verification must use these deployment scripts without copied schemas, database test harnesses or fabricated fixtures.

## Remaining deliverables

Payment processing, notification handling, payment lifecycle schema, daily reporting, AWS deployment reasoning, production operability/secrets, the full AI workflow record and the 5–10 minute walkthrough remain unfinished. No AWS deployment is required. Deferred stub scenarios are listed in [Docs/TODO.md](Docs/TODO.md).
