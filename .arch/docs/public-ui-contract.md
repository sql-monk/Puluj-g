# Public UI contract v1

Status: normative U01 contract for U02–U12. It defines a new public read-only
surface; proposed endpoints are not claimed to exist yet. Normative cases:
[public-ui-contract.fixture.json](fixtures/public-ui-contract.fixture.json).
Wire time is ISO-8601 UTC and the UI renders Europe/Kyiv.

## Identity

| Term | Meaning |
| --- | --- |
| Raw-message revision | One stored revision; root is (sourceId, sourceMessageKey); sourceRevision is opaque, not numeric. A messages row is one revision. |
| Observation | One stored extracted fact (legacy Target), linked to one raw revision and zero or more aggregates. Every stored observation has a detail. |
| Track / incident / alert | Canonical moving aggregate / static aggregate / AirAlert. Incident is capability-gated; alert can have source but no raw. |
| Evidence | Observation membership supporting an aggregate, never identity. |

A public reference has kind (track, incident, alert, observation) and decimal
string id. Every database long ID is decimal string in new API, URL, cursor,
GeoJSON, map selection and SignalR v2: raw, observation, track, alert,
revision and relation IDs. UUID generations/runs are UUID strings; taxonomy and
place IDs are numbers. New UI must not convert ID to Number, do ID arithmetic,
or lexically sort decimal IDs.

Canonicality is persisted evidence-to-aggregate membership in the pinned
dataset, not TargetLink, text similarity or createsIncident. Omit an observation
only when an accessible canonical aggregate represents it; otherwise retain it
with explicit status. Details return canonicalRefs separately. Typed relations
(shared_message, evidence, continuation, duplicate, merge, split, related)
have explicit from/to, optional confidence and explanation; relations never
merge identity. Event-kind codes, entity kinds, event categories
(target, alert, incident, info) and taxonomy IDs are separate dimensions.

## Routes and UI state

| Hash | Section | Default |
| --- | --- | --- |
| #/map/live | Map / online | server map window |
| #/map/history | Map / history | 24 hours |
| #/analytics | Analytics | 24 hours |
| #/entities | Targets and events | 24 hours |
| #/entities/{kind}/{id} | Entity detail | inherited/explicit period |
| #/messages | Messages | 24 hours |
| #/messages/{rawMessageId} | Message detail | inherited/explicit period |

| Legacy input | Normalized output |
| --- | --- |
| empty hash or #/ | #/map/live |
| #/kyiv | #/map/live?preset=kyiv |
| #/stats?tab=targets&p=7d | #/analytics?metric=targets&preset=7d |
| #/stats?tab=alerts&from=…&to=… | #/analytics?metric=alerts&from=…&to=… |

Preserve compatible query values during normalization. Kyiv is a map preset,
not fifth menu item; resolve each preset immediately to absolute UTC from/to.
URL is reproducible data state: reload, Back and Forward restore it; each
section remembers its own query; transfer occurs only through explicit
drill-down. Theme, density and panel state are local display settings.
Navigation must not clear regionId.

## Filter grammar and time

Selections are comma-separated stable codes/IDs. Missing means all; empty UI
selection serializes missing and displays “All”. Same-field values are OR;
different fields are AND.

| Parameter | Scope |
| --- | --- |
| eventKinds, entityKinds | observations/entities/map and supported analytics |
| categoryIds, classIds, familyIds, modelIds | taxonomy-bearing records |
| sourceIds | observations/entities/messages/alerts |
| regionId | one actual location area |
| from, to, q | periods and supported indexed search |
| status, confidence, location, hasResults | advertised capability only |
| sort, cursor, pageSize, dataset | collection/pagination/pinned dataset |

Region matches evidence actual location plus place ancestry. Records without
location do not match. Point without PlaceId needs verified spatial containment;
multi-region areas follow a server geometry rule and never a centroid. Origin
and destination filters, where offered, are separately named predicates.

For aggregate/message filters, source, kind, taxonomy, region and confidence
must match one same evidence observation in [from,to). Multiple facts of a raw
cannot form a false match. Alert source is AirAlert.SourceId, its region is
PlaceId/ancestors, and alert taxonomy is unsupported without confirmed evidence.
Unsupported filter is typed 400, never unfiltered 200.

Intervals are UTC [from,to), require from<to, max 366 days, default 24h.
Messages use PublishedAt; observations use ObservedAt; catalogue uses qualifying
evidence ObservedAt; alerts intersect [StartedAt,EndedAt); tracks-opened
analytics uses FirstSeenAt and is labelled, not a catalogue-total drill-down.

History uses historyBasis=reconstructed: effective/event time from selected data,
not what was known then. For from<=at<to, default at=to−1ms; exact link uses
at=t and to>t. Future class, location, confidence and links cannot fill a
historical frame. Missing mutable history is unavailable, not copied from now.
Past map range selects history.

Focus precedence: explicit entity/map locator, area fit, then last manual
camera. Area bounds fit central 70% of the viewport after panels, preserving
aspect ratio on constrained axis. This is not zoom multiplication and geometry
does not invent event points. Missing/multiple locations need explanation or
choice. Show-on-map loads closed/history data outside live TTL.

## Public API v1

