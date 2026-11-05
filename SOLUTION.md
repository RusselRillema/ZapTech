# Solution

**Video walkthrough:** [Watch on YouTube](https://youtu.be/q7kO6svREB8).

Please select a higher playback resolution in YouTube's video settings for clearer text. YouTube sometimes selects a lower resolution automatically.

## 1. Architecture and Design Decisions

The solution consists of three main components: the .NET 8 Payment Integration service, a PostgreSQL database and a simulated Acme Pay gateway. I've also included a small console application to demonstrate the payment scenarios.

Everything can run either through Docker Compose or natively. I wanted somebody reviewing the assignment to be able to get it running without installing unnecessary tooling or having to understand the entire solution first. The setup instructions are in `README.md`.

### Payment processing

A payment is recorded in the database before calling Acme. This gives us a local record even if the request times out or the connection fails.

I've made the following assumptions and decisions:

- `AppPaymentId` is globally unique. I'd normally confirm this with the team responsible for the app.
- Submitting the same `AppPaymentId` with identical details is treated as a retry. The existing payment is returned without calling Acme again. Changed payment details result in a conflict.
- I added `CustomerCardId` to the request. A customer can have multiple stored cards, so the app needs to identify which card to use. A future endpoint could return the customer's available cards and friendly names.
- Approved, Declined, Failed and Unresolved are separate states. In particular, an Acme timeout does not mean the payment failed.
- The approved amount comes from Acme's payment response or Real Time Transaction Query (RTTQ), rather than assuming the requested amount was approved.

When Acme does not provide a conclusive response, the service attempts one RTTQ using the original transaction identifier. I limited this to one attempt to keep the implementation manageable. In production I'd have a background process periodically checking unresolved payments until they can be reconciled or require investigation.

Notifications can be duplicated or arrive out of order. Rather than relying on their arrival order, I store the events and evaluate them against the current payment state.

All changes to a payment are processed within a database transaction using a row lock. This allows separate payments to run concurrently while preventing multiple service instances from updating the same payment simultaneously.

### Acme integration

One of the benefits I've found with AI-assisted development is that it often introduces good implementation practices without me having to explicitly request every detail. For example, the Acme client uses HTTP connection pooling and limits concurrent outbound requests. These weren't things I specifically included in my prompts, but they're useful safeguards when dealing with a potentially slow external service. Of course, these decisions still need to be reviewed rather than assuming that everything AI generates is appropriate.

For Acme's `429/E29` rate-limit response, I used a fairly simple approach: wait for twice the supplied `Retry-After`, with a minimum of three seconds, and retry using the original transaction identity.

The documentation doesn't explain whether Acme's rate limits apply per IP address, merchant account or something else. With that information, I'd consider a shared rate limiter or token bucket approach. The current limiter works per application instance, not across the whole Acme account.

For the two notification endpoints, I provided AI with the explicit authentication requirements from Appendix A and had it implement them: Basic authentication for Payment Advice and HMAC-SHA256 for Transaction Status notifications. This is another example where providing the correct documentation and requirements was more important than writing the implementation myself.

I chose to treat the payment response or RTTQ as the authoritative source for the approved amount, rather than using the amount supplied in Payment Advice. I also chose to ignore `Authorize` and `Capture` Payment Advice notifications because this implementation only supports the direct payment flow.

### Acme stub assumptions

The assignment specifically mentioned that the stub was supporting infrastructure and not part of what was being assessed. I therefore used AI to build most of it from Appendix A, concentrating my own efforts on the payment integration and the decisions around payment processing.

The stub supports holding, reordering and redelivering callbacks, along with full refunds and reversals. These are the scenarios needed to demonstrate duplicate and out-of-order handling.

The stub stores its transactions in memory, so restarting it loses the transaction history and any queued callbacks.

## 2. Database Design and Reporting

I chose PostgreSQL and explicit SQL scripts rather than Entity Framework.

For a project of this size, I find the database design easier to understand when the schema is defined directly. Database access is through stored procedures, with business logic and payment calculations kept in C# so they can be tested independently.

The main model contains customers, stored cards, sub-merchants, payments and payment events.

Payments hold the current state, while payment events retain the history of interactions with Acme. Event payloads are stored as JSONB. This preserves the original information, including fields Acme may add in future, without requiring database changes for every API change.

I haven't investigated the performance implications of JSONB compared with a more fully relational event model. For a production system I'd evaluate this against actual reporting and investigation requirements.

A few deliberate simplifications:

- City, region and country are stored directly against sub-merchants. In production I'd consider lookup tables to enforce consistency in names, abbreviations and casing.
- The database setup uses fixed development credentials rather than reading them from the environment files. These scripts are only intended for initial local setup, and the credentials are explicitly not production secrets.
- The SQL scripts create a fresh database and schema. For an existing production database, I'd use versioned migrations executed as part of deployment, not by each running application instance.

### Daily reporting

The daily settlement summary is available as a PDF through an API endpoint, grouped by sub-merchant and currency.

The report includes the count and value of approved payments, declined payments, reversals and refunds. A payment can appear in more than one category, for example if it was approved and then refunded.

The report reconstructs historical payment state from stored events rather than maintaining a separate reporting table. This avoids duplicating payment rules, but historical reconstruction is more complicated than I initially expected.

For larger volumes I'd revisit this design, considering persisted financial movements, reporting checkpoints or pre-calculated daily summaries. Correctness would need to come before performance optimisation.

## 3. AWS Readiness and Operability

The application is containerised but hasn't been deployed to AWS, as this isn't required by the assignment.

My AWS experience has mostly been with standard virtual machines and using load balancers as an additional layer of security rather than for actually distributing load. I haven't worked directly with AWS's managed container services or a container orchestrator. I am aware of what orchestration is used for and have a general understanding of how it works, but don't have hands-on experience with it.

For a production deployment, I'd investigate ECS/Fargate and EKS, particularly if the team is already using Kubernetes, along with Amazon RDS for PostgreSQL. Before choosing an approach, I'd want to understand the pros and cons of each, including performance, scaling and how the managed services handle the application lifecycle. In particular, I'd want to understand how shutdown notifications are handled and what happens to work in progress when AWS stops, replaces or moves containers.

The main considerations would be:

**Configuration and secrets:** Application settings would continue to come from environment variables, with sensitive values supplied through AWS Secrets Manager rather than stored in deployment files. Local `.env` files are only a development convenience.

**Scaling:** The service should be able to run across multiple containers behind a load balancer. Database row locking already provides coordination for updates to the same payment. I'd configure automatic scaling based on load, within agreed limits, while taking the database capacity and Acme's rate limits into account.

**Graceful shutdown:** When a container is being stopped, the orchestrator sends a termination signal (`SIGTERM`). The service should stop accepting new work and allow requests and database transactions that are still being processed to finish within the configured shutdown period. This is particularly important when dealing with payments that might already have reached Acme.

**Monitoring:** I'd use CloudWatch for structured application logs, metrics and alarms. Important signals include Acme response times, timeout and error rates, unresolved payments, pending notifications, database failures and application health. Specific error conditions should trigger notifications through SNS or the team's existing alerting system rather than requiring somebody to monitor logs manually.

I haven't implemented this type of monitoring on AWS, but I have used similar functionality on GCP, using application logging and monitoring to trigger email notifications when problems were detected.

## 4. AI-Assisted Development

I used ChatGPT and Codex throughout the assignment, but for different purposes.

ChatGPT was primarily a planning tool and sounding board. I created a project with the assignment PDF as context and used it to discuss architecture, technology choices, assumptions and alternatives. Some of the technologies were unfamiliar to me, so being able to challenge an idea and ask follow-up questions was useful.

Codex was used for more detailed implementation planning, code generation, testing and review.

I don't have a fixed AI workflow that I use for every project. The tools change quickly, and I adapt how I use them to the work. If I were working with a team, I'd encourage everyone to share examples of where AI has worked well and where it hasn't. Depending on the size and experience of the team, I'd also consider introducing a more structured workflow and shared rules to make sure everyone maintains a good understanding of the code being produced.

For this assignment I kept the repository-level AI guidance fairly simple. The repository contains `AGENTS.md` and two small compliance instructions for Acme and SQL. Given the relatively short assignment PDF, I didn't feel a more elaborate set of skills was justified. In a larger project I'd narrow the compliance checks so the agents only had to load the relevant documentation.

I made the architectural and business-rule decisions myself, then used Codex to implement specific parts of the solution. The supporting Acme stub was a good candidate for substantial AI generation because the assignment explicitly treats it as supporting infrastructure.

I used Codex to review major changes, but generally only performed one review pass per major PR. Ordinarily I'd expect multiple passes, especially for core or critical code.

## 5. Validation and Limitations

I did some validation throughout development, but not nearly as much as I would have liked.

I reviewed parts of the AI-generated code and made sure the project structure and general approach were what I wanted. I paid particular attention to the database design. Although the database itself is fairly simple, changing a database structure once a system is in production can be extremely difficult, so I wanted to be comfortable with that design from the start.

For automated testing, I generally allowed AI to decide which tests to write rather than specifying every test case myself. During code reviews, when issues were identified, I required regression tests to be added to cover those specific problems.

The solution has automated tests covering payment processing, notifications, event handling and reporting. There are also end-to-end demonstrations running against the actual application, PostgreSQL database and Acme stub. These provide useful coverage, but I wouldn't consider them sufficient proof that the entire system is correct.

My normal approach to validating software, particularly something like the reporting, would be to spend time working through a variety of scenarios, writing down the expected results beforehand and then running the system to see whether the results match. I think it's important to have a clear expectation of what the system should produce before testing it, rather than running it, looking at the results and then deciding whether they seem correct.

Unfortunately, I ran out of time before I could do this to the extent I would have liked. I spent more time on implementation, reviewing individual changes and getting the overall system working than on independently interrogating the accuracy of the completed solution. In hindsight, this may have been a time management oversight on my part.

There are also some known issues and areas that would need further attention:

- **Reporting:** There are open issues ([#39](https://github.com/RusselRillema/ZapTech/issues/39), [#40](https://github.com/RusselRillema/ZapTech/issues/40) and [#41](https://github.com/RusselRillema/ZapTech/issues/41)) relating to historical reporting, particularly how payment events are interpreted when they happen or arrive on different days.
- **Reconciliation:** The immediate RTTQ attempt is implemented, but ongoing reconciliation of unresolved payments and notifications is not.
- **Concurrency and recovery:** I'd want to do more testing with multiple instances running, database failures and shutdowns while processing payments.
- **Acme stub:** It covers the main scenarios required for the assignment but doesn't simulate every possible payment outcome.

Overall, I did review code, make technical decisions and enforce the structure I wanted, but I didn't spend as much time questioning and independently verifying the behaviour of the system as I normally would. That was primarily due to time constraints.

## 6. What I Would Do Differently

### Reporting requirements

I underestimated the complexity of the daily reporting requirement.

My initial assumption was that if I stored the payment data and events correctly, producing the report would be relatively straightforward. In hindsight, that was a poor assumption.

The complications aren't purely technical. There are business decisions to make around payments that are approved on one day and refunded or reversed on another, notifications that arrive late, and how those events should affect historical reports.

This is the type of problem-solving I enjoy, but I didn't allow myself enough time to properly reason through the scenarios and their implications.

I would spend more time understanding exactly what the report is supposed to represent and working through the edge cases before implementing it.

### Development setup

I underestimated the time involved in setting up the development environment. The first several hours were spent working through native versus Docker configuration, environment files and PostgreSQL setup before I was properly ready to tackle the payment integration itself.

I wanted the project to be easy for someone else to run, which I still think was worth doing, but I could have managed the time better.

### Planning and parallel development

I'd define the main interfaces and class responsibilities earlier, before starting implementation.

This would have made it easier to divide the implementation into independent pieces and work on several parts in parallel. I think I could have made better use of AI's ability to work on multiple tasks at once, rather than having to wait for one part of the implementation to be completed before starting another.

### AI review process

I'd also spend more time reviewing AI-generated changes. One review pass per major PR was not enough for some of the more complicated payment and reporting scenarios.

In particular, I'd use more independent review and spend additional time testing assumptions and edge cases rather than relying primarily on the scenarios I'd already considered.
