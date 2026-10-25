# Storefront — Comprehensive End-to-End Security Audit

## Role

Act as a senior application security engineer conducting a thorough, evidence-based security assessment of the entire Storefront project. Review it as a production-deployed web application, not merely as a codebase.

The objective is to discover, validate, prioritize, and help remediate security vulnerabilities while preserving all existing functionality.

## 1. Project context

The project uses:

- Frontend: React 19, TypeScript, Vite.
- Backend: ASP.NET Core .NET 10, C#.
- Data access: Entity Framework Core.
- Database: PostgreSQL in production.
- Hosting: Render, with the frontend deployed separately from the API.
- API: central API client, health endpoints, production CORS configuration, authentication/session handling, and rate limiting.
- Checkout: idempotency protection, explicit transaction handling, and recovery from uncertain request outcomes.
- Testing: unit, component, browser/Playwright, backend, and deployment smoke tests.

The implementation details above are context, not proof that a security control is correctly implemented. Verify every control directly in the current repository and, where authorized, against the deployed application's observable behavior.

Inspect the actual code, configuration, tests, deployment documentation, migrations, and Git history where available. Do not assume a feature exists simply because it is documented.

## 2. Safety rules and authorization boundaries

This is an authorized defensive audit of my own Storefront application.

1. Begin in **read-only audit mode**. Do not modify files, commit, push, deploy, change Render settings, rotate secrets, run migrations, or alter production data during the initial assessment.
2. Do not perform destructive testing, denial-of-service testing, high-volume fuzzing, credential stuffing, real payment attempts, or tests that create, modify, or delete production accounts or orders.
3. Do not expose secrets in terminal output, reports, screenshots, logs, or generated artifacts. Redact tokens, passwords, connection strings, private keys, session cookies, and personal information.
4. Never print environment-variable values just to establish whether they exist. Report presence, configuration source, and risk without disclosing the value.
5. Do not test production using another person's account or data. Do not attempt to access unrelated tenants or real customer records.
6. Do not trust documentation alone. Trace each security claim to its implementation and relevant test evidence.
7. Distinguish confirmed vulnerabilities from plausible risks, missing evidence, hardening opportunities, and false positives.
8. Use local test environments, disposable databases, mocks, and synthetic accounts for exploit validation wherever possible.
9. Use passive production checks only unless I explicitly authorize a narrowly scoped active test. Do not assume that access to a deployed URL grants permission for intrusive testing.
10. If a finding requires a production change or active production test, explain the risk and request approval before proceeding.

## 3. Establish the audit baseline

Before reviewing security controls:

- Identify the repository structure, application entry points, API routes, controllers, services, middleware, data models, authentication flows, and frontend route structure.
- Identify all dependency manifests and lockfiles, Dockerfiles, Render configuration, CI workflows, environment templates, database migrations, and deployment documentation.
- Identify the current branch and working-tree state, if available.
- Preserve uncommitted work. Do not reset, stash, overwrite, or discard existing changes.
- Run existing tests and static checks only when safe. Record the exact commands and results.
- Identify which checks require network access, credentials, external services, or production access.
- Create a security coverage matrix linking each area to the files inspected, tests run, evidence obtained, and unresolved gaps.

Never claim a check passed if it was not run. Never claim production is secure based solely on a successful build or smoke test.

## 4. Authentication, authorization, and session security

Inspect the complete authentication lifecycle, including registration, sign-in, token issuance, token renewal, sign-out, session persistence, and failure recovery.

Investigate:

