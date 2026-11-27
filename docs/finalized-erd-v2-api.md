# Backend contract changes for finalized ERD v2

This backend-first update follows [business specification v2](<../../others/workflow_basic/Real Estate CRM — Business Rules & System Flow.md>) and the [finalized ERD](../../others/real_estate_crm_erd_simplified.html). The model/migration alignment is described in [schema notes](finalized-erd-v2-schema.md). It is not a claim that every documented workflow or access rule is implemented.

The subsequent [Part 01 workflow implementation](../../docs/development/part01-implementation.md) connects Contact capture/reuse, Draft/publication, pre-opportunity qualification and Open Inquiry creation through the staff UI and API. It uses the existing v2 schema without another migration.

The [Part 02 workflow implementation](../../docs/development/part02-implementation.md) adds Open stage progression, exclusive Reservation/Closing, generated stage history and the optional Viewing-completion prompt. It also uses the existing schema; its delivery record distinguishes source, test and real-provider evidence.

The [Part 03 workflow implementation](../../docs/development/part03-implementation.md) adds explicit Won/Lost closure commands, automatic competing losses, Open-only commission-rate editing and computed Won commission. It preserves the existing schema and protected generic PUT fields.

## Existing API shape changes

Routes continue to use cookie authentication, CSRF for writes, camelCase JSON and full PUT payloads. Old request shapes need updating; obsolete fields in the strict DTOs return validation errors.

| Resource | Request / response changes |
| --- | --- |
| Contacts | Type is Buyer, Seller or Both. LastName is required, with Email or Phone. Source is Walk-in, Referral, Website, Facebook, Phone or Other. Phone uses international `+` format and supported formatting separators are normalized before validation. Status is read-only; use IsManuallyInactive for a manual override. On creation an Agent's omitted OwnerId defaults to self; Manager/Admin supply an active Agent. Full PUT includes OwnerId and only Manager/Admin may reassign it. |
| Properties | City and PropertyType are required. Price and Bedrooms are nullable for Draft. Creation is Draft-only; omitted ListingAgentId defaults to an Agent creator, while Manager/Admin select an active Agent. Full PUT supports the basic Draft/Available/Withdrawn lifecycle, including positive-price publication. Seller may be Seller or Both; normal PUT cannot create Reserved/Sold or reopen Sold. |
| Opportunities | Creation is Open at the immutable INQUIRY stage; omitted StageId is resolved server-side and non-Inquiry creation is rejected. Omitted AgentId defaults to the buyer owner; only Manager/Admin may choose another active Agent. OfferAmount is optional at Inquiry and positive when provided. Buyer may be Buyer or Both. Status, FinalPrice, LostReason, ClosedAt and CommissionRate remain server-managed. |
| Activities | OccurredAt is removed. Provide Subject, ContactId, UserId and at least ScheduledAt or CompletedAt as UTC. OpportunityId/PropertyId are optional. Opportunity-linked activities use its buyer/property; property-only activities use its seller. Viewing requires Opportunity. CreatedById is taken from the authenticated session. Generated history fields are read-only. |
| Users | Phone and IsActive are exposed through directory DTOs. Phone is backed by Identity's PhoneNumber and persisted in the Phone column. Deactivated users cannot log in or use existing cookies; deleting a directory user deactivates it and retains Identity/history. |
| PipelineStages | Responses include immutable Code and SortOrder. The migration supplies six seed stages. The API supports GET and Admin-only name updates; POST/DELETE/custom stage creation and reorder are removed. |
| Audited resources | Responses expose CreatedAt, UpdatedAt and IsDeleted. Requests do not accept them. PUT preserves CreatedAt and other server-managed fields. Ordinary deletions soft-delete, subject to retention guards. |
| Documents | Document entity, uploader FK and audit fields are persisted. Upload/storage/download APIs are not part of this schema update. |

## Persistence compatibility and safeguards

