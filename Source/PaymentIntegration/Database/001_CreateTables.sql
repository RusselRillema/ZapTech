-- PostgreSQL. Included by setup.sql before 002_SeedSampleData.sql.
-- The caller owns the transaction. For standalone deployment, use psql --single-transaction.
-- CustomerId and SubMerchantId are Zapper's identifiers, not additional internal IDs.
\set ON_ERROR_STOP on

CREATE TABLE public."Customers"
(
    "CustomerId" text NOT NULL,
    "PreferredName" text,
    "Email" varchar(254) NOT NULL,

    CONSTRAINT "PK_Customers" PRIMARY KEY ("CustomerId")
);

CREATE TABLE public."SubMerchants"
(
    "SubMerchantId" varchar(15) NOT NULL,
    "TradingName" text NOT NULL,
    "City" text NOT NULL,
    "RegionCode" text NOT NULL,
    "CountryCode" varchar(2) NOT NULL,
    "MerchantCategoryCode" varchar(4) NOT NULL,

    CONSTRAINT "PK_SubMerchants" PRIMARY KEY ("SubMerchantId"),
    CONSTRAINT "CK_SubMerchants_CountryCode_Length"
        CHECK (char_length("CountryCode") = 2)
);

-- Acme's descriptor uses the first two region-code characters and a two-character country code.
-- Keep full merchant names and cities; the integration will format CardAcceptorName.

CREATE TABLE public."CustomerCards"
(
    "CustomerCardId" uuid NOT NULL,
    "CustomerId" text NOT NULL,
    "AcmeCardToken" text NOT NULL,
    "FriendlyName" text NOT NULL,

    CONSTRAINT "PK_CustomerCards" PRIMARY KEY ("CustomerCardId"),
    CONSTRAINT "FK_CustomerCards_Customers"
        FOREIGN KEY ("CustomerId") REFERENCES public."Customers" ("CustomerId"),
    CONSTRAINT "UQ_CustomerCards_CustomerId_FriendlyName"
        UNIQUE ("CustomerId", "FriendlyName")
);
