# Main Global Instructions

1. Never commit without a specific instruction to do so.
2. Never push to `main`.
3. Never merge a pull request.
4. When an instruction or requirement is unclear, ask for clarification instead of making assumptions.
5. During partial development, do not overengineer unspecified paths or create mock data unless specifically requested. For unfinished work, use `NotImplementedException` with an accompanying comment that begins with `//TODO:`.
6. Keep the README.md and the SOLUTION.md file small and suitable for human consumption. If these files are changed validate their content against the instructions in `Docs/Zapper - Team_Lead Assignment.pdf`
7. Store AI-created lists, progress files, decision documents, and similar artifacts in the repository's `/Docs` directory. Prompt the user and obtain approval before creating any such file.
8. When the user asked to add something to the TODO list, it should be added to /Docs/TODO.md. This file is for todo notes for the user and should not be modified by agents unless explicitly asked. Notes should be short points of items that we need to come back to.
9. When the user asked to add something to the notes list, it should be added to /Docs/Notes.md. This file is for general notes and assumptions for the user and should not be modified by agents unless explicitly asked.
10. All database operations performed by the service must go through stored procedures called with named parameters.
11. Business logic and calculations belong in testable C# application code.
12. Use tests only to verify functional application operations and their observable behavior.
13. All database interactions must be safe under concurrent calls and multiple running container instances. Follow the concurrency handling protocol below so C# validates payment lifecycle transitions against the locked persisted state and updates are committed atomically.

## Concurrency Handling

Use database row locks to coordinate payment event processing across all running containers. Events for different payments may be processed concurrently; processing for the same payment must be serialized through its payment row lock. Business rules and calculations remain in C# application code.

Process each incoming payment event using one database connection and one transaction for the entire flow:

1. Begin the database transaction.
2. Await acquisition of the payment row lock while retrieving the payment's latest persisted state.
3. Insert the new payment event with processing state **Pending**.
4. Retrieve all **Pending** events for that payment.
5. Apply the payment event processing rules and perform calculations in C#, reconsidering pending events as other events are applied.
6. Persist the updated payment and event processing states within the same transaction.
7. Commit the transaction, releasing the payment row lock.

All database operations in this flow must use stored procedures called with named parameters. These calls must share the same connection and transaction, retaining the row lock throughout the C# processing and database updates. All code paths that modify a payment's state or event processing states must follow the same locking protocol.

On failure, roll back the transaction and release its locks and database resources. Log failures, including timeouts waiting for the payment row lock and failed database transactions, with the payment identifier, event identifier where available, failed operation, and exception details.

## Additional Paths and Links

Before implementing, changing, testing, or reviewing payment lifecycle behavior—including Acme responses and notifications, event processing, reversals, refunds, state queries, or reconciliation—read and follow [Payment Event Processing Rules](Docs/payment-event-processing-rules.md). Ask the user to clarify any ambiguity or conflict before implementing business logic.

Before creating a commit, opening a pull request, or reviewing a pull request, read and follow [ai/skills/AcmeCompliance.md](ai/skills/AcmeCompliance.md) and [ai/skills/SqlCompliance.md](ai/skills/SqlCompliance.md).
