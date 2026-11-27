START TRANSACTION;

ALTER TABLE "Properties" ADD "SellerUserId" integer;

CREATE INDEX "IX_Properties_SellerUserId" ON "Properties" ("SellerUserId");

ALTER TABLE "Properties" ADD CONSTRAINT "FK_Properties_Users_SellerUserId" FOREIGN KEY ("SellerUserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT;

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261010161816_AddPropertySellerUser', '8.0.11');

COMMIT;