- New opportunity creation snapshots required deployment configuration `DefaultCommissionRate` as a fraction in `[0,1]`. Missing/invalid configuration rejects opportunity creation with a safe 503; it does not prevent migration tooling or startup. Changing the configuration does not rewrite existing rates. Generic PUT preserves outcome/rate data rather than replacing it with DTO defaults.
- Contact creation derives Lead or manual Inactive. Contact updates and supported property/opportunity writes recompute Inactive > Client > Active > Lead from current related records. Email is trimmed/lowercased; blank channels become null. Visible duplicate matches include a safe `existingContactId` ProblemDetails extension for deliberate reuse; hidden matches expose no identifier or private Contact details. Unique indexes still protect concurrent writes.
- Agents read/edit their own Contacts and opportunities and edit their own listings. Other listings remain readable, with private seller summaries redacted. An assigned opportunity Agent may retain its unchanged private buyer reference without gaining access to that Contact; replacement buyers require visibility. Property/opportunity writes, affected Contact status and required price/assignment System history commit atomically.
- A first Inquiry may target Available or Reserved without changing reservation state. A new attempt after a closed buyer/property pair requires Available. A second non-deleted Open pair is rejected by a friendly check and the database index.
- Opportunity full PUT remains supported. Stage movement permits forward skips or one position backward, using fixed Code/SortOrder. Offer and later destinations require a positive OfferAmount. Same-stage offer saves create no Stage Change entry. Draft/Withdrawn/Sold block normal progression. Generic outcome fields remain protected.
- Reservation and Closing share one exclusive phase. Entry requires Available and no other Open holder; the same transaction makes the listing Reserved. Closing → Reservation retains it; Reservation → Negotiation releases only the exact holder's reservation. A property cannot be relinked while the old or destination stage is exclusive. Reservation conflicts return safe 409 field errors without revealing competing records.
- Opportunity writes and listing mutations serialize on the affected property rows inside their transaction, then validate fresh state. Participating operations acquire Properties first, then sorted Opportunity rows, then sorted affected Contacts; a changed property association is a safe conflict. Each actual stage change creates one immutable generated Activity with stage IDs, event description, old/new outcome/offer/property state, responsible Agent and authenticated initiator. Contact recomputation and history commit or roll back with the transition. No process-local reservation lock or schema migration is used.
- Activity creation preserves authenticated authorship. Completion requires the assigned agent, including a task created by another author; that exception permits changing only completion time. Author/Manager/Admin may edit other metadata. Existing completion is distinguished from a new completion so a Manager can correct completed metadata without impersonating the performer. A completed performer's UserId cannot change, and generic PUT cannot clear CompletedAt. Clients cannot create System/Stage Change types or mark ordinary entries as generated. Generated history cannot be edited/deleted.
- An assigned Agent may read Activities on their visible opportunity and use that opportunity's exact retained buyer/property target for a linked Activity even when the buyer Contact is private. This grants no Contact detail access or unrelated private target. Completing a linked Viewing prompts optional ordinary progression to Viewing if the opportunity is earlier; completion persists if that offer is declined or progression fails.
- Note request targets are limited to Contact, Property or Opportunity with a positive EntityId. Scoped reads and creation validate target visibility; creation also checks authenticated authorship. Existing author/Manager guards and the edit window remain in force.
- Retention checks include soft-deleted references. Contacts with related properties, opportunities, activities or retained attachments cannot be deleted. Reserved/Sold properties and Open opportunities cannot be deleted. Referenced targets remain retained. Generic PUT does not expose outcome or rate fields.

## Part 03 outcome and commission commands

These cookie/CSRF-protected commands return 200 with the authorized target's normal OpportunityResponse. A new nullable `commissionAmount` is computed on the server for Won only; it is null for Open/Lost and is never stored in a new column.

