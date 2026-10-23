# Prompt: Storefront Frontend — Responsive UI, API Integration & User Experience

You are a senior frontend engineer working on my existing **Storefront** repository. Your task is to audit, improve, implement, and verify the frontend across three areas:

1. Responsive UI
2. API Integration
3. User Experience (UX)

## Project Context

Storefront is an existing full-stack e-commerce application with an ASP.NET Core backend built on .NET 10. The backend has undergone improvements to its architecture, authentication and security, database persistence, and error handling.

The frontend already has an existing implementation, components, styling, API interactions, cart functionality, and automated tests. Inspect the repository to confirm the current state rather than assuming any specific framework, file structure, library, endpoint, or feature.

Your job is to improve the existing application, not rebuild it from scratch.

## Primary Objective

Deliver a responsive, accessible, reliable, and polished storefront that communicates correctly with the actual backend.

Preserve the existing brand identity and established functionality wherever practical. Avoid unnecessary dependencies, unrelated refactoring, and purely cosmetic changes that do not improve usability.

---

## 1. Responsive UI

Review the entire storefront across mobile, tablet, laptop, and desktop viewport sizes.

### Layout and responsiveness

- Inspect the home page, product catalogue, product details, cart, checkout, navigation, authentication screens, and other existing routes.
- Use a mobile-first approach where appropriate.
- Ensure layouts adapt naturally to different viewport widths without horizontal overflow, clipped content, overlapping elements, or inaccessible controls.
- Make product grids, product cards, navigation, forms, buttons, and checkout summaries responsive.
- Ensure images maintain suitable aspect ratios and do not become distorted.
- Make cart and checkout layouts usable on small screens without hiding important totals, actions, or validation messages.
- Ensure navigation works well with touch input and does not depend on hover.
- Review fixed and sticky elements to prevent them from covering content or actions.
- Use consistent spacing, typography, alignment, and visual hierarchy across pages.

### Accessibility

- Use semantic HTML and meaningful heading structure.
- Ensure keyboard navigation and visible focus states work correctly.
- Provide accessible labels for form fields, icon buttons, and interactive controls.
- Maintain adequate contrast and avoid communicating status through color alone.
- Provide appropriate alternative text for meaningful images.
- Respect reduced-motion preferences when animations are present.
- Ensure dialogs, menus, notifications, and other interactive elements are accessible.

### Visual consistency

- Preserve the existing visual identity while improving consistency.
- Standardize repeated UI patterns such as buttons, form controls, cards, spacing, and feedback messages.
- Avoid introducing an entirely new design system unless the existing implementation genuinely requires it.
- Prefer small, reusable styling improvements over duplicated CSS.

**Deliverable:** A consistent, responsive interface that remains functional and readable across supported screen sizes.

## 2. API Integration

Inspect the frontend's existing API client, service layer, environment configuration, and all backend endpoints it consumes.

The backend implementation is the source of truth for API contracts. Inspect its actual routes, request models, response models, authentication requirements, validation behavior, and error responses before changing the frontend.

### API architecture

- Centralize API communication in the existing API client or service layer.
- Avoid scattering raw `fetch` calls or duplicated request logic throughout components.
- Use the existing API client and state-management approach where suitable.
- Configure the backend base URL through the appropriate environment configuration.
- Keep local development and production configuration separate.
- Do not hardcode secrets, tokens, credentials, or environment-specific URLs into application code.
- Handle JSON serialization, response parsing, HTTP errors, and network failures consistently.
- Avoid adding dependencies when native browser capabilities or existing libraries are sufficient.

### Contract compatibility

