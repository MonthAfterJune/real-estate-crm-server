# Finalized ERD v2 backend schema

This change aligns persistence entities with [the finalized diagram](../../others/real_estate_crm_erd_simplified.html) and section 8 of [the business specification](<../../others/workflow_basic/Real Estate CRM — Business Rules & System Flow.md>). It does not assert that every workflow, access policy, or frontend is implemented.

The local database upgrade was applied and physically verified on November 25, 2026 using the explicitly approved demo inputs and ownership correction. See the [migration verification record](finalized-erd-v2-migration-verification.md) for results, preservation checks and backup information.

## Model contract

- Nine domain entities and three Identity infrastructure entities; the shared `AuditableEntity` CLR base is not an EF entity or table.
- Seven audited entities have server-stamped UTC `CreatedAt`, nullable `UpdatedAt`, and `IsDeleted`. Ordinary business deletions become soft deletions. Users must be deactivated. Generated Activity history cannot be edited or deleted through the context.
- All 17 domain foreign keys use restricted deletes. Activity Opportunity, Property, FromStage and ToStage references are nullable. Polymorphic Note/Document targets remain application-validated logical links.
- `User.Phone` is a nonmapped CLR alias over Identity's `PhoneNumber`, mapped to the database column `Phone`. The migration renames the existing column, preserving phone values and Identity behavior rather than creating two phone stores.
- Contact Email is trimmed/lowercased before EF saves. Unique partial indexes cover non-deleted Contact Email/Phone and Open buyer/property opportunity pairs. Other checks validate supported values, identity data, publication requirements, outcome fields and generated history shape.
- Six fixed stage Code/SortOrder pairs are seeded. Reservation exclusivity and stage-dependent rules require transaction-aware application behavior; an index must not assume stable stage IDs.

## Upgrade inputs and retained data

The prepared migration is scoped to the inspected legacy shape: existing Users/Contacts/Properties may be retained, but existing Opportunities, Activities, Notes or PipelineStages require an explicit history/stage conversion plan. Its fail-fast guards reject unsupported populated histories instead of inventing actors, deal outcomes or prior stages.

Legacy property City and PropertyType have no source columns. For the inspected single-property database, provide `crm.migration.city` and `crm.migration.property_type` as PostgreSQL session settings in the same session that applies the EF-generated SQL. Blank inputs or multiple legacy property rows abort rather than applying one guessed value to multiple records. These settings are data inputs, not permanent schema fields.

Legacy `Active` property status maps to `Available`; Contact `New` maps into the v2 computed status priority. Existing source values must already be supported. Unknown identity data, invalid enum values, duplicates or overlength values are rejected by migration guards/constraints and require correction before retrying.

No historical creation timestamps exist in the legacy tables. The migration records its transaction timestamp as the legacy audit baseline; this is the upgrade time, not a claimed historical creation date. New records are stamped by the application.

Existing accounts are preserved, including roles outside the three staff roles. Account/portal policy changes are outside this schema upgrade. `DefaultCommissionRate` is deployment configuration; a business default must be supplied separately before creating new opportunities.

## Verification and recovery

Use the workspace-local EF Core 8.0.11 tool under `CRM/tools/dotnet-ef8`; the global tool is preserved. Build/test the solution with `-m:1` where the host's parallel MSBuild startup fails. Metadata/SQLite tests verify model shape and local relational behavior; they do not apply PostgreSQL SQL or prove PostgreSQL lifecycle/concurrency behavior.

Before application, review the new migration, designer, snapshot and SQL together, verify the live preflight still matches the guarded shape, and retain a database backup. Application and credential handling belong to the coordinating agent. A schema mutation is not performed by generating SQL.

Restore the backup with the matching old application if recovery is required. Do not promise an automatic downgrade: the historical `AlignSimplifiedErd.Down` already rejects reversal, and this conversion changes audit/status representation.
