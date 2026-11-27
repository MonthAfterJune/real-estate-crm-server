START TRANSACTION;

DO $guard$
DECLARE
    property_count bigint;
    city text := NULLIF(btrim(current_setting('crm.migration.city', true)), '');
    property_type text := NULLIF(btrim(current_setting('crm.migration.property_type', true)), '');
BEGIN
    IF EXISTS (SELECT 1 FROM "Opportunities") OR EXISTS (SELECT 1 FROM "Activities")
       OR EXISTS (SELECT 1 FROM "Notes") OR EXISTS (SELECT 1 FROM "PipelineStages") THEN
        RAISE EXCEPTION 'AlignFinalizedErdV2 needs an explicit conversion plan for populated deal/activity/note/stage histories. No changes applied.';
    END IF;
    SELECT count(*) INTO property_count FROM "Properties";
    IF property_count > 1 THEN
        RAISE EXCEPTION 'AlignFinalizedErdV2 requires per-property City/PropertyType inputs for multiple legacy properties.';
    END IF;
    IF property_count = 1 AND (city IS NULL OR property_type IS NULL OR length(city) > 100 OR length(property_type) > 50) THEN
        RAISE EXCEPTION 'Set valid crm.migration.city and crm.migration.property_type session inputs before applying this migration.';
    END IF;
    IF EXISTS (SELECT 1 FROM "Properties" WHERE "Status" NOT IN ('Active', 'Draft', 'Available', 'Withdrawn')) THEN
        RAISE EXCEPTION 'Legacy Reserved/Sold property history needs an explicit deal conversion plan.';
    END IF;
    IF EXISTS (SELECT 1 FROM "Contacts" WHERE NULLIF(btrim("FirstName"), '') IS NULL
        OR NULLIF(btrim("LastName"), '') IS NULL OR NULLIF(btrim("Email"), '') IS NULL
        OR "Type" NOT IN ('Buyer', 'Seller', 'Both')
        OR "Status" NOT IN ('New', 'Lead', 'Active', 'Client', 'Inactive')
        OR "Source" NOT IN ('Walk-in', 'Referral', 'Website', 'Facebook', 'Phone', 'Other')) THEN
        RAISE EXCEPTION 'Legacy contacts require valid identity, type, status and source data before conversion.';
    END IF;
    IF EXISTS (SELECT 1 FROM "Contacts" GROUP BY lower(btrim("Email")) HAVING count(*) > 1) THEN
        RAISE EXCEPTION 'Normalize and resolve duplicate legacy Contact emails before conversion.';
    END IF;
    IF EXISTS (SELECT 1 FROM "Users" WHERE length("PhoneNumber") > 30) THEN
        RAISE EXCEPTION 'A legacy user phone exceeds the supported length; review it before conversion.';
    END IF;
    IF EXISTS (SELECT 1 FROM "Contacts" c JOIN "Users" u ON u."Id" = c."OwnerId" JOIN "Roles" r ON r."Id" = u."RoleId" WHERE lower(r."Name") <> 'agent')
        OR EXISTS (SELECT 1 FROM "Properties" p JOIN "Users" u ON u."Id" = p."ListingAgentId" JOIN "Roles" r ON r."Id" = u."RoleId" WHERE lower(r."Name") <> 'agent') THEN
        RAISE EXCEPTION 'Legacy record owners/listing agents must reference Agent users before conversion.';
    END IF;
END $guard$;

DROP INDEX "IX_Opportunities_BuyerContactId";

ALTER TABLE "Activities" RENAME COLUMN "OccurredAt" TO "CompletedAt";

ALTER TABLE "Users" RENAME COLUMN "PhoneNumber" TO "Phone";

ALTER TABLE "Users" ALTER COLUMN "Phone" TYPE character varying(30);

ALTER TABLE "Users" ADD "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP);

ALTER TABLE "Users" ADD "IsActive" boolean NOT NULL DEFAULT TRUE;