- Verify that frontend requests match the actual backend's routes, HTTP methods, request payloads, and response structures.
- Ensure product data, cart operations, checkout, authentication, and order confirmation work with the updated backend.
- Handle `400`, `401`, `403`, `404`, `409`, `413`, `429`, and `5xx` responses appropriately wherever relevant to the actual API.
- Parse `ProblemDetails` responses when supplied by the backend and show useful, safe feedback to the user.
- Handle expired or missing authentication without creating redirect loops.
- Ensure authenticated requests use the backend's intended authentication mechanism.
- Do not expose credentials or sensitive server details in client-visible errors.
- Respect server-side validation and never treat frontend validation as a security boundary.

### Cart and checkout reliability

- Ensure cart totals, quantities, and checkout summaries are updated consistently.
- Treat backend-calculated prices and totals as authoritative.
- Never assume a client-submitted price is trustworthy.
- If the backend returns a `409 Conflict` because the checkout total or quote is stale, explain the situation and let the user review the updated total before retrying.
- Prevent repeated submissions while a checkout request is processing.
- Handle network failures and uncertain checkout outcomes carefully. Do not automatically submit another order when the first request might have succeeded.
- If the backend supports idempotency keys, use them according to its documented contract.
- Clear or update the cart only when the backend's order result justifies doing so.
- Ensure a failed request does not silently erase the user's cart or entered information.

### API state management

- Provide clear loading, success, empty, and error states for API-driven views.
- Avoid unnecessary duplicate requests and race conditions.
- Cancel obsolete requests when appropriate.
- Prevent stale responses from overwriting newer user selections.
- Ensure the UI remains stable if the backend is temporarily unavailable.
- Do not replace real API calls with mock data merely to make the interface appear functional.
- If mock data is needed for tests, keep it isolated from production behavior.

**Deliverable:** A consistent and reliable API integration that accurately reflects the backend's actual behavior.

## 3. User Experience (UX)

Improve the complete shopping journey, from discovering products to completing checkout.

### Navigation and product discovery

- Make the primary navigation clear and consistent.
- Ensure users can easily find products, view details, access the cart, and continue to checkout.
- Improve product card readability, including product names, prices, images, and relevant actions.
- Provide useful empty states when no products or search results are available.
- Preserve the user's context when navigating between catalogue, product details, and cart where practical.
- Avoid adding search, filtering, sorting, or pagination features unless they fit the existing functionality and can be implemented correctly.

### Feedback and loading states

- Display clear feedback when an operation starts, succeeds, or fails.
- Provide loading indicators for operations that take noticeable time.
- Prevent duplicate actions during important in-progress requests.
- Use helpful, actionable error messages instead of generic alerts or silent failures.
- Distinguish between validation errors, unavailable services, checkout conflicts, and unexpected failures.
- Avoid intrusive notifications for minor events.
- Ensure transient messages are accessible and remain visible long enough to be understood.

### Forms and checkout

- Make forms easy to understand and complete on both mobile and desktop.
- Provide clear field labels, sensible input types, and appropriate autocomplete attributes.
- Validate input early where helpful, while still respecting backend validation.
- Show field-level errors when the API provides enough information to map errors to fields.
- Preserve entered data when recoverable requests fail.
- Clearly display the cart contents, quantities, subtotal, applicable charges, and final total supported by the backend.
- Require the user to review and explicitly confirm material price changes before submitting checkout again.
- Ensure confirmation screens reflect a successful backend result rather than a frontend-only assumption.
- Prevent accidental repeated submissions without leaving the user stuck in a loading state.

### Cart and persistence

- Inspect how the cart is currently stored and synchronized.
- Preserve cart state across normal navigation and refreshes where appropriate.
- If browser storage is used, handle unavailable storage, invalid JSON, and outdated data gracefully.
- Ensure cart initialization behaves correctly under React Strict Mode if the application uses React.
- Avoid creating a second source of truth that conflicts with the backend.
- Explain which cart data is local and which data is persisted on the server.
- If authenticated users and guests have different cart behavior, keep that distinction explicit and tested.

### Performance and polish

