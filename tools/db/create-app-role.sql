-- Least-privilege runtime role for the Storefront API (Security-Audit.md, SA-13).
--
-- Run with psql, signed in as the role that owns the database and its tables (on Render, the
-- database's default user). Safe to run again: it only adds what's missing. It sets no password,
-- so no password ever appears in a file, a log or shell history; set it afterwards with
--   \password storefront_app
-- See Documentation/Deployment.md, section 13.
--
-- What the API gets: read and write access to the rows of the existing tables, and of every table
-- later migrations create (default privileges), and nothing else. It can't create, alter, drop or
-- truncate tables, create roles or databases, or read other databases. Migrations keep running
-- as the owner, separately (Deployment.md, section 3).

\set ON_ERROR_STOP on
SELECT current_user AS owner, current_database() AS dbname \gset

-- The login role. NOINHERIT and no role memberships, so it holds only what's granted below.
-- A new role has no special attributes (no superuser, create-database, create-role, replication or
-- row-security bypass). A non-superuser owner can't even switch those off explicitly, so instead
-- of setting them the script checks them, and stops if a pre-existing role has any.
SELECT NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'storefront_app') AS create_role \gset
\if :create_role
  CREATE ROLE storefront_app LOGIN NOINHERIT NOCREATEDB NOCREATEROLE;
\endif
SELECT NOT (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls) AND rolcanlogin
       AND NOT EXISTS (SELECT FROM pg_auth_members m WHERE m.member = r.oid) AS role_is_plain
  FROM pg_roles r WHERE rolname = 'storefront_app' \gset
\if :role_is_plain
\else
  \echo 'storefront_app already exists with extra attributes or role memberships. Stopping; nothing more was changed.'
  \quit 3
\endif

-- Limits that hold even if the app misbehaves (Security-Audit.md, SA-04):
--  * connection limit: the app's pool is 20 per instance (Database:MaxPoolSize); 25 leaves room
--    for a rolling deploy's overlap, and keeps the rest of the server's connections for the owner;
--  * statement timeout: no API query should take seconds; a runaway one is stopped at 30 s;
--  * idle-in-transaction timeout: a transaction left open (a bug, a stuck request) can't hold
--    locks on orders for more than a minute.
ALTER ROLE storefront_app CONNECTION LIMIT 25;
ALTER ROLE storefront_app IN DATABASE :"dbname" SET statement_timeout = '30s';
ALTER ROLE storefront_app IN DATABASE :"dbname" SET idle_in_transaction_session_timeout = '60s';

-- Data access only.
GRANT CONNECT ON DATABASE :"dbname" TO storefront_app;
GRANT USAGE ON SCHEMA public TO storefront_app;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO storefront_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO storefront_app;
-- The migration history is the owner's record of which migrations ran; the API only reads it (the
-- readiness check reports pending migrations).
REVOKE INSERT, UPDATE, DELETE ON "__EFMigrationsHistory" FROM storefront_app;

-- Tables and sequences the owner creates later (new migrations) are granted automatically.
ALTER DEFAULT PRIVILEGES FOR ROLE :"owner" IN SCHEMA public
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO storefront_app;
ALTER DEFAULT PRIVILEGES FOR ROLE :"owner" IN SCHEMA public
  GRANT USAGE, SELECT ON SEQUENCES TO storefront_app;

-- Nobody but the owner creates objects in the schema. (The default since PostgreSQL 15; explicit
-- for older servers.)
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

-- Report what the role ended up with.
SELECT rolname, rolsuper, rolcreaterole, rolcreatedb, rolinherit, rolconnlimit, rolconfig
  FROM pg_roles WHERE rolname = 'storefront_app';
SELECT rolname, setconfig FROM pg_db_role_setting s JOIN pg_roles r ON r.oid = s.setrole
  WHERE rolname = 'storefront_app';
-- has_table_privilege with a list is true if ANY is held, so each privilege is checked on its own.
SELECT count(*) FILTER (WHERE has_table_privilege('storefront_app', t, 'SELECT') AND has_table_privilege('storefront_app', t, 'INSERT')
                          AND has_table_privilege('storefront_app', t, 'UPDATE') AND has_table_privilege('storefront_app', t, 'DELETE')) AS tables_with_data_access,
       count(*) FILTER (WHERE has_table_privilege('storefront_app', t, 'TRUNCATE') OR has_table_privilege('storefront_app', t, 'REFERENCES')
                          OR has_table_privilege('storefront_app', t, 'TRIGGER')) AS tables_with_more_than_data_access,
       count(*) AS tables
  FROM (SELECT format('%I.%I', schemaname, tablename) AS t FROM pg_tables
         WHERE schemaname = 'public' AND tablename <> '__EFMigrationsHistory') AS public_tables;
SELECT has_table_privilege('storefront_app', '"__EFMigrationsHistory"', 'SELECT') AS history_readable,
       has_table_privilege('storefront_app', '"__EFMigrationsHistory"', 'DELETE')
         OR has_table_privilege('storefront_app', '"__EFMigrationsHistory"', 'INSERT')
         OR has_table_privilege('storefront_app', '"__EFMigrationsHistory"', 'UPDATE') AS history_writable;
