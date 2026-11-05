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
    CONSTRAINT "UQ_CustomerCards_CustomerCardId_CustomerId"
        UNIQUE ("CustomerCardId", "CustomerId"),
    CONSTRAINT "FK_CustomerCards_Customers"
        FOREIGN KEY ("CustomerId") REFERENCES public."Customers" ("CustomerId"),
    CONSTRAINT "UQ_CustomerCards_CustomerId_FriendlyName"
        UNIQUE ("CustomerId", "FriendlyName")
);

-- A payment is persisted before calling Acme. The application also sends PaymentId
-- as TransactionID; OrderNumber is generated locally before that call.
-- Amounts are whole lowest monetary units. Unknown amounts remain NULL, not zero.
CREATE TABLE public."Payments"
(
    "PaymentId" uuid NOT NULL,
    "AppPaymentId" text NOT NULL,
    "CustomerId" text NOT NULL,
    "CustomerCardId" uuid NOT NULL,
    "SubMerchantId" varchar(15) NOT NULL,
    "OrderNumber" varchar(50) NOT NULL,
    "CurrencyCode" varchar(3) NOT NULL,
    "RequestedAmount" bigint NOT NULL,
    "ApprovedAmount" bigint,
    "RemainingAmount" bigint,
    "State" text NOT NULL,
    "CreatedAt" timestamptz(3) NOT NULL,

    CONSTRAINT "PK_Payments" PRIMARY KEY ("PaymentId"),
    CONSTRAINT "UQ_Payments_AppPaymentId" UNIQUE ("AppPaymentId"),
    CONSTRAINT "FK_Payments_Customers"
        FOREIGN KEY ("CustomerId") REFERENCES public."Customers" ("CustomerId"),
    CONSTRAINT "FK_Payments_CustomerCards"
        FOREIGN KEY ("CustomerCardId", "CustomerId")
        REFERENCES public."CustomerCards" ("CustomerCardId", "CustomerId"),
    CONSTRAINT "FK_Payments_SubMerchants"
        FOREIGN KEY ("SubMerchantId") REFERENCES public."SubMerchants" ("SubMerchantId"),
    CONSTRAINT "CK_Payments_OrderNumber_Alphanumeric"
        CHECK ("OrderNumber" ~ '^[A-Za-z0-9]+$'),
    CONSTRAINT "CK_Payments_CurrencyCode_Format"
        CHECK ("CurrencyCode" ~ '^[A-Z]{3}$'),
    CONSTRAINT "CK_Payments_RequestedAmount_Nonnegative"
        CHECK ("RequestedAmount" >= 0),
    CONSTRAINT "CK_Payments_ApprovedAmount_Nonnegative"
        CHECK ("ApprovedAmount" >= 0),
    CONSTRAINT "CK_Payments_RemainingAmount_Range"
        CHECK ("RemainingAmount" IS NULL OR
            ("ApprovedAmount" IS NOT NULL AND "RemainingAmount" BETWEEN 0 AND "ApprovedAmount")),
    CONSTRAINT "CK_Payments_State_NotBlank"
        CHECK (btrim("State") <> '')
);

-- Acme requires the first 12 order-reference characters to be unique across the
-- configured account. Derive the RRN instead of storing a second copy.
CREATE UNIQUE INDEX "UQ_Payments_RetrievalReferenceNumber"
    ON public."Payments" (left("OrderNumber", 12));

-- Only response/query AuthAmount establishes ApprovedAmount; Advice.Amount does not.
-- C# interprets events and calculates values. Future transition procedures must
-- lock and validate persisted state, then update the payment and event atomically.
CREATE TABLE public."PaymentEvents"
(
    "PaymentEventId" uuid NOT NULL,
    "PaymentId" uuid,
    "Source" text NOT NULL,
    "Payload" jsonb,
    "HttpStatusCode" smallint,
    "ReceivedAt" timestamptz(3) NOT NULL,
    "ProcessingState" text NOT NULL,
    "ProcessingComment" text,
    "ProcessedAt" timestamptz(3),

    CONSTRAINT "PK_PaymentEvents" PRIMARY KEY ("PaymentEventId"),
    CONSTRAINT "FK_PaymentEvents_Payments"
        FOREIGN KEY ("PaymentId") REFERENCES public."Payments" ("PaymentId"),
    CONSTRAINT "CK_PaymentEvents_Source"
        CHECK ("Source" IN ('PaymentResponse', 'TransactionQuery', 'PaymentAdvice', 'TransactionStatus')),
    CONSTRAINT "CK_PaymentEvents_HttpStatusCode_Range"
        CHECK ("HttpStatusCode" BETWEEN 100 AND 599),
    CONSTRAINT "CK_PaymentEvents_ProcessingState"
        CHECK ("ProcessingState" IN ('Pending', 'Applied', 'Ignored')),
    CONSTRAINT "CK_PaymentEvents_Ignored_Comment"
        CHECK ("ProcessingState" <> 'Ignored' OR
            nullif(btrim("ProcessingComment"), '') IS NOT NULL),
    CONSTRAINT "CK_PaymentEvents_ProcessedAt"
        CHECK (("ProcessingState" = 'Pending' AND "ProcessedAt" IS NULL) OR
            ("ProcessingState" IN ('Applied', 'Ignored') AND "ProcessedAt" IS NOT NULL)),
    CONSTRAINT "CK_PaymentEvents_Applied_PaymentId"
        CHECK ("ProcessingState" <> 'Applied' OR "PaymentId" IS NOT NULL)
);

-- Keep the entire Acme body, including message envelopes and unknown fields.
-- A missing HTTP response body is SQL NULL. Acme identifiers and amounts retain
-- their original field names in Payload rather than duplicated local columns.

CREATE INDEX "IX_PaymentEvents_PaymentId_ProcessingState"
    ON public."PaymentEvents" ("PaymentId", "ProcessingState");

-- Preserve duplicate receipts for audit, but never apply the same Switch event
-- twice. Its transactionUUID identifies the adjustment, not the original payment.
CREATE UNIQUE INDEX "UQ_PaymentEvents_Applied_TransactionUUID"
    ON public."PaymentEvents" (("Payload" ->> 'transactionUUID'))
    WHERE "Source" = 'TransactionStatus' AND "ProcessingState" = 'Applied';
