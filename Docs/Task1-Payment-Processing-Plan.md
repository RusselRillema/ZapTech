# Task 1 Payment Processing Plan

Implement Task 1 in the following steps. Plan each step separately before implementing it, working through the relevant documentation, business rules, and technical details at that point.

1. Create the PaymentIntegration application project and test project.
2. Update the SQL scripts to support payment processing.
3. Create the new payment endpoint.

   - **3.1** Implement Real Time Transaction Query (RTTQ).

4. Create the get payment status endpoint.
5. Implement the payment lifecycle notification callback according to the Acme specification.
6. Implement the reversal and refund callback. This will likely need to be broken into smaller steps during its planning.
7. Plan and implement demonstrations for the scenarios required by Task 1. This will likely need to be broken into smaller steps during its planning.

## Testing rules for every implementation chat

- Add meaningful service-layer tests where applicable in each step.
- Mock the DAL so these tests never interact with a real database.
- Manage full end-to-end testing in step 6. Do not build support for it into each earlier step.
- Follow the user's standing testing policy and repository instructions.

## References

- [Assignment and Acme specification](Zapper%20-%20Team_Lead%20Assignment.pdf)
- [Repository instructions](../AGENTS.md)
- [Acme compliance](../ai/skills/AcmeCompliance.md)
- [SQL compliance](../ai/skills/SqlCompliance.md)
