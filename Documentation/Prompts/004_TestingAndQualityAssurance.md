# Prompt: Storefront — Testing & Quality Assurance

You are a senior QA engineer and test automation engineer working on my existing **Storefront** full-stack e-commerce repository.

Your task is to audit, improve, implement, and document the project's testing and quality assurance practices across five areas:

1. Unit Testing
2. Component Testing
3. Function Testing
4. User Testing
5. Test Reporting with Coverage Analysis

## Project Context

Storefront is a full-stack e-commerce application with:

**Frontend**
- React 19
- TypeScript with strict type checking
- Vite
- Plain CSS with design tokens
- Vitest and React Testing Library
- Headless Chromium browser checks

**Backend**
- ASP.NET Core on .NET 10
- C# and xUnit
- Entity Framework Core and database persistence
- Authentication and authorization
- Checkout processing, server-side price validation, and idempotency handling
- Centralized error handling and ProblemDetails responses

The repository has previously reported:
- 98 frontend Vitest and Testing Library tests.
- 106 backend xUnit tests.
- Browser tests against the real local API.
- Mutation checks covering 13 safeguards, with 11 detected by tests and two found to be redundant with other protections.

These are baseline figures from previous project notes, not assumed current results. Inspect the actual repository and rerun the relevant tests before reporting the current state.

## Primary Objective

Establish a maintainable testing strategy that verifies correctness, catches regressions, checks frontend/backend integration, and produces useful coverage and test reports.

Do not rewrite the application or replace the existing testing frameworks without a compelling technical reason. Preserve existing behavior and API contracts unless a verified defect requires a change.

**Important:** Do not chase a high test count or coverage percentage at the expense of test quality. Tests must verify meaningful behavior, include useful assertions, and fail when the behavior they protect is broken.

---

## 1. Unit Testing

### Frontend unit tests

Review and test isolated functions, utilities, services, hooks, and business rules.

Prioritize:

- API request construction, timeout handling, cancellation, and error normalization.
- Response parsers and invalid-response handling.
- Authentication token expiry, refresh behavior, and rejected sessions.
- Idempotency-key generation and reuse.
- Checkout outcome classification.
- Cart calculations, quantity updates, and storage parsing.
- Product filtering, sorting, and other pure utility functions.
- Validation and error-mapping logic.
- Handling of malformed data, empty values, and unexpected API responses.

Requirements:

- Use the existing Vitest setup and conventions.
- Mock external dependencies only where isolation is appropriate.
- Test success paths, failure paths, boundary conditions, and important race conditions.
- Use fake timers only where they improve determinism.
- Avoid tests that depend on real network access or timing-sensitive sleeps.
- Ensure asynchronous tests wait for the actual condition rather than relying on arbitrary delays.

### Backend unit tests

Review the existing xUnit tests and identify business logic that can be tested independently of HTTP, database infrastructure, or external services.

Prioritize:

- Checkout totals and server-side price validation.
- Product availability and quantity validation.
- Idempotency behavior and duplicate-submission prevention.
- Authentication and authorization decision logic where appropriate.
- Error classification and ProblemDetails mapping.
- Domain rules and service-level behavior.
- Validation and boundary conditions.

Use mocks or fakes when appropriate, but do not mock the behavior that a test is specifically intended to verify.

**Deliverable:** A focused suite of fast, deterministic unit tests that protects the application's important rules.

---

## 2. Component Testing

Review the React component test suite and ensure that tests verify what users can observe and do.

Prioritize:

- Product cards and catalogue states.
- Product details dialogs and URL-driven product links.
- Cart contents, quantity controls, and totals.
- Checkout forms and confirmation flows.
- Login and registration forms.
- Account information and order history.
- Loading, empty, success, and error states.
- Modal focus management, keyboard interaction, Escape behavior, and focus restoration.
- Accessible names, labels, validation messages, and disabled controls.
- Responsive UI behavior where it can be tested reliably.

Requirements:

- Use React Testing Library and query elements by accessible role, label, or other user-facing semantics whenever possible.
- Prefer tests that interact with the component as a shopper would.
- Avoid excessive reliance on internal implementation details, component state, or CSS class names.
- Use the existing API fixtures and mock `fetch` infrastructure where appropriate.
- Verify important state transitions, not just whether a component renders.
- Test React Strict Mode behavior where it affects initialization or effects.
- Ensure tests clean up after themselves and do not leak state between cases.

