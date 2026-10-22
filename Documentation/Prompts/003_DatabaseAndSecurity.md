# Prompt: Storefront Backend — Architecture, Authentication, Database Integration & Error Handling

You are a senior .NET backend engineer working on my existing **Storefront** repository. Your task is to strengthen the backend in four areas:

1. Architecture Implementation
2. Authentication & Security
3. Database Integration
4. Error Handling

## Project Context

The backend is built with **ASP.NET Core on .NET 10**. The repository already has a functioning API, checkout and cart-related logic, validation, and automated tests. The backend has successfully restored and built, and its real xUnit test suite has passed **106/106 tests**.

I currently do not have a database configured. I want to introduce a real database as part of this work rather than continue relying entirely on in-memory state or external mock APIs.

Treat the existing repository as the source of truth. Inspect its actual structure, implementation, configuration, tests, and documentation before deciding how to change it. Do not assume that components or files exist just because they are mentioned in this prompt.

## Primary Objective

Implement these four areas in a practical, maintainable, locally runnable way. Prefer a solution that works on a developer's machine without requiring a paid service, cloud account, or an already-running database server.

Do not rewrite the application from scratch. Preserve existing API contracts and frontend compatibility wherever possible. Avoid unnecessary dependencies, overengineering, and unrelated refactoring.

### 1. Architecture Implementation

Review the current backend structure and determine whether responsibilities are clearly separated.

- Separate HTTP concerns, business logic, data access, and domain models where doing so provides a real benefit.
- Use dependency injection and appropriate service interfaces where they improve testability and maintainability.
- Keep controllers or endpoint handlers focused on HTTP request and response handling.
- Keep business rules, checkout calculations, price verification, and order processing in the appropriate application or domain services.
- Introduce a repository or data-access abstraction only where it adds value; do not create layers purely for architectural appearance.
- Centralize configuration and use the existing ASP.NET Core configuration conventions.
- Maintain the existing API routes and response contracts unless a change is necessary and explicitly documented.
- Ensure the design remains easy to understand for a junior developer who needs to explain and maintain it.

**Deliverable:** A clearly structured backend with documented responsibilities, sensible dependency boundaries, and automated tests.

### 2. Authentication & Security

Assess the application's existing authentication and authorization requirements before implementing a solution.

- Determine which endpoints should be public and which should require an authenticated user.
- If user accounts or protected customer operations are within the current scope, implement a suitable authentication mechanism using established ASP.NET Core facilities and secure password handling.
- Apply authorization policies to protected endpoints rather than relying on frontend-only restrictions.
- Never store plaintext passwords, hardcode credentials, commit secrets, or expose sensitive configuration in logs or API responses.
- Validate and constrain incoming data, enforce reasonable request-size limits, and protect against mass assignment and unauthorized access to another user's resources.
- Review CORS, HTTPS configuration, trusted hosts, and relevant security headers for the intended development and deployment environments.
- Keep development conveniences separate from production settings.
- Do not add a custom JWT implementation or build a full identity platform unnecessarily. Choose an established approach that fits the existing application's needs.

If full user-account functionality is outside the current scope, implement the appropriate security foundations and document what remains. Do not pretend authentication is implemented if it is not.

**Deliverable:** A documented authentication and authorization design, secure configuration, and tests for protected and public endpoints.

### 3. Database Integration

Introduce a persistent database that can be used locally without requiring a paid service or a separately administered database server.

**Preferred starting point:** Evaluate SQLite with Entity Framework Core as the default local-development option. It is lightweight, file-based, and suitable for a project that currently has no database. Confirm compatibility with .NET 10 and the existing project dependencies before selecting packages.

If the repository has requirements that make SQLite unsuitable, explain the reason and choose the simplest appropriate alternative.

Implementation requirements:

- Inspect the existing catalogue, cart, checkout, pricing, and order models before defining database entities.
- Introduce `DbContext` and entity configurations where appropriate.
- Persist the data that genuinely needs persistence, such as orders and order items, and any other data required by the current business requirements.
- Decide deliberately whether catalogue data should be persisted locally, retrieved from the existing upstream API, or handled through a clearly defined hybrid approach. Do not accidentally replace the existing catalogue behavior.
- Preserve server-side price verification. Never trust prices or totals submitted by the client.
- Ensure order items retain the product names, quantities, unit prices, and totals needed for historical records, even if the catalogue changes later.
- Use database transactions for operations that must succeed or fail together.
- Add EF Core migrations and document how to create or update the database.
- Use configuration for the database connection string and choose a safe, predictable location for the local SQLite file.
- Do not commit real credentials or generated database files unless the repository explicitly requires them.
- Add appropriate indexes, constraints, and uniqueness rules where justified by the data model.
- Handle concurrent checkout requests and duplicate submissions appropriately. Where necessary, implement idempotency so that retrying a request does not unintentionally create duplicate orders.
- Use database-backed persistence rather than pretending that in-memory collections provide durable storage.
- Keep automated tests isolated from the developer's local database. Prefer a temporary SQLite database or another testing strategy compatible with the application's EF Core usage.

