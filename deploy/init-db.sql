\set ON_ERROR_STOP on
\getenv migration_password PASTO_MIGRATION_PASSWORD
\getenv runtime_password PASTO_RUNTIME_PASSWORD

BEGIN;
CREATE ROLE pasto_migrator LOGIN PASSWORD :'migration_password';
CREATE ROLE pasto_runtime LOGIN PASSWORD :'runtime_password';
REVOKE ALL ON DATABASE pasto FROM PUBLIC;
GRANT CONNECT ON DATABASE pasto TO pasto_migrator, pasto_runtime;
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT USAGE, CREATE ON SCHEMA public TO pasto_migrator;
GRANT USAGE ON SCHEMA public TO pasto_runtime;
COMMIT;
