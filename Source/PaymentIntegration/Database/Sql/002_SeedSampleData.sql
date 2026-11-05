-- PostgreSQL. Included by setup.sql after 001_CreateTables.sql for local demonstration data.
-- The caller owns the transaction. For standalone deployment, use psql --single-transaction.
-- All records are synthetic. Card tokens are examples for the Acme stub, not live tokens.
-- IDs and tokens were randomly generated once and are fixed so this script is repeatable.
-- Existing rows with these primary keys are preserved; other constraint violations fail the transaction.
\set ON_ERROR_STOP on

INSERT INTO public."Customers" ("CustomerId", "PreferredName", "Email")
VALUES
    ('860e4537-28e4-4b83-a399-d4bd13f63a26', 'Amina', 'amina.patel@example.com'),
    ('a6aa4dfd-4af5-4a3f-9905-ef3ee36b1e0e', 'Thandi', 'thandi.dlamini@example.com'),
    ('5ea13584-8c4b-45e0-899d-03d77f3ee8b6', 'Liam', 'liam.jacobs@example.com'),
    ('d36e086f-cf12-4bbe-ae25-3a8394c92092', 'Sipho', 'sipho.nkosi@example.com'),
    ('05abb53e-f113-4672-818a-1e3f7f813c7d', 'Maya', 'maya.naidoo@example.com')
ON CONFLICT ("CustomerId") DO NOTHING;

-- Fictional dining merchants use the documentation's example MCC (5812) and WC / ZA codes.
-- Each name fits the 23-character merchant section with the example ZAP* prefix;
-- each city fits the 13-character city section of CardAcceptorName.
INSERT INTO public."SubMerchants"
    ("SubMerchantId", "TradingName", "City", "RegionCode", "CountryCode", "MerchantCategoryCode")
VALUES
    ('ZAP9193145856', 'COPPER KETTLE', 'CAPE TOWN', 'WC', 'ZA', '5812'),
    ('ZAP5556953435', 'CEDAR TABLE', 'PAARL', 'WC', 'ZA', '5812'),
    ('ZAP5705721897', 'MORNING FIG', 'STELLENBOSCH', 'WC', 'ZA', '5812')
ON CONFLICT ("SubMerchantId") DO NOTHING;

-- Friendly names repeat across customers, but never within the same customer.
INSERT INTO public."CustomerCards"
    ("CustomerCardId", "CustomerId", "AcmeCardToken", "FriendlyName")
VALUES
    ('8ee3f008-84f5-4152-80a5-21821abdd045', '860e4537-28e4-4b83-a399-d4bd13f63a26', '6c96d4589480d19d', 'Everyday'),
    ('86d37e0e-ad75-465a-9c08-320de8213c1a', '860e4537-28e4-4b83-a399-d4bd13f63a26', '59d9e65cb40e3c80', 'Travel'),
    ('177f7b4d-27c8-4411-bf18-4e1d5642fae9', 'a6aa4dfd-4af5-4a3f-9905-ef3ee36b1e0e', 'fcd48ee9a37ccfcf', 'Everyday'),
    ('3c0aa049-6ca5-401a-88f7-f5d63edfecd2', 'a6aa4dfd-4af5-4a3f-9905-ef3ee36b1e0e', '0c10f3a3279d1c16', 'Travel'),
    ('15c5afcd-5d2e-4dfc-9749-4e59f5bfa091', '5ea13584-8c4b-45e0-899d-03d77f3ee8b6', '8eea3baf9ce50b1b', 'Everyday'),
    ('75aabf2d-0aa0-4159-bad8-39f924f59507', '5ea13584-8c4b-45e0-899d-03d77f3ee8b6', '69392b3706a8e06e', 'Travel'),
    ('b01aa340-344c-4e37-bacf-d3988e6f3c19', 'd36e086f-cf12-4bbe-ae25-3a8394c92092', '824b74a905f67715', 'Everyday'),
    ('205b6e95-51ac-45ff-8c00-7f443e0fc7e4', '05abb53e-f113-4672-818a-1e3f7f813c7d', 'f879c9ccc33b6c94', 'Everyday')
ON CONFLICT ("CustomerCardId") DO NOTHING;


-- Fixed reporting examples for 4 November 2026 (SAST): four payments per currency.
-- Each currency has one approval, one decline, one full refund and one full reversal.
-- These are predefined synthetic snapshots and receipts, not payment calculations.
-- Insert receipts only for payments created by this statement. Reruns leave existing
-- payments and their audit history untouched; any other identifier collision fails.
WITH sample_payments
    (payment_id, app_payment_id, sub_merchant_id, order_number, currency_code,
     requested_amount, approved_amount, remaining_amount, state) AS
