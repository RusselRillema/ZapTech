# Solution Notes

TODO: complete against [the assignment brief, including Appendix A](Docs/Zapper%20-%20Team_Lead%20Assignment.pdf). Record confirmed decisions and assumptions as implementation proceeds.

## Architecture, scope and trade-offs

TODO: describe .NET 8 service/stub boundaries, SQL database choice and payment flow. Explain the small synthetic seed dataset and scope exclusions: caller authentication, pagination, batch operations, infrastructure as code and load testing.

## Payment integration and notifications — Task 1

- TODO: describe customer/card-token and sub-merchant lookups, required merchant attributes, request validation, FNB's fixed 40-character CardAcceptorName and token-only card handling.
- TODO: document app payment ID idempotency, concurrent submissions, Acme TransactionID reuse (24-hour replay), account-wide unique 12-character RRN and correlation across messages/events.
- TODO: explain gateway status versus acquirer response codes, partial authorisations, currency defaults, minor units, SAST timestamps and acceptance of unknown fields.
- TODO: describe bounded outbound concurrency, HTTP connection handling, 20 requests/second limit, Retry-After, gateway errors and timeout reconciliation through RTTQ without a new TransactionID.
- TODO: explain Payment Advice Basic authentication and Transaction Status webhook Base64 HMAC-SHA256 over exact request bytes; HTTP 200 acknowledgements and Acme's three retries.
- TODO: explain durable lifecycle updates, duplicate/out-of-order callbacks, reversal/refund correlation by RRN, delayed events and partial refunds.
- TODO: record stub behaviours and how approval, decline, repeated submission, repeated/reordered notifications, reversal, refund and timeout scenarios are demonstrated.

## Data model and daily summary — Task 2

- TODO: describe sub-merchants, customers/stored cards, payments, lifecycle history, notifications and Acme interaction audit data; constraints, transactions, concurrency, indexes and schema evolution trade-offs.
- TODO: specify the summary endpoint or SQL query, grouped by sub-merchant and currency, with counts and values for approvals, declines, reversals and refunds.
- TODO: confirm authoritative settlement rules before implementation: date/timezone attribution, approved versus authorised values, partial authorisations/refunds, late events and lifecycle counting. Record confirmed assumptions here.

## AWS readiness and operability — Task 3

- TODO: explain local container operation and the proposed AWS service/database deployment; deployment itself is not required.
- TODO: describe environment configuration, secret management, scaling, in-flight work during graceful shutdown and recovery.
- TODO: identify useful logs, correlation identifiers, metrics, health signals and troubleshooting steps; explain protection of tokens, credentials and customer information.

## AI tools, workflow and artifacts — Task 4

- TODO: list tools and their uses, context supplied, task breakdown, delegated work, personal decisions, review/iteration and what to change next time.
- TODO: link useful artifacts in `ai/` and team-shareable AI configuration in tool-required locations. Include Acme client session exports where supported, with personal information removed.

## Validation and review

TODO: document meaningful checks of actual application behaviour, callback authentication, lifecycle transitions, concurrency and error handling. Record test commands, results and any database verification limits.

TODO: explain personal review of AI-generated work, rejected or rewritten suggestions, and evidence supporting correctness.

## Walkthrough

TODO: link the 5–10 minute video and outline architecture, important design decisions, trade-offs and implementation highlights.