ALTER TABLE "Users" ADD "IsDeleted" boolean NOT NULL DEFAULT FALSE;

ALTER TABLE "Users" ADD "UpdatedAt" timestamp with time zone;

ALTER TABLE "Properties" ALTER COLUMN "Price" DROP NOT NULL;

ALTER TABLE "Properties" ALTER COLUMN "Bedrooms" DROP NOT NULL;

ALTER TABLE "Properties" ADD "City" character varying(100);

ALTER TABLE "Properties" ADD "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP);

ALTER TABLE "Properties" ADD "IsDeleted" boolean NOT NULL DEFAULT FALSE;

ALTER TABLE "Properties" ADD "PropertyType" character varying(50);

ALTER TABLE "Properties" ADD "UpdatedAt" timestamp with time zone;

ALTER TABLE "PipelineStages" ADD "Code" character varying(30) NOT NULL DEFAULT '';

ALTER TABLE "Opportunities" ALTER COLUMN "OfferAmount" DROP NOT NULL;

ALTER TABLE "Opportunities" ADD "ClosedAt" timestamp with time zone;

ALTER TABLE "Opportunities" ADD "CommissionRate" numeric(9,6) NOT NULL DEFAULT 0.0;

ALTER TABLE "Opportunities" ADD "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP);

ALTER TABLE "Opportunities" ADD "FinalPrice" numeric(18,2);

ALTER TABLE "Opportunities" ADD "IsDeleted" boolean NOT NULL DEFAULT FALSE;

ALTER TABLE "Opportunities" ADD "LostReason" character varying(50);

ALTER TABLE "Opportunities" ADD "Status" character varying(20) NOT NULL DEFAULT '';

ALTER TABLE "Opportunities" ADD "UpdatedAt" timestamp with time zone;

ALTER TABLE "Notes" ADD "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP);

ALTER TABLE "Notes" ADD "IsDeleted" boolean NOT NULL DEFAULT FALSE;

ALTER TABLE "Notes" ADD "UpdatedAt" timestamp with time zone;

ALTER TABLE "Contacts" ADD "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP);

ALTER TABLE "Contacts" ADD "IsDeleted" boolean NOT NULL DEFAULT FALSE;

ALTER TABLE "Contacts" ADD "IsManuallyInactive" boolean NOT NULL DEFAULT FALSE;

ALTER TABLE "Contacts" ADD "Phone" character varying(30);

ALTER TABLE "Contacts" ADD "UpdatedAt" timestamp with time zone;

ALTER TABLE "Activities" ALTER COLUMN "OpportunityId" DROP NOT NULL;

ALTER TABLE "Activities" ALTER COLUMN "CompletedAt" DROP NOT NULL;

ALTER TABLE "Activities" ADD "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP);

ALTER TABLE "Activities" ADD "CreatedById" integer NOT NULL DEFAULT 0;

ALTER TABLE "Activities" ADD "Details" text;

ALTER TABLE "Activities" ADD "FromStageId" integer;

ALTER TABLE "Activities" ADD "IsDeleted" boolean NOT NULL DEFAULT FALSE;

ALTER TABLE "Activities" ADD "IsSystemGenerated" boolean NOT NULL DEFAULT FALSE;

ALTER TABLE "Activities" ADD "PropertyId" integer;

ALTER TABLE "Activities" ADD "ScheduledAt" timestamp with time zone;

ALTER TABLE "Activities" ADD "Subject" character varying(200) NOT NULL DEFAULT '';

ALTER TABLE "Activities" ADD "ToStageId" integer;

ALTER TABLE "Activities" ADD "UpdatedAt" timestamp with time zone;

ALTER TABLE "Activities" ADD "UserId" integer NOT NULL DEFAULT 0;