(
    VALUES
        ('27a42100-5e88-427b-9b87-38fdb2065a0b', 'seed-report-zar-approved', 'ZAP5556953435', 'SEEDZAR00001', 'ZAR', 113000, 113000, 113000, 'Approved'),
        ('819cda28-b4a4-4752-a702-a5eb5a8e111b', 'seed-report-zar-declined', 'ZAP5705721897', 'SEEDZAR00002', 'ZAR', 21000, NULL, NULL, 'Declined'),
        ('fdcbfc58-c95a-4d90-b178-d0a162f95353', 'seed-report-zar-refunded', 'ZAP9193145856', 'SEEDZAR00003', 'ZAR', 73000, 73000, 0, 'Refunded'),
        ('9967e244-08f8-49b0-9bce-71fe643f41d6', 'seed-report-zar-reversed', 'ZAP5705721897', 'SEEDZAR00004', 'ZAR', 50000, 50000, 0, 'Reversed'),
        ('8e103ea5-db05-4b5e-a76a-cefac325e3d9', 'seed-report-usd-approved', 'ZAP5556953435', 'SEEDUSD00001', 'USD', 113000, 113000, 113000, 'Approved'),
        ('35c139e0-ecb2-4bc6-a568-6ece49ec9461', 'seed-report-usd-declined', 'ZAP5705721897', 'SEEDUSD00002', 'USD', 21000, NULL, NULL, 'Declined'),
        ('edf2fafc-48e3-4857-8921-9ea4b060dd1f', 'seed-report-usd-refunded', 'ZAP9193145856', 'SEEDUSD00003', 'USD', 73000, 73000, 0, 'Refunded'),
        ('8d62aba8-baf9-4965-a966-d8d149ffd941', 'seed-report-usd-reversed', 'ZAP5705721897', 'SEEDUSD00004', 'USD', 50000, 50000, 0, 'Reversed'),
        ('72f6e6ca-337e-45a7-bac8-cbf95d22c472', 'seed-report-eur-approved', 'ZAP5556953435', 'SEEDEUR00001', 'EUR', 113000, 113000, 113000, 'Approved'),
        ('feaa7307-c468-4a57-9112-ba5a797f86b0', 'seed-report-eur-declined', 'ZAP5705721897', 'SEEDEUR00002', 'EUR', 21000, NULL, NULL, 'Declined'),
        ('3054e890-2769-4741-92b8-ef7f2d14a3c0', 'seed-report-eur-refunded', 'ZAP9193145856', 'SEEDEUR00003', 'EUR', 73000, 73000, 0, 'Refunded'),
        ('d2353baa-0c46-40cf-81c7-af0e54c1fb09', 'seed-report-eur-reversed', 'ZAP5705721897', 'SEEDEUR00004', 'EUR', 50000, 50000, 0, 'Reversed'),
        ('e07013b8-692d-4e30-940b-218a5095d055', 'seed-report-gbp-approved', 'ZAP5556953435', 'SEEDGBP00001', 'GBP', 113000, 113000, 113000, 'Approved'),
        ('5d24a612-2606-44fa-beff-3ae84453bc1a', 'seed-report-gbp-declined', 'ZAP5705721897', 'SEEDGBP00002', 'GBP', 21000, NULL, NULL, 'Declined'),
        ('9bcd20e8-c7bd-4ef5-9321-c08b8ff900f3', 'seed-report-gbp-refunded', 'ZAP9193145856', 'SEEDGBP00003', 'GBP', 73000, 73000, 0, 'Refunded'),
        ('8bf40e35-d485-4b6c-9f30-c06b25e5e1f0', 'seed-report-gbp-reversed', 'ZAP5705721897', 'SEEDGBP00004', 'GBP', 50000, 50000, 0, 'Reversed')
),
sample_responses
    (event_id, payment_id, message_id, response_code, auth_amount, auth_code) AS
