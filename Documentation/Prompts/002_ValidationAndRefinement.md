# Task: Audit and Finalize an Existing Full-Stack E-Commerce Storefront

## Role

Act as a senior .NET 10 and React 19 engineer specializing in integration testing, API reliability, security, and production-readiness reviews.

## Project Context

An existing application is provided and includes:

- Backend: ASP.NET Core Web API on .NET 10.
- Frontend: React 19, TypeScript, and Vite.
- External product source: FakeStoreAPI.
- Product catalogue, search, category filtering, and sorting.
- Persistent browser-based shopping cart.
- Cart quantity management and subtotal, tax, and shipping calculations.
- Server-side product price verification at checkout.
- Checkout confirmation and cart clearing.
- Error handling and keyboard-accessible cart drawer.
- README documentation and `tools/fakestore_mock.py` for offline testing.

The README documents the architecture, folder tree, configuration, setup instructions, API examples, troubleshooting, and acceptance review.

## Existing Verification Results

Treat these as reported results, not as independently verified facts:

**Frontend**
- Strict TypeScript checking passes.
- Production build succeeds.
- All 54 Vitest and Testing Library tests pass.
- Headless browser checks confirm cart persistence, correct totals, checkout confirmation, error recovery, and drawer focus behavior.

**Backend**
- API compilation reportedly succeeds without warnings.
- Endpoints were exercised against a local FakeStoreAPI stand-in.
- Expected responses, error handling, and CORS behavior were observed.
- A reported set of 99 backend test cases passed using a lightweight test stand-in, not the actual xUnit test runner.

**Outstanding verification**
- Actual NuGet package restoration and `dotnet test` execution.
- Swagger/OpenAPI availability.
- Live FakeStoreAPI integration.
- Full compatibility with the intended development environment.

Do not represent any outstanding verification as successful without executing it.

## Phase 1: Inspect Before Changing

1. Inspect the repository and README.
2. Identify the solution and project files, target frameworks, package versions, test projects, configuration files, and external dependencies.
3. Review the existing architecture and identify incomplete or inconsistent implementations.
4. Preserve working functionality and avoid unnecessary rewrites.
5. Establish a baseline of the current build and test results.

Do not assume file paths, package versions, environment variables, or existing implementations without inspecting the project.

## Phase 2: Verify the Backend

Run the actual .NET toolchain against the existing project:

1. Check the installed .NET SDK and runtime.
2. Run `dotnet restore`.
3. Build the solution in the appropriate configuration.
4. Run the real xUnit test projects using `dotnet test`.
5. Record the actual number of tests discovered, passed, failed, skipped, or not executed.
6. Investigate package compatibility, test discovery, restore failures, and build warnings.
7. Verify Swagger/OpenAPI configuration and endpoint availability.
8. Exercise the API against the supplied mock server.
9. Test malformed upstream responses, timeouts, HTTP failures, missing products, invalid quantities, and checkout price mismatches.
10. Test CORS using the documented frontend origin.

If network access, package feeds, or upstream services are unavailable, report the precise limitation and the commands needed to finish verification elsewhere. Do not fabricate results.

## Phase 3: Verify Pricing and Checkout Integrity

Inspect the checkout implementation for the following:

- The server never trusts client-submitted prices or totals.
- Product IDs and quantities are validated.
- Subtotal, tax, shipping, rounding, and total calculations are consistent.
- Stale checkout quotes are returned with a clear response contract and require renewed customer confirmation.
- Failed checkout preserves the cart.
- Successful checkout clears the cart only after the server confirms success.
- Currency and pricing rules are documented and consistently applied.
- Product catalogue caching handles expiration and upstream failures safely.

Identify any remaining risks involving concurrent requests, stale prices, unavailable catalogue data, or inconsistent client/server calculations.

Distinguish safeguards already implemented from improvements that would require additional work.

## Phase 4: Verify the Frontend

Confirm that:

- Strict TypeScript checking succeeds.
- The production build succeeds.
- All existing Vitest and Testing Library tests run with their actual configured test runner.
- Cart state persists across refreshes.
- Cart badge counts reflect quantities rather than distinct product count.
- Quantity controls and removal behavior remain consistent.
- Drawer focus management, Escape handling, and keyboard accessibility work.
- Loading, empty, error, retry, and checkout confirmation states behave correctly.

Run browser-level tests where the available environment supports them.

## Phase 5: Documentation and Reproducibility

Verify that the README accurately describes:

- Required SDK and runtime versions.
- Backend and frontend setup.
- Configuration and environment variables.
- API endpoints and response formats.
- Swagger access.
- Mock server startup and failure modes.
- Test commands and their prerequisites.
- Known limitations and unverified external integrations.

Ensure the instructions work from a clean checkout as far as the environment permits.

## Phase 6: Deliver Findings and Fixes

Provide:

1. **Executive summary:** Overall status and highest-priority remaining work.
2. **Verification matrix:** Each check, command executed, actual result, and any environmental limitation.
3. **Defects found:** File path, root cause, impact, and proposed or implemented fix.
4. **Security and correctness review:** Especially checkout repricing, input validation, error handling, and configuration.
5. **Changes made:** Exact files modified and the reason for each change.
6. **Reproduction instructions:** Commands for running the backend, frontend, tests, mock server, and Swagger.
7. **Remaining limitations:** Clearly distinguish code defects from environmental blockers.
8. **Final acceptance review:** Mark each criterion as passed, failed, blocked, or not tested.

## Constraints

- Work with the existing codebase; do not recreate the project from scratch.
- Avoid introducing unnecessary dependencies or architectural complexity.
- Do not replace real test execution with handwritten simulations or a substitute test runner.
- Do not weaken tests merely to obtain a passing result.
- Do not claim live external API verification when only a mock was used.
- Do not claim package restoration or Swagger verification without executing the relevant checks.
- Preserve the existing application behavior unless a defect or explicitly approved improvement requires a change.
- Provide complete patches or edited files for any changes.
- Prioritize reproducible correctness over cosmetic improvements.

Begin by inspecting the supplied repository, then execute the highest-value verification steps and report evidence-based findings.