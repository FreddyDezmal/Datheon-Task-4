# Project: Full-Stack E-Commerce Catalogue and Interactive Shopping Cart

## Role

Act as a senior full-stack software engineer specializing in React, TypeScript, C#, ASP.NET Core Web API, RESTful architecture, and modern e-commerce user experiences.

Build a polished, functional e-commerce storefront that demonstrates advanced frontend state management, backend API design, reliable monetary calculations, persistent cart state, and interactive checkout workflows.

The result should be a maintainable, runnable application suitable for a developer portfolio or technical assessment.

## 1. Technology Stack

Use the following architecture:

**Frontend**
- React with TypeScript.
- Vite for frontend tooling.
- CSS Grid and Flexbox for responsive layouts.
- React Context API with useReducer, or an equally well-justified state management approach.
- React Router if multiple pages are needed.
- Fetch API or Axios for HTTP communication.
- Browser localStorage for persistent cart state.

**Backend**
- C# with ASP.NET Core Web API.
- Use a modern, stable .NET release compatible with the development environment.
- Follow a clean separation of concerns using controllers or minimal APIs, services, models, and DTOs.
- Use dependency injection and asynchronous operations.
- Add Swagger/OpenAPI documentation.
- Use appropriate validation and consistent error responses.

**External product data**
- FakeStoreAPI: https://fakestoreapi.com/products
- Single-product endpoint: https://fakestoreapi.com/products/{id}
- Category endpoint: https://fakestoreapi.com/products/categories

Keep the frontend and backend independently runnable during development.

## 2. Product Catalogue

Create an attractive storefront featuring a responsive product grid.

Each product card must display:
- Product image.
- Product title, truncated gracefully when necessary.
- Price formatted as currency.
- Product category.
- Rating and review count, where available.
- A contextual badge, such as "Top Rated", "Popular", or "Best Value", derived from available product data or clearly documented demo rules.
- An Add to Cart button with visible interaction feedback.

Implement:
- Product search by title.
- Category filters for Electronics, Jewelry, Men's Clothing, and Women's Clothing, grouped under a general Clothing filter where appropriate.
- An All Products filter.
- Sorting by price ascending, price descending, and rating.
- Loading indicators, empty states, and recoverable error states.
- Responsive layouts for mobile, tablet, and desktop.

Normalize the source API's category strings so filtering works consistently. Do not assume the API provides categories named exactly "Clothing" or "Jewelry" in every case.

## 3. Backend API and Integration

Build an ASP.NET Core API that acts as the storefront's backend-for-frontend layer.

The backend should retrieve product information from FakeStoreAPI through a reusable HttpClient configured with IHttpClientFactory.

Suggested endpoints:

- GET /api/products — retrieve products, supporting optional search, category, sorting, and filtering parameters.
- GET /api/products/{id} — retrieve a product by ID.
- GET /api/categories — retrieve available normalized categories.
- GET /api/cart — retrieve the current cart if server-side cart sessions are implemented.
- POST /api/checkout — validate and process a simulated checkout.

For the cart, choose one of these approaches and explain the decision:
1. A client-managed cart persisted in localStorage, with the backend validating the submitted order at checkout.
2. A server-managed cart with a session or cart identifier and persistent storage.

Prefer the first approach for a manageable portfolio project unless server-side persistence is specifically needed. Do not implement both approaches unnecessarily.

The backend must:
- Use asynchronous HTTP calls and cancellation tokens where appropriate.
- Handle upstream API failures, timeouts, missing products, and malformed responses.
- Validate product IDs and requested quantities.
- Avoid trusting product prices supplied by the frontend during checkout.
- Retrieve or verify authoritative prices before calculating the final order total.
- Use DTOs rather than exposing internal implementation details.
- Return meaningful HTTP status codes and structured error responses.
- Keep configuration, validation, and business logic separate from controllers.
- Include CORS configuration restricted to the development frontend origin.

Do not create fake database persistence while claiming orders are stored permanently. Clearly distinguish simulated checkout from production order processing.

