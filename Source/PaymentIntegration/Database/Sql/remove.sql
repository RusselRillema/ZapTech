-- Explicitly remove only this application's database and login.
-- Stop application connections first. Run outside a transaction using psql.
\set ON_ERROR_STOP on
\set ECHO none
\set VERBOSITY terse

\connect -reuse-previous=on postgres
-- No FORCE: active connections must be stopped by the operator.
DROP DATABASE IF EXISTS payment_integration;
-- No cascading cleanup: dependencies elsewhere must remain untouched.
DROP ROLE IF EXISTS payment_integration_app;