Provide clear setup instructions, including the exact commands to restore dependencies, create or apply migrations, start the API, and verify that records persist after restarting it.

**Deliverable:** A functioning database integration with migrations, persistence, test coverage, and local setup documentation.

### 4. Error Handling

Review the existing exception-handling and validation behavior, then make it consistent across the API.

- Use centralized exception handling where appropriate.
- Return consistent `ProblemDetails` responses with meaningful HTTP status codes.
- Distinguish validation failures, malformed requests, missing resources, authentication failures, authorization failures, conflicts, database failures, and unexpected server errors.
- Preserve the existing request-size protections and ensure oversized requests are handled correctly.
- Never return stack traces, database internals, secrets, or sensitive implementation details to clients in production.
- Log unexpected failures with useful diagnostic context while avoiding passwords, tokens, payment details, and other sensitive information.
- Handle database constraint violations and transaction failures safely.
- Return appropriate conflict responses for stale checkout totals or inventory/price conflicts, consistent with the existing API contract.
- Avoid swallowing exceptions or returning success when an operation failed.
- Add tests for important error paths and ensure error responses remain predictable.

**Deliverable:** Consistent, secure error responses and tests covering common client errors, conflicts, database failures, and unexpected exceptions.

## Working Method

Follow these stages in order.

### Stage 1 — Inspect and plan

1. Inspect the repository structure, backend projects, package versions, configuration files, API endpoints, domain models, services, and tests.
2. Identify existing behavior that must be preserved.
3. Identify the actual gaps in the four target areas.
4. Propose the smallest practical implementation plan, including the database choice and any new dependencies.
5. Identify potential breaking changes before making them.

Do not stop at a generic audit. Once the plan is clear, proceed with implementation unless a genuinely blocking decision requires my input.

### Stage 2 — Implement

Implement the changes incrementally. Keep the solution aligned with the existing code style and framework versions.

- Avoid unrelated frontend changes.
- Update dependency injection and configuration correctly.
- Add migrations and persistent storage.
- Update tests alongside implementation.
- Keep local-development settings safe and clearly separated from production configuration.
- Update documentation to reflect the actual implementation, not an aspirational design.

### Stage 3 — Verify

Run the relevant commands from the repository's actual project structure. At minimum:

- Restore dependencies.
- Build the backend in Release configuration.
- Run the real xUnit test suite.
- Add and run tests for persistence, checkout, authentication/authorization, and error handling.
- Verify that data persists across API restarts.
- Verify that invalid requests return the expected status codes and response format.
- Verify that protected endpoints reject unauthenticated requests and enforce authorization.
- Verify that existing API behavior and frontend integration remain compatible.
- Check that a fresh developer can initialize the database using the documented commands.

Do not report a test as passing unless it was actually executed successfully. If an environment limitation prevents a check, state exactly what could not be verified and why.

### Stage 4 — Report

When finished, provide:

1. **Architecture:** What changed and why.
2. **Authentication & Security:** What is implemented, which routes are protected, and any remaining gaps.
3. **Database:** The selected database, entities persisted, migration instructions, and how to inspect or reset local data safely.
4. **Error Handling:** The response conventions and failure cases covered.
5. **Files Changed:** The important files added or modified.
6. **Commands to Run:** Exact commands to start and verify the application locally.
7. **Test Results:** Actual build and test outcomes, including any failures or skipped tests.
8. **Remaining Risks:** Any production-readiness issues that are not yet resolved.

## Constraints

- Use the existing repository and its actual code as the source of truth.
- Prefer the simplest maintainable solution over unnecessary enterprise architecture.
- Do not remove existing security controls or weaken server-side validation.
- Do not trust client-supplied prices, totals, roles, or user identifiers.
- Do not introduce hardcoded secrets or require paid infrastructure for local development.
- Do not claim database persistence, authentication, migrations, or tests are complete unless they work.
- Do not break existing endpoints silently.
- Keep the implementation focused on these four areas.
- Explain significant design decisions briefly so I can understand and defend them during a code review or internship assessment.

**Success criteria:** The Storefront backend has a clearer architecture, an honest and functional authentication/security implementation appropriate to its scope, a working local persistent database, consistent error handling, passing automated tests, and setup instructions that another developer can follow from a fresh checkout.