(
    VALUES
        ('d0662c38-d621-4dee-b17c-b8cafc90ef1b', '27a42100-5e88-427b-9b87-38fdb2065a0b', '8336cabc-547c-4b4e-b512-5597d89b6471', '00', 113000, '238699'),
        ('f32274de-a1e7-4070-b90c-68dfad137e74', '819cda28-b4a4-4752-a702-a5eb5a8e111b', '1306ee5a-ffed-4889-9a90-07a131e02c13', '05', NULL, NULL),
        ('5ada1a1f-c47b-4b9a-adc9-d7c510f77dfe', 'fdcbfc58-c95a-4d90-b178-d0a162f95353', 'c6e2e78e-1d02-472f-9f21-01006c8b6e63', '00', 73000, '238699'),
        ('2afebd43-22ad-419d-bbb8-70db02f88fc9', '9967e244-08f8-49b0-9bce-71fe643f41d6', 'b5909174-7201-4b90-943e-d626ec72918d', '00', 50000, '238699'),
        ('43c36438-2017-48bc-8a2a-12459482399a', '8e103ea5-db05-4b5e-a76a-cefac325e3d9', 'eb1715bf-4b62-490d-8912-abce337118ee', '00', 113000, '238699'),
        ('a810b014-5655-4b60-aefc-3b88747b3133', '35c139e0-ecb2-4bc6-a568-6ece49ec9461', 'acb5b56a-3de3-43ba-994b-c69b93e02db5', '05', NULL, NULL),
        ('82121a7e-14e7-498f-b442-f61b7653525b', 'edf2fafc-48e3-4857-8921-9ea4b060dd1f', 'c9422177-6636-42d9-81e3-220d5c6343a7', '00', 73000, '238699'),
        ('6d923691-b514-4bb8-b995-107fcf8b10c7', '8d62aba8-baf9-4965-a966-d8d149ffd941', '093e1f5d-0a76-49db-9539-cc44dc96786a', '00', 50000, '238699'),
        ('40b8cf47-ddad-45e5-b553-8311ef98733e', '72f6e6ca-337e-45a7-bac8-cbf95d22c472', 'e322d328-70e3-4c1d-a3df-a55fa05db1f7', '00', 113000, '238699'),
        ('f7cb8cdb-fcdc-4a65-8aa1-2fdd4dcfaf6b', 'feaa7307-c468-4a57-9112-ba5a797f86b0', '36d7f3bc-160a-45ba-9895-8f8f4c132dfd', '05', NULL, NULL),
        ('7f483266-c14c-4a9e-9a5b-e79d87ccd22a', '3054e890-2769-4741-92b8-ef7f2d14a3c0', '8bca9926-ca42-4a35-8666-bf38ddc0b093', '00', 73000, '238699'),
        ('ecf5c1ae-7b3a-4401-976f-1af544232fb8', 'd2353baa-0c46-40cf-81c7-af0e54c1fb09', '5af5575d-44a0-4e96-8e5f-31282585abb6', '00', 50000, '238699'),
        ('50efaad1-b7fd-4e75-bed5-6a5bcf596afd', 'e07013b8-692d-4e30-940b-218a5095d055', '04f8122c-c094-467a-905d-e93c0ea890ea', '00', 113000, '238699'),
        ('f9104fb5-09f8-468e-9278-43e7726d78e0', '5d24a612-2606-44fa-beff-3ae84453bc1a', '4a4f9411-a181-4161-aa29-9d16d1bf42d5', '05', NULL, NULL),
        ('4a738346-ceea-4d3e-a803-80456109df84', '9bcd20e8-c7bd-4ef5-9321-c08b8ff900f3', 'b6a3ecf9-d0f8-49d3-b2fc-97b6b0f6111c', '00', 73000, '238699'),
        ('3dc61b6f-8c5f-4cea-8a72-2d91612d7e58', '8bf40e35-d485-4b6c-9f30-c06b25e5e1f0', '1f450b7d-a791-4327-b16c-64295bee1dbe', '00', 50000, '238699')
),
sample_adjustments
    (event_id, payment_id, transaction_uuid, transaction_type, amount) AS
