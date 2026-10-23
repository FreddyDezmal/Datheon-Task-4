# Datheon Internship — Live System Availability

## Objective

Review and implement the Live System Availability requirements for the Storefront application. Build on the existing React 19 + TypeScript frontend and ASP.NET Core .NET 10 backend without unnecessarily changing the current architecture or breaking existing functionality.

The application already has comprehensive unit, component, API integration, and Playwright browser tests. Preserve these tests and extend them where appropriate.

## Phase 1 — Audit the current implementation

Before changing code, inspect the repository and document the current availability-related behavior.

Review:

- Frontend API timeout, cancellation, loading, retry, and error-handling behavior.
- Backend health endpoints and dependency health checks.
- Database connectivity and database failure behavior.
- Startup and shutdown behavior.
- External API dependency handling, particularly the FakeStoreAPI integration.
- Logging, exception handling, and correlation/request identifiers.
- Configuration and environment-specific settings.
- Docker, deployment, hosting, or process-management configuration, if present.
- Whether users receive useful feedback during outages and can recover without refreshing the entire application.

Identify what already exists, what is missing, and what can be improved. Do not assume infrastructure or hosting capabilities that are not present in the repository.

## Phase 2 — Implement appropriate availability safeguards

Implement the improvements that are justified by the audit.

### A. Health checks

- Provide a lightweight liveness endpoint that indicates whether the application process is running.
- Provide a readiness endpoint that indicates whether the application can safely serve requests.
- Check critical dependencies, particularly the database, where appropriate.
- Avoid making liveness depend on external services, since an external outage should not necessarily cause the application process to be restarted.
- Return appropriate HTTP status codes and avoid exposing secrets, connection strings, stack traces, or sensitive infrastructure details.
- Keep health-check responses simple and suitable for monitoring.

### B. Dependency failure handling

- Handle database and external API failures gracefully.
- Apply explicit timeouts to outbound HTTP requests.
- Use bounded retries with backoff only for transient failures and safe operations.
- Do not blindly retry checkout, order creation, or other non-idempotent operations.
- Preserve the existing checkout idempotency and uncertain-outcome recovery behavior.
- Distinguish timeouts, unavailable dependencies, validation errors, and authentication failures.
- Return consistent, appropriately sanitized API error responses.

### C. Frontend resilience

- Show useful loading, offline, timeout, and service-unavailable states.
- Provide retry actions where retrying is safe.
- Avoid retry loops and duplicate user actions.
- Preserve the cart and checkout recovery information when doing so is safe.
- Make it clear when a checkout result is uncertain and guide the user to recover safely.
- Avoid showing a successful operation when the server has not confirmed it.
- Ensure outage messages are accessible and work on mobile screens.

### D. Observability

- Use structured logging for unexpected errors and dependency failures.
- Ensure requests can be correlated across relevant logs where practical.
- Avoid logging passwords, access tokens, refresh tokens, payment details, or other sensitive information.
- Record enough diagnostic information to identify failures without exposing internal details to clients.
- Add metrics or diagnostic endpoints only if they fit the current architecture and deployment needs.

### E. Startup, shutdown, and configuration

- Review how the application starts, handles configuration errors, and shuts down.
- Fail fast for genuinely required invalid configuration, with useful diagnostic logs.
- Use graceful shutdown where supported by the existing hosting setup.
- Do not introduce containers, cloud infrastructure, orchestration, or a new hosting platform unless the repository or internship requirements justify them.

## Phase 3 — Automated availability testing

Add tests for the safeguards that are implemented.

Cover relevant scenarios such as:

- Liveness remains healthy when an optional external service is unavailable.
- Readiness reports an unhealthy state when a critical dependency is unavailable.
- Database failures produce safe, predictable responses.
- External service timeouts do not hang requests indefinitely.
- Transient failures are retried only within configured limits.
- Non-idempotent checkout operations are not duplicated by retries.
- Frontend service-unavailable and timeout states are usable and recoverable.
- Health endpoints do not disclose sensitive configuration or exception details.
- Existing authentication, checkout idempotency, and order-recovery tests continue to pass.

Use deterministic test doubles or controlled failure injection. Avoid tests that depend on real outages or unreliable public services.

## Phase 4 — Documentation and evidence

Update the relevant documentation with:

- Health endpoint paths and their meanings.
- How to run the application and its health checks locally.
- Required configuration and dependency assumptions.
- How to diagnose common availability failures.
- Which operations can be retried safely.
- How to run the new availability tests.
- Any known limitations or deployment-specific requirements.

Provide a concise implementation report listing changed files, behavior added, tests run, exact results, and remaining risks.

## Constraints

- Inspect first and implement second.
- Preserve the existing architecture and successful functionality.
- Avoid unnecessary dependencies and infrastructure.
- Do not claim high availability, production readiness, or uptime guarantees without deployment evidence.
- Do not expose internal errors or secrets in API responses.
- Run the existing test suites and all new relevant tests.
- Clearly distinguish verified behavior from recommendations that require real deployment infrastructure.

## Definition of done

The availability work is complete when the audit findings have been addressed appropriately, health checks and failure handling are implemented where applicable, recovery behavior is tested, documentation is updated, and all relevant existing and new tests pass.


## What to verify before calling it complete


The most important distinction is between application resilience and actual live-system availability.
Area	What constitutes evidence
Health checks	Liveness and readiness return the expected statuses under healthy and failed dependency conditions.
Dependency resilience	Controlled database and external API failures produce bounded, predictable behavior.
Checkout safety	Interrupted requests and retries do not create duplicate orders.
Recovery	Users can recover from transient errors without losing safe-to-preserve state.
Monitoring	Failures are visible in useful logs without sensitive information being exposed.
Live operation	The deployed application has been observed running and responding in its actual hosting environment.

Important: Passing automated tests establishes that the tested safeguards behave as expected. It does not, by itself, establish uptime, automatic recovery after a process crash, or availability under real production traffic.