- Password hashing algorithm, work factor, password handling, and whether plaintext passwords or sensitive credentials are ever logged or persisted.
- Password policy and whether it is proportionate to the application's risk.
- Account enumeration through response messages, status codes, response timing, and registration/sign-in behavior.
- Brute-force protection, distributed abuse, rate-limit key generation, and whether client-controlled headers can spoof a shopper's identity or bypass limits.
- Access-token lifetime, signature and algorithm validation, issuer, audience, expiration, and key management, as applicable to the actual implementation.
- Refresh-token entropy, storage, expiration, rotation, reuse detection, revocation, and concurrent refresh behavior.
- Whether refresh tokens or sensitive authentication material are exposed to JavaScript, localStorage, URLs, analytics, logs, or browser error reports.
- CSRF exposure for cookie-based authentication, including SameSite, Secure, HttpOnly, origin validation, and anti-forgery protections where applicable.
- Session fixation, logout semantics, token invalidation, and behavior after password or account changes.
- Authorization checks on every protected API operation, including object-level access to orders and account information.
- Whether one user's identifiers, order IDs, or request parameters can be manipulated to access another user's data.
- Registration and sign-in race conditions, duplicate accounts, and error-handling behavior.
- Any encryption keys or signing keys stored in configuration, their protection at rest, rotation strategy, and production failure behavior.

Validate with isolated test accounts and automated tests. Do not attempt real credential stuffing or access real users' records.

## 5. API and HTTP security

Inspect every API endpoint, not just the endpoints covered by smoke tests.

Check for:

- Missing authentication or authorization on sensitive routes.
- Broken object-level and function-level authorization.
- Excessive data exposure and over-broad DTOs.
- Mass assignment and unsafe model binding.
- Input validation gaps, malformed JSON handling, oversized requests, and unsafe content types.
- SQL injection, unsafe raw SQL, unsafe dynamic query construction, and ORM misuse.
- Cross-site scripting risks in data returned by the API or rendered by the frontend.
- Open redirects, SSRF, unsafe URL fetching, path traversal, and file handling if those capabilities exist.
- Insecure CORS policies, wildcard origins, credentialed cross-origin requests, and incorrect origin matching.
- Missing or weak security headers, including HSTS, Content Security Policy, frame protections, content-type sniffing protections, and Referrer-Policy where appropriate.
- Excessive error details, stack traces, database errors, internal paths, and secrets in API responses.
- Unsafe HTTP method handling, cache headers on sensitive responses, and inconsistent status codes.
- Request-size, timeout, concurrency, pagination, and resource-exhaustion protections.
- Trust placed in X-Forwarded-* headers or other proxy headers without correct trusted-proxy configuration.
- Inadequate separation between liveness and readiness checks.
- Whether operational or diagnostic endpoints reveal sensitive deployment information.

Produce a route-by-route table containing HTTP method, route, authentication requirement, authorization requirement, input validation, rate limiting, test coverage, and findings.

## 6. Checkout, orders, and business-logic abuse

Treat checkout as a high-priority area because a technically secure API can still have serious business-logic vulnerabilities.

Trace the full flow from browser request through the API, idempotency storage, transaction, database writes, and response.

Check:

- Whether order ownership is always derived from trusted authenticated identity rather than a client-supplied user ID.
- Whether prices, totals, discounts, quantities, product IDs, and other authoritative values are recalculated or validated on the server.
- Whether a client can submit negative quantities, invalid prices, unexpected product identifiers, duplicate items, overflow values, or manipulated totals.
- Whether stale or manipulated catalogue data can cause inconsistent orders.
- Whether inventory, stock, limits, or availability are enforced if applicable.
- Whether idempotency keys are validated, scoped appropriately, stored durably, and protected by database uniqueness and concurrency controls.
- Whether simultaneous requests with the same key can create multiple orders.
- Whether reusing a key with different request details is rejected.
- Whether a replay returns the original outcome without repeating writes.
- Whether an uncertain commit, process crash, timeout, or database failure can produce duplicates or falsely report success.
- Whether failed requests leave partial data behind.
- Whether retry behavior is safe across page reloads and session recovery.
- Whether rate limiting and transaction boundaries can be bypassed by parallel requests.
- Whether error responses accidentally disclose internal order details or idempotency records.
- Whether test, debug, or administrative endpoints can create orders without the expected authorization.

Create deterministic automated tests using a disposable database for concurrency, replay, rollback, and recovery scenarios. Never create or delete test records in production during this audit.

If the application does not process payments or reserve stock, explicitly state that those controls are not applicable rather than assuming they exist.