CREATE TABLE "Documents" (
    "Id" integer GENERATED BY DEFAULT AS IDENTITY,
    "UploadedById" integer NOT NULL,
    "EntityType" character varying(50) NOT NULL,
    "EntityId" integer NOT NULL,
    "Category" character varying(30) NOT NULL,
    "FileName" character varying(255) NOT NULL,
    "FilePath" character varying(2048) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL DEFAULT (CURRENT_TIMESTAMP),
    "UpdatedAt" timestamp with time zone,
    "IsDeleted" boolean NOT NULL DEFAULT FALSE,
    CONSTRAINT "PK_Documents" PRIMARY KEY ("Id"),
    CONSTRAINT "CK_Documents_Category" CHECK ("Category" IN ('ID', 'Offer', 'Contract', 'Title', 'Photo', 'Other')),
    CONSTRAINT "CK_Documents_Target" CHECK ("EntityType" IN ('Contact', 'Property', 'Opportunity') AND "EntityId" > 0),
    CONSTRAINT "FK_Documents_Users_UploadedById" FOREIGN KEY ("UploadedById") REFERENCES "Users" ("Id") ON DELETE RESTRICT
);

UPDATE "Properties" SET
    "City" = NULLIF(btrim(current_setting('crm.migration.city', true)), ''),
    "PropertyType" = NULLIF(btrim(current_setting('crm.migration.property_type', true)), ''),
    "Status" = CASE "Status" WHEN 'Active' THEN 'Available' ELSE "Status" END;
UPDATE "Contacts" SET
    "Email" = lower(btrim("Email")),
    "IsManuallyInactive" = ("Status" = 'Inactive');
UPDATE "Contacts" c SET "Status" = CASE
    WHEN c."IsManuallyInactive" THEN 'Inactive'
    WHEN EXISTS (SELECT 1 FROM "Properties" p WHERE p."SellerContactId" = c."Id" AND p."Status" = 'Sold') THEN 'Client'
    WHEN EXISTS (SELECT 1 FROM "Properties" p WHERE p."SellerContactId" = c."Id" AND p."Status" IN ('Available', 'Reserved')) THEN 'Active'
    ELSE 'Lead' END;

ALTER TABLE "Properties" ALTER COLUMN "City" SET NOT NULL;

ALTER TABLE "Properties" ALTER COLUMN "PropertyType" SET NOT NULL;

INSERT INTO "PipelineStages" ("Id", "Code", "Name", "SortOrder")
VALUES (1, 'INQUIRY', 'Inquiry', 1);
INSERT INTO "PipelineStages" ("Id", "Code", "Name", "SortOrder")
VALUES (2, 'VIEWING', 'Viewing', 2);
INSERT INTO "PipelineStages" ("Id", "Code", "Name", "SortOrder")
VALUES (3, 'OFFER', 'Offer', 3);
INSERT INTO "PipelineStages" ("Id", "Code", "Name", "SortOrder")
VALUES (4, 'NEGOTIATION', 'Negotiation', 4);
INSERT INTO "PipelineStages" ("Id", "Code", "Name", "SortOrder")
VALUES (5, 'RESERVATION', 'Reservation', 5);
INSERT INTO "PipelineStages" ("Id", "Code", "Name", "SortOrder")
VALUES (6, 'CLOSING', 'Closing', 6);

ALTER TABLE "Users" ADD CONSTRAINT "CK_Users_NotDeleted" CHECK ("IsDeleted" = false);

ALTER TABLE "Properties" ADD CONSTRAINT "CK_Properties_Bedrooms" CHECK ("Bedrooms" IS NULL OR "Bedrooms" >= 0);

ALTER TABLE "Properties" ADD CONSTRAINT "CK_Properties_Price" CHECK ("Status" IN ('Draft', 'Withdrawn') OR ("Price" IS NOT NULL AND "Price" > 0));

ALTER TABLE "Properties" ADD CONSTRAINT "CK_Properties_RequiredDetails" CHECK (length(trim("Address")) > 0 AND length(trim("City")) > 0 AND length(trim("PropertyType")) > 0);