| Endpoint | Response |
| --- | --- |
| GET /api/public/entities | EntityPage: canonical aggregates and eligible observations |
| GET /api/public/entities/{kind}/{id} | EntityDetail, including every observation |
| GET /api/public/entities/{kind}/{id}/evidence | bounded cursor evidence |
| GET /api/public/entities/{kind}/{id}/relations | bounded cursor relations |
| GET /api/public/messages | MessagePage: one raw revision/row |
| GET /api/public/messages/{id} | raw revision, retained results and map locators |
| GET /api/public/messages/{id}/results | cursor result collection |
| GET /api/public/map/chunk | bounded map entities for at; string IDs |
| GET /api/public/dictionaries | historical metadata including disabled values |

| DTO | Required fields |
| --- | --- |
| EntitySummary | ref, title, eventKindCode/category, status, primaryTime, sourceIds, locationSummary, canonicalRefs, mapLocator, datasetId |
| EntityDetail | EntitySummary plus evidencePreview, relationsPreview, mapLocators, capability and explicit unavailable fields |
| Evidence | observationRef, rawMessageRef, ObservedAt, source, kind, taxonomy, confidence, location, membership/canonical refs |
| Relation | id string, kind, from, to, direction, confidence nullable, explanation nullable |
| MessageSummary | rawMessageRef, sourceId, sourceMessageKey, sourceRevision, PublishedAt, ReceivedAt, hasResults, resultCount |
| MessageDetail | MessageSummary plus immutable raw content/provenance, resultPreview and mapLocators |
| MapLocator | ref, at, point nullable, placeId nullable, regionId nullable, locationStatus, historyBasis |
| DatasetDescriptor | id, kind, active flag, watermark nullable, readiness, supportedKinds, supportedFilters, history availability |

The normal JSON casing is camelCase. Nullable fields are omitted rather than
invented. sourceMessageKey/sourceRevision remain strings; a raw revision is not
rewritten to its root identity in a response.

Page response fields are items, nextCursor, hasMore, appliedFilters, and dataset
(id, legacy/generation kind, optional watermark), plus capabilities keyed by
feature. A capability includes available, optional reason
(projection_not_ready/history_unavailable/filter_unsupported), supportedFilters
and optional reconstructed history basis.

Detail returns ref, canonicalRefs, evidencePreview, relationsPreview,
mapLocators and dataset descriptor. Inline evidence/relations/results preview
is maximum 10; cursor endpoints are complete. Map locator contains typed ref,
time and an actual point or area/place fit only.

One request pins its dataset for joins/evidence/relations. Cursor from another
dataset returns 409 dataset_changed; retired/unavailable pinned data returns
410 dataset_unavailable; unavailable projection is explicit. Descriptor declares
legacy or active UUID, supported kinds/history/filters and watermark/readiness:
processing.generations schema alone is not readiness.

## Pagination, errors and dictionaries

Default page size is 50, max 100. Opaque cursors fingerprint dataset/filter/sort
and stable order has final decimal-string ID tiebreaker.

Error response has one stable envelope:

~~~json
{ "error": { "code": "invalid_query", "fields": { "to": "must be after from" }, "requestId": "trace-id" } }
~~~

| Situation | HTTP code | UI |
| --- | --- | --- |
| invalid field/date/range, from>=to, >366d | 400 invalid_query | retain field and show error |
| known inapplicable filter | 400 filter_unsupported | retain and explain |
| unknown detail | 404 not_found | absent state |
| stale cursor dataset | 409 dataset_changed | refresh first page |
| unavailable pinned data | 410 dataset_unavailable | offer available data |
| projection not ready | 503 projection_unavailable | reason, never empty results |

Historical dictionary metadata keeps disabled values referenced by stored data
valid in URLs. Existing event-kinds/taxonomy endpoints alone cannot do this
because they hide disabled values.

## Budget, safety and handoff

U13 measures these release targets with test dataset size, hardware, plans and
cold/warm p95 evidence; they are not current-production claims.

| Operation | Rows/bytes | SQL/timeout | Cache | p95 cold/warm | UI |
| --- | --- | --- | --- | --- | --- |
| list | 50, max100 / 256KiB | <=3 / 2s | none | 1500/500ms | — |
| detail | 1 + previews / 256KiB | <=4 / 2s | none | 1500/500ms | — |
| evidence/relation | 50, max100 / 128KiB | <=2 / 2s | none | 1000/350ms | — |
| map chunk | 500 / 512KiB | <=3 / 2s | 32, TTL30s, single-flight | 1500/500ms | <=16ms |
| analytics | <=2000 / 512KiB | <=4 / 3s | existing 64, TTL2m, single-flight | 2500/750ms | <=16ms |

No cache is unbounded. Public DTOs exclude claims, workers/internal audits,
credentials and admin fields. Fixture coverage includes multi-fact raw,
multi-relation fact, raw without facts, region-only, old closed track, disabled
new kind/model, source-only alert, late membership/LLM result, upper boundary,
nested area, point without PlaceId, and adjacent IDs above 2^53.

| Consumer | Obligation |
| --- | --- |
| U02/U03 | route/legacy normalization/query codec/URL restore/applicability UX |
| U04/U05 | pages, errors, cursor/dataset/dictionary implementation |
| U06/U07 | string map transport, reconstructed history, 70% fit |
| U08/U09/U10 | catalogue/message/detail pagination and typed map return |
| U11/U12/U13 | labelled analytics; contract/mobile/accessibility/legacy/budget evidence |