(
    VALUES
        ('34d63657-66f0-48db-bbbb-19961965c008', 'fdcbfc58-c95a-4d90-b178-d0a162f95353', '0b6a6519-b353-4c64-83f5-2c8b29a616f0', 'Refund', 73000),
        ('11afc344-5b3f-4c73-bbb5-2bba00a35df6', '9967e244-08f8-49b0-9bce-71fe643f41d6', '4bede77e-3f54-42a2-bdb1-39dcce0cc9d9', 'Reversal', 50000),
        ('3f438e96-8577-4aeb-b967-f307931945e8', 'edf2fafc-48e3-4857-8921-9ea4b060dd1f', 'a7d05e3c-e656-4d7c-8539-caf83172b95f', 'Refund', 73000),
        ('a24fa42c-1354-4c66-abf7-4e524ea342fa', '8d62aba8-baf9-4965-a966-d8d149ffd941', 'af1ad799-93bc-4d34-9fae-ff91bbba36c7', 'Reversal', 50000),
        ('55198251-1f51-4b54-99ae-7039022c4355', '3054e890-2769-4741-92b8-ef7f2d14a3c0', '7095a9e4-ecc5-4863-8e90-5ec791f489ce', 'Refund', 73000),
        ('f0bd80bf-a41b-48f4-b189-636c18fceeed', 'd2353baa-0c46-40cf-81c7-af0e54c1fb09', '8835551a-9b9b-46cc-b871-2f9a2a9cf5df', 'Reversal', 50000),
        ('f9a00068-1ad1-4f7c-91fe-5aa915d9e55d', '9bcd20e8-c7bd-4ef5-9321-c08b8ff900f3', 'f925f7e9-4fc3-400d-9b62-6d337013e615', 'Refund', 73000),
        ('c459aef6-dab9-4481-9893-6d31b150da45', '8bf40e35-d485-4b6c-9f30-c06b25e5e1f0', '7f717ecd-5c73-4ff5-8f72-7e550b264246', 'Reversal', 50000)
),
response_details (code, description, client_message) AS
(
    VALUES
        ('00', 'Approved or completed successfully', 'Approved.'),
        ('05', 'Do not honour', 'Your bank declined this payment.')
),
inserted_payments AS
(
    INSERT INTO public."Payments"
        ("PaymentId", "AppPaymentId", "CustomerId", "CustomerCardId", "SubMerchantId",
         "OrderNumber", "CurrencyCode", "RequestedAmount", "ApprovedAmount", "RemainingAmount",
         "State", "CreatedAt")
    SELECT s.payment_id::uuid, s.app_payment_id, '860e4537-28e4-4b83-a399-d4bd13f63a26',
        '8ee3f008-84f5-4152-80a5-21821abdd045'::uuid, s.sub_merchant_id,
        s.order_number, s.currency_code, s.requested_amount, s.approved_amount, s.remaining_amount,
        s.state, TIMESTAMPTZ '2026-11-04 09:59:00.000+02'
    FROM sample_payments AS s
    ON CONFLICT ("PaymentId") DO NOTHING
    RETURNING "PaymentId", "OrderNumber", "CurrencyCode"
)
INSERT INTO public."PaymentEvents"
    ("PaymentEventId", "PaymentId", "Source", "Payload", "HttpStatusCode",
     "ReceivedAt", "ProcessingState", "ProcessingComment", "ProcessedAt")
SELECT s.event_id::uuid, p."PaymentId", 'PaymentResponse',
    jsonb_build_object(
        'MessageHeader', jsonb_build_object(
            'MessageDateTime', '2026-11-04T10:00:00.000', 'MessageID', s.message_id, 'Channel', 'App'),
        'PaymentResponse', jsonb_strip_nulls(jsonb_build_object(
            'TransactionID', p."PaymentId"::text,
            'TransactionDateTime', '2026-11-04T10:00:00.000',
            'OrderNumber', p."OrderNumber", 'ReconID', 'RECON' || p."OrderNumber",
            'RetrievalReferenceNumber', p."OrderNumber", 'AuthCode', s.auth_code,
            'TransactionStatus', 'Success',
            'ResponseDetail', jsonb_build_object(
                'Source', 'Acquirer', 'Code', s.response_code,
                'Description', d.description, 'ClientMessage', d.client_message),
            'AuthAmount', s.auth_amount))),
    200, TIMESTAMPTZ '2026-11-04 10:00:00.000+02', 'Applied', NULL,
    TIMESTAMPTZ '2026-11-04 10:00:00.001+02'
FROM sample_responses AS s
JOIN inserted_payments AS p ON p."PaymentId" = s.payment_id::uuid
JOIN response_details AS d ON d.code = s.response_code
UNION ALL
SELECT s.event_id::uuid, p."PaymentId", 'TransactionStatus',
    jsonb_build_object(
        'transactionUUID', s.transaction_uuid, 'transactionType', s.transaction_type,
        'transactionDateTime', '2026-11-04T10:05:00.000', 'rrn', p."OrderNumber",
        'transactionAmount', s.amount, 'currencyCode', p."CurrencyCode",
        'txResultCode', '00', 'isApproved', true),
    NULL, TIMESTAMPTZ '2026-11-04 10:05:00.000+02', 'Applied', NULL,
    TIMESTAMPTZ '2026-11-04 10:05:00.001+02'
FROM sample_adjustments AS s
JOIN inserted_payments AS p ON p."PaymentId" = s.payment_id::uuid;