ALTER TABLE "Properties" ADD CONSTRAINT "CK_Properties_Retention" CHECK ("IsDeleted" = false OR "Status" NOT IN ('Reserved', 'Sold'));

ALTER TABLE "Properties" ADD CONSTRAINT "CK_Properties_Status" CHECK ("Status" IN ('Draft', 'Available', 'Reserved', 'Sold', 'Withdrawn'));

CREATE UNIQUE INDEX "IX_PipelineStages_Code" ON "PipelineStages" ("Code");

CREATE UNIQUE INDEX "IX_PipelineStages_SortOrder" ON "PipelineStages" ("SortOrder");

ALTER TABLE "PipelineStages" ADD CONSTRAINT "CK_PipelineStages_Seed" CHECK (("Code" = 'INQUIRY' AND "SortOrder" = 1) OR ("Code" = 'VIEWING' AND "SortOrder" = 2) OR ("Code" = 'OFFER' AND "SortOrder" = 3) OR ("Code" = 'NEGOTIATION' AND "SortOrder" = 4) OR ("Code" = 'RESERVATION' AND "SortOrder" = 5) OR ("Code" = 'CLOSING' AND "SortOrder" = 6));

CREATE UNIQUE INDEX "IX_Opportunities_BuyerContactId_PropertyId" ON "Opportunities" ("BuyerContactId", "PropertyId") WHERE "IsDeleted" = false AND "Status" = 'Open';

ALTER TABLE "Opportunities" ADD CONSTRAINT "CK_Opportunities_Offer" CHECK ("OfferAmount" IS NULL OR "OfferAmount" > 0);

ALTER TABLE "Opportunities" ADD CONSTRAINT "CK_Opportunities_Outcome" CHECK (("Status" = 'Open' AND "FinalPrice" IS NULL AND "LostReason" IS NULL AND "ClosedAt" IS NULL) OR ("Status" = 'Won' AND "FinalPrice" IS NOT NULL AND "FinalPrice" > 0 AND "LostReason" IS NULL AND "ClosedAt" IS NOT NULL) OR ("Status" = 'Lost' AND "FinalPrice" IS NULL AND "LostReason" IS NOT NULL AND "LostReason" IN ('Price', 'Bought elsewhere', 'Financing', 'Changed mind', 'Property sold', 'No response', 'Other') AND "ClosedAt" IS NOT NULL));

ALTER TABLE "Opportunities" ADD CONSTRAINT "CK_Opportunities_Rate" CHECK ("CommissionRate" >= 0 AND "CommissionRate" <= 1);

ALTER TABLE "Opportunities" ADD CONSTRAINT "CK_Opportunities_Retention" CHECK ("IsDeleted" = false OR "Status" <> 'Open');

ALTER TABLE "Notes" ADD CONSTRAINT "CK_Notes_Target" CHECK ("EntityType" IN ('Contact', 'Property', 'Opportunity') AND "EntityId" > 0);

CREATE UNIQUE INDEX "IX_Contacts_Email" ON "Contacts" ("Email") WHERE "IsDeleted" = false AND "Email" IS NOT NULL;

CREATE UNIQUE INDEX "IX_Contacts_Phone" ON "Contacts" ("Phone") WHERE "IsDeleted" = false AND "Phone" IS NOT NULL;

ALTER TABLE "Contacts" ADD CONSTRAINT "CK_Contacts_Identity" CHECK (length(trim("FirstName")) > 0 AND length(trim("LastName")) > 0 AND (NULLIF(trim("Email"), '') IS NOT NULL OR NULLIF(trim("Phone"), '') IS NOT NULL));

ALTER TABLE "Contacts" ADD CONSTRAINT "CK_Contacts_Source" CHECK ("Source" IN ('Walk-in', 'Referral', 'Website', 'Facebook', 'Phone', 'Other'));

