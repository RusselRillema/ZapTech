-- Synthetic reporting data for 2026-11-04 in Africa/Johannesburg (UTC+02:00).
-- Requires the existing schema and 002_SeedSampleData.sql reference records.
-- Included by setup.sql; for an existing database use psql --single-transaction.
-- These are literal demonstration snapshots and Acme receipts, not processing rules.
-- Only newly inserted payments receive events, preserving existing payment histories
-- on reruns. Both inserts are one statement, so payments cannot be seeded without
-- their receipts. Existing business calculations remain in C#.
\set ON_ERROR_STOP on

WITH inserted_payments AS
(
    INSERT INTO public."Payments"
        ("PaymentId", "AppPaymentId", "CustomerId", "CustomerCardId", "SubMerchantId",
         "OrderNumber", "CurrencyCode", "RequestedAmount", "ApprovedAmount",
         "RemainingAmount", "State", "CreatedAt")
    VALUES
        -- COPPER KETTLE: prior-day approval, two report-day refunds, decline,
        -- unresolved submission and a later refund that changes the current balance.
        ('a6c11004-0000-4000-8000-000000000001', 'seed-report-20261104-01',
         '860e4537-28e4-4b83-a399-d4bd13f63a26', '8ee3f008-84f5-4152-80a5-21821abdd045',
         'ZAP9193145856', 'SEED26110401', 'ZAR', 100000, 100000, 70000, 'PartiallyRefunded', '2026-11-03T16:00:00+02:00'),
        ('a6c11004-0000-4000-8000-000000000002', 'seed-report-20261104-02',
         'a6aa4dfd-4af5-4a3f-9905-ef3ee36b1e0e', '177f7b4d-27c8-4411-bf18-4e1d5642fae9',
         'ZAP9193145856', 'SEED26110402', 'ZAR', 25000, 25000, 25000, 'Approved', '2026-11-04T08:00:00+02:00'),
        ('a6c11004-0000-4000-8000-000000000003', 'seed-report-20261104-03',
         '5ea13584-8c4b-45e0-899d-03d77f3ee8b6', '15c5afcd-5d2e-4dfc-9749-4e59f5bfa091',
         'ZAP9193145856', 'SEED26110403', 'ZAR', 40000, 40000, 30000, 'PartiallyRefunded', '2026-11-04T09:00:00+02:00'),
        ('a6c11004-0000-4000-8000-000000000004', 'seed-report-20261104-04',
         'd36e086f-cf12-4bbe-ae25-3a8394c92092', 'b01aa340-344c-4e37-bacf-d3988e6f3c19',
         'ZAP9193145856', 'SEED26110404', 'ZAR', 15000, NULL, NULL, 'Declined', '2026-11-04T11:00:00+02:00'),
        ('a6c11004-0000-4000-8000-000000000005', 'seed-report-20261104-05',
         '05abb53e-f113-4672-818a-1e3f7f813c7d', '205b6e95-51ac-45ff-8c00-7f443e0fc7e4',
         'ZAP9193145856', 'SEED26110405', 'ZAR', 12000, NULL, NULL, 'Unresolved', '2026-11-04T14:00:00+02:00'),

        -- CEDAR TABLE: prior-day payment reversed on the report date, full refund,
        -- partial authorisation, bank decline and gateway rejection.
        ('a6c11004-0000-4000-8000-000000000006', 'seed-report-20261104-06',
         '860e4537-28e4-4b83-a399-d4bd13f63a26', '8ee3f008-84f5-4152-80a5-21821abdd045',
         'ZAP5556953435', 'SEED26110406', 'ZAR', 50000, 50000, 0, 'Reversed', '2026-11-03T17:00:00+02:00'),
        ('a6c11004-0000-4000-8000-000000000007', 'seed-report-20261104-07',
         'a6aa4dfd-4af5-4a3f-9905-ef3ee36b1e0e', '177f7b4d-27c8-4411-bf18-4e1d5642fae9',
         'ZAP5556953435', 'SEED26110407', 'ZAR', 18000, 18000, 0, 'Refunded', '2026-11-04T08:15:00+02:00'),
        ('a6c11004-0000-4000-8000-000000000008', 'seed-report-20261104-08',
         '5ea13584-8c4b-45e0-899d-03d77f3ee8b6', '15c5afcd-5d2e-4dfc-9749-4e59f5bfa091',
         'ZAP5556953435', 'SEED26110408', 'ZAR', 65000, 60000, 60000, 'Approved', '2026-11-04T12:00:00+02:00'),
        ('a6c11004-0000-4000-8000-000000000009', 'seed-report-20261104-09',
         'd36e086f-cf12-4bbe-ae25-3a8394c92092', 'b01aa340-344c-4e37-bacf-d3988e6f3c19',
         'ZAP5556953435', 'SEED26110409', 'ZAR', 21000, NULL, NULL, 'Declined', '2026-11-04T16:00:00+02:00'),
        ('a6c11004-0000-4000-8000-000000000010', 'seed-report-20261104-10',
         '05abb53e-f113-4672-818a-1e3f7f813c7d', '205b6e95-51ac-45ff-8c00-7f443e0fc7e4',
         'ZAP5556953435', 'SEED26110410', 'ZAR', 9000, NULL, NULL, 'Failed', '2026-11-04T18:00:00+02:00'),

        -- MORNING FIG: opening balance, two partial refunds, unresolved submission
        -- and an approval just after midnight on the following day.
        ('a6c11004-0000-4000-8000-000000000011', 'seed-report-20261104-11',
         '860e4537-28e4-4b83-a399-d4bd13f63a26', '8ee3f008-84f5-4152-80a5-21821abdd045',
         'ZAP5705721897', 'SEED26110411', 'ZAR', 32000, 32000, 32000, 'Approved', '2026-11-03T15:00:00+02:00'),
        ('a6c11004-0000-4000-8000-000000000012', 'seed-report-20261104-12',
         'a6aa4dfd-4af5-4a3f-9905-ef3ee36b1e0e', '177f7b4d-27c8-4411-bf18-4e1d5642fae9',
         'ZAP5705721897', 'SEED26110412', 'ZAR', 7500, 7500, 7500, 'Approved', '2026-11-04T07:00:00+02:00'),
        ('a6c11004-0000-4000-8000-000000000013', 'seed-report-20261104-13',
         '5ea13584-8c4b-45e0-899d-03d77f3ee8b6', '15c5afcd-5d2e-4dfc-9749-4e59f5bfa091',
         'ZAP5705721897', 'SEED26110413', 'ZAR', 50000, 50000, 25000, 'PartiallyRefunded', '2026-11-04T13:00:00+02:00'),
        ('a6c11004-0000-4000-8000-000000000014', 'seed-report-20261104-14',
         'd36e086f-cf12-4bbe-ae25-3a8394c92092', 'b01aa340-344c-4e37-bacf-d3988e6f3c19',
         'ZAP5705721897', 'SEED26110414', 'ZAR', 22000, NULL, NULL, 'Unresolved', '2026-11-04T19:00:00+02:00'),
        ('a6c11004-0000-4000-8000-000000000015', 'seed-report-20261104-15',
         '05abb53e-f113-4672-818a-1e3f7f813c7d', '205b6e95-51ac-45ff-8c00-7f443e0fc7e4',
         'ZAP5705721897', 'SEED26110415', 'ZAR', 30000, 30000, 30000, 'Approved', '2026-11-04T23:59:00+02:00')
    ON CONFLICT ("PaymentId") DO NOTHING
    RETURNING "PaymentId"
),
sample_events (event_id, payment_id, source, payload, http_status_code, received_at) AS
(
    VALUES
        ('a6c11004-0001-4000-8000-000000000001'::uuid, 'a6c11004-0000-4000-8000-000000000001'::uuid, 'PaymentResponse',
         '{"MessageHeader":{"MessageDateTime":"2026-11-03T16:00:01.000","MessageID":"a6c11004-0002-4000-8000-000000000001","Channel":"App"},"PaymentResponse":{"TransactionID":"a6c11004-0000-4000-8000-000000000001","OrderNumber":"SEED26110401","TransactionDateTime":"2026-11-03T16:00:01.000","RetrievalReferenceNumber":"SEED26110401","ReconID":"SEEDRECON01","AuthCode":"100001","TransactionStatus":"Success","ResponseDetail":{"Source":"Acquirer","Code":"00","Description":"Approved","ClientMessage":"Approved."},"AuthAmount":100000}}'::jsonb,
         200::smallint, '2026-11-03T16:00:01+02:00'::timestamptz),
        ('a6c11004-0001-4000-8000-000000000002', 'a6c11004-0000-4000-8000-000000000002', 'PaymentResponse',
         '{"MessageHeader":{"MessageDateTime":"2026-11-04T08:00:01.000","MessageID":"a6c11004-0002-4000-8000-000000000002","Channel":"App"},"PaymentResponse":{"TransactionID":"a6c11004-0000-4000-8000-000000000002","OrderNumber":"SEED26110402","TransactionDateTime":"2026-11-04T08:00:01.000","RetrievalReferenceNumber":"SEED26110402","ReconID":"SEEDRECON02","AuthCode":"100002","TransactionStatus":"Success","ResponseDetail":{"Source":"Acquirer","Code":"00","Description":"Approved","ClientMessage":"Approved."},"AuthAmount":25000}}',
         200, '2026-11-04T08:00:01+02:00'),
        ('a6c11004-0001-4000-8000-000000000003', 'a6c11004-0000-4000-8000-000000000003', 'PaymentResponse',
         '{"MessageHeader":{"MessageDateTime":"2026-11-04T09:00:01.000","MessageID":"a6c11004-0002-4000-8000-000000000003","Channel":"App"},"PaymentResponse":{"TransactionID":"a6c11004-0000-4000-8000-000000000003","OrderNumber":"SEED26110403","TransactionDateTime":"2026-11-04T09:00:01.000","RetrievalReferenceNumber":"SEED26110403","ReconID":"SEEDRECON03","AuthCode":"100003","TransactionStatus":"Success","ResponseDetail":{"Source":"Acquirer","Code":"00","Description":"Approved","ClientMessage":"Approved."},"AuthAmount":40000}}',
         200, '2026-11-04T09:00:01+02:00'),
        ('a6c11004-0001-4000-8000-000000000004', 'a6c11004-0000-4000-8000-000000000004', 'PaymentResponse',
         '{"MessageHeader":{"MessageDateTime":"2026-11-04T11:00:01.000","MessageID":"a6c11004-0002-4000-8000-000000000004","Channel":"App"},"PaymentResponse":{"TransactionID":"a6c11004-0000-4000-8000-000000000004","OrderNumber":"SEED26110404","TransactionDateTime":"2026-11-04T11:00:01.000","RetrievalReferenceNumber":"SEED26110404","ReconID":"SEEDRECON04","AuthCode":"","TransactionStatus":"Success","ResponseDetail":{"Source":"Acquirer","Code":"05","Description":"Do not honour","ClientMessage":"Your bank declined this payment."}}}',
         200, '2026-11-04T11:00:01+02:00'),
        ('a6c11004-0001-4000-8000-000000000006', 'a6c11004-0000-4000-8000-000000000006', 'PaymentResponse',
         '{"MessageHeader":{"MessageDateTime":"2026-11-03T17:00:01.000","MessageID":"a6c11004-0002-4000-8000-000000000006","Channel":"App"},"PaymentResponse":{"TransactionID":"a6c11004-0000-4000-8000-000000000006","OrderNumber":"SEED26110406","TransactionDateTime":"2026-11-03T17:00:01.000","RetrievalReferenceNumber":"SEED26110406","ReconID":"SEEDRECON06","AuthCode":"100006","TransactionStatus":"Success","ResponseDetail":{"Source":"Acquirer","Code":"00","Description":"Approved","ClientMessage":"Approved."},"AuthAmount":50000}}',
         200, '2026-11-03T17:00:01+02:00'),
        ('a6c11004-0001-4000-8000-000000000007', 'a6c11004-0000-4000-8000-000000000007', 'PaymentResponse',
         '{"MessageHeader":{"MessageDateTime":"2026-11-04T08:15:01.000","MessageID":"a6c11004-0002-4000-8000-000000000007","Channel":"App"},"PaymentResponse":{"TransactionID":"a6c11004-0000-4000-8000-000000000007","OrderNumber":"SEED26110407","TransactionDateTime":"2026-11-04T08:15:01.000","RetrievalReferenceNumber":"SEED26110407","ReconID":"SEEDRECON07","AuthCode":"100007","TransactionStatus":"Success","ResponseDetail":{"Source":"Acquirer","Code":"00","Description":"Approved","ClientMessage":"Approved."},"AuthAmount":18000}}',
         200, '2026-11-04T08:15:01+02:00'),
        ('a6c11004-0001-4000-8000-000000000008', 'a6c11004-0000-4000-8000-000000000008', 'PaymentResponse',
         '{"MessageHeader":{"MessageDateTime":"2026-11-04T12:00:01.000","MessageID":"a6c11004-0002-4000-8000-000000000008","Channel":"App"},"PaymentResponse":{"TransactionID":"a6c11004-0000-4000-8000-000000000008","OrderNumber":"SEED26110408","TransactionDateTime":"2026-11-04T12:00:01.000","RetrievalReferenceNumber":"SEED26110408","ReconID":"SEEDRECON08","AuthCode":"100008","TransactionStatus":"Success","ResponseDetail":{"Source":"Acquirer","Code":"00","Description":"Approved","ClientMessage":"Approved."},"AuthAmount":60000}}',
         200, '2026-11-04T12:00:01+02:00'),
        ('a6c11004-0001-4000-8000-000000000009', 'a6c11004-0000-4000-8000-000000000009', 'PaymentResponse',
         '{"MessageHeader":{"MessageDateTime":"2026-11-04T16:00:01.000","MessageID":"a6c11004-0002-4000-8000-000000000009","Channel":"App"},"PaymentResponse":{"TransactionID":"a6c11004-0000-4000-8000-000000000009","OrderNumber":"SEED26110409","TransactionDateTime":"2026-11-04T16:00:01.000","RetrievalReferenceNumber":"SEED26110409","ReconID":"SEEDRECON09","AuthCode":"","TransactionStatus":"Success","ResponseDetail":{"Source":"Acquirer","Code":"51","Description":"Insufficient funds","ClientMessage":"There was not enough money in the account."}}}',
         200, '2026-11-04T16:00:01+02:00'),
        ('a6c11004-0001-4000-8000-000000000010', 'a6c11004-0000-4000-8000-000000000010', 'PaymentResponse',
         '{"MessageHeader":{"MessageDateTime":"2026-11-04T18:00:01.000","MessageID":"a6c11004-0002-4000-8000-000000000010","Channel":"App"},"PaymentResponse":{"TransactionID":"a6c11004-0000-4000-8000-000000000010","OrderNumber":"SEED26110410","TransactionStatus":"Failure","ResponseDetail":{"Source":"Application","Code":"E01","Description":"Required field is missing","ClientMessage":"We could not process that payment."}}}',
         400, '2026-11-04T18:00:01+02:00'),
        ('a6c11004-0001-4000-8000-000000000011', 'a6c11004-0000-4000-8000-000000000011', 'PaymentResponse',
         '{"MessageHeader":{"MessageDateTime":"2026-11-03T15:00:01.000","MessageID":"a6c11004-0002-4000-8000-000000000011","Channel":"App"},"PaymentResponse":{"TransactionID":"a6c11004-0000-4000-8000-000000000011","OrderNumber":"SEED26110411","TransactionDateTime":"2026-11-03T15:00:01.000","RetrievalReferenceNumber":"SEED26110411","ReconID":"SEEDRECON11","AuthCode":"100011","TransactionStatus":"Success","ResponseDetail":{"Source":"Acquirer","Code":"00","Description":"Approved","ClientMessage":"Approved."},"AuthAmount":32000}}',
         200, '2026-11-03T15:00:01+02:00'),
        ('a6c11004-0001-4000-8000-000000000012', 'a6c11004-0000-4000-8000-000000000012', 'PaymentResponse',
         '{"MessageHeader":{"MessageDateTime":"2026-11-04T07:00:01.000","MessageID":"a6c11004-0002-4000-8000-000000000012","Channel":"App"},"PaymentResponse":{"TransactionID":"a6c11004-0000-4000-8000-000000000012","OrderNumber":"SEED26110412","TransactionDateTime":"2026-11-04T07:00:01.000","RetrievalReferenceNumber":"SEED26110412","ReconID":"SEEDRECON12","AuthCode":"100012","TransactionStatus":"Success","ResponseDetail":{"Source":"Acquirer","Code":"00","Description":"Approved","ClientMessage":"Approved."},"AuthAmount":7500}}',
         200, '2026-11-04T07:00:01+02:00'),
        ('a6c11004-0001-4000-8000-000000000013', 'a6c11004-0000-4000-8000-000000000013', 'PaymentResponse',
         '{"MessageHeader":{"MessageDateTime":"2026-11-04T13:00:01.000","MessageID":"a6c11004-0002-4000-8000-000000000013","Channel":"App"},"PaymentResponse":{"TransactionID":"a6c11004-0000-4000-8000-000000000013","OrderNumber":"SEED26110413","TransactionDateTime":"2026-11-04T13:00:01.000","RetrievalReferenceNumber":"SEED26110413","ReconID":"SEEDRECON13","AuthCode":"100013","TransactionStatus":"Success","ResponseDetail":{"Source":"Acquirer","Code":"00","Description":"Approved","ClientMessage":"Approved."},"AuthAmount":50000}}',
         200, '2026-11-04T13:00:01+02:00'),
        ('a6c11004-0001-4000-8000-000000000015', 'a6c11004-0000-4000-8000-000000000015', 'PaymentResponse',
         '{"MessageHeader":{"MessageDateTime":"2026-11-05T00:01:00.000","MessageID":"a6c11004-0002-4000-8000-000000000015","Channel":"App"},"PaymentResponse":{"TransactionID":"a6c11004-0000-4000-8000-000000000015","OrderNumber":"SEED26110415","TransactionDateTime":"2026-11-05T00:01:00.000","RetrievalReferenceNumber":"SEED26110415","ReconID":"SEEDRECON15","AuthCode":"100015","TransactionStatus":"Success","ResponseDetail":{"Source":"Acquirer","Code":"00","Description":"Approved","ClientMessage":"Approved."},"AuthAmount":30000}}',
         200, '2026-11-05T00:01:00+02:00'),

        ('a6c11004-0001-4000-8000-000000000101', 'a6c11004-0000-4000-8000-000000000001', 'TransactionStatus',
         '{"transactionUUID":"a6c11004-0003-4000-8000-000000000101","transactionType":"Refund","transactionDateTime":"2026-11-04T10:30:00.000","rrn":"SEED26110401","transactionAmount":20000,"currencyCode":"ZAR","txResultCode":"00","isApproved":true}',
         NULL, '2026-11-04T10:30:00+02:00'),
        ('a6c11004-0001-4000-8000-000000000102', 'a6c11004-0000-4000-8000-000000000001', 'TransactionStatus',
         '{"transactionUUID":"a6c11004-0003-4000-8000-000000000102","transactionType":"Refund","transactionDateTime":"2026-11-05T09:00:00.000","rrn":"SEED26110401","transactionAmount":10000,"currencyCode":"ZAR","txResultCode":"00","isApproved":true}',
         NULL, '2026-11-05T09:00:00+02:00'),
        ('a6c11004-0001-4000-8000-000000000103', 'a6c11004-0000-4000-8000-000000000003', 'TransactionStatus',
         '{"transactionUUID":"a6c11004-0003-4000-8000-000000000103","transactionType":"Refund","transactionDateTime":"2026-11-04T15:00:00.000","rrn":"SEED26110403","transactionAmount":10000,"currencyCode":"ZAR","txResultCode":"00","isApproved":true}',
         NULL, '2026-11-04T15:00:00+02:00'),
        ('a6c11004-0001-4000-8000-000000000106', 'a6c11004-0000-4000-8000-000000000006', 'TransactionStatus',
         '{"transactionUUID":"a6c11004-0003-4000-8000-000000000106","transactionType":"Reversal","transactionDateTime":"2026-11-04T08:30:00.000","rrn":"SEED26110406","transactionAmount":50000,"currencyCode":"ZAR","txResultCode":"00","isApproved":true}',
         NULL, '2026-11-04T08:30:00+02:00'),
        ('a6c11004-0001-4000-8000-000000000107', 'a6c11004-0000-4000-8000-000000000007', 'TransactionStatus',
         '{"transactionUUID":"a6c11004-0003-4000-8000-000000000107","transactionType":"Refund","transactionDateTime":"2026-11-04T14:00:00.000","rrn":"SEED26110407","transactionAmount":18000,"currencyCode":"ZAR","txResultCode":"00","isApproved":true}',
         NULL, '2026-11-04T14:00:00+02:00'),
        ('a6c11004-0001-4000-8000-000000000113', 'a6c11004-0000-4000-8000-000000000013', 'TransactionStatus',
         '{"transactionUUID":"a6c11004-0003-4000-8000-000000000113","transactionType":"Refund","transactionDateTime":"2026-11-04T16:00:00.000","rrn":"SEED26110413","transactionAmount":10000,"currencyCode":"ZAR","txResultCode":"00","isApproved":true}',
         NULL, '2026-11-04T16:00:00+02:00'),
        ('a6c11004-0001-4000-8000-000000000114', 'a6c11004-0000-4000-8000-000000000013', 'TransactionStatus',
         '{"transactionUUID":"a6c11004-0003-4000-8000-000000000114","transactionType":"Refund","transactionDateTime":"2026-11-04T17:00:00.000","rrn":"SEED26110413","transactionAmount":15000,"currencyCode":"ZAR","txResultCode":"00","isApproved":true}',
         NULL, '2026-11-04T17:00:00+02:00')
)
INSERT INTO public."PaymentEvents"
    ("PaymentEventId", "PaymentId", "Source", "Payload", "HttpStatusCode",
     "ReceivedAt", "ProcessingState", "ProcessingComment", "ProcessedAt")
SELECT e.event_id, e.payment_id, e.source, e.payload, e.http_status_code::smallint,
       e.received_at, 'Applied', NULL, e.received_at
FROM sample_events AS e
JOIN inserted_payments AS p ON p."PaymentId" = e.payment_id;
