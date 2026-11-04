-- Initial development setup, executed by psql using an existing admin connection.
-- Do not run inside a transaction: CREATE DATABASE requires autocommit.
\set ON_ERROR_STOP on
\set ECHO none
\set VERBOSITY terse

-- Reject collisions before creating anything. Never reset existing credentials.
DO $preflight$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_database WHERE datname = 'payment_integration') THEN
        RAISE EXCEPTION 'Setup stopped: database payment_integration already exists.';
    END IF;
    IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = 'payment_integration_app') THEN
        RAISE EXCEPTION 'Setup stopped: login payment_integration_app already exists.';
    END IF;
END;
$preflight$;

SELECT current_user AS deployment_owner \gset
CREATE DATABASE payment_integration OWNER :"deployment_owner";
\connect -reuse-previous=on payment_integration

BEGIN;
SET LOCAL ROLE :"deployment_owner";
SET LOCAL password_encryption = 'scram-sha-256';

-- Public development credentials, deliberately independent of .env files.
CREATE ROLE payment_integration_app
    WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS
    PASSWORD 'payment_integration_dev_only';

REVOKE ALL PRIVILEGES ON DATABASE payment_integration FROM PUBLIC;
GRANT CONNECT ON DATABASE payment_integration TO payment_integration_app;
REVOKE ALL PRIVILEGES ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO payment_integration_app;

ALTER DEFAULT PRIVILEGES FOR ROLE :"deployment_owner"
    REVOKE EXECUTE ON ROUTINES FROM PUBLIC;

-- Resolve includes relative to this file for both native and Docker setup.
-- These scripts share this transaction; neither starts or commits its own.
\ir 001_CreateTables.sql
\ir 002_SeedSampleData.sql
\ir 003_PaymentProcedures.sql

GRANT EXECUTE ON ALL PROCEDURES IN SCHEMA public TO payment_integration_app;
COMMIT;