ALTER TABLE "Contacts" ADD CONSTRAINT "CK_Contacts_Status" CHECK ("Status" IN ('Lead', 'Active', 'Client', 'Inactive'));

ALTER TABLE "Contacts" ADD CONSTRAINT "CK_Contacts_Type" CHECK ("Type" IN ('Buyer', 'Seller', 'Both'));

CREATE INDEX "IX_Activities_CreatedById" ON "Activities" ("CreatedById");

CREATE INDEX "IX_Activities_FromStageId" ON "Activities" ("FromStageId");

CREATE INDEX "IX_Activities_PropertyId" ON "Activities" ("PropertyId");

CREATE INDEX "IX_Activities_ToStageId" ON "Activities" ("ToStageId");

CREATE INDEX "IX_Activities_UserId" ON "Activities" ("UserId");

ALTER TABLE "Activities" ADD CONSTRAINT "CK_Activities_Generated" CHECK (("IsSystemGenerated" = false AND "Type" NOT IN ('Stage Change', 'System') AND "FromStageId" IS NULL AND "ToStageId" IS NULL) OR ("IsSystemGenerated" = true AND "Type" IN ('Stage Change', 'System') AND "CompletedAt" IS NOT NULL AND "ScheduledAt" IS NULL AND NULLIF(trim("Details"), '') IS NOT NULL AND "IsDeleted" = false AND (("Type" = 'Stage Change' AND "FromStageId" IS NOT NULL AND "ToStageId" IS NOT NULL) OR ("Type" = 'System' AND "FromStageId" IS NULL AND "ToStageId" IS NULL))));

ALTER TABLE "Activities" ADD CONSTRAINT "CK_Activities_Subject" CHECK (length(trim("Subject")) > 0);

ALTER TABLE "Activities" ADD CONSTRAINT "CK_Activities_Time" CHECK ("ScheduledAt" IS NOT NULL OR "CompletedAt" IS NOT NULL);

ALTER TABLE "Activities" ADD CONSTRAINT "CK_Activities_Type" CHECK ("Type" IN ('Call', 'Email', 'Meeting', 'Viewing', 'Task', 'Stage Change', 'System'));

ALTER TABLE "Activities" ADD CONSTRAINT "CK_Activities_Viewing" CHECK ("Type" <> 'Viewing' OR "OpportunityId" IS NOT NULL);

CREATE INDEX "IX_Documents_UploadedById" ON "Documents" ("UploadedById");

ALTER TABLE "Activities" ADD CONSTRAINT "FK_Activities_PipelineStages_FromStageId" FOREIGN KEY ("FromStageId") REFERENCES "PipelineStages" ("Id") ON DELETE RESTRICT;

ALTER TABLE "Activities" ADD CONSTRAINT "FK_Activities_PipelineStages_ToStageId" FOREIGN KEY ("ToStageId") REFERENCES "PipelineStages" ("Id") ON DELETE RESTRICT;

ALTER TABLE "Activities" ADD CONSTRAINT "FK_Activities_Properties_PropertyId" FOREIGN KEY ("PropertyId") REFERENCES "Properties" ("Id") ON DELETE RESTRICT;

ALTER TABLE "Activities" ADD CONSTRAINT "FK_Activities_Users_CreatedById" FOREIGN KEY ("CreatedById") REFERENCES "Users" ("Id") ON DELETE RESTRICT;

ALTER TABLE "Activities" ADD CONSTRAINT "FK_Activities_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT;

SELECT setval(
    pg_get_serial_sequence('"PipelineStages"', 'Id'),
    GREATEST(
        (SELECT MAX("Id") FROM "PipelineStages") + 1,
        nextval(pg_get_serial_sequence('"PipelineStages"', 'Id'))),
    false);

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261124170627_AlignFinalizedErdV2', '8.0.11');

COMMIT;

