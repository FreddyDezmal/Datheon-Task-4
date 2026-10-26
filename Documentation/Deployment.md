# Deployment preparation: PostgreSQL and Docker

This covers running the Storefront API on PostgreSQL in a container as a Render web service, and the frontend as a Render Static Site (section 10). The API is live at `https://datheon-task-4.onrender.com` (since 24 October 2026); the results are under "Verification". Each item is marked:
- *verified*: run as described, with the results recorded;
- *not run here*: something that still has to happen on a machine with registry access, or on Render.

---

## 1. Database provider

`Database:Provider` selects the engine. It has **no default and no fallback**:

| Value | Context and migrations | Use |
|---|---|---|
| `PostgreSql` | `PostgreSqlStorefrontDbContext`, migrations in `Data/Migrations/PostgreSql` | Production |
| `Sqlite` | `StorefrontDbContext`, migrations in `Data/Migrations` (unchanged) | Local development and tests. Set in `appsettings.Development.json`. |

- `appsettings.json`, shared by every environment, now names **no provider and no connection string**. So a deployment missing either fails at startup with a message naming the setting. It doesn't quietly use a local SQLite file, and there's no in-memory option at all. *Verified by tests.*
- A malformed PostgreSQL connection string, or one without `Host` or `Database`, also stops startup. The error never repeats the connection string, because it contains the password. *Verified.*
- `Sqlite` set explicitly outside Development still works, with a warning in the log. The browser test suite relies on that. It's never chosen implicitly.
- **No retrying execution strategy** (`EnableRetryOnFailure`) is configured, on purpose. It would re-run database work without the caller knowing, and can't be combined with checkout's explicit transaction. Retries stay with the client, which reuses its `Idempotency-Key`. *Verified by a test.*

Production settings, as environment variables:

| Variable | Example | Notes |
|---|---|---|
| `Database__Provider` | `PostgreSql` | Required |
| `ConnectionStrings__Storefront` | `Host=…;Port=5432;Database=storefront;Username=storefront_app;Password=…;SSL Mode=Require` (live since 26 October 2026: the internal host, `storefront_app`, TLS required) | Npgsql **key=value** format. A `postgres://user:pass@host/db` URL (the form Render shows) must be converted. Use the TLS mode your provider requires. Keep it in the platform's secret settings, never in a file. |
| `AllowedHosts` | `storefront-api.onrender.com` | **Required.** The default (`localhost;127.0.0.1`) answers 400 to any other host name (*verified*). See blockers. |
| `Cors__AllowedOrigins__0` | the Static Site's origin, e.g. `https://<site>.onrender.com` | The frontend's exact origin: scheme and host, no path, no trailing slash (section 10). It replaces the `http://localhost:5173` default. |
| `PORT` | set by Render | The image falls back to 10000 |
| `ASPNETCORE_ENVIRONMENT` | `Production` | Already the image default |
| `Database__ApplyMigrationsOnStartup` | leave unset (`false`) | Migrations are applied separately (section 3) |
| `ForwardedHeaders__Enabled` | `true` on Render (*verified*, section 7) | Off: client addresses and HTTPS aren't taken from proxy headers |
| `ForwardedHeaders__ClientAddressHeader` | **`CF-Connecting-IP`** on Render. **Not** `X-Forwarded-For`, which is forgeable there. | The header Render's edge sets and clients can't supply |
| `ForwardedHeaders__TrustedProxyHops` | `1` (default) | How many right-most entries of that header come from Render's proxies (1–5) |
| `DataProtection__Certificate__Pfx` | contents of `storefront-data-protection.pfx.base64.txt` | **Required outside Development** (section 8). The certificate that encrypts the sign-in keys, as base64. Secret. |
| `DataProtection__Certificate__Password` | the password chosen when creating it | Secret |
| `DataProtection__PreviousCertificates__0__Pfx` / `__Password` | only while rotating | Decrypt-only: the certificate being replaced (section 8) |
| `DataProtection__AllowUnencryptedKeys` | leave unset | `true` runs without a certificate and stores the keys readable. Only for throwaway local runs. |
| `Database__MaxPoolSize` | leave unset (20) | The connection pool's ceiling when the connection string doesn't set `Maximum Pool Size` (1–100). Npgsql's own default of 100 is a small plan's entire connection limit (Security-Audit.md, SA-04). The startup log shows the value: `(connection pool up to 20)`. |
| `Health__ReadinessCacheSeconds` | leave unset (0) | Optionally reuse a readiness result for this many seconds. Not needed: simultaneous probes already share one database check. |
| `Sessions__RefreshReuseGraceSeconds`, `Sessions__MaxActiveSessionsPerUser` | leave unset (30, 10) | How long a just-replaced refresh token is still honoured (a retry after a lost response), and how many active sessions an account keeps (SA-02) |
| `SignInProtection__FailuresPerAccountAndClient`, `__FailuresPerAccount`, `__WindowMinutes` | leave unset (5, 20, 15) | Failed sign-ins before 429 for one account from one client, and from all clients, per window (SA-03, SA-06) |
| `FakeStore__MaxResponseKilobytes` | leave unset (2048) | The largest FakeStoreAPI response read (SA-14) |

## 2. Schema review: SQLite vs PostgreSQL

The SQLite migration was generated for SQLite types (`TEXT` for decimals and dates, `Sqlite:Autoincrement`), so it isn't portable. PostgreSQL has its own migration set, generated from the same model.

| Area | SQLite (unchanged) | PostgreSQL |
|---|---|---|
| Money (`Subtotal`, `Tax`, `Shipping`, `Total`, `UnitPrice`, `LineTotal`) | `TEXT`. Check constraints `CAST(… AS REAL) >= 0` | `numeric`, exact and unbounded. Check constraints compare exactly (`"Total" >= 0`). Values round-trip exactly (*verified*: 22.30, 4.20, 56.75). |
| `CreatedAtUtc` | `TEXT`, read back marked UTC | `timestamp with time zone`. Npgsql only accepts UTC `DateTime` values, and the app only writes UTC (*verified*). Precision is **microseconds**, so ticks beyond that are dropped. |
| Identity `LockoutEnd` (`DateTimeOffset`) | `TEXT` | `timestamp with time zone`. Lockout is no longer used (sign-in protection replaced it), so nothing writes it now; the column stays because it belongs to Identity's schema. |
| `UserSessions` times (`CreatedAtUtc`, `ExpiresAtUtc`, …) | `TEXT` | `timestamp with time zone`, written as UTC (*verified* by the PostgreSQL tests) |
| Keys | `INTEGER` autoincrement | `bigint`/`integer` identity columns (`GENERATED BY DEFAULT AS IDENTITY`) |
| Strings | `TEXT` | `character varying(n)` where a maximum length is configured, otherwise `text` |
| Idempotency key | unique partial index `WHERE "IdempotencyKey" IS NOT NULL` | Same partial unique index (*verified* in `pg_indexes`) |
| Other constraints | unique `Reference`, unique (`OrderId`, `ProductId`), FKs with SET NULL / CASCADE | Same |
| Comparison of keys and emails | binary | Binary (default collation). Identity compares normalized upper-case values, as before. |

**Transactions and idempotency.** Checkout writes in an explicit transaction, and on PostgreSQL that's `READ COMMITTED`. It needs no stronger isolation, because the **unique index on the idempotency key** settles races:
- A concurrent insert with the same key waits for the first transaction, then fails with `23505`.
- Checkout treats `23505` as "someone else won the race": it rolls back and returns the winner's order, or 422 if the details differ.
- *Verified* with 8 simultaneous submissions (one order) and a deterministic race test. Without `23505` handling, 3 PostgreSQL tests fail.

**Error mapping** (`DatabaseErrors`). PostgreSQL failures get the same codes as their SQLite equivalents:

| PostgreSQL | Code |
|---|---|
| `40001` serialization, `40P01` deadlock, `55P03` lock not available | 503 `DATABASE_BUSY` |
| No answer from the server (refused, reset, timeout); `08…` connection errors; `57P01`–`57P03` shutting down; `53300` too many connections; `3D000` no such database; `28…` authentication | 503 `DATABASE_UNAVAILABLE` |
| `23…` integrity violations | 409 `CONSTRAINT_CONFLICT` (`23505` during checkout means the race above) |
| Anything else | 500 `DATABASE_ERROR` |

