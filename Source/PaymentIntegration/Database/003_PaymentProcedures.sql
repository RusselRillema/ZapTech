-- PostgreSQL. Included by setup.sql after the tables and seed data.
-- Existing databases: deploy this actual file as the object owner using
-- psql --single-transaction. The application owns all runtime transactions.
\set ON_ERROR_STOP on

CREATE OR REPLACE PROCEDURE public.payment_details_get(
    IN p_customer_id text, IN p_customer_card_id uuid, IN p_sub_merchant_id text,
    OUT p_result jsonb)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp
AS $procedure$
BEGIN
    SELECT jsonb_build_object(
        'AcmeCardToken', c."AcmeCardToken", 'Email', u."Email",
        'TradingName', m."TradingName", 'City', m."City", 'RegionCode', m."RegionCode",
        'CountryCode', m."CountryCode", 'MerchantCategoryCode', m."MerchantCategoryCode")
    INTO p_result
    FROM public."Customers" u
    JOIN public."CustomerCards" c ON c."CustomerId" = u."CustomerId"
    CROSS JOIN public."SubMerchants" m
    WHERE u."CustomerId" = p_customer_id AND c."CustomerCardId" = p_customer_card_id
        AND m."SubMerchantId" = p_sub_merchant_id;
END;
$procedure$;

CREATE OR REPLACE PROCEDURE public.payments_get_by_app_id(
    IN p_app_payment_id text, OUT p_result jsonb)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp
AS $procedure$
BEGIN
    SELECT to_jsonb(p) || jsonb_build_object('HasPendingEvents', EXISTS (
        SELECT 1 FROM public."PaymentEvents" e
        WHERE e."PaymentId" = p."PaymentId" AND e."ProcessingState" = 'Pending'))
    INTO p_result FROM public."Payments" p WHERE p."AppPaymentId" = p_app_payment_id;
END;
$procedure$;

CREATE OR REPLACE PROCEDURE public.payments_create(
    IN p_payment_id uuid, IN p_app_payment_id text, IN p_customer_id text,
    IN p_customer_card_id uuid, IN p_sub_merchant_id text, IN p_order_number text,
    IN p_currency_code text, IN p_requested_amount bigint, IN p_state text,
    IN p_created_at timestamptz, OUT p_result jsonb)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp
AS $procedure$
DECLARE
    v_created boolean;
    v_payment jsonb;
BEGIN
    -- The unique app identifier coordinates creation across all instances.
    INSERT INTO public."Payments" ("PaymentId", "AppPaymentId", "CustomerId",
        "CustomerCardId", "SubMerchantId", "OrderNumber", "CurrencyCode",
        "RequestedAmount", "State", "CreatedAt")
    VALUES (p_payment_id, p_app_payment_id, p_customer_id, p_customer_card_id,
        p_sub_merchant_id, p_order_number, p_currency_code, p_requested_amount,
        p_state, p_created_at)
    ON CONFLICT ("AppPaymentId") DO NOTHING;
    v_created := FOUND;

    -- Wait for the winning row, then read using a fresh statement snapshot so
    -- the payment and pending-event flag agree after any concurrent processing.
    PERFORM 1 FROM public."Payments" WHERE "AppPaymentId" = p_app_payment_id FOR UPDATE;
    SELECT to_jsonb(p) || jsonb_build_object('HasPendingEvents', EXISTS (
        SELECT 1 FROM public."PaymentEvents" e
        WHERE e."PaymentId" = p."PaymentId" AND e."ProcessingState" = 'Pending'))
    INTO v_payment FROM public."Payments" p WHERE p."AppPaymentId" = p_app_payment_id;
    p_result := jsonb_build_object('Created', v_created, 'Payment', v_payment);
END;
$procedure$;

CREATE OR REPLACE PROCEDURE public.payments_get_for_update(
    IN p_payment_id uuid, OUT p_result jsonb)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp
AS $procedure$
BEGIN
    -- Retained by the caller's transaction throughout C# event processing.
    PERFORM 1 FROM public."Payments" WHERE "PaymentId" = p_payment_id FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Payment does not exist.' USING ERRCODE = 'P0002'; END IF;
    SELECT to_jsonb(p) || jsonb_build_object('HasPendingEvents', EXISTS (
        SELECT 1 FROM public."PaymentEvents" e
        WHERE e."PaymentId" = p_payment_id AND e."ProcessingState" = 'Pending'))
    INTO p_result FROM public."Payments" p WHERE p."PaymentId" = p_payment_id;
END;
$procedure$;

