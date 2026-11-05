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
