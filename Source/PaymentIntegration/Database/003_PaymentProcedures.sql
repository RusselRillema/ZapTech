-- PostgreSQL. Included by setup.sql after the tables and seed data.
-- Permissions are configured by setup.sql. The application owns runtime transactions.
\set ON_ERROR_STOP on

-- Explicit result contract for the small collection of pending events.
-- Only Acme's variable audit payload uses JSONB.
CREATE TYPE public.payment_event_result AS
(
    payment_event_id uuid,
    payment_id uuid,
    source text,
    payload jsonb,
    http_status_code smallint,
    received_at timestamptz,
    processing_state text,
    processing_comment text,
    processed_at timestamptz
);

CREATE PROCEDURE public.payment_details_get
(
    IN p_customer_id text,
    IN p_customer_card_id uuid,
    IN p_sub_merchant_id text,
    OUT p_result_acme_card_token text,
    OUT p_result_email text,
    OUT p_result_trading_name text,
    OUT p_result_city text,
    OUT p_result_region_code text,
    OUT p_result_country_code text,
    OUT p_result_merchant_category_code text
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $procedure$
BEGIN
    SELECT
        c."AcmeCardToken",
        u."Email",
        m."TradingName",
        m."City",
        m."RegionCode",
        m."CountryCode",
        m."MerchantCategoryCode"
    INTO
        p_result_acme_card_token,
        p_result_email,
        p_result_trading_name,
        p_result_city,
        p_result_region_code,
        p_result_country_code,
        p_result_merchant_category_code
    FROM public."Customers" AS u
    JOIN public."CustomerCards" AS c
        ON c."CustomerId" = u."CustomerId"
    CROSS JOIN public."SubMerchants" AS m
    WHERE u."CustomerId" = p_customer_id
        AND c."CustomerCardId" = p_customer_card_id
        AND m."SubMerchantId" = p_sub_merchant_id;
END;
$procedure$;

CREATE PROCEDURE public.payments_get_by_app_id
(
    IN p_app_payment_id text,
    OUT p_result_payment_id uuid,
    OUT p_result_app_payment_id text,
    OUT p_result_customer_id text,
    OUT p_result_customer_card_id uuid,
    OUT p_result_sub_merchant_id text,
    OUT p_result_order_number text,
    OUT p_result_currency_code text,
    OUT p_result_requested_amount bigint,
    OUT p_result_approved_amount bigint,
    OUT p_result_remaining_amount bigint,
    OUT p_result_state text,
    OUT p_result_created_at timestamptz,
    OUT p_result_has_pending_events boolean
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $procedure$
BEGIN
    SELECT
        p."PaymentId",
        p."AppPaymentId",
        p."CustomerId",
        p."CustomerCardId",
        p."SubMerchantId",
        p."OrderNumber",
        p."CurrencyCode",
        p."RequestedAmount",
        p."ApprovedAmount",
        p."RemainingAmount",
        p."State",
        p."CreatedAt",
        EXISTS (
            SELECT 1
            FROM public."PaymentEvents" AS e
            WHERE e."PaymentId" = p."PaymentId"
                AND e."ProcessingState" = 'Pending'
        )
    INTO
        p_result_payment_id,
        p_result_app_payment_id,
        p_result_customer_id,
        p_result_customer_card_id,
        p_result_sub_merchant_id,
        p_result_order_number,
        p_result_currency_code,
        p_result_requested_amount,
        p_result_approved_amount,
        p_result_remaining_amount,
        p_result_state,
        p_result_created_at,
        p_result_has_pending_events
    FROM public."Payments" AS p
    WHERE p."AppPaymentId" = p_app_payment_id;
END;
$procedure$;

CREATE PROCEDURE public.payments_create
(
    IN p_payment_id uuid,
    IN p_app_payment_id text,
    IN p_customer_id text,
    IN p_customer_card_id uuid,
    IN p_sub_merchant_id text,
    IN p_order_number text,
    IN p_currency_code text,
    IN p_requested_amount bigint,
    IN p_state text,
    IN p_created_at timestamptz,
    OUT p_result_created boolean,
    OUT p_result_payment_id uuid
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $procedure$
BEGIN
    -- The unique app identifier coordinates creation across all instances.
    INSERT INTO public."Payments"
    (
        "PaymentId",
        "AppPaymentId",
        "CustomerId",
        "CustomerCardId",
        "SubMerchantId",
        "OrderNumber",
        "CurrencyCode",
        "RequestedAmount",
        "State",
        "CreatedAt"
    )
    VALUES
    (
        p_payment_id,
        p_app_payment_id,
        p_customer_id,
        p_customer_card_id,
        p_sub_merchant_id,
        p_order_number,
        p_currency_code,
        p_requested_amount,
        p_state,
        p_created_at
    )
    ON CONFLICT ("AppPaymentId") DO NOTHING;

    p_result_created := FOUND;

    -- Wait for the winning row after a concurrent insert.
    SELECT p."PaymentId"
    INTO p_result_payment_id
    FROM public."Payments" AS p
    WHERE p."AppPaymentId" = p_app_payment_id
    FOR UPDATE;
END;
$procedure$;

CREATE PROCEDURE public.payments_get_for_update
(
    IN p_payment_id uuid,
    OUT p_result_payment_id uuid,
    OUT p_result_app_payment_id text,
    OUT p_result_customer_id text,
    OUT p_result_customer_card_id uuid,
    OUT p_result_sub_merchant_id text,
    OUT p_result_order_number text,
    OUT p_result_currency_code text,
    OUT p_result_requested_amount bigint,
    OUT p_result_approved_amount bigint,
    OUT p_result_remaining_amount bigint,
    OUT p_result_state text,
    OUT p_result_created_at timestamptz,
    OUT p_result_has_pending_events boolean
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $procedure$
BEGIN
    -- Retained by the caller's transaction throughout C# event processing.
    PERFORM 1
    FROM public."Payments"
    WHERE "PaymentId" = p_payment_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Payment does not exist.'
            USING ERRCODE = 'P0002';
    END IF;

    -- Read the state and event flag with a fresh snapshot after acquiring the lock.
    SELECT
        p."PaymentId",
        p."AppPaymentId",
        p."CustomerId",
        p."CustomerCardId",
        p."SubMerchantId",
        p."OrderNumber",
        p."CurrencyCode",
        p."RequestedAmount",
        p."ApprovedAmount",
        p."RemainingAmount",
        p."State",
        p."CreatedAt",
        EXISTS (
            SELECT 1
            FROM public."PaymentEvents" AS e
            WHERE e."PaymentId" = p_payment_id
                AND e."ProcessingState" = 'Pending'
        )
    INTO
        p_result_payment_id,
        p_result_app_payment_id,
        p_result_customer_id,
        p_result_customer_card_id,
        p_result_sub_merchant_id,
        p_result_order_number,
        p_result_currency_code,
        p_result_requested_amount,
        p_result_approved_amount,
        p_result_remaining_amount,
        p_result_state,
        p_result_created_at,
        p_result_has_pending_events
    FROM public."Payments" AS p
    WHERE p."PaymentId" = p_payment_id;
END;
$procedure$;

CREATE PROCEDURE public.payment_events_create
(
    IN p_payment_event_id uuid,
    IN p_payment_id uuid,
    IN p_source text,
    IN p_payload jsonb,
    IN p_http_status_code smallint,
    IN p_received_at timestamptz
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $procedure$
BEGIN
    PERFORM 1
    FROM public."Payments"
    WHERE "PaymentId" = p_payment_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Payment does not exist.'
            USING ERRCODE = 'P0002';
    END IF;

    INSERT INTO public."PaymentEvents"
    (
        "PaymentEventId",
        "PaymentId",
        "Source",
        "Payload",
        "HttpStatusCode",
        "ReceivedAt",
        "ProcessingState"
    )
    VALUES
    (
        p_payment_event_id,
        p_payment_id,
        p_source,
        p_payload,
        p_http_status_code,
        p_received_at,
        'Pending'
    );
END;
$procedure$;

CREATE PROCEDURE public.payment_events_get_pending
(
    IN p_payment_id uuid,
    OUT p_result_events public.payment_event_result[]
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $procedure$
BEGIN
    SELECT coalesce(
        array_agg(
            ROW(
                e."PaymentEventId",
                e."PaymentId",
                e."Source",
                e."Payload",
                e."HttpStatusCode",
                e."ReceivedAt",
                e."ProcessingState",
                e."ProcessingComment",
                e."ProcessedAt"
            )::public.payment_event_result
            ORDER BY e."ReceivedAt", e."PaymentEventId"
        ),
        ARRAY[]::public.payment_event_result[]
    )
    INTO p_result_events
    FROM public."PaymentEvents" AS e
    WHERE e."PaymentId" = p_payment_id
        AND e."ProcessingState" = 'Pending';
END;
$procedure$;

CREATE PROCEDURE public.payments_update
(
    IN p_payment_id uuid,
    IN p_state text,
    IN p_approved_amount bigint,
    IN p_remaining_amount bigint
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $procedure$
BEGIN
    -- UPDATE acquires the payment row lock if the caller does not already hold it.
    -- C# supplies the values and lifecycle decisions.
    UPDATE public."Payments"
    SET
        "State" = p_state,
        "ApprovedAmount" = p_approved_amount,
        "RemainingAmount" = p_remaining_amount
    WHERE "PaymentId" = p_payment_id;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Payment does not exist.'
            USING ERRCODE = 'P0002';
    END IF;
END;
$procedure$;

CREATE PROCEDURE public.payment_events_update
(
    IN p_payment_id uuid,
    IN p_payment_event_id uuid,
    IN p_processing_state text,
    IN p_processing_comment text,
    IN p_processed_at timestamptz
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $procedure$
BEGIN
    PERFORM 1
    FROM public."Payments"
    WHERE "PaymentId" = p_payment_id
    FOR UPDATE;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Payment does not exist.'
            USING ERRCODE = 'P0002';
    END IF;

    UPDATE public."PaymentEvents"
    SET
        "ProcessingState" = p_processing_state,
        "ProcessingComment" = p_processing_comment,
        "ProcessedAt" = p_processed_at
    WHERE "PaymentId" = p_payment_id
        AND "PaymentEventId" = p_payment_event_id;

    IF NOT FOUND THEN
        RAISE EXCEPTION 'Payment event does not exist.'
            USING ERRCODE = 'P0002';
    END IF;
END;
$procedure$;