## 4. Shopping Cart and State Management

Create a persistent slide-out cart drawer accessible from the navigation bar.

The navbar must include:
- Store logo or brand name.
- Search field.
- Category navigation.
- Shopping cart icon.
- Cart badge showing the total number of units, not the number of distinct products.

Implement these cart operations:

- addToCart(product)
- removeFromCart(productId)
- updateQuantity(productId, quantity)
- clearCart()
- calculateSubtotal()
- calculateTax()
- calculateShipping()
- calculateTotal()

Cart requirements:
- Adding an existing product increases its quantity instead of creating a duplicate line item.
- Each line item displays its image, title, unit price, quantity controls, line subtotal, and remove action.
- Decrementing a quantity from one either removes the item or is prevented with clear UI feedback. Choose one consistent behavior and document it.
- Quantity values must always be positive integers for retained cart items.
- Removing an item immediately updates the badge and totals.
- The cart drawer supports opening, closing, and continuing shopping.
- Clicking outside the drawer or pressing Escape closes it.
- Provide an accessible close button and sensible keyboard navigation.
- Prevent background interaction when the drawer is modal.
- Display a useful empty-cart state with a link or action to return to shopping.
- Persist cart contents across page refreshes using localStorage.
- Handle invalid or outdated localStorage data without crashing the application.
- Avoid stale state, accidental mutation, and inconsistent derived totals.

Use a centralized cart state or reducer. Treat subtotal, tax, shipping, and total as derived values rather than separately mutable state.

## 5. Monetary Calculations

Implement accurate and testable pricing calculations.

Use a clearly defined currency configuration. FakeStoreAPI's sample prices do not necessarily establish the intended store currency, so make the currency configurable and document the default.

Use the following illustrative business rules unless otherwise specified:

- Subtotal = sum of unit price multiplied by quantity for every cart item.
- Tax = 8% of the merchandise subtotal.
- Shipping = $5.99 when the subtotal is below $50, otherwise free.
- Total = subtotal + tax + shipping.

These are demonstration values, not claims about any actual jurisdiction's tax laws.

Requirements:
- Use decimal arithmetic on the C# backend for monetary values.
- Avoid repeated floating-point accumulation in frontend calculations. Use integer minor units where practical or a consistent rounding strategy.
- Round monetary amounts to two decimal places at defined calculation boundaries.
- Make sure displayed totals and submitted checkout totals follow the same rules.
- Ensure that an empty cart has a total of zero.
- Define how shipping thresholds and tax interact, and document the selected policy.
- Recalculate the order using backend-verified product prices at checkout.

Include examples or unit tests covering quantity changes, multiple products, tax, shipping thresholds, rounding, and empty carts.

## 6. Slide-Out Checkout Experience

Implement a polished drawer or checkout panel with:
- Itemized cart contents.
- Quantity controls.
- Subtotal, tax, shipping, and final total.
- A prominent Checkout button.
- A Continue Shopping action.

When the user checks out:
1. Validate that the cart is not empty.
2. Open a checkout modal or dedicated checkout step.
3. Collect minimal demo customer details, such as name and email, with validation.
4. Submit the cart and checkout details to the ASP.NET Core backend.
5. Revalidate product existence, quantities, prices, and totals on the server.
6. Display a loading state while the request is processed.
7. Display a clear order confirmation with a generated demo order reference and order summary.
8. Clear the cart only after successful checkout.
9. Preserve the cart if checkout fails.
10. Show a useful retry path for recoverable failures.

Do not integrate a real payment gateway. Do not request credit card numbers or other unnecessary sensitive information.

If FakeStoreAPI is unavailable, handle the error gracefully and explain what the user can do next.

## 7. Visual Design and User Experience

Create a modern, premium-looking storefront inspired by contemporary retail websites.

Use:
- Clean typography and a consistent spacing system.
- A restrained color palette with a clear accent color.
- Well-designed product cards with subtle shadows, borders, and hover states.
- High-quality product image presentation using object-fit.
- Clear visual hierarchy and prominent calls to action.
- Smooth but restrained drawer and modal transitions.
- Skeleton loaders or equivalent loading feedback.
- Helpful success and error notifications.
- Consistent button, input, badge, and dialog styling.