## 7. PostgreSQL and data protection

Inspect EF Core configuration, entity models, migrations, constraints, connection handling, and database permissions.

Check:

- Least-privilege database credentials and separation of runtime and migration privileges where practical.
- Whether production can accidentally use SQLite, an in-memory database, or a local file.
- Database connection strings and credentials are not committed or embedded in Docker images.
- TLS verification for database connections and whether certificate validation is weakened.
- Parameterized queries and unsafe SQL.
- Unique constraints and concurrency behavior for authentication, refresh tokens, and checkout idempotency.
- Foreign keys, cascade deletes, data integrity, and orphaned records.
- Sensitive personal data stored unnecessarily or without an appropriate retention policy.
- Backup, restoration, and recovery arrangements, including the limitations of the actual database plan.
- Database migration safety, permissions, failure handling, and whether multiple application instances could race to migrate.
- Logging and error handling that might expose query parameters or database credentials.

Do not run migrations, destructive SQL, cleanup commands, or restore operations against production.

## 8. Frontend security

Inspect React components, route guards, API client behavior, environment handling, and browser storage.

Check:

- Secrets or credentials embedded in the frontend bundle, source maps, repository, or build configuration.
- Unsafe HTML rendering, `dangerouslySetInnerHTML`, DOM injection, URL handling, and untrusted content rendering.
- Authentication tokens and sensitive data stored in localStorage, sessionStorage, or other browser-accessible locations.
- Whether frontend route guards are incorrectly relied upon instead of backend authorization.
- Checkout retry behavior, stale idempotency keys, duplicate submission, and error recovery.
- API base URL configuration and accidental calls to localhost in production.
- CORS and mixed-content errors.
- Client-side validation that lacks equivalent server-side validation.
- Dependency risks, vulnerable packages, outdated libraries, and unnecessary packages.
- Sensitive information exposed in browser console messages, analytics, source maps, URLs, or crash reporting.
- SPA routing, cache behavior, service workers, and stale bundles if applicable.
- Security headers and content security policy compatibility with the actual deployed site.

Do not recommend moving tokens into cookies or changing the authentication architecture without analyzing the existing threat model and implementation first.

## 9. Render, Docker, deployment, and supply chain

Inspect deployment configuration and production-facing evidence available to the agent.

Check:

- HTTPS enforcement, certificate validation, redirect behavior, and secure API origins.
- Correct Render service separation and environment-variable scope.
- Exact production CORS origin configuration.
- Health checks and deploy behavior.
- Container base-image freshness, OS packages, CA certificates, non-root execution, writable paths, and unnecessary runtime tools.
- Secrets in image layers, build logs, Git history, CI logs, or deployment artifacts.
- Debug mode, development settings, Swagger, and diagnostics enabled in production.
- Build reproducibility, dependency lockfiles, package integrity, and dependency update practices.
- GitHub Actions permissions, untrusted pull-request execution, unsafe shell interpolation, and exposed CI secrets.
- Branch protection, review requirements, and release/deployment permissions where evidence is available.
- Dependency and container vulnerability scanning options.
- Database plan limitations, backup retention, recovery procedures, and service sleep/cold-start behavior.
- Security implications of untrusted external API responses, particularly catalogue data consumed from upstream services.
- Whether upstream outages could cause unsafe fallback behavior or unexpected checkout behavior.

Use current package and platform advisories only where the environment permits. Record the date, advisory identifier, affected package/version, and whether the issue is confirmed applicable.

Do not claim to have inspected Render's private configuration unless actual authorized evidence is available. Distinguish repository configuration from settings that must be verified manually in the dashboard.

## 10. Logging, privacy, and operational resilience

Inspect logs, correlation IDs, exception handling, monitoring, and failure recovery.

Check:

- Passwords, access tokens, refresh tokens, session identifiers, connection strings, personal data, or request bodies appearing in logs.
- Correlation and trace IDs being generated, propagated, sanitized, and logged consistently.
- Whether clients can inject newlines or control characters into log entries.
- Whether error responses leak sensitive information.
- Whether authentication and checkout failures are observable without exposing customer secrets.
- Alerting for suspicious authentication activity, repeated authorization failures, rate-limit abuse, and unusual checkout patterns.
- Data retention and deletion procedures.
- Incident response, secret rotation, backup restoration, and service recovery documentation.
- Whether readiness failures or upstream outages are reported accurately and safely.

## 11. Security testing and tools

Use available tools where appropriate, such as:

- Existing unit, component, integration, and Playwright tests.
- .NET analyzers and dependency vulnerability checks.
- `npm audit` or an equivalent package advisory scanner, interpreting findings rather than blindly applying fixes.
- Secret scanning against tracked files and Git history, without printing secret values.
- Static application security testing (SAST).
- Software composition analysis (SCA).
- Container image scanning if Docker is available.
- OWASP ZAP baseline or equivalent passive HTTP scanning against an explicitly authorized target.
- Targeted regression tests for every confirmed finding.

Do not run unbounded scans, aggressive active scans, denial-of-service tests, or destructive checks against production. Do not automatically upgrade all dependencies; breaking upgrades can introduce new problems.

If a tool is unavailable, explain the limitation and give the exact command or manual step needed to complete that check.

## 12. Threat model and prioritization

Identify the most relevant threats for this particular application, including:

- Account takeover.
- Unauthorized access to customer information and orders.
- Credential theft and token replay.
- Checkout duplication or manipulation.
- Abuse of registration, sign-in, and checkout endpoints.
- Database compromise or data loss.
- Exposed secrets and compromised build/deployment credentials.
- Cross-origin attacks and frontend injection.
- Upstream API compromise or outage.
- Unsafe deployment or recovery procedures.

For each finding, assign a severity of Critical, High, Medium, Low, or Informational based on impact, exploitability, required access, and actual exposure.

Use CVSS where applicable, but do not invent scores for business-logic findings. Explain assumptions and business impact.

## 13. Required report

Create a comprehensive report at `Documentation/Security-Audit.md`, but only after completing the read-only audit. Do not overwrite existing user work; inspect the file first if it already exists.

The report must contain:

1. Executive summary.
2. Scope and exclusions.
3. Architecture and threat model.
4. Audit methodology and tools used.
5. A coverage matrix for every security domain.
6. Findings ordered by severity.
7. For each finding:
   - Unique ID.
   - Severity and confidence.
   - Affected component and file/line references.
   - Evidence and a concise explanation of the vulnerability.
   - Realistic attack scenario.
   - Security and business impact.
   - Preconditions and exposure.
   - Recommended remediation.
   - Regression test needed.
   - Whether the finding was confirmed, partially verified, or not validated.
8. Positive controls verified with evidence.
9. Checks not completed and why.
10. False positives or findings dismissed, with rationale.
11. Production dashboard checks requiring my manual verification.
12. Prioritized remediation plan: immediate, before the next release, and longer-term hardening.
13. Exact commands and test results, including failed checks.
14. A clear statement of what was and was not tested against production.

Do not label the application "secure" simply because no vulnerabilities were found.

## 14. Stop after the initial audit

The first phase is assessment and reporting only.

Do not implement fixes, change production configuration, commit, push, rotate secrets, or deploy as part of the initial audit.

At the end, provide a concise summary of the highest-priority findings and ask for my approval to remediate them in a separate phase.

If no confirmed vulnerabilities are found, report that honestly while highlighting unverified controls, remaining exposure, and recommended hardening work.


## Three things I especially want the audit to get right

- Authentication and rate limiting: verify the actual implementation, including whether identity-based limits can be bypassed — not just whether the environment variables are set.
- Checkout idempotency: stress-test concurrency and uncertain outcomes using a disposable PostgreSQL database, because passing ordinary checkout tests doesn't prove race conditions are safe.
- Production secrets and deployment: check what is in Git history, the built frontend bundle, Docker image, and deployment configuration, while ensuring secrets aren't copied into the audit report.