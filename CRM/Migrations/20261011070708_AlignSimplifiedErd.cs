using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CRM.Migrations;

public partial class AlignSimplifiedErd : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Convert and archive legacy data BEFORE removing its columns/tables.
        // EF runs this migration in a transaction; unresolved assignments abort it.
        migrationBuilder.AddColumn<int>("OwnerId", "Contacts", type: "integer", nullable: true);
        migrationBuilder.AddColumn<string>("Source", "Contacts", type: "character varying(100)", maxLength: 100, nullable: false, defaultValue: "Unknown");
        migrationBuilder.AddColumn<string>("Status", "Contacts", type: "character varying(50)", maxLength: 50, nullable: false, defaultValue: "New");
        migrationBuilder.Sql("""
            DO $migration$
            DECLARE
                lead_record record;
                property_record record;
                mapped_contact integer;
                fallback_owner integer;
                legacy_name text;
            BEGIN
                -- Imports with explicit IDs may have left identity sequences behind.
                PERFORM setval(pg_get_serial_sequence('"Contacts"', 'Id'), COALESCE((SELECT max("Id") FROM "Contacts"), 0) + 1, false);
                PERFORM setval(pg_get_serial_sequence('"Notes"', 'Id'), COALESCE((SELECT max("Id") FROM "Notes"), 0) + 1, false);
                SELECT u."Id" INTO fallback_owner FROM "Users" u JOIN "Roles" r ON r."Id" = u."RoleId"
                  ORDER BY CASE lower(r."Name") WHEN 'agent' THEN 0 WHEN 'admin' THEN 1 ELSE 2 END, u."Id" LIMIT 1;

                UPDATE "Contacts" c SET "OwnerId" = COALESCE(
                  (SELECT p."ListingAgentId" FROM "Properties" p WHERE p."SellerContactId" = c."Id" ORDER BY p."Id" LIMIT 1),
                  (SELECT o."AgentId" FROM "Opportunities" o WHERE o."BuyerContactId" = c."Id" ORDER BY o."Id" LIMIT 1),
                  (SELECT l."OwnerId" FROM "Leads" l WHERE l."ConvertedContactId" = c."Id" ORDER BY l."Id" LIMIT 1),
                  fallback_owner);
                IF EXISTS (SELECT 1 FROM "Contacts" WHERE "OwnerId" IS NULL) THEN
                  RAISE EXCEPTION 'Cannot migrate contacts without an owner. Create a user and retry.';
                END IF;

                FOR lead_record IN SELECT * FROM "Leads" ORDER BY "Id" LOOP
                  mapped_contact := lead_record."ConvertedContactId";
                  IF mapped_contact IS NULL THEN
                    legacy_name := trim(lead_record."Name");
                    INSERT INTO "Contacts" ("FirstName", "LastName", "Email", "Type", "Status", "Source", "OwnerId")
                    VALUES (left(split_part(legacy_name, ' ', 1), 80), left(ltrim(substring(legacy_name from length(split_part(legacy_name, ' ', 1)) + 1)), 80), NULL, 'Prospect', 'New',
                      COALESCE(NULLIF(trim(lead_record."Source"), ''), 'Unknown'), lead_record."OwnerId")
                    RETURNING "Id" INTO mapped_contact;
                  ELSE
                    UPDATE "Contacts" SET "Status" = 'Active',
                      "Source" = CASE WHEN "Source" = 'Unknown' THEN COALESCE(NULLIF(trim(lead_record."Source"), ''), 'Unknown') ELSE "Source" END
                      WHERE "Id" = mapped_contact;
                  END IF;

                  INSERT INTO "Notes" ("AuthorId", "EntityType", "EntityId", "Body")
                  VALUES (lead_record."OwnerId", 'Contact', mapped_contact,
                    jsonb_build_object('migration', 'AlignSimplifiedErd', 'legacyLead', to_jsonb(lead_record),
                      'mappedContactId', mapped_contact,
                      'legacyActivities', COALESCE((SELECT jsonb_agg(to_jsonb(a)) FROM "Activities" a WHERE a."LeadId" = lead_record."Id"), '[]'::jsonb),
                      'legacyNotes', COALESCE((SELECT jsonb_agg(to_jsonb(n)) FROM "Notes" n WHERE lower(n."EntityType") IN ('lead', 'leads') AND n."EntityId" = lead_record."Id"), '[]'::jsonb))::text);
                  UPDATE "Notes" SET "EntityType" = 'Contact', "EntityId" = mapped_contact
                    WHERE lower("EntityType") IN ('lead', 'leads') AND "EntityId" = lead_record."Id";
                  UPDATE "Activities" SET "ContactId" = mapped_contact
                    WHERE "LeadId" = lead_record."Id" AND "ContactId" IS NULL;
                END LOOP;

                FOR property_record IN
                  SELECT p.*, u."FullName" AS seller_name, u."Email" AS seller_email
                  FROM "Properties" p LEFT JOIN "Users" u ON u."Id" = p."SellerUserId"
                  WHERE p."SellerUserId" IS NOT NULL OR p."Bedrooms" IS NULL
                  ORDER BY p."Id"
                LOOP
                  IF property_record."SellerUserId" IS NOT NULL AND property_record."SellerContactId" IS NULL THEN
                    SELECT c."Id" INTO mapped_contact FROM "Contacts" c
                    WHERE c."Type" = 'Seller' AND lower(c."Email") = lower(property_record.seller_email)
                      AND c."OwnerId" = property_record."ListingAgentId" ORDER BY c."Id" LIMIT 1;
                    IF mapped_contact IS NULL THEN
                      INSERT INTO "Contacts" ("FirstName", "LastName", "Email", "Type", "Status", "Source", "OwnerId")
                      VALUES (left(split_part(trim(property_record.seller_name), ' ', 1), 80), left(ltrim(substring(trim(property_record.seller_name) from length(split_part(trim(property_record.seller_name), ' ', 1)) + 1)), 80),
                        property_record.seller_email, 'Seller', 'Active', 'Legacy seller account', property_record."ListingAgentId")
                      RETURNING "Id" INTO mapped_contact;
                    END IF;
                    UPDATE "Properties" SET "SellerContactId" = mapped_contact WHERE "Id" = property_record."Id";
                  END IF;
                  INSERT INTO "Notes" ("AuthorId", "EntityType", "EntityId", "Body")
                    VALUES (property_record."ListingAgentId", 'Property', property_record."Id",
                      jsonb_build_object('migration', 'AlignSimplifiedErd', 'legacyProperty', to_jsonb(property_record),
                        'explanation', 'Seller account mapped to a contact; unknown bedroom counts default to zero.')::text);
                END LOOP;
                UPDATE "Properties" SET "Bedrooms" = 0 WHERE "Bedrooms" IS NULL;
                IF EXISTS (SELECT 1 FROM "Properties" p LEFT JOIN "Contacts" c ON c."Id" = p."SellerContactId"
                    WHERE p."SellerContactId" IS NULL OR c."Type" <> 'Seller') THEN
                  RAISE EXCEPTION 'Cannot migrate: each property needs an existing Seller contact. Fix seller assignments and retry.';
                END IF;

                UPDATE "Activities" a SET "ContactId" = o."BuyerContactId" FROM "Opportunities" o
                  WHERE a."ContactId" IS NULL AND a."OpportunityId" = o."Id";
                -- Do not manufacture opportunities or silently discard standalone activities.
                IF EXISTS (SELECT 1 FROM "Activities" WHERE "ContactId" IS NULL OR "OpportunityId" IS NULL) THEN
                  RAISE EXCEPTION 'Cannot migrate: each activity needs ContactId and OpportunityId per the ERD. Assign missing references and retry.';
                END IF;
            END $migration$;
            """);

        migrationBuilder.DropForeignKey("FK_Activities_Leads_LeadId", "Activities");
        migrationBuilder.DropForeignKey("FK_Properties_Users_SellerUserId", "Properties");
        migrationBuilder.DropTable("Leads");
        migrationBuilder.DropIndex("IX_Properties_SellerUserId", "Properties");
        migrationBuilder.DropIndex("IX_Activities_LeadId", "Activities");
        migrationBuilder.DropColumn("SellerUserId", "Properties");
        migrationBuilder.DropColumn("LeadId", "Activities");
        migrationBuilder.AlterColumn<int>("OwnerId", "Contacts", type: "integer", nullable: false, oldClrType: typeof(int), oldType: "integer", oldNullable: true);
        migrationBuilder.AlterColumn<int>("SellerContactId", "Properties", type: "integer", nullable: false, oldClrType: typeof(int), oldType: "integer", oldNullable: true);
        migrationBuilder.AlterColumn<int>("Bedrooms", "Properties", type: "integer", nullable: false, oldClrType: typeof(int), oldType: "integer", oldNullable: true);
        migrationBuilder.AlterColumn<int>("ContactId", "Activities", type: "integer", nullable: false, oldClrType: typeof(int), oldType: "integer", oldNullable: true);
        migrationBuilder.AlterColumn<int>("OpportunityId", "Activities", type: "integer", nullable: false, oldClrType: typeof(int), oldType: "integer", oldNullable: true);
        migrationBuilder.CreateIndex("IX_Contacts_OwnerId", "Contacts", "OwnerId");
        migrationBuilder.AddForeignKey("FK_Contacts_Users_OwnerId", "Contacts", "OwnerId", "Users", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("This data-converting migration cannot be reversed automatically. Restore the pre-upgrade database backup with the matching old application version.");
}