CREATE OR REPLACE PROCEDURE public.payment_events_create(
    IN p_payment_event_id uuid, IN p_payment_id uuid, IN p_source text,
    IN p_payload jsonb, IN p_http_status_code smallint, IN p_received_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp
AS $procedure$
BEGIN
    PERFORM 1 FROM public."Payments" WHERE "PaymentId" = p_payment_id FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Payment does not exist.' USING ERRCODE = 'P0002'; END IF;
    INSERT INTO public."PaymentEvents" ("PaymentEventId", "PaymentId", "Source",
        "Payload", "HttpStatusCode", "ReceivedAt", "ProcessingState")
    VALUES (p_payment_event_id, p_payment_id, p_source, p_payload,
        p_http_status_code, p_received_at, 'Pending');
END;
$procedure$;

CREATE OR REPLACE PROCEDURE public.payment_events_get_pending(
    IN p_payment_id uuid, OUT p_result jsonb)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp
AS $procedure$
BEGIN
    SELECT coalesce(jsonb_agg(to_jsonb(e) ORDER BY e."ReceivedAt", e."PaymentEventId"), '[]'::jsonb)
    INTO p_result FROM public."PaymentEvents" e
    WHERE e."PaymentId" = p_payment_id AND e."ProcessingState" = 'Pending';
END;
$procedure$;

CREATE OR REPLACE PROCEDURE public.payments_update(
    IN p_payment_id uuid, IN p_state text, IN p_approved_amount bigint, IN p_remaining_amount bigint)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp
AS $procedure$
BEGIN
    -- UPDATE itself acquires the payment row lock if not already held.
    -- Values and lifecycle decisions are supplied by C#, never calculated here.
    UPDATE public."Payments" SET "State" = p_state, "ApprovedAmount" = p_approved_amount,
        "RemainingAmount" = p_remaining_amount WHERE "PaymentId" = p_payment_id;
    IF NOT FOUND THEN RAISE EXCEPTION 'Payment does not exist.' USING ERRCODE = 'P0002'; END IF;
END;
$procedure$;

CREATE OR REPLACE PROCEDURE public.payment_events_update(
    IN p_payment_id uuid, IN p_payment_event_id uuid, IN p_processing_state text,
    IN p_processing_comment text, IN p_processed_at timestamptz)
LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, pg_temp
AS $procedure$
BEGIN
    PERFORM 1 FROM public."Payments" WHERE "PaymentId" = p_payment_id FOR UPDATE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Payment does not exist.' USING ERRCODE = 'P0002'; END IF;
    UPDATE public."PaymentEvents" SET "ProcessingState" = p_processing_state,
        "ProcessingComment" = p_processing_comment, "ProcessedAt" = p_processed_at
    WHERE "PaymentId" = p_payment_id AND "PaymentEventId" = p_payment_event_id;
    IF NOT FOUND THEN RAISE EXCEPTION 'Payment event does not exist.' USING ERRCODE = 'P0002'; END IF;
END;
$procedure$;

-- Explicit grants also make standalone deployment safe, regardless of defaults.
REVOKE ALL ON PROCEDURE public.payment_details_get(text, uuid, text) FROM PUBLIC;
REVOKE ALL ON PROCEDURE public.payments_get_by_app_id(text) FROM PUBLIC;
REVOKE ALL ON PROCEDURE public.payments_create(uuid, text, text, uuid, text, text, text, bigint, text, timestamptz) FROM PUBLIC;
REVOKE ALL ON PROCEDURE public.payments_get_for_update(uuid) FROM PUBLIC;
REVOKE ALL ON PROCEDURE public.payment_events_create(uuid, uuid, text, jsonb, smallint, timestamptz) FROM PUBLIC;
REVOKE ALL ON PROCEDURE public.payment_events_get_pending(uuid) FROM PUBLIC;
REVOKE ALL ON PROCEDURE public.payments_update(uuid, text, bigint, bigint) FROM PUBLIC;
REVOKE ALL ON PROCEDURE public.payment_events_update(uuid, uuid, text, text, timestamptz) FROM PUBLIC;
GRANT EXECUTE ON PROCEDURE public.payment_details_get(text, uuid, text) TO payment_integration_app;
GRANT EXECUTE ON PROCEDURE public.payments_get_by_app_id(text) TO payment_integration_app;
GRANT EXECUTE ON PROCEDURE public.payments_create(uuid, text, text, uuid, text, text, text, bigint, text, timestamptz) TO payment_integration_app;
GRANT EXECUTE ON PROCEDURE public.payments_get_for_update(uuid) TO payment_integration_app;
GRANT EXECUTE ON PROCEDURE public.payment_events_create(uuid, uuid, text, jsonb, smallint, timestamptz) TO payment_integration_app;
GRANT EXECUTE ON PROCEDURE public.payment_events_get_pending(uuid) TO payment_integration_app;
GRANT EXECUTE ON PROCEDURE public.payments_update(uuid, text, bigint, bigint) TO payment_integration_app;
GRANT EXECUTE ON PROCEDURE public.payment_events_update(uuid, uuid, text, text, timestamptz) TO payment_integration_app;