| Command | Request and behavior |
| --- | --- |
| `POST /api/Opportunities/{id}/won` | Required `finalPrice` > 0, maximum `9999999999999999.99`, at most two fractional places. Only an Open Closing opportunity that solely holds its Reserved listing can win. It becomes Won with server UTC ClosedAt; Property becomes Sold; every other Open opportunity on that listing becomes Lost with `Property sold`, cleared FinalPrice and server UTC ClosedAt. Already-closed competitors remain unchanged. |
| `POST /api/Opportunities/{id}/lost` | Required exact `lostReason`: Price, Bought elsewhere, Financing, Changed mind, Property sold, No response or Other. Any Open stage may close, including against Draft/Withdrawn/Sold or inconsistent reservation state. Only a proven sole exclusive holder releases Reserved to Available; otherwise listing state is preserved. |
| `PUT /api/Opportunities/{id}/commission-rate` | Manager/Admin only; required fractional `commissionRate` in 0..1 with at most six fractional places; Open only. It changes that snapshot, never the deployment default or another deal. An unchanged numeric rate is a no-op without a new event. |

New command DTOs reject unmapped fields and excess significant fractional precision; insignificant trailing zeros are accepted. Missing/hidden targets return 404; Agent rate editing returns 403; invalid inputs/stage return 400; closed targets, invalid winning reservation or concurrency/association conflicts return 409. Existing generic PUT closed-error behavior remains compatible. Repeat closure does not create another event.

Outcome(s), property state, all affected buyer/seller status recomputation and immutable System histories share one transaction. Outcome closure retains the stage and does not fabricate Stage Change history. Each automatic loss records its own responsible Agent and the initiating authenticated user; no hidden competitor identifiers are returned. Generated events retain the existing immutability/access rules. Rate changes also receive transactional System history.

Won commission uses decimal `FinalPrice × CommissionRate`, rounded once to two decimals with the [user-approved half-up rule](../../docs/business/amendments/2026-11-26-commission-rounding.md); exact 0.005 becomes 0.01. No client float calculation or retrospective default rewrite is used.

## Remaining workflow work

Part 04 reopening is still unimplemented. It must adopt the same serialization, Contact recomputation and atomic-history protocol. Parts 01–03 do not establish complete reporting, account lifecycle, attachment or portal behavior.

Part 01 adds owner-scoped Contact/opportunity access, listing write scope, assignment guards and target visibility for the existing qualification/Note paths. These changes do not establish complete production provisioning, every account lifecycle, document storage or all attachment workflows. The Document table still does not implement file safety or external storage by itself.

## Frontend compatibility

The Part 01 staff pages and typed services now use Buyer/Seller/Both, computed Contact status, City/PropertyType, nullable Draft values and ScheduledAt/CompletedAt. Contacts exposes pre-opportunity qualification Activities; Listings supports Draft save/edit/publication; Pipeline creates fixed Open Inquiry opportunities without a mandatory offer. Manager/Admin can reach the staff workspace, while public signup remains Agent/Buyer/Seller only.

The Pipeline also supports **Edit offer → Save offer**, valid forward/back-one stage choices, visible Property status, readable generated history and optional **Move to Viewing / Keep current stage** after Viewing completion. Controls identify the buyer so competing opportunities for one listing remain distinguishable. A saved mutation followed by a failed secondary refresh is reported as saved and offers a read-only Refresh action.

Selected Open opportunities offer **Mark as Won → Final price (₱) → Confirm Won** when Closing/Reserved, and **Mark as Lost → Lost reason → Confirm Lost** at every stage. Manager/Admin can **Edit commission rate → Commission rate (decimal) → Save commission rate**. Closed views show saved outcome, timestamp, price/reason, property state and server Won commission, with scalar/outcome edit controls removed. Existing ordinary Activity/Note behavior is retained; no reopening control is added.

The directly affected Dashboard/history consumers use v2 timestamps/statuses and nullable values. The recent PHP Price display and seller-only picker behavior are retained. Buyer/seller portal prototypes, broader reporting and later outcome automation remain separate work. Builds and mocked browser fixtures are not proof of real persistence; consult the Part 01 and Part 02 delivery evidence for actual isolated API/PostgreSQL checks.
