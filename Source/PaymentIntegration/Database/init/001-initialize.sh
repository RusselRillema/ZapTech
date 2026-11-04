#!/usr/bin/env bash
set -euo pipefail

: "${POSTGRES_USER:?POSTGRES_USER is required}"

# Docker initializes the server; native and Docker deployments use the same SQL.
# Its temporary server listens on a Unix socket until initialization completes.
psql --no-psqlrc --no-password \
    --username="$POSTGRES_USER" --dbname=postgres \
    --set=ON_ERROR_STOP=1 \
    --file=/opt/paymentintegration/database/setup.sql