**Deliverable:** Component tests that demonstrate that key storefront interactions behave correctly and remain accessible.

---

## 3. Function Testing and Integration Testing

Interpret function testing as verification of complete functions, workflows, and interactions across module boundaries. First inspect how the repository currently distinguishes unit, component, integration, and browser tests. Preserve useful existing terminology while documenting a clear test taxonomy.

### Frontend function and workflow tests

Cover complete user-facing flows such as:

- Loading the catalogue and displaying products.
- Opening a product from a shared URL.
- Adding products to the cart and updating quantities.
- Registering and signing in.
- Restoring a session after a page reload.
- Refreshing an expired access token.
- Recovering gracefully from rejected refresh tokens.
- Completing a checkout.
- Handling price changes and unavailable products.
- Recovering from an uncertain checkout outcome without creating a duplicate order.
- Viewing order history.
- Signing out and clearing local session credentials.

### Backend integration tests

Verify behavior across the actual ASP.NET Core application pipeline where practical.

Cover:

- HTTP routes, request binding, validation, and response status codes.
- Authentication and authorization middleware.
- ProblemDetails responses.
- Database persistence and transactions.
- Entity Framework Core constraints and uniqueness rules.
- Checkout and idempotency behavior.
- Unauthorized access to protected resources.
- Isolation of orders belonging to different users.
- Correct handling of invalid, expired, or missing tokens.

Use a real test database where necessary. If SQLite is used for tests, account for differences between SQLite and the production database provider, if one exists. Do not assume that mocked repository tests verify database behavior.

Keep tests deterministic, isolated, and safe to run repeatedly. Never use a production database or real customer data in automated tests.

### Test categorization

Organize tests into sensible, documented categories, such as:

- Unit
- Component
- Integration
- End-to-end / browser

Use the existing naming conventions and runner capabilities. Do not add multiple test frameworks simply to create separate labels.

**Deliverable:** Automated tests covering important workflows and integration boundaries, with clear separation between isolated tests and tests requiring infrastructure.

---

## 4. User Testing

Improve confidence in the application from the perspective of actual shoppers.

### Automated browser testing

Inspect the existing headless Chromium setup and reuse it if practical. Do not add another browser automation framework without a clear benefit.

Cover realistic shopper journeys:

1. Open the storefront and browse products.
2. Open product details and navigate back to the catalogue.
3. Add an item to the cart and change its quantity.
4. Register or sign in.
5. Complete checkout successfully.
6. Handle a changed price or unavailable product.
7. Recover from an interrupted or uncertain checkout without duplicating an order.
8. Review order history.
9. Sign out.
10. Exercise important keyboard navigation and accessibility paths.

Test important failure scenarios as well as successful flows.

### Responsive and accessibility testing

- Verify representative viewport widths of 320, 375, 768, 1024, and 1440 pixels.
- Detect horizontal overflow, clipped content, and controls that are difficult to reach.
- Check dialog focus management, keyboard navigation, accessible names, and validation announcements.
- Add an automated accessibility scan if a suitable tool is already installed or can be introduced with minimal overhead.
- Document any accessibility checks that require manual review or a screen reader.

Do not treat a passing automated accessibility scan as proof of full accessibility compliance.

### Manual user-testing plan

Create a concise, repeatable manual testing checklist that another person can follow without needing to understand the code.

Include:

- Preconditions and setup.
- Test steps.
- Expected results.
- Actual results.
- Pass/fail status.
- Defect notes and reproduction steps.

Use realistic, non-sensitive test data. If no human participants are available, document the checklist for future execution rather than claiming user testing has occurred.

**Deliverable:** Repeatable browser-based shopper journey tests and a practical manual user-testing checklist.

---

## 5. Test Reporting with Coverage Analysis

Make testing results measurable and easy to review.

### Frontend coverage

Inspect the current Vitest configuration and configure coverage using a compatible provider if coverage is not already available.

Prefer a maintainable report format that includes:

- Statements
- Branches
- Functions
- Lines

Generate machine-readable coverage data and a human-readable HTML report. Use LCOV or another compatible format if the repository or CI tooling benefits from it.

### Backend coverage

Inspect the .NET test project and configure a compatible coverage collector, such as Coverlet, if one is not already in use.

Generate a report showing:

- Line coverage.
- Branch coverage where supported.
- Coverage by project, namespace, class, or file.
- Important untested areas.

Use an appropriate report generator if needed, but avoid unnecessary dependencies.

