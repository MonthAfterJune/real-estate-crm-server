# Finalized ERD v2 migration verification - November 25, 2026

Applied migration `20261124170627_AlignFinalizedErdV2` to the configured local PostgreSQL target `localhost:5432`, database `CRM`, schema `public`.

The user explicitly approved the demo values and the legacy ownership correction. The correction and EF-generated migration SQL committed in one transaction. An earlier attempt stopped at its first guard and made no schema changes because the legacy Contact owner was not an Agent.

## Approved inputs and resulting data

| Item | Verified result |
| --- | --- |
| Property 8 City | `Makati` |
| Property 8 PropertyType | `Condominium` |
| Property 8 Status | `Available` (converted from legacy `Active`) |
| Contact 1 OwnerId | `4`, the existing Agent/listing agent (corrected from Seller user `5`) |
| Contact 1 Status | `Active`, recomputed from the Available listing |
| Application DefaultCommissionRate | `0.03`, a decimal fraction configured in `CRM/appsettings.json` |

The ownership change is an approved legacy-data repair recorded here and in the operational evidence. No authenticated application Activity actor or historical creation date was invented. Legacy audit timestamps use the upgrade transaction timestamp as documented in the [schema notes](finalized-erd-v2-schema.md).

## Live physical-schema checks

Compared PostgreSQL catalog metadata with the embedded definitions in the [finalized ERD](../../others/real_estate_crm_erd_simplified.html):

- All nine domain tables and all 89 ERD fields exist with matching requiredness and PostgreSQL data types; numeric precision/scale matches the EF model.
- Each domain table has its single `Id` primary key. All 17 domain foreign keys reference the expected table/column and use restricted deletes.
- Optional Activity references remain nullable. The three Identity infrastructure tables and EF migration history are retained; no unexpected tables or extra non-Identity domain columns were found.
- All 23 expected check constraints exist. Required unique indexes, including the non-deleted Contact channels and Open buyer/property pair filters, are present.
- All six fixed stage codes and order values are seeded correctly. The stage identity sequence advances beyond the seeded IDs.
- EF history includes the applied v2 migration. The EF Core 8.0.11 pending-model check reports no differences from the migration snapshot.

Existing row counts, user credentials/account fields, role records, property identity/assignments and contact identity data were verified using preservation fingerprints. The approved Contact owner correction and documented status/email normalization are explicitly accounted for. No existing records were removed. The new Document table is present and empty.

## Backup and evidence

- Fresh pre-upgrade custom-format backup: `CRM/artifacts/database-backups/pre-finalized-erd-v2-approved-demo-20261125.dump`; verified readable with `pg_restore --list` (84 archive entries).
- Local operational evidence: `CRM/artifacts/finalized-erd-v2/`, including preflight, backup metadata, approved inputs, execution log, physical schema metadata and `verification.json` (zero errors).
- Latest backend build passed with zero warnings/errors; the regression suite passed 126 tests before the final database transaction. No application code changed during the final application step.

This verifies the applied database structure and specified conversions. It does not establish full pipeline, authorization, document-storage or frontend compliance; remaining work is described in the [API compatibility notes](finalized-erd-v2-api.md). Recovery requires the verified backup and matching old application; the data-converting migration does not support an automatic downgrade.
