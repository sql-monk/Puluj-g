# Puluj Entity Extractor

The service receives one queued raw message at `POST /extract`, runs the current enabled Python extractors from
`ee_extractors`, and writes captured entities into registered `ee_*` tables. It shares PostgreSQL with Puluj but never
updates the legacy `raw_messages` processing fields or legacy entity tables.

EE tracks are retired: `RetireEntityTracks` disables their extractor, entity definition and map visibility.
The installer repeats this retirement and never enables the archived `extractors/track.py` code, including on
reinstallation. Existing `ee_tracks` rows and extraction evidence are retained. Disabled definitions are excluded
from extraction/LLM schemas and public definitions, catalogue, snapshots and related history; old public track
detail links return not found. Generic line entities and the legacy track pipeline are unaffected.

For a running installation, stop admission of EE jobs and drain active extraction requests before applying the
migration or installer; then verify both track flags are disabled and resume processing. An already-running
transaction may have read the old registry, so the migration alone is not an in-flight write barrier. Do not
clear historical tables or reprocess messages for this rollout. Rolling back the migration does not automatically
re-enable tracks because the previous operator-managed enabled state is unknown.

An extractor defines either:

```python
def extract(message, write):
    if "вибух" in (message.get("text") or "").lower():
        write("explosions", {
            "occurredAt": message.get("publishedAt"),
            "label": "Вибух",
            "attributes": {},
        })
```

or `Extractor.extract(message, write)`. `write` has exactly two parameters. Its first parameter can be the registered
physical name (`ee_explosions`), the registered entity name (`explosion`), or the unprefixed registered table alias
(`explosions`). Every alias is resolved through the enabled entity registry; arbitrary table names remain rejected.
The writer only captures a name and a key/value collection in the child process. The host validates registered fields
and commits them afterward. A failed extractor discards all writes captured by that extractor.

No event without a time: when an entity has an `occurredAt` field and the extractor (or the LLM) leaves it empty, the
host fills in the message's `publishedAt` (`receivedAt` if the source gave none).

Extractors have no database, so a geometry field may name a place instead of carrying GeoJSON; the database resolves it
against the `places` gazetteer on insert (`ee_place_geometry`, migration `EntityPlacesAndEventTime`):

```python
write("targets", {"geometry": {"place": "Носівку", "hint": ["Чернігівська обл."], "required": True}, ...})
write("alerts", {"geometry": {"place": "Чугуївський район", "region": "Харківська обл."}, ...})
```

Names may be inflected ("на Носівку", "до Славутича"). `region` restricts the match to one oblast, `hint` (one name or a
list) only prefers it, and an unqualified village name that exists in several oblasts resolves to nothing rather than a
guess. A point field gets the place's centroid, a polygon field its area (a settlement's hromada), a line field the
segment between two places or `{"lon", "lat"}` ends. With `"required": true` an unresolved place drops the entity
instead of storing it without geometry.

## Shipped extractors

`extractors/` installs seven active seeded entities (alert, target, explosion, impact,
airDefenseAction, launch, takeoff), written against the messages of the collected channels; `tests/test_extractors.py`
runs them on real message texts. The sandbox imports only the standard-library allowlist, so each stored extractor is
`common.py` followed by its own file. Store and enable them (idempotent; `--disable` stores them switched off):

```sh
python entity-extractor/extractors/install.py | docker exec -i puluj-g-postgis-1 psql -U puluj -d puluj
```

## History

The live path audits every message and every extractor call, which does not scale to the collected history. For
history, `python -m app.backfill` runs the extractors over `raw_messages` by id: one sandbox child per batch of
messages, place references resolved once per distinct place, one transaction per batch that replaces the entity rows of
its messages and moves the checkpoint (`app_settings` `EntityExtractor:Backfill:NextRawMessageId`). It can be stopped
and restarted at any time, and re-running a range writes the same rows rather than duplicates. It connects as the
database owner:

```sh
cd entity-extractor
DATABASE_URL=postgresql://puluj:<password>@localhost:5442/puluj python -m app.backfill --workers 4
```

Without `--to-id` it stops at the newest message the live path has already seen; run it again after a deployment to
cover the messages in between. `--include-disabled` runs extractors that are stored but switched off.

The live path also runs all extractors of a message in one sandbox child (falling back to one child per extractor
when that child dies or times out).

Alerts are states: `ee_alerts.state_key` names the area and `map_settings.keyField` makes the map show only the latest
row per key, so an ended alert leaves the map.

Extractor code runs in a separate process group with a wall-clock timeout, CPU/memory/process/file limits, an empty
environment, a restricted import allowlist, and no `open`, socket, subprocess, or `os` access. Timeout handling kills
the whole process group. This is containment for trusted administrator-authored rules, not a security boundary for
hostile Python: CPython introspection and the shared container kernel/filesystem cannot provide a complete sandbox.
Run untrusted rules in a separate locked-down container or VM.

## HTTP API

- `GET /health`
- `POST /extract` — camel-case delivery contract; response body is scalar `1` or `0`; when configured, requires
  `X-Entity-Extractor-Token`
- `POST /admin/validate` — syntax and entry-point validation
- `POST /admin/test` — isolated execution with captured writes and no production entity inserts

When `ADMIN_TOKEN` is set, admin endpoints require it in `X-Admin-Token`.

## Configuration

- `ConnectionStrings__Puluj` accepts the existing .NET-style PostgreSQL connection string.
- `DATABASE_URL` accepts a PostgreSQL URI as a fallback.
- `ENTITY_EXTRACTOR_TOKEN` (or `EntityExtractor__Token`) protects `/extract`. If omitted, `/extract` remains open for
  backward-compatible local deployments; production deployments should always set it.
- The LLM configuration is the one the processor uses, edited in the LLM section of the admin UI (`app_settings`
  `Llm:*`, read through `ee_get_llm_settings()`): `Llm:Enabled`, `Llm:Provider` (the active one: `Anthropic`, `OpenAI`
  or `Ollama`) and a section per provider — `Llm:Anthropic:*`, `Llm:OpenAI:*`, `Llm:Ollama:*` with its own `ApiKey`,
  `Model`, `BaseUrl` and `*UsdPerMillionTokens`. All providers can be configured at once; switching is a single
  `Llm:Provider` change and needs no restart.
- Anthropic goes over the Messages API; OpenAI and Ollama over the OpenAI chat-completions API in JSON mode, at
  `https://api.openai.com/v1` and `http://localhost:11434/v1` unless `BaseUrl` says otherwise (from a container,
  Ollama on the host is `http://host.docker.internal:11434/v1`). Ollama needs no key and is costed at zero.
- `Llm__*` environment variables (`Llm__Provider`, `Llm__Ollama__BaseUrl`, ...) are fallbacks for keys absent from
  `app_settings`; database values take precedence. `ANTHROPIC_API_KEY` / `OPENAI_API_KEY` stand in for a missing
  `Llm:Anthropic:ApiKey` / `Llm:OpenAI:ApiKey`.
- `ENTITY_EXTRACTOR_CONCURRENCY` bounds concurrently processed messages.

The LLM fallback reads the existing `Llm:*` settings and writes its full sanitized request/response and usage to the
existing `llm_requests` table.

## Tests

```sh
pip install -e '.[test]'
pytest -q
```