### Coverage policy

- Establish the actual current coverage baseline before setting thresholds.
- Exclude generated code, build output, and other justified exclusions from meaningful coverage calculations.
- Avoid broad exclusions designed to inflate the percentage.
- Do not set arbitrary high thresholds that make the suite brittle or encourage low-value tests.
- If practical, introduce reasonable thresholds based on the observed baseline and raise them gradually.
- Treat coverage as a diagnostic measure, not proof of correctness.
- Investigate critical code with low coverage, particularly checkout, idempotency, authentication, authorization, and error handling.

### Test reports

Provide clear commands to generate and view reports locally.

The final report should distinguish:

- Tests discovered.
- Tests executed.
- Tests passed.
- Tests failed.
- Tests skipped.
- Execution duration.
- Coverage percentage and denominator.
- Coverage exclusions.
- New tests added.
- Known untested or weakly tested areas.

If a test fails, investigate the cause. Do not silently skip it, remove assertions, or weaken the test just to achieve a green build.

**Deliverable:** Reproducible frontend and backend coverage reports with documented baselines, meaningful exclusions, and actionable findings.

---

## Working Method

### Stage 1 — Audit

1. Inspect the frontend and backend test projects and configuration.
2. Identify existing test commands and available CI or browser tooling.
3. Run the existing suites before making changes.
4. Record actual test counts, failures, execution time, and current coverage if available.
5. Identify critical behaviors with inadequate test coverage.
6. Produce a concise implementation plan.

Do not assume the historical test counts or earlier results still apply.

### Stage 2 — Improve

1. Fix unreliable or misleading tests.
2. Add missing unit and component tests.
3. Add integration tests for important API and database boundaries.
4. Expand browser coverage for critical shopper journeys.
5. Create a manual testing checklist.
6. Configure coverage collection and reporting.
7. Document how to run each test category independently and how to run the complete suite.

Prioritize high-risk behavior over low-value test quantity.

### Stage 3 — Verify

Run the actual commands supported by the repository.

At minimum:

- Run frontend tests.
- Run backend xUnit tests.
- Build the backend in Release configuration.
- Run frontend type checking and the production build.
- Run integration tests against an isolated test database where required.
- Run the available browser journey tests.
- Generate frontend and backend coverage reports.
- Confirm that the documented commands work from a clean or reproducible setup.

Keep unit tests independent of external network availability. Where live API tests require the backend or upstream services, document and isolate those prerequisites.

Report actual outcomes. Do not claim tests or coverage reports passed if they were not executed.

### Stage 4 — Final Report

When finished, provide:

1. **Existing Test Baseline:** The actual initial counts and results.
2. **Unit Testing:** Important logic tested and gaps addressed.
3. **Component Testing:** Key components and user interactions covered.
4. **Integration Testing:** API, authentication, database, and checkout scenarios covered.
5. **User Testing:** Browser journeys, viewport checks, accessibility checks, and the manual checklist.
6. **Coverage Analysis:** Before-and-after percentages, report locations, denominators, and exclusions where available.
7. **Files Changed:** Important test files and configuration changes.
8. **Commands:** Exact commands to execute each suite and generate reports.
9. **Actual Results:** Passed, failed, skipped, and unexecuted tests.
10. **Remaining Risks:** Important areas that still require automated or manual verification.

## Constraints

- Preserve the existing frontend and backend frameworks.
- Reuse existing testing tools wherever practical.
- Never use production data or a production database for tests.
- Do not introduce flaky tests dependent on arbitrary sleeps or uncontrolled external services.
- Do not hide failures or reduce assertions to achieve passing results.
- Do not fabricate user testing or coverage figures.
- Do not confuse mocked API tests with real integration tests.
- Keep the suite understandable, maintainable, and appropriate for an internship project.

**Success criteria:** Storefront has reliable automated tests at the unit, component, integration, and browser levels; a documented manual user-testing plan; reproducible frontend and backend coverage reports; and clear evidence that critical shopping, authentication, database, and checkout behavior works as intended.

## Extra Attention

Please pay extra attention to the following:

Checkout and idempotency: Tests should prove that interrupted requests and retries do not accidentally create duplicate orders.



Authentication boundaries: Tests should verify that one user cannot access another user's orders and that invalid or expired tokens cannot silently become guest checkout.



Coverage quality: A lower but honest coverage percentage is more useful than a high percentage achieved by testing trivial code while missing critical business rules.