Ensure:
- Responsive design across common screen sizes.
- Accessible labels and semantic HTML.
- Sufficient color contrast.
- Visible keyboard focus indicators.
- Appropriate ARIA attributes for modal and drawer behavior.
- Reduced-motion preferences are respected.

Avoid excessive animations, cluttered layouts, and unnecessary dependencies.

## 8. Architecture and Code Quality

Organize the project into a clear frontend/backend structure, for example:

/storefront
  /frontend
    /src
      /components
      /pages
      /context
      /hooks
      /services
      /types
      /utils
      /styles
  /backend
    /Controllers
    /Services
    /Models
    /DTOs
    /Middleware
    /Tests

Adapt the structure where appropriate and explain any deviations.

Code requirements:
- Use descriptive names and focused components.
- Keep API access separate from UI components.
- Keep cart state management separate from presentation.
- Extract reusable calculation and formatting utilities.
- Avoid giant components, duplicated business logic, and unnecessary abstractions.
- Use strict TypeScript settings where practical.
- Validate external API responses and checkout inputs.
- Include meaningful comments only where they clarify non-obvious behavior.
- Never expose secrets or rely on frontend validation as the sole security control.

## 9. Testing

Provide a practical testing strategy with representative tests.

At minimum, test:
- Product filtering and searching.
- Adding a product to the cart.
- Adding the same product multiple times.
- Incrementing and decrementing quantities.
- Removing an item.
- Cart badge counts.
- Subtotal, tax, shipping, and final total calculations.
- LocalStorage hydration and invalid stored data.
- Checkout success and failure.
- Backend product retrieval and upstream API failures.
- Server-side price validation and quantity validation.

Use appropriate tools such as Vitest and React Testing Library for frontend tests and xUnit for backend tests.

## 10. Required Deliverables

Produce the project in a practical implementation sequence:

1. Explain the architecture and the responsibilities of the frontend and backend.
2. Show the complete folder structure.
3. List the prerequisites and installation commands.
4. Implement the ASP.NET Core backend, including configuration, DTOs, services, endpoints, validation, and error handling.
5. Implement the React frontend, including product fetching, search, filtering, state management, cart drawer, calculations, and checkout.
6. Provide the necessary CSS for the complete responsive interface.
7. Add representative frontend and backend tests.
8. Provide example API requests and responses.
9. Include environment configuration examples without real secrets.
10. Explain how to run the backend and frontend locally, including the expected URLs and ports.
11. Include troubleshooting guidance for CORS, API failures, and dependency installation.

For each major file, show its exact path and complete contents. Include all imports and dependencies. Avoid pseudocode, unexplained omissions, and placeholder implementations for core functionality.

If the response is too long for one message, divide the implementation into clearly numbered parts. Maintain consistency across files and explicitly identify which files belong to each part. Do not silently omit required files.

## 11. Acceptance Criteria

The implementation is complete only when:

- The product catalogue loads from FakeStoreAPI through the backend.
- Search, category filters, and sorting work together.
- The cart drawer opens and closes correctly.
- Cart quantities and badge counts remain consistent.
- Removing or updating items immediately updates derived totals.
- Tax and shipping rules behave correctly at and around the free-shipping threshold.
- The cart survives a browser refresh.
- Invalid stored state and upstream API errors are handled gracefully.
- Checkout verifies prices and quantities on the backend.
- Successful checkout produces an order confirmation and clears the cart.
- Failed checkout preserves the cart and allows recovery.
- The interface is responsive and keyboard-accessible.
- Core calculation and cart behavior have automated tests.
- The project can be started by following the supplied instructions.

Before finishing, review the implementation against every acceptance criterion. Identify any known limitations explicitly and do not claim that untested functionality has passed tests.

Begin by presenting the architecture and implementation plan, then build the working application step by step.