Proceed with the approved Storefront security remediation in the three phases described below, using `Documentation/Security-Audit.md` as the authoritative source for findings and evidence.

1. Preserve all existing uncommitted work. Inspect the current working tree before editing.
2. Remediate SA-01, SA-04, SA-08, SA-09, and SA-12 first. Inspect dependencies before disabling endpoints or authentication modes. For SA-04, bound connection-pool usage and investigate readiness-probe behavior; do not treat a connection-string change alone as the full fix.
3. Then remediate SA-02 and SA-03, including durable server-side refresh-token revocation, invalidation after password changes, IPv6-aware rate limiting, and per-account sign-in protection. Address the remaining Low findings when appropriate, following the audit's evidence and recommendations.
4. Add regression tests for each fix. Use disposable databases and test accounts. Preserve existing checkout idempotency, transaction, recovery, authorization, and price-validation guarantees.
5. Run the relevant tests after each logical change, followed by the complete existing test suites and production build checks. Do not suppress failures, weaken assertions, or mark unexecuted tests as passed.
6. Update `Documentation/Security-Audit.md` with remediation status, files changed, test evidence, and unresolved issues. Preserve its original findings and evidence; add a clear remediation section rather than deleting historical findings.
7. Do not commit, push, deploy, change Render settings, rotate production secrets, or perform destructive production tests.
8. If a fix requires a breaking API change, a change to the frontend/backend contract, or a production configuration change, explain the requirement and prepare the code and instructions without applying the production change.
9. At completion, provide a summary grouped by finding ID, the exact commands and results, remaining risks, and a separate list of manual production steps.

Start with the immediate-risk findings. Stop and report if the implementation requires a significant security design decision rather than silently choosing a risky approach.