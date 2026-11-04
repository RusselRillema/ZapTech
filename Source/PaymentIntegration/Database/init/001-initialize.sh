#!/usr/bin/env bash
set -euo pipefail

: "${POSTGRES_USER:?POSTGRES_USER is required}"
: "${POSTGRES_DB:?POSTGRES_DB is required}"
: "${PAYMENT_DB_USER:?PAYMENT_DB_USER is required}"
: "${PAYMENT_DB_PASSWORD:?PAYMENT_DB_PASSWORD is required}"

if [[ "$PAYMENT_DB_USER" == "$POSTGRES_USER" ]]; then
    printf '%s\n' 'PAYMENT_DB_USER must differ from the administrator account.' >&2
    exit 1
fi

# The official entrypoint already created POSTGRES_DB, owned by POSTGRES_USER.
# Connect through its temporary Unix socket; TCP is available after init completes.
# Read the password inside psql rather than placing it in command-line arguments.
psql --no-psqlrc --no-password \
    --username="$POSTGRES_USER" --dbname="$POSTGRES_DB" \
    --set=ON_ERROR_STOP=1 --set=ECHO=none --set=VERBOSITY=terse \
    --single-transaction --file=- <<'SQL'
\getenv database_owner POSTGRES_USER
\getenv database_name POSTGRES_DB
\getenv application_user PAYMENT_DB_USER
\getenv application_password PAYMENT_DB_PASSWORD

-- Keep password-bearing statements out of server logs, including on failure.
SET LOCAL log_statement = 'none';
SET LOCAL log_min_error_statement = 'panic';
SET LOCAL password_encryption = 'scram-sha-256';

CREATE ROLE :"application_user"
    WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS
    PASSWORD :'application_password';
\unset application_password

-- Remove inherited public access, including temporary-table creation.
REVOKE ALL PRIVILEGES ON DATABASE :"database_name" FROM PUBLIC;
GRANT CONNECT ON DATABASE :"database_name" TO :"application_user";
REVOKE ALL PRIVILEGES ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO :"application_user";

-- PostgreSQL normally gives PUBLIC execution access to new routines.
-- This default is database-wide for objects created by the administrator.
ALTER DEFAULT PRIVILEGES FOR ROLE :"database_owner"
    REVOKE EXECUTE ON ROUTINES FROM PUBLIC;

-- Currently there are no procedures. Future deployments must repeat this grant
-- after creating procedures; a default ROUTINES grant would include functions.
GRANT EXECUTE ON ALL PROCEDURES IN SCHEMA public TO :"application_user";
SQL
