# Zapper Assessment

Scaffold only for the .NET 8 payments integration assessment in [the assignment brief](Docs/Zapper%20-%20Team_Lead%20Assignment.pdf).

## Repository layout

- `Source/AcmePay.Stub/`: TODO: Acme Pay API stub from Appendix A.
- `Source/PaymentIntegration/`: TODO: payment service, SQL persistence and daily reporting.
- `Source/PaymentIntegration.Tests/`: TODO: meaningful application tests.
- `ai/`: TODO: useful plans, prompts, task breakdowns, review notes and sanitised Acme client sessions.
- `SOLUTION.md`: design, trade-offs, AWS approach and AI workflow.

## Prerequisites and configuration

TODO: document .NET 8, the selected SQL database (PostgreSQL, SQL Server or SQLite), and container tooling.

TODO: list environment variables for database access, Acme URL, merchant ID, API key, advice credentials and webhook secret; explain local secret setup without committing credentials.

## Run locally

TODO: provide build, database setup/seed, container startup and shutdown commands, service URLs and diagnostics instructions. Seed a few sub-merchants, customers, stored card tokens and payments.

## Exercise the integration

TODO: show payment submission using customer ID, sub-merchant ID, amount and app payment ID; payment status queries; both Acme callbacks; and daily summaries by date, sub-merchant and currency.

TODO: document demonstrations of approval, decline, duplicate submissions, duplicate/out-of-order notifications, reversal, refund and timeout recovery.

## Validation and walkthrough

TODO: provide test commands and verification limits. Link a 5–10 minute video covering architecture, key decisions, trade-offs and implementation highlights.
