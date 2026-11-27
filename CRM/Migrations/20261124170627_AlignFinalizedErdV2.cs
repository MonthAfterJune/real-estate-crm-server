using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace CRM.Migrations
{
    /// <inheritdoc />
    public partial class AlignFinalizedErdV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // This bounded conversion must not invent historical actors, deal outcomes or stages.
            // Caller supplies missing property facts as session settings in the applying connection.
            migrationBuilder.Sql("""
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
                """);

            migrationBuilder.DropIndex(
                name: "IX_Opportunities_BuyerContactId",
                table: "Opportunities");

            migrationBuilder.RenameColumn(
                name: "OccurredAt",
                table: "Activities",
                newName: "CompletedAt");

            migrationBuilder.RenameColumn(
                name: "PhoneNumber",
                table: "Users",
                newName: "Phone");

            migrationBuilder.AlterColumn<string>(
                name: "Phone",
                table: "Users",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "CURRENT_TIMESTAMP");

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "Users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "Price",
                table: "Properties",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)",
                oldPrecision: 18,
                oldScale: 2);

            migrationBuilder.AlterColumn<int>(
                name: "Bedrooms",
                table: "Properties",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<string>(
                name: "City",
                table: "Properties",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "Properties",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "CURRENT_TIMESTAMP");

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "Properties",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PropertyType",
                table: "Properties",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "Properties",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Code",
                table: "PipelineStages",
                type: "character varying(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AlterColumn<decimal>(
                name: "OfferAmount",
                table: "Opportunities",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)",
                oldPrecision: 18,
                oldScale: 2);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClosedAt",
                table: "Opportunities",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "CommissionRate",
                table: "Opportunities",
                type: "numeric(9,6)",
                precision: 9,
                scale: 6,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "Opportunities",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "CURRENT_TIMESTAMP");

            migrationBuilder.AddColumn<decimal>(
                name: "FinalPrice",
                table: "Opportunities",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "Opportunities",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "LostReason",
                table: "Opportunities",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Opportunities",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "Opportunities",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "Notes",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "CURRENT_TIMESTAMP");

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "Notes",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "Notes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "Contacts",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "CURRENT_TIMESTAMP");

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "Contacts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsManuallyInactive",
                table: "Contacts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Phone",
                table: "Contacts",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "Contacts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "OpportunityId",
                table: "Activities",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<DateTime>(
                name: "CompletedAt",
                table: "Activities",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTime),
                oldType: "timestamp with time zone");

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "Activities",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "CURRENT_TIMESTAMP");

            migrationBuilder.AddColumn<int>(
                name: "CreatedById",
                table: "Activities",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Details",
                table: "Activities",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FromStageId",
                table: "Activities",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "Activities",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsSystemGenerated",
                table: "Activities",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "PropertyId",
                table: "Activities",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ScheduledAt",
                table: "Activities",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Subject",
                table: "Activities",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "ToStageId",
                table: "Activities",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "UpdatedAt",
                table: "Activities",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UserId",
                table: "Activities",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "Documents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UploadedById = table.Column<int>(type: "integer", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    EntityId = table.Column<int>(type: "integer", nullable: false),
                    Category = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    FilePath = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Documents", x => x.Id);
                    table.CheckConstraint("CK_Documents_Category", "\"Category\" IN ('ID', 'Offer', 'Contract', 'Title', 'Photo', 'Other')");
                    table.CheckConstraint("CK_Documents_Target", "\"EntityType\" IN ('Contact', 'Property', 'Opportunity') AND \"EntityId\" > 0");
                    table.ForeignKey(
                        name: "FK_Documents_Users_UploadedById",
                        column: x => x.UploadedById,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // Legacy timestamps describe the upgrade baseline, not unknown creation dates.
            migrationBuilder.Sql("""
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
                """);

            migrationBuilder.AlterColumn<string>(
                name: "City", table: "Properties", type: "character varying(100)", maxLength: 100, nullable: false,
                oldClrType: typeof(string), oldType: "character varying(100)", oldMaxLength: 100, oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "PropertyType", table: "Properties", type: "character varying(50)", maxLength: 50, nullable: false,
                oldClrType: typeof(string), oldType: "character varying(50)", oldMaxLength: 50, oldNullable: true);

            migrationBuilder.InsertData(
                table: "PipelineStages",
                columns: new[] { "Id", "Code", "Name", "SortOrder" },
                values: new object[,]
                {
                    { 1, "INQUIRY", "Inquiry", 1 },
                    { 2, "VIEWING", "Viewing", 2 },
                    { 3, "OFFER", "Offer", 3 },
                    { 4, "NEGOTIATION", "Negotiation", 4 },
                    { 5, "RESERVATION", "Reservation", 5 },
                    { 6, "CLOSING", "Closing", 6 }
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_Users_NotDeleted",
                table: "Users",
                sql: "\"IsDeleted\" = false");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Properties_Bedrooms",
                table: "Properties",
                sql: "\"Bedrooms\" IS NULL OR \"Bedrooms\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Properties_Price",
                table: "Properties",
                sql: "\"Status\" IN ('Draft', 'Withdrawn') OR (\"Price\" IS NOT NULL AND \"Price\" > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Properties_RequiredDetails",
                table: "Properties",
                sql: "length(trim(\"Address\")) > 0 AND length(trim(\"City\")) > 0 AND length(trim(\"PropertyType\")) > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Properties_Retention",
                table: "Properties",
                sql: "\"IsDeleted\" = false OR \"Status\" NOT IN ('Reserved', 'Sold')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Properties_Status",
                table: "Properties",
                sql: "\"Status\" IN ('Draft', 'Available', 'Reserved', 'Sold', 'Withdrawn')");

            migrationBuilder.CreateIndex(
                name: "IX_PipelineStages_Code",
                table: "PipelineStages",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PipelineStages_SortOrder",
                table: "PipelineStages",
                column: "SortOrder",
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_PipelineStages_Seed",
                table: "PipelineStages",
                sql: "(\"Code\" = 'INQUIRY' AND \"SortOrder\" = 1) OR (\"Code\" = 'VIEWING' AND \"SortOrder\" = 2) OR (\"Code\" = 'OFFER' AND \"SortOrder\" = 3) OR (\"Code\" = 'NEGOTIATION' AND \"SortOrder\" = 4) OR (\"Code\" = 'RESERVATION' AND \"SortOrder\" = 5) OR (\"Code\" = 'CLOSING' AND \"SortOrder\" = 6)");

            migrationBuilder.CreateIndex(
                name: "IX_Opportunities_BuyerContactId_PropertyId",
                table: "Opportunities",
                columns: new[] { "BuyerContactId", "PropertyId" },
                unique: true,
                filter: "\"IsDeleted\" = false AND \"Status\" = 'Open'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Opportunities_Offer",
                table: "Opportunities",
                sql: "\"OfferAmount\" IS NULL OR \"OfferAmount\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Opportunities_Outcome",
                table: "Opportunities",
                sql: "(\"Status\" = 'Open' AND \"FinalPrice\" IS NULL AND \"LostReason\" IS NULL AND \"ClosedAt\" IS NULL) OR (\"Status\" = 'Won' AND \"FinalPrice\" IS NOT NULL AND \"FinalPrice\" > 0 AND \"LostReason\" IS NULL AND \"ClosedAt\" IS NOT NULL) OR (\"Status\" = 'Lost' AND \"FinalPrice\" IS NULL AND \"LostReason\" IS NOT NULL AND \"LostReason\" IN ('Price', 'Bought elsewhere', 'Financing', 'Changed mind', 'Property sold', 'No response', 'Other') AND \"ClosedAt\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Opportunities_Rate",
                table: "Opportunities",
                sql: "\"CommissionRate\" >= 0 AND \"CommissionRate\" <= 1");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Opportunities_Retention",
                table: "Opportunities",
                sql: "\"IsDeleted\" = false OR \"Status\" <> 'Open'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Notes_Target",
                table: "Notes",
                sql: "\"EntityType\" IN ('Contact', 'Property', 'Opportunity') AND \"EntityId\" > 0");

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_Email",
                table: "Contacts",
                column: "Email",
                unique: true,
                filter: "\"IsDeleted\" = false AND \"Email\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Contacts_Phone",
                table: "Contacts",
                column: "Phone",
                unique: true,
                filter: "\"IsDeleted\" = false AND \"Phone\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Contacts_Identity",
                table: "Contacts",
                sql: "length(trim(\"FirstName\")) > 0 AND length(trim(\"LastName\")) > 0 AND (NULLIF(trim(\"Email\"), '') IS NOT NULL OR NULLIF(trim(\"Phone\"), '') IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Contacts_Source",
                table: "Contacts",
                sql: "\"Source\" IN ('Walk-in', 'Referral', 'Website', 'Facebook', 'Phone', 'Other')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Contacts_Status",
                table: "Contacts",
                sql: "\"Status\" IN ('Lead', 'Active', 'Client', 'Inactive')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Contacts_Type",
                table: "Contacts",
                sql: "\"Type\" IN ('Buyer', 'Seller', 'Both')");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_CreatedById",
                table: "Activities",
                column: "CreatedById");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_FromStageId",
                table: "Activities",
                column: "FromStageId");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_PropertyId",
                table: "Activities",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_ToStageId",
                table: "Activities",
                column: "ToStageId");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_UserId",
                table: "Activities",
                column: "UserId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Activities_Generated",
                table: "Activities",
                sql: "(\"IsSystemGenerated\" = false AND \"Type\" NOT IN ('Stage Change', 'System') AND \"FromStageId\" IS NULL AND \"ToStageId\" IS NULL) OR (\"IsSystemGenerated\" = true AND \"Type\" IN ('Stage Change', 'System') AND \"CompletedAt\" IS NOT NULL AND \"ScheduledAt\" IS NULL AND NULLIF(trim(\"Details\"), '') IS NOT NULL AND \"IsDeleted\" = false AND ((\"Type\" = 'Stage Change' AND \"FromStageId\" IS NOT NULL AND \"ToStageId\" IS NOT NULL) OR (\"Type\" = 'System' AND \"FromStageId\" IS NULL AND \"ToStageId\" IS NULL)))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Activities_Subject",
                table: "Activities",
                sql: "length(trim(\"Subject\")) > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Activities_Time",
                table: "Activities",
                sql: "\"ScheduledAt\" IS NOT NULL OR \"CompletedAt\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Activities_Type",
                table: "Activities",
                sql: "\"Type\" IN ('Call', 'Email', 'Meeting', 'Viewing', 'Task', 'Stage Change', 'System')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Activities_Viewing",
                table: "Activities",
                sql: "\"Type\" <> 'Viewing' OR \"OpportunityId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_UploadedById",
                table: "Documents",
                column: "UploadedById");

            migrationBuilder.AddForeignKey(
                name: "FK_Activities_PipelineStages_FromStageId",
                table: "Activities",
                column: "FromStageId",
                principalTable: "PipelineStages",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Activities_PipelineStages_ToStageId",
                table: "Activities",
                column: "ToStageId",
                principalTable: "PipelineStages",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Activities_Properties_PropertyId",
                table: "Activities",
                column: "PropertyId",
                principalTable: "Properties",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Activities_Users_CreatedById",
                table: "Activities",
                column: "CreatedById",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Activities_Users_UserId",
                table: "Activities",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException("This data-converting migration cannot be reversed automatically. Restore the verified pre-upgrade backup with its matching application version.");
    }
}
