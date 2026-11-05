-- PostgreSQL. Included by setup.sql after 003_PaymentProcedures.sql.
-- The caller owns the deployment transaction. Reporting writes no payment data.
\set ON_ERROR_STOP on

CREATE TYPE public.payment_history_result AS
(
    payment_id uuid,
    app_payment_id text,
    customer_id text,
    customer_card_id uuid,
    sub_merchant_id text,
    sub_merchant_name text,
    order_number text,
    currency_code text,
    requested_amount bigint,
    created_at timestamptz,
    events public.payment_event_result[]
);

CREATE PROCEDURE public.payments_get_report_history
(
    IN p_created_before timestamptz,
    OUT p_result_histories public.payment_history_result[]
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $procedure$
BEGIN
    IF p_created_before IS NULL THEN
        RAISE EXCEPTION 'A reporting cutoff is required.' USING ERRCODE = '22004';
    END IF;

    -- One statement gives the original details and all associated receipts from
    -- one snapshot. Later receipts are needed only to detect subsequent activity.
    -- C# selects effective dates, reconstructs state and calculates report values.
    SELECT coalesce(
        array_agg(
            ROW(
                p."PaymentId",
                p."AppPaymentId",
                p."CustomerId",
                p."CustomerCardId",
                p."SubMerchantId",
                m."TradingName",
                p."OrderNumber",
                p."CurrencyCode",
                p."RequestedAmount",
                p."CreatedAt",
                ARRAY(
                    SELECT ROW(
                        e."PaymentEventId", e."PaymentId", e."Source", e."Payload",
                        e."HttpStatusCode", e."ReceivedAt", e."ProcessingState",
                        e."ProcessingComment", e."ProcessedAt"
                    )::public.payment_event_result
                    FROM public."PaymentEvents" AS e
                    WHERE e."PaymentId" = p."PaymentId"
                    ORDER BY e."ReceivedAt", e."PaymentEventId"
                )
            )::public.payment_history_result
            ORDER BY p."SubMerchantId", p."CurrencyCode", p."CreatedAt", p."PaymentId"
        ),
        ARRAY[]::public.payment_history_result[]
    )
    INTO p_result_histories
    FROM public."Payments" AS p
    JOIN public."SubMerchants" AS m ON m."SubMerchantId" = p."SubMerchantId"
    WHERE p."CreatedAt" < p_created_before;
END;
$procedure$;