As on SQLite, checkout only lets the 503s through for failures **before COMMIT is sent**, where nothing can have been saved. Any failure **during COMMIT** is reported as an unknown outcome (500 `DATABASE_ERROR`), and a retry with the same key reconciles it. *Verified on PostgreSQL* by injecting real Npgsql exceptions before, at and after COMMIT, and checking each response against the database. That includes a commit that succeeded but reported an error, followed by a retry while FakeStoreAPI was down.

## 3. Controlled migration procedure

Migrations are never applied automatically in production. Run them deliberately, before starting the version that needs them. From `backend/`, with the commit you're deploying checked out:

1. **Generate the script** for review:
   ```bash
   dotnet tool restore
   dotnet ef migrations script --idempotent --project Storefront.Api --context PostgreSqlStorefrontDbContext --output postgresql-migrations.sql
   ```
   `--idempotent` means each migration runs only if it's missing from `__EFMigrationsHistory`, so re-running the script is harmless. *Verified*: the second run is a no-op.
2. **Review** the SQL. It's plain DDL in one transaction per migration. Keep it with the release.
3. **Back up** the database (a provider snapshot, or `pg_dump`) and note the current migrations:
   ```sql
   SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY 1;
   ```
4. **Apply** the script as the role that owns the schema (not the app's role):
   ```bash
   psql "host=… port=5432 dbname=storefront user=storefront_owner sslmode=require" -v ON_ERROR_STOP=1 -f postgresql-migrations.sql
   ```
5. **Grants:** none needed after a migration, once the app role exists. `tools/db/create-app-role.sql` (section 13) sets *default privileges*, so every table the owner creates is granted to `storefront_app` automatically. *Verified* in the rehearsal: a table created afterwards by the owner was usable by the app straight away. Before that script existed, the grants had to be repeated after each migration that added a table.
6. **Verify**: list the migrations against the database (every one should be applied, none pending):
   ```bash
   dotnet ef migrations list --project Storefront.Api --context PostgreSqlStorefrontDbContext --connection "<owner connection string>"
   ```
   After the app starts, `GET /health/ready` should be 200. If a migration was missed, it's 503 and the log says `Readiness: N database migration(s) not applied.`
7. **Rollback**: restore the backup. For future changes, prefer additive (expand, then contract) migrations, so the previous app version keeps working while the new schema is in place.

An alternative to steps 1 and 4 is `dotnet ef database update --project Storefront.Api --context PostgreSqlStorefrontDbContext --connection "<owner connection string>"`. It's *verified* on a disposable database, but nothing gets reviewed before it runs.

**Always pass `--context`.** The project has two contexts, so EF refuses a command without one. Each context has its own design-time factory. Without the SQLite one, EF would pick the PostgreSQL factory even for SQLite commands, because the factory interface is covariant and the PostgreSQL context derives from the SQLite one. A test guards that the two migration sets stay apart.

Adding a model change later needs a migration for **each** provider:
```bash
dotnet ef migrations add <Name> --project Storefront.Api --context StorefrontDbContext --output-dir Data/Migrations
dotnet ef migrations add <Name> --project Storefront.Api --context PostgreSqlStorefrontDbContext --output-dir Data/Migrations/PostgreSql
```
Tests fail until both match the model.

## 4. Container image

`backend/Dockerfile`, built with `backend/` as the context (the API has no project references):

```bash
docker build --pull -t storefront-api backend          # --pull: latest .NET 10 base images
docker build --pull --no-cache -t storefront-api backend   # also re-runs the CA refresh and NuGet restore
```

- **Build stage:** `mcr.microsoft.com/dotnet/sdk:10.0` restores from the project file first, so the package layer is cached, then publishes `Release` to `/app/publish`.
- **Runtime stage:** `mcr.microsoft.com/dotnet/aspnet:10.0`:
  - refreshes `ca-certificates`;
  - copies only the published output;
  - runs as the image's non-root `app` user (UID 1654);
  - sets `ASPNETCORE_ENVIRONMENT=Production` and clears the base image's 8080 default.
- **Port:** the entrypoint runs `exec dotnet Storefront.Api.dll --urls http://0.0.0.0:${PORT:-10000}`. `exec` makes the API PID 1, so `SIGTERM` reaches it and readiness turns 503 while in-flight requests finish. Nothing in the app's configuration sets a port.
- **Logs:** stdout/stderr, JSON outside Development.
- **`backend/.dockerignore`** keeps out:
  - build and test output, and the test project;
  - local databases;
  - `.env` files, `appsettings.Production.json`, `*.local.json`, certificates and keys;
  - `launchSettings.json`, and editor and tool files.

  *Verified*: the context was listed with planted decoy files, and only sources, settings and both migration sets went in.

**Running the image locally** now needs the Data Protection certificate too (section 8): pass `-e DataProtection__Certificate__Pfx=… -e DataProtection__Certificate__Password=…`, or, for a throwaway run only, `-e DataProtection__AllowUnencryptedKeys=true`. Without either, the container exits at startup with `DataProtection:Certificate:Pfx is required outside Development`. This applies to `verify-container.ps1` as well.

## 5. Verification

| Check | Where | Result |
|---|---|---|
| Backend tests, SQLite (unit, database, API) | Linux workspace | **Passed**: 293 passed, 0 failed. The 14 PostgreSQL tests are skipped without a server, and say why. |
| Backend tests, with PostgreSQL 16.15 | Linux workspace, disposable local server | **Passed: 329 of 329** (312 before key encryption; 302 before the key-ring and forwarded-header changes) |
| Key encryption, end to end | Linux workspace: the published API in Production on PostgreSQL, with a certificate made by `tools/new-data-protection-certificate.cs` | Without a certificate: startup refused with the message above. With it: the startup log names the certificate's thumbprint; register, sign-in and `/api/orders` 200; after a restart the same token 200; the key row in `DataProtectionKeys` is `<encryptedSecret>` and no "No XML encryptor" warning is logged. **A key written earlier without encryption stayed unencrypted and in use** until deleted, hence step 5 of section 8. Restarted with a different certificate: the old token 401, `/health/ready` 200. |
| Deliberate breakages | same | Without PostgreSQL `23505` handling, 3 tests fail (including the double-click test). Without lost-connection classification, 2 fail. |
| Migrations | disposable PostgreSQL database | `database update` applies the migration and a second run is a no-op. The idempotent script applies, and re-running it is a no-op. Schema inspected: numeric, timestamptz, partial unique index, check constraints. |
| Frontend typecheck, build, Vitest with coverage | Linux workspace | **Passed**: 260/260, thresholds met (frontend unchanged) |
| Browser suite (Development, SQLite) | Linux workspace | **Passed: 18 of 18** |
| Production-like run of the published API | Linux workspace. Not a container: the image couldn't be built there. | Started with the image's exact command, as a non-root UID 1654 user that can't write the app folder, with `PORT=10000` and Production settings, on PostgreSQL with least-privilege grants. `/health/live` 200; `/health/ready` 200; CORS preflight allowed for the configured origin and refused for others; exposed headers correct; Swagger 404; JSON logs on stdout. |
| Smoke test against that run | same, mock upstream | **68 passed, 0 failed, 1 skipped**: the Swagger check, since Swagger is off in Production. Covers repricing, idempotency, accounts, CORS and upstream failures. |
| Restart persistence | same | After SIGTERM (graceful shutdown logged) and a restart: orders, accounts and the idempotent replay were intact. **After a "new container"** (fresh home folder): first data intact but the old sign-in token **rejected (401)**. **After the key-ring change:** the access token (200) and refresh token (200) were both accepted, with one key in PostgreSQL and none on disk. |
| Upgrade procedure (section 3) | same | On the database migrated earlier, the regenerated idempotent script applied only `AddDataProtectionKeys`. Until step 5's grants were re-run, the app role got "permission denied for table DataProtectionKeys", which is why that step must follow every migration that adds a table. |
| `docker build` and the image itself | owner's Windows machine, Docker Desktop, 23 October 2026 (it couldn't be built in the workspace, where both registries are blocked) | **Passed**: builds and runs as `uid=1654(app)`, entrypoint `exec dotnet Storefront.Api.dll --urls http://0.0.0.0:${PORT:-10000}`, `ASPNETCORE_ENVIRONMENT=Production`. The idempotent migration script applied to a `postgres:16` container and re-applied as a no-op. `/health/live` and `/health/ready` 200. CORS allowed and refused origins correctly. Smoke test against live FakeStoreAPI: 57 passed, 0 failed, 2 skipped (Swagger; failure modes without the mock). Orders: 205 before and after `docker restart`, and 205 in a **new container**. `docker stop` logged a graceful shutdown. The Data Protection warning about keys not surviving the container appeared, as expected (blocker 1). |
| **Live on Render** (`datheon-task-4.onrender.com`, Free instance with Free PostgreSQL, Frankfurt, 24 October 2026) | Render | Migrations applied with the reviewed script (both listed in `__EFMigrationsHistory`). `/health/live` and `/health/ready` 200, database Healthy. Smoke test against live FakeStoreAPI: first **45 passed, 12 failed**, every failure a **429** from the per-client checkout limit (30 a minute), which confirms per-shopper limits in production. With limits raised temporarily: **57 passed, 0 failed, 2 skipped** (Swagger; failure modes without the mock), covering repricing, validation, idempotent replay and key reuse, accounts, signed-in orders, token refresh and CORS. Client-address setting measured as in section 7. |
| Backend tests after key encryption | owner's Windows machine, 24 October 2026, no PostgreSQL server | **Passed**: 309 passed, 0 failed, 15 skipped (the PostgreSQL tests, skipped without a server). The certificate and encrypted-key tests ran, so loading the certificate and encrypting and decrypting keys work on Windows too. |
| **Key encryption live on Render** | Render, 24 October 2026 | Certificate made with `tools/new-data-protection-certificate.cs` and set as two environment variables. The deploy log showed `Sign-in keys are encrypted with certificate A3402286C1D7C36EA6EF4EB8E3370B5000F291CB`, matching the tool's output. The key written before encryption was deleted (`DELETE 1`), the service redeployed, and a test account registered and signed in. `DataProtectionKeys`: **1 key, 1 encrypted**. |
| PostgreSQL tests against Docker's PostgreSQL | owner's machine | **First run: 17 of 18.** One case (connection lost just before COMMIT) failed after 50 s at an order-count query; the error message wasn't captured. **Two further full runs** (via `localhost` and via `127.0.0.1`): **18 of 18 each**. It passed 6 of 6 repeated runs in the workspace, and checks showed no lingering transactions or locks after any injected failure. **Cause not established**: it looks like a slow test connection (that test opens the most fresh, unpooled connections, and was again the slowest, at 12 s via `localhost`), but that's unconfirmed. If it recurs, rerun with the `trx` logger to capture the message. |

How to run the PostgreSQL tests against any disposable server (never a production one):

```powershell
$env:STOREFRONT_TEST_POSTGRES = "Host=localhost;Port=55433;Username=postgres;Password=…"
dotnet test -c Release --filter Category=PostgreSql
```

Each test creates its own `storefront_test_<guid>` database and drops it afterwards.

## 6. Blockers and risks before going live

1. ~~**Sign-in sessions don't survive a new container.**~~ **Resolved, including the follow-up.**
   - **Was:** the Data Protection keys that sign bearer and refresh tokens lived in the container's home folder, which every deploy replaces. So every deploy signed all shoppers out (*verified*: 401 after a fresh key ring).
   - **Now:** the key ring is stored in the app database (`DataProtectionKeys` table, migration `AddDataProtectionKeys` for both providers) under the fixed application name `Storefront.Api`. Every instance and every new container uses the same keys.
     - *Verified on PostgreSQL:* after a restart with an empty home folder, the same access token (200) and refresh token (200) still worked, no key files were written to disk, and the "may not be persisted outside of the container" warning is gone.
     - *Verified by tests:* a new instance accepts earlier tokens, and once the keys are deleted from the database a new instance rejects them. Without the change, both tests fail.
   - ~~**Follow-up: the keys are stored unencrypted.**~~ **Resolved:** each key is now encrypted with a certificate before it's written (section 8), so a copy of the database alone can't forge sign-ins. Outside Development the app refuses to start without the certificate.
2. **`AllowedHosts` must name the public host**, or every request gets 400. No code change is needed: it's the `AllowedHosts` environment variable, set when the Render service is created.
   - **Render's health checks** send the service's `onrender.com` subdomain as the `Host` header, or a verified custom domain once one exists ([Render: Health Checks](https://render.com/docs/health-checks)). So set `AllowedHosts` to that subdomain (e.g. `storefront-api.onrender.com`), and add any custom domain, separated by `;`, **before** verifying it.
   - A wrong value fails safely. The health check gets 400, which Render counts as a failure, so the deploy is cancelled and the previous version keeps serving.
   - **Health check path:** use `/health/ready`. A deploy then only goes live once the database is reachable and migrated, and an outage at FakeStoreAPI alone doesn't fail it (it reports `Degraded`, which is 200). Trade-off: while the database is down, the check fails too, and Render may restart instances that couldn't serve anyway.
3. ~~**Client addresses behind Render's proxy.**~~ **Resolved on Render (24 October 2026): `ForwardedHeaders__Enabled=true` with `ForwardedHeaders__ClientAddressHeader=CF-Connecting-IP`.** `X-Forwarded-For` turned out to be forgeable there. Results are in section 7.
   - Without it, every shopper shares the proxy's address and so one rate limit (10 sign-in requests and 30 checkouts a minute in total), and the API can't tell that a request arrived over HTTPS (no HSTS).
   - The `ForwardedHeaders` setting is **off by default** (*verified*: claimed addresses are then ignored).
   - When on, the client address comes from a chosen header, counting a set number of trusted entries from the right, and the scheme from `X-Forwarded-Proto`.
     - *Verified by tests:* each client gets its own limit; a forged prefix doesn't change who the client is; two hops and an alternative header (e.g. `True-Client-IP`) work; invalid values stop startup.
     - *Verified over real TCP* in Production mode, including HSTS being sent only when forwarding is on and the request came over HTTPS.
   - **Not yet known:** which header and how many hops are right for Render. Its staff said (2021) the first `X-Forwarded-For` entry is the client, but a user's test (2024) found Render appends to a client-supplied header rather than replacing it, so the first entry can be forged ([Render feedback](https://feedback.render.com/features/p/send-the-correct-xforwardedfor)). Don't enable it until section 7's check passes.
4. ~~**The image hasn't been built or run yet.**~~ Done on the owner's machine (section 5).
5. ~~**Connection limits.**~~ **Resolved in code (SA-04):** without `Maximum Pool Size` in the connection string, the pool is capped at 20 (`Database:MaxPoolSize`), leaving room for a second instance during a deploy and for an administrator within a small plan's ~90 usable connections. Simultaneous `/health/ready` probes share one database check. *Verified:* 1,200 readiness calls, 300 at a time, added no connections; a burst of signed-in requests stayed within a cap of 4.
6. **The SQLite → PostgreSQL data move isn't covered.** Production starts with an empty PostgreSQL database. Existing local SQLite orders and accounts aren't migrated, and no tool is provided for it.

## 7. Client addresses behind the proxy: finding the right setting on Render

The rate limit shows which address the API thinks a request came from: two requests from the "same client" share it, and different clients don't. That makes it possible to check the forwarded-header setting on a real deploy with no diagnostic code. Do this **before launch**, because it temporarily lowers a limit.

1. Set `RateLimiting__AccountPermitLimit=1` and `ForwardedHeaders__Enabled=true` (header `X-Forwarded-For`, 1 hop), then deploy.
2. **Can it be forged?** From one computer, send two sign-in requests that claim different addresses:
   ```powershell
   curl.exe -s -o NUL -w "%{http_code}`n" -X POST https://<service>.onrender.com/api/account/login -H "Content-Type: application/json" -H "X-Forwarded-For: 1.1.1.1" -d "{}"
   curl.exe -s -o NUL -w "%{http_code}`n" -X POST https://<service>.onrender.com/api/account/login -H "Content-Type: application/json" -H "X-Forwarded-For: 2.2.2.2" -d "{}"
   ```
   The second must be **429**: the API ignored the forged addresses and saw one client. If it's 400, the forged value was trusted, so **switch forwarding off** and try `True-Client-IP` (or more hops) instead.
3. **Does it tell clients apart?** Within a minute, send one request from a different network (for example a phone on mobile data). It must **not** be 429. If it is, everyone still looks like one client (the proxy's own address), so try `TrustedProxyHops=2`, then the other header.
4. When both checks pass, restore `RateLimiting__AccountPermitLimit` (default 10), keep the forwarding settings, and record the result here.

If no combination passes both checks, leave forwarding **off**. Limits are then shared but can't be spoofed. Ask Render support which header it guarantees.

### Results on Render (24 October 2026, `datheon-task-4.onrender.com`, Free instance, Frankfurt)

Measured with `RateLimiting__AccountPermitLimit=1` and the sign-in endpoint, as above:

| Test | Setting | Result | Meaning |
|---|---|---|---|
| Two requests claiming `X-Forwarded-For: 1.1.1.1`, then `2.2.2.2` | `X-Forwarded-For`, 1 hop | `400`, `400` | **Forgeable**: the client-supplied value was taken as the client's address |
| Two plain requests; then two that both claim `1.1.1.1` | same | `400`, `429`; `400`, `429` | The limit works, which confirms the row above: the forged value is what gets used. Render keeps a client-supplied entry where this setting reads it (in line with Render staff saying the *first* entry is the real client). |
| Two requests claiming `CF-Connecting-IP: 1.1.1.1`, then `2.2.2.2` | `CF-Connecting-IP` | `403`, `403` | Rejected at Render's edge, before the app, so clients **can't supply** this header |
| One request from home, then one from a phone hotspot within a minute | same | `400`, `400` | Different networks are **different clients** |
| Two plain requests from home | same | `400`, `429` | Repeated requests from one client are **the same client**: the header is present and consistent |

**Decision:** `ForwardedHeaders__Enabled=true`, `ForwardedHeaders__ClientAddressHeader=CF-Connecting-IP`, `TrustedProxyHops=1`, and `RateLimiting__AccountPermitLimit` restored to the default (10).

**Caveat:** this relies on Render's edge setting `CF-Connecting-IP` and rejecting client-supplied copies, which is how it behaved on this date. That isn't documented by Render. If Render stops setting the header, the API falls back to the proxy's address, so limits become shared but can't be spoofed. Re-run the last three tests after any change to Render networking or a move to a custom domain or another host.

## 8. Encrypting the sign-in keys

The Data Protection keys sign every bearer and refresh token. Stored readable, anyone who can read `DataProtectionKeys` (a leaked database URL, a backup) could forge a sign-in for any account. With a certificate configured, each key is encrypted with the certificate's public key before it's written (`ProtectKeysWithCertificate`), and only an app holding the private key can use it.

- **Required outside Development.** Without `DataProtection:Certificate:Pfx` the app stops at startup and says how to fix it, rather than quietly writing readable keys. A value that isn't base64, a wrong password, a certificate without its private key, or an RSA key under 2048 bits also stop startup. The messages never include the certificate or the password. *Verified by tests.*
- **Logged at startup:** `Sign-in keys are encrypted with certificate <thumbprint> (expires <date>)`. From 30 days before the expiry date there's also a warning to rotate. Expiry doesn't stop the certificate working; it's only the reminder.
- **Development and the test suites** run without a certificate, as before.
- **If the certificate is lost or replaced without rotating:** keys encrypted with it can't be read, so everyone is signed out and has to sign in again. Orders and accounts are unaffected and the API keeps serving (*verified*).

### Creating the certificate

From the repository root, on your own machine (it needs the .NET 10 SDK, nothing else):

```powershell
dotnet run tools/new-data-protection-certificate.cs
```

It asks for a password twice (at least 12 characters; nothing is shown as you type) and writes two files to `%USERPROFILE%\storefront-secrets` (or `-- <folder>`):

| File | Use |
|---|---|
| `storefront-data-protection.pfx` | The certificate and private key, password-protected (AES-256). Your backup. |
| `storefront-data-protection.pfx.base64.txt` | The same file as base64: the value of `DataProtection__Certificate__Pfx` |

It's a self-signed, 3072-bit RSA certificate valid for 3 years, used only for this; no certificate authority is involved. The tool refuses a folder inside a git repository, never overwrites an existing certificate, adds nothing to the Windows certificate store, and prints nothing secret. Keep the `.pfx` and its password in a password manager. Never commit them, or paste them into chat or a ticket.

### Turning it on (Render)

The order matters, because the new version won't start without the certificate.

1. **Create the certificate** as above.
2. **Copy the value** to the clipboard without displaying it:
   ```powershell
   Get-Content "$env:USERPROFILE\storefront-secrets\storefront-data-protection.pfx.base64.txt" -Raw | Set-Clipboard
   ```
3. **Render → the service → Environment:** add `DataProtection__Certificate__Pfx` (paste), and `DataProtection__Certificate__Password`. Save. The version already running ignores both.
4. **Deploy** the version with this change (push the commit). Check the deploy log for `Sign-in keys are encrypted with certificate <thumbprint>`, matching the thumbprint the tool printed. If a variable is missing or wrong, the new version stops at startup and Render keeps the previous one serving.
5. **Replace the key written before encryption.** Existing keys aren't re-encrypted: a key created unencrypted stays readable, and in use, until it expires in up to 90 days (*verified*). Delete it, then restart, so the app creates a new, encrypted one. This signs everyone out once:
   ```powershell
   $db = Read-Host "Paste the External Database URL"
   $db = "$db" + "?sslmode=require"
   'DELETE FROM "DataProtectionKeys";' | docker run --rm -i postgres:16 psql $db -v ON_ERROR_STOP=1
   ```
   Then **Manual Deploy → Deploy latest commit** (or restart the service), so the running instance drops the key it holds in memory. Then sign in once, so that a new key is created. Use `Invoke-RestMethod`: Windows PowerShell strips the double quotes from a JSON argument passed to `curl.exe`, so the API gets invalid JSON and answers 400 (*seen on Render*):
   ```powershell
   $body = @{ email = "keycheck@example.com"; password = "<a test password>" } | ConvertTo-Json
   Invoke-RestMethod -Method Post -Uri https://<service>.onrender.com/api/account/register -ContentType "application/json" -Body $body
   $login = Invoke-RestMethod -Method Post -Uri https://<service>.onrender.com/api/account/login -ContentType "application/json" -Body $body
   "Signed in: token received ($($login.accessToken.Length) characters)"
   ```
6. **Verify** that every key is encrypted. The two numbers must be equal and at least 1:
   ```powershell
   'SELECT count(*) AS keys, count(*) FILTER (WHERE "Xml" LIKE ''%<encryptedSecret%'') AS encrypted FROM "DataProtectionKeys";' | docker run --rm -i postgres:16 psql $db
   ```
   Close the PowerShell window afterwards.

### Rotating the certificate

Before it expires, or straight away if the `.pfx` or its password may have leaked:

1. Create a new certificate into **another folder** (`dotnet run tools/new-data-protection-certificate.cs -- <folder>`).
2. On Render, move the current values to `DataProtection__PreviousCertificates__0__Pfx` and `DataProtection__PreviousCertificates__0__Password`, and put the new ones in `DataProtection__Certificate__Pfx` and `__Password`. Deploy. Existing sign-ins keep working (*verified by a test*), and new keys use the new certificate.
3. Keep the previous certificate listed for **105 days**: the current key stays in use for up to 90 days, and refresh tokens it signed last 14 days. Then remove the two `PreviousCertificates` variables.

**After a leak, don't wait:** skip step 3. Set only the new certificate, delete the rows in `DataProtectionKeys`, and restart, as in step 5 above. Everyone signs in again, but nothing signed with the leaked key is accepted any more.

## 9. Free plan: moving to a new Free database, keeping the data

A Free Render database expires **30 days after it's created**. It's then unreachable, and Render deletes it 14 days later. Free databases have no backups. The owner's choice (26 October 2026) is to stay on the Free plan, so the database is moved to a new Free one before each expiry.

**Three rules shape the move:**
- **Only one Free database can exist per workspace** (Render: "Only one Free Render Postgres database can be active for any given workspace"). The new one can't be created until the old one is deleted, so the database is down while the move happens: plan **20–30 minutes**.
- **An expired database can't be read**, so the move must happen **before** the expiry. Moving early just restarts the 30-day clock early, so do it in the last week. For the current database (created 24 October 2026, expires about 23 November): **between 16 and 20 November 2026.**
- **Deleting is irreversible.** The backup is the only copy of the data, so it's taken *right before* the delete, and checked by restoring it first. Anything written between the backup and the delete is lost, so pick a quiet time.

**What carries over:** accounts, orders, sessions and the sign-in keys. The keys are encrypted with the same certificate, so shoppers stay signed in. **What doesn't:** the `storefront_app` role and its password (roles belong to the server, not the dump), and the IP rule. Both are recreated below.

**Rehearsed** on 26 October 2026, on PostgreSQL 16. A dump with `--no-owner --no-privileges` was restored into a fresh database, owned by a non-superuser with Render's privileges and with no `storefront_app` role:
- `pg_restore --exit-on-error` succeeded, and every table matched the source exactly: 3 users, 362 orders, 530 order lines, 3 sessions, 1 key, 3 migrations. The tables are owned by the new owner.
- `create-app-role.sql` ran cleanly: 11 tables with data access, 0 with more, and the history read-only.
- The API, as `storefront_app`, was ready and passed the smoke test 68 of 68, with 0 permission errors.

**On the day.** Use one PowerShell window in the `storefront` folder.

1. **Set the old database's URL** and record its live row counts:
   ```powershell
   $db = Read-Host "Paste the OLD External Database URL"; $db = "$db" + "?sslmode=require"
   $counts = 'SELECT (SELECT count(*) FROM "AspNetUsers") AS users, (SELECT count(*) FROM "Orders") AS orders, (SELECT count(*) FROM "OrderLines") AS lines, (SELECT count(*) FROM "__EFMigrationsHistory") AS migrations;'
   $counts | docker run --rm -i postgres:18 psql $db
   ```
2. **Back up, and check the backup restores as a non-superuser**, the way Render will restore it:
   ```powershell
   $dir = "$HOME\Documents\storefront-backups"; New-Item -ItemType Directory -Force $dir | Out-Null
   $file = "storefront-move-$(Get-Date -Format yyyyMMdd-HHmm).dump"
   docker run --rm -v "${dir}:/backup" postgres:18 pg_dump $db --format=custom --no-owner --no-privileges --file "/backup/$file"
   docker run -d --name sf-restore-test -e POSTGRES_PASSWORD=restore-test -v "${dir}:/backup" postgres:18 | Out-Null
   Start-Sleep 10
   docker exec sf-restore-test psql -U postgres -qc "CREATE ROLE owner_check LOGIN CREATEROLE CREATEDB"
   docker exec sf-restore-test createdb -U postgres -O owner_check check
   docker exec sf-restore-test pg_restore -U owner_check -d check --no-owner --no-privileges --exit-on-error "/backup/$file"
   $counts | docker exec -i sf-restore-test psql -U owner_check -d check
   docker rm -f sf-restore-test | Out-Null
   ```
   **Continue only if** `pg_restore` printed no errors **and** the counts equal step 1's. If anything differs, stop: the old database is untouched.
3. **Delete the old database.** On Render: the database → **Settings** → **Delete Database**. From here until step 8, the API's readiness is 503, and sign-in and checkout fail.
4. **Create the new one:** **New → Postgres**, **Free**, the **same region (Frankfurt)** as the API, **PostgreSQL 18**. Wait until it shows **Available**.
5. **Allow only your IP:** the new database → **Info** → **Networking**. Replace `0.0.0.0/0` with your IP and `/32` (`curl.exe -s https://api.ipify.org`).
6. **Restore** into it, then compare the counts with step 1:
   ```powershell
   $new = Read-Host "Paste the NEW External Database URL"; $new = "$new" + "?sslmode=require"
   docker run --rm -v "${dir}:/backup" postgres:18 pg_restore --no-owner --no-privileges --exit-on-error -d $new "/backup/$file"
   $counts | docker run --rm -i postgres:18 psql $new
   ```
7. **Recreate the API's role** on the new server (section 13, steps 2 and 3, with `$new` in place of `$db`):
   ```powershell
   Get-Content tools\db\create-app-role.sql -Raw | docker run --rm -i postgres:16 psql $new
   'SHOW log_statement; SHOW password_encryption;' | docker run --rm -i postgres:16 psql $new
   $b = New-Object byte[] 24; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
   $pw = -join ($b | ForEach-Object { $_.ToString('x2') })
   "ALTER ROLE storefront_app PASSWORD '$pw';" | docker run --rm -i postgres:16 psql -q $new
   ```
   Run the password line only if the check shows `none` and `scram-sha-256`.
8. **Point the API at it.** API service → **Environment** → `ConnectionStrings__Storefront`:
   - **Host:** take it from the new database's **Internal Database URL** (the `dpg-…-a` part).
   - **Database name:** the new one.
   - **Username:** `storefront_app`.
   - **Password:** `$pw` (`Set-Clipboard $pw`, then Ctrl+V in the browser).
   - Keep `Port=5432`, `SSL Mode=Require` and `GSS Encryption Mode=Disable`.

   Save; this redeploys.
9. **Verify:**
   - `/health/ready` returns 200;
   - you're still signed in on the site, or can sign in with your existing account;
   - your earlier orders are under **Your account → Orders**;
   - a new order goes through;
   - the logs contain no `permission denied`.
10. **Record and clean up:**
    - Note the new database's creation date here and in section 14. Its expiry is 30 days later; the next move falls in the week before.
    - Set the next reminder.
    - Run `Set-Clipboard -Value ' '; Remove-Variable pw, b`.
    - Keep the move dump until the new database has run for a week. It's sensitive, so treat it as section 14 describes.

**If step 6 or 7 fails:** the dump is intact, and the old database is gone in any case. Delete the new database, create another, and repeat steps 4–7. The data is only in the dump, so don't delete it.

## 10. Frontend: Render Static Site

The frontend (React 19, TypeScript, Vite) is plain static files: `npm run build` writes them to `frontend/dist`. Render serves them from its CDN, and the browser calls the API directly. No server code is involved, and a static site doesn't sleep. The API still does, on the Free plan.

### What was checked before deploying

- **One way to the API.** Every request goes through `src/services/apiClient.ts`, which prefixes `VITE_API_BASE_URL`. Nothing else calls `fetch`, and no production address is hard-coded. `http://localhost:5080` is only the development default (`src/config/env.ts`).
- **Read at build time.** Vite compiles `VITE_*` values into the JavaScript, so everyone can read them. The only one that matters, `VITE_API_BASE_URL`, isn't a secret. The production bundle contains the API address and no other configuration, no source maps, and no `localhost` (*verified*).
- **The build checks the address.** `vite build` stops with a message if `VITE_API_BASE_URL` is empty, not an absolute address, uses `http://` for anything other than `localhost`, or includes credentials, a query string or a fragment. On Render (which sets `RENDER` during builds) it must also be set. A failed build leaves the previous deploy serving. Without the check, a missing address would make the site call itself: the rewrite below would answer `/api/…` with `index.html`, and every page would show "unexpected response". A local build can still leave it unset, in which case the bundle calls the API on its own origin. *Verified* for each case (`src/config/apiBaseUrl.ts`, 19 tests).
- **No client-side routes.** The app is one page at `/`. The open product, category, search and sort live in the query string, which a reload keeps (*verified* by the browser tests). The rewrite below is still needed, so that any other path shows the shop instead of Render's 404.
- **Sessions and cart.** Tokens are kept in `sessionStorage` (per tab) and the cart in `localStorage`, both on the site's own origin. Requests carry `Authorization: Bearer …`, not cookies, so the API's CORS policy doesn't allow credentials and no wildcard origin is involved.
- **Failures.** A timed-out or dropped checkout is treated as an uncertain outcome. The idempotency key is kept and "Try again" reuses it, so the API replays the original order instead of creating a second one. Only definite failures (validation, price change, `DATABASE_BUSY`/`DATABASE_UNAVAILABLE`, catalogue outage) say the order wasn't placed. Browsing falls back to the API's last good catalogue copy, and an offline browser holds checkout.
- **Cold starts.** The first request after the Free API has slept can take about a minute, longer than the frontend's 15-second request limit. The catalogue then shows "The store is taking too long to respond" with a retry, which works once the API is awake. A checkout sent during that window would be treated as uncertain, as above, so it can't be duplicated. Before a demo, open `https://datheon-task-4.onrender.com/health/live` a minute ahead.

### Render settings

**New → Static Site**, connected to the same GitHub repository:

| Setting | Value |
|---|---|
| Branch | `main` |
| Root Directory | `frontend` |
| Build Command | `npm ci && npm run build` (the lockfile, and `tsc -b` before `vite build`) |
| Publish Directory | `dist` |
| Environment variable | `VITE_API_BASE_URL` = `https://datheon-task-4.onrender.com` (no trailing slash; not a secret) |
| Node version | Render's default for new services (24.22.1 since 21 October 2026) meets every requirement in the lockfile. To pin one, set `NODE_VERSION`. |

`npm ci` installs the dev dependencies (TypeScript, Vite), because Render sets `NODE_ENV=production` only at runtime, not during builds.

**Redirects/Rewrites** (the site's settings, after it's created):

| Source | Destination | Action |
|---|---|---|
| `/*` | `/index.html` | **Rewrite** |

Render applies rules only to paths where no file exists, so scripts, styles and `favicon.svg` are unaffected. A Rewrite keeps the address in the browser, where a Redirect would change it. Source: [Render: Redirects and Rewrites](https://render.com/docs/redirects-rewrites).

**Response headers** (the site's **Headers** settings, path `/*`). These are required (Security-Audit.md, SA-12). The values come from `frontend/src/config/securityHeaders.ts`, which `vite preview` also serves, so the browser tests run the production bundle under exactly this policy and fail on any violation:

| Header | Value |
|---|---|
| `Content-Security-Policy` | `default-src 'self'; script-src 'self'; style-src 'self' https://fonts.googleapis.com; font-src https://fonts.gstatic.com; img-src 'self' data: https://fakestoreapi.com; connect-src https://datheon-task-4.onrender.com; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'` |
| `X-Content-Type-Options` | `nosniff` |
| `X-Frame-Options` | `DENY` |
| `Referrer-Policy` | `strict-origin-when-cross-origin` |
| `Cross-Origin-Opener-Policy` | `same-origin` |
| `Strict-Transport-Security` | `max-age=31536000` |

If the API address or the image host ever changes, update `connect-src` or `img-src` to match. A unit test pins the exact policy above, so the code and this table can't drift apart unnoticed.

`VITE_API_BASE_URL` is compiled in, so changing it needs a new build. Saving the variable on Render starts one.

### Backend CORS

On the **API** service, set:

```
Cors__AllowedOrigins__0 = https://<site>.onrender.com
```

Live value: `https://datheon-task-4-1.onrender.com` (Static Site `Datheon-Task-4-1`).

That's the site's exact origin as Render shows it: `https://`, the host, no path and no trailing slash. It replaces the `http://localhost:5173` default (array index 0), so no local origin stays allowed in production. Add a custom domain later as `Cors__AllowedOrigins__1`. The policy allows `GET` and `POST` with the API's own headers, and no credentials. Saving redeploys the API.

### Deploying, in a safe order

1. **Commit and push** the frontend changes. The push also redeploys the API with no change, from cache.
2. **Create the Static Site** with the settings above, and wait for "Your site is live". Without `VITE_API_BASE_URL`, the build stops with `VITE_API_BASE_URL is not set`. Fix it and redeploy.
3. **Add the rewrite** (`/*` → `/index.html`, Rewrite).
4. **Note the site's origin** shown at the top of its page, then set `Cors__AllowedOrigins__0` on the API to exactly that. Wait for the API's redeploy.
5. **Check CORS** from PowerShell: the first must print the site's origin, the second nothing:
   ```powershell
   $site = "https://<site>.onrender.com"
   foreach ($o in $site, "https://evil.example") {
     $r = Invoke-WebRequest -Method Options -Uri https://datheon-task-4.onrender.com/api/checkout -Headers @{ Origin = $o; "Access-Control-Request-Method" = "POST"; "Access-Control-Request-Headers" = "content-type,idempotency-key" } -UseBasicParsing
     "$o -> '$($r.Headers['Access-Control-Allow-Origin'])'"
   }
   ```
6. **Go through the checklist below** in a normal browser window with DevTools open.

To undo: suspend or delete the Static Site. The API isn't affected, apart from the CORS value, which can stay.

### Checklist on the live site

| Check | How |
|---|---|
| Loads over HTTPS | The address bar shows `https://<site>.onrender.com` with the lock icon; the catalogue appears. |
| Reloads and other paths | Open a product, then reload: it reopens. Visit `/anything`: the shop loads (the rewrite), not a 404. |
| Calls the deployed API | DevTools → Network: requests go to `datheon-task-4.onrender.com`, none to `localhost`. |
| CORS | Step 5 above, and no CORS errors in the Console. |
| Accounts | Register, sign in, reload (still signed in), open My orders, sign out. Closing the tab ends the session, by design. Signing out also ends it on the server (`/api/account/logout`). |
| Browsing and cart | Search, filter by category, sort; add items, change quantities; reload: the cart is kept. |
| One order per checkout | Place an order: one confirmation, and My orders lists it once. |
| Uncertain checkout | DevTools → Network → throttling "Slow 4G". Place an order and switch to **Offline** while it's still pending, then back online and press **Try again**. The confirmation shows the same reference, and My orders lists one order. Covered automatically by "an interrupted checkout is recovered with Try again, without a duplicate order". |
| API outage | DevTools → Network → block requests to `datheon-task-4.onrender.com`, then reload: the catalogue shows an error with a retry, and the cart is kept. Unblock and press retry: it recovers. |
| Console and secrets | No errors in the Console. Sources: the bundle contains the API address and nothing secret. |

### Verification (24 October 2026, Linux workspace, Node 22.22.0)

| Check | Result |
|---|---|
| `npm run typecheck` | **Passed** |
| Linting | No lint tool is configured in this repository. |
| `npm run test:coverage` (unit and component) | **279 of 279 passed** (260 before, plus 19 for the address check). Coverage 94.39% statements, 90.3% branches, 94.27% functions, 96.37% lines; thresholds met. `apiBaseUrl.ts` 100%. |
| `npm run build` as on Render (`RENDER=true`, the live API address) | **Passed**. `dist/index.html`, one JS file (291 KB, 88 KB gzip), one CSS file, `favicon.svg`. Contains `https://datheon-task-4.onrender.com`; no `localhost`, no other `VITE_` values, no source maps. |
| Build with a missing (on Render), empty or `http://` public address | **Stopped** with the messages above |
| Browser suite against the Vite dev server | **18 of 18 passed** |
| Browser suite against the **production build** (`E2E_PRODUCTION_BUILD=1`: `vite build`, then `vite preview`, which also serves `index.html` for unknown paths) | **18 of 18 passed**. This covers journeys, checkout recovery, outages, offline, sessions, accessibility (axe) and layouts, and fails on unexpected console errors. |
| Backend | Unchanged by this work |
| **Live site** (`https://datheon-task-4-1.onrender.com`, Render Static Site, 24 October 2026) | Built from commit `999d073`, before the address check and the production-build test mode were committed. Node 24.22.1 (Render default), `npm ci` 0 vulnerabilities, `tsc -b` passed. Output `index-DQvjdsAS.js`, the same content hash as the workspace build with `VITE_API_BASE_URL=https://datheon-task-4.onrender.com`, so the deployed bundle is the one tested above. Rewrite `/*` → `/index.html` added. API `Cors__AllowedOrigins__0=https://datheon-task-4-1.onrender.com`: preflight from the site answered `Access-Control-Allow-Origin: https://datheon-task-4-1.onrender.com`, and from `https://evil.example` no header. No Console errors or failed requests. **The owner reported every item of the checklist above working.** |

## 11. Deploying the security remediation

The changes from `Documentation/Security-Audit.md`, section 14, need one database migration and a few dashboard steps. In this order, so the live site keeps working throughout:

1. **Apply the migration before deploying the code.** `AddUserSessions` only adds a table, and the version running now ignores it. From `backend/`, regenerate the reviewed idempotent script (section 3) and apply it with the External Database URL:
   ```powershell
   dotnet ef migrations script --idempotent --project Storefront.Api --context PostgreSqlStorefrontDbContext --output postgresql-migrations.sql
   $db = Read-Host "Paste the External Database URL"
   $db = "$db" + "?sslmode=require"
   Get-Content postgresql-migrations.sql -Raw | docker run --rm -i postgres:16 psql $db -v ON_ERROR_STOP=1
   'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY 1;' | docker run --rm -i postgres:16 psql $db
   ```
   **Before applying, check it's the right database.** Run only the last command first. It should list exactly the two PostgreSQL migrations, `20261023135229_InitialCreate` and `20261023192043_AddDataProtectionKeys`. If you see `20261022204121_…` or `20261023192040_…` instead (the SQLite IDs), or nothing at all, stop: the URL points at the wrong database. An empty `$db` (for example in a new PowerShell window) makes `psql` fail with `connection to server on socket "/var/run/postgresql/.s.PGSQL.5432" failed`, without touching anything.

   After applying, three migrations should be listed, the last exactly `20261024190035_AddUserSessions`. The SQLite migration is `20261024190032_AddUserSessions`; if that one appears, the wrong script was applied, and readiness reports the PostgreSQL migration as pending. If the deploy happens before the migration, readiness reports it pending (503), and Render keeps the previous version serving.

   Reading the output: `NOTICE: relation "__EFMigrationsHistory" already exists, skipping` and `CREATE TABLE` are normal. One `START TRANSACTION … COMMIT` block per migration; the `DO` lines of already-applied migrations do nothing.
2. **Check that no account has 2FA switched on.** Sign-in no longer looks at Identity's 2FA flag, and the endpoint that set it is gone. Expect `0`:
   ```powershell
   'SELECT count(*) FROM "AspNetUsers" WHERE "TwoFactorEnabled";' | docker run --rm -i postgres:16 psql $db
   ```
3. **Commit and push.** Both services redeploy. In the API's deploy log, look for `connection pool up to 20` and `on .NET 10.0.x`, the runtime patch level (SA-11).
4. **Everyone signs in again, once.** Tokens issued before sessions existed carry no session, so the API refuses them (401), and the site shows "session expired".
5. **Static site headers:** add the six headers in section 10 to the Static Site and save. That redeploys the site; the same files are served with the new headers.
6. **Check:**
   - `curl.exe -sI https://datheon-task-4-1.onrender.com/` shows the headers;
   - `curl.exe -sI https://datheon-task-4.onrender.com/api/orders` shows `Cache-Control: no-store` (the status is 405, because `-I` sends `HEAD` and the route only accepts `GET`); `curl.exe -s -o NUL -w "%{http_code}" https://datheon-task-4.onrender.com/api/orders` prints `401`;
   - the static site's `strict-transport-security` is Render's own (`max-age=315360000; includeSubdomains; preload`), which replaces the one configured: that's expected and stronger;
   - sign in, sign out, and confirm that a reload stays signed out;
   - the browser console stays free of `Refused to …` CSP messages.
7. **Periodically, "Clear build cache & deploy"** on the API, so the image picks up .NET and OS patches (Render had reused the cached runtime layers). The startup log then shows the new patch level.

Close the PowerShell window afterwards.


## 12. Continuous integration and deploy gating

GitHub Actions builds and tests every push (`.github/workflows/ci.yml`; what each job checks is in `Testing.md`, section 2a). On its own, that only reports. These settings make it gate production. They're *not run here*: they're dashboard settings for the owner.

**1. Push the workflows and check the first run.** After pushing, open the repository's **Actions** tab. **CI** should show five jobs (the browser tests run twice), all green, in about 10 minutes. Run **Security scan** once by hand (**Run workflow**). Its first result shows whether the current image or packages have known advisories.

**2. Render: deploy only commits that passed.** On **both** services (`datheon-task-4` and `Datheon-Task-4-1`), go to **Settings → Build & Deploy → Auto-Deploy** and choose **After CI Checks Pass**. Render then waits for every check on the commit and deploys only if all pass. It doesn't deploy if a check fails, or if a commit has no checks at all. The previous version keeps serving. *Manual Deploy* still works, for an emergency.

**3. GitHub: protect `main`.** Under **Settings → Rules → Rulesets → New branch ruleset**, set the target to the default branch, and enable:
- **Restrict deletions** and **Block force pushes.** This keeps the history that CI and the secret scan rely on.
- *Optional, stricter:* **Require a pull request before merging**, plus **Require status checks to pass**, adding the CI job names (`Backend build and tests`, `Frontend typecheck, tests and build`, `Browser tests (dev)`, `Browser tests (production)`, `Container image builds`, `Secret scan`). Direct pushes to `main` are then refused, and every change goes through a pull request. For one developer, step 2 alone already keeps an untested commit out of production. This option adds review and stops a broken commit from landing on `main` at all.

**4. Notifications.** GitHub emails you when a workflow you triggered fails, including the weekly security scan. Check under **GitHub → Settings → Notifications → Actions**.

**When CI fails on `main`:** nothing is deployed. Fix the problem and push again. The test report or Playwright report is attached to the failed run under **Artifacts**. For a failed **Security scan**:
- update the named package;
- or, for a base-image finding, run "Clear build cache & deploy" on Render. CI's image build always pulls fresh base images, so a rebuild picks up their patches.

## 13. A least-privilege database role for the API (SA-13)

Until now, the API connected as the database's default user, which owns every table and can create roles and databases. That means a bug or an injection could alter or drop the schema. From this section on, the API connects as **`storefront_app`**, which can only read and write rows. Migrations keep running as the default user, the owner (sections 3 and 11).

`tools/db/create-app-role.sql` creates the role. Run it as the owner. It's safe to run again.

What the role gets:
- `SELECT`, `INSERT`, `UPDATE` and `DELETE` on every table, including tables future migrations create (default privileges);
- read-only access to `__EFMigrationsHistory`;
- a limit of 25 connections (the app's pool is 20);
- `statement_timeout` of 30 seconds and `idle_in_transaction_session_timeout` of 60 seconds.

It can't create, alter, drop or truncate tables, create functions, roles, schemas or databases, or switch to the owner's role. The script sets no password; you set one interactively, so the password never appears in a file, a log or shell history.

**Rehearsed** (25 October 2026) on PostgreSQL 16. The database was owned by a non-superuser role with only `CREATEROLE`, like Render's default user, and the three migrations had been applied as that owner.
- **The script:** runs cleanly twice. It covers 11 tables with data access and none with more. The rehearsal caught a mistake in the first draft: a non-superuser can't run `ALTER ROLE … NOSUPERUSER`. The script now checks the role's attributes instead of setting them.
- **What the role is refused:** each of the following was refused as `storefront_app`:
  - `CREATE TABLE`, `DROP TABLE`, `ALTER TABLE` and `TRUNCATE`;
  - `CREATE SCHEMA`, `CREATE FUNCTION`, `CREATE ROLE` and `CREATE DATABASE`;
  - deleting from the migration history;
  - `SET ROLE` to the owner;
  - `COPY … TO PROGRAM`.
- **Limits and future tables:** a 31-second query was cancelled; a transaction left open was ended after 60 seconds; the 26th and 27th simultaneous connections were refused; a table the owner created afterwards was writable at once.
- **The API, connected as `storefront_app`:** Production mode, readiness 200. The smoke test passed 68 of 68 (Swagger is Development-only), including the outage modes. The checkout stress tests A–E gave the same results as in the audit. There were 0 permission errors.
- Production runs **PostgreSQL 18.6**. The rules involved (roles created by a non-superuser, default privileges, per-role settings) are the same there, and step 2 below prints the result, so any difference shows at once.

**Steps.** Use one PowerShell window, from the `storefront` folder, with `$db` set to the External Database URL as in section 11.

1. **Check first** (read-only). This was done on 25 October 2026: `storefront_9f4i_user` has `CREATEROLE` and owns all 12 tables.
2. **Create the role:**
   ```powershell
   Get-Content tools\db\create-app-role.sql -Raw | docker run --rm -i postgres:16 psql $db
   ```
   The last four results should be:
   - `storefront_app | f | f | f | f | 25`;
   - the two timeouts;
   - `11 | 0 | 11` (tables with data access, tables with more than that, tables);
   - `t | f` (migration history readable, not writable).

   If it prints `already exists with extra attributes`, stop. Nothing was changed.
3. **Set its password** without it touching a file or the screen. First check that the server doesn't log statements and stores passwords hashed. Expect `none` and `scram-sha-256`. If `log_statement` shows anything else, stop:
   ```powershell
   'SHOW log_statement; SHOW password_encryption;' | docker run --rm -i postgres:16 psql $db
   $b = New-Object byte[] 24; [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b)
   $pw = -join ($b | ForEach-Object { $_.ToString('x2') })
   "ALTER ROLE storefront_app PASSWORD '$pw';" | docker run --rm -i postgres:16 psql -q $db
   ```
   The statement goes to docker on standard input, over TLS. It doesn't appear on screen, in PowerShell's history (which records only `$pw`) or in the process list. With `log_statement = none`, a statement that succeeds isn't logged.

   *Why not `\password`:* it would send only a hash, which is better in principle. But in some Windows consoles, Ctrl+V inside `docker run -it` sends a literal Ctrl+V character instead of pasting. Both hidden prompts then "match" and the wrong password is set. That happened on 25 October 2026: the sign-in test then failed with `password authentication failed`.
4. **Test signing in as the role** before touching Render:
   ```powershell
   $app = $db -replace '^postgres(ql)?://[^@]+@', "postgresql://storefront_app:$pw@"
   'SELECT current_user, count(*) AS orders FROM "Orders";' | docker run --rm -i postgres:16 psql $app
   'CREATE TABLE must_fail (id int);' | docker run --rm -i postgres:16 psql $app
   ```
   The first should print `storefront_app` and a count. The second should fail with `permission denied for schema public`.
5. **Switch the API.** On Render, open the API service → **Environment** → `ConnectionStrings__Storefront` → edit.
   - Change only the username to `storefront_app` (the `Username=` or `User ID=` part).
   - Change the password to the new one. Copy it with `Set-Clipboard $pw` and paste it with Ctrl+V; the browser pastes normally.
   - Keep the host (the internal one), the database and every other setting.

   Save; that redeploys.
6. **Verify:**
   - The deploy log shows `Using PostgreSQL … (connection pool up to 20)`, and the service goes live.
   - On the site: sign in, add to cart, place an order, view it under your account.
   - The service's logs contain no `permission denied`.
   - Then confirm what the API connects as, and whether it uses TLS. Run this **as `storefront_app`**: the owner can't see the details of another role's sessions, so the same query run as the owner silently leaves them out. Wake the API first:
     ```powershell
     curl.exe -s -o NUL -w "API ready: %{http_code}`n" https://datheon-task-4.onrender.com/health/ready
     'SELECT a.application_name, s.ssl, s.version, (a.client_addr << ''10.0.0.0/8''::inet) AS private_network, count(*) FROM pg_stat_activity a JOIN pg_stat_ssl s USING (pid) WHERE a.usename = current_user GROUP BY 1, 2, 3, 4;' | docker run --rm -i postgres:16 psql $app
     ```
     A row with an empty `application_name` is the API's pooled connection, and `psql` is yours.
7. **Clean up:** `Set-Clipboard -Value ' '`, then `Remove-Variable pw, app, b`, and close the window.

**Done in production on 25 October 2026:**

| Step | Result |
|---|---|
| Pre-check | PostgreSQL 18.6; `storefront_9f4i_user` has `CREATEROLE` and owns the 12 tables |
| Role script | Role created with a connection limit of 25 |
| Password | Set with the statement method in step 3, after the `\password` paste problem |
| Sign-in test | Connected as `storefront_app`; `CREATE TABLE` refused with `permission denied for schema public` |
| `ConnectionStrings__Storefront` | Switched to `storefront_app` on Render; the deploy went live |
| Readiness | 200 |
| Sign-in on the site | Works |
| Verification query | One API connection as `storefront_app`, over the private network, **TLS 1.3** |

So the internal connection is encrypted without any setting. **On 26 October 2026, `;SSL Mode=Require` was added** to `ConnectionStrings__Storefront`, so the API now refuses to connect at all rather than ever fall back to plain text. After the redeploy, `/health/ready` returned 200 (0.58 s), which proves the connection is encrypted: with no TLS available, the API couldn't connect and readiness would return 503. To undo it, remove that part of the setting and save.

Checkout couldn't be tested that evening, for an unrelated reason: FakeStoreAPI answered **403** to the API's requests from Render (19:02 and 19:13 UTC), while answering 200 to a home connection. That points to its bot protection blocking Render's addresses. The API failed safely: 502 `UPSTREAM_UNAVAILABLE`, no order created, the cart kept, a reference shown. *Open:* retry the next day. If it keeps happening, the choices are to price checkout from the saved catalogue copy for a limited time, or to serve the catalogue from our own database.

**Rollback:** set the username and password in `ConnectionStrings__Storefront` back to the default user's; they're in the database's **Internal Database URL** on Render. Save. The role can stay; it's unused until something connects with it.

**Later migrations:** unchanged. Run them as the owner with the External Database URL (section 11). New tables are granted automatically.

## 14. External access and backups (SA-13, SA-17)

**External access is limited to the owner's IP** (26 October 2026). Render's default rule, `0.0.0.0/0`, let any address try credentials against the database's external URL. The rule now allows one `/32` address, the owner's home connection. It's under the database → **Info** → **Networking** (access control).

These rules apply only to the **external** URL. The API connects over the private network, so it's unaffected. *Verified* after the change: `psql` from home still connects, and `/health/ready` returns 200 in 0.49 s.

Home IPs change from time to time. If a migration or a backup times out, or fails with "connection refused", check `curl.exe -s https://api.ipify.org` and update the rule.

**Backups are manual.** Free Render databases have no backups of any kind. Take one before anything risky (migrations, role changes, a database move), and roughly weekly. Run this in a window with `$db` set (section 11).

`pg_dump` must be at least as new as the server, which runs **PostgreSQL 18**, so use the `postgres:18` image here, not `postgres:16`:
```powershell
$dir = "$HOME\Documents\storefront-backups"; New-Item -ItemType Directory -Force $dir | Out-Null
$file = "storefront-$(Get-Date -Format yyyyMMdd-HHmm).dump"
docker run --rm -v "${dir}:/backup" postgres:18 pg_dump $db --format=custom --no-owner --no-privileges --file "/backup/$file"
```
A backup only counts once it has been restored. Restore it into a throwaway local server and count the rows:
```powershell
docker run -d --name sf-restore-test -e POSTGRES_PASSWORD=restore-test -v "${dir}:/backup" postgres:18 | Out-Null
Start-Sleep 10
docker exec sf-restore-test createdb -U postgres check
docker exec sf-restore-test pg_restore -U postgres -d check --no-owner --no-privileges "/backup/$file"
'SELECT (SELECT count(*) FROM "AspNetUsers") AS users, (SELECT count(*) FROM "Orders") AS orders, (SELECT count(*) FROM "__EFMigrationsHistory") AS migrations;' | docker exec -i sf-restore-test psql -U postgres -d check
docker rm -f sf-restore-test | Out-Null
```
*First backup, 26 October 2026:* `storefront-20261026-1701.dump`, 31 KB. It restored with no errors: 2 users, 2 orders, 3 migrations.

**The dump is sensitive.** It holds customer names and emails, password hashes and the (certificate-encrypted) sign-in keys.
- Keep it out of the repository.
- Keep it out of folders that sync to shared storage, unless that's intended.
- Delete old dumps you no longer need.

`--no-owner --no-privileges` makes it restorable into a database with a different owner. Afterwards, re-run `tools/db/create-app-role.sql` (section 13), because the grants aren't in the dump.

**The database expires.** It was created on **24 October 2026**, when the API went live. Free databases expire 30 days after creation, so this one becomes unreachable around **23 November 2026**. Render then deletes it, with all its data, after a 14-day grace period, around **7 December 2026**, unless it's upgraded to a paid plan.

Before the expiry, either:
- **upgrade** the database to a paid plan, which keeps the data and adds Render's own backups; or
- **move to a new Free database**, keeping the data: follow the checklist in **section 9**. It's the owner's choice, and was rehearsed on 26 October 2026.

Set a reminder for **16 November 2026**: do the move between 16 and 20 November (section 9).
