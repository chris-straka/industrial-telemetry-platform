-- Idempotent workload roles for the development database. Runs on every Compose start
-- from the postgres-init one-shot (not only on first init), so it also converges
-- databases initialized before this file existed.
--
-- The diagnostics worker connects as a non-superuser confined to industrial_db.
-- It still needs DDL because the worker applies EF migrations at startup, so this
-- is separation of credential and privilege, not read-only least privilege.
--
-- :'diag_password' is a psql variable (see the postgres-init command), not server
-- syntax: it cannot appear inside a DO $$ block, where psql passes text through
-- uninterpolated, so the role statements are generated and executed via \gexec.
SELECT format('CREATE ROLE diagnostics LOGIN PASSWORD %L', :'diag_password')
WHERE NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'diagnostics')\gexec

SELECT format('ALTER ROLE diagnostics WITH LOGIN PASSWORD %L', :'diag_password')
WHERE EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'diagnostics')\gexec

GRANT CONNECT ON DATABASE industrial_db TO diagnostics;
GRANT USAGE, CREATE ON SCHEMA public TO diagnostics;
GRANT ALL PRIVILEGES ON ALL TABLES IN SCHEMA public TO diagnostics;
GRANT ALL PRIVILEGES ON ALL SEQUENCES IN SCHEMA public TO diagnostics;
-- Tables the worker's future migrations create (owned by diagnostics) and tables
-- host EF tooling creates as admin must both stay usable by the worker.
ALTER DEFAULT PRIVILEGES FOR ROLE diagnostics IN SCHEMA public
    GRANT ALL PRIVILEGES ON TABLES TO diagnostics;
ALTER DEFAULT PRIVILEGES FOR ROLE diagnostics IN SCHEMA public
    GRANT ALL PRIVILEGES ON SEQUENCES TO diagnostics;
ALTER DEFAULT PRIVILEGES FOR ROLE admin IN SCHEMA public
    GRANT ALL PRIVILEGES ON TABLES TO diagnostics;
ALTER DEFAULT PRIVILEGES FOR ROLE admin IN SCHEMA public
    GRANT ALL PRIVILEGES ON SEQUENCES TO diagnostics;