- Avoid unnecessary component re-renders and repeated API requests.
- Optimize images and asset loading where appropriate.
- Lazy-load noncritical content when beneficial.
- Avoid layout shifts and overly large bundles.
- Use subtle, purposeful transitions rather than excessive animations.
- Ensure buttons, links, and interactive controls have clear hover, focus, active, and disabled states.
- Remove broken links, dead buttons, placeholder actions, and misleading success messages.
- Keep the interface visually coherent without sacrificing performance or accessibility.

**Deliverable:** A smooth, predictable shopping experience with clear feedback and fewer opportunities for user error.

---

## Working Method

### Stage 1 — Inspect the existing frontend

Before making changes:

1. Inspect the repository structure and identify the frontend framework, build tools, routes, component organization, styling approach, and test setup.
2. Review the existing frontend implementation and identify its strengths and weaknesses.
3. Inspect the backend API contracts, authentication requirements, and error-response format.
4. Identify existing features that must continue working.
5. Check the current test suite and available browser-testing tools.
6. Create a concise implementation plan organized by the three target areas.

Do not assume that the backend's new behavior is already reflected in the frontend. Verify the actual contracts and implementation.

Once the plan is clear, proceed without asking for approval unless a genuine blocking decision requires my input.

### Stage 2 — Implement improvements

Work incrementally.

- Preserve existing routes and functionality where practical.
- Reuse existing components and styling conventions.
- Keep API communication consistent and centralized.
- Improve mobile layouts and accessibility alongside visual styling.
- Implement meaningful loading, empty, error, and success states.
- Ensure cart and checkout behavior matches the backend's actual rules.
- Update or add tests as functionality changes.
- Do not disable failing tests, remove meaningful assertions, or use hardcoded responses to disguise integration problems.

### Stage 3 — Test and verify

Run the actual commands appropriate to the repository.

At minimum:

- Install or restore dependencies using the project's documented process.
- Run linting and type checking if configured.
- Run the production build.
- Run the existing unit and component test suites.
- Add tests for API error handling, cart updates, checkout conflicts, loading states, and relevant authentication flows.
- Use browser automation if already configured or reasonably available.
- Verify layouts at representative mobile, tablet, and desktop viewport sizes.
- Check keyboard navigation and form accessibility.
- Verify that the frontend works against the real local backend rather than relying only on mocked API responses.
- Check browser console errors, failed network requests, and unexpected redirects.
- Confirm that existing backend and frontend contracts remain compatible.

If the backend must be running for integration tests, start it using the repository's documented process or clearly state what needs to be started.

Do not claim that a browser check, accessibility audit, integration test, or viewport test passed unless it was actually executed. Clearly report any environment limitations.

### Stage 4 — Final report

When finished, provide:

1. **Responsive UI:** The key layout and accessibility improvements.
2. **API Integration:** The API services changed and the backend contracts verified.
3. **User Experience:** Improvements to loading states, error messages, cart, checkout, and navigation.
4. **Files Changed:** Important files added or modified.
5. **Testing:** Actual lint, type-check, build, unit-test, integration-test, and browser-test results.
6. **How to Run:** Exact commands and any required environment variables.
7. **Remaining Issues:** Any incomplete features, unverified behavior, or known limitations.

## Constraints

- Work within the existing repository and framework.
- Keep the current backend as the source of truth.
- Do not rebuild the entire frontend without a demonstrated need.
- Do not introduce unnecessary dependencies or a new state-management library solely for this task.
- Do not expose secrets or weaken authentication.
- Do not trust frontend prices, totals, or authorization checks.
- Do not silently swallow API failures.
- Do not introduce fake functionality or claim unverified results.
- Keep the implementation focused on Responsive UI, API Integration, and User Experience.

**Success criteria:** The Storefront frontend is responsive, accessible, integrated with the actual backend, resilient to network and API errors, and easy to use throughout the shopping and checkout journey. Existing functionality continues working, automated checks pass, and another developer can understand and run the application using the documentation.