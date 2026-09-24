# Puluj Entity Extractor

The service receives one queued raw message at `POST /extract`, runs the current enabled Python extractors from
`ee_extractors`, and writes captured entities into registered `ee_*` tables. It shares PostgreSQL with Puluj but never
updates the legacy `raw_messages` processing fields or legacy entity tables.

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
write("tracks", {"geometry": {"from": {"place": "Узина"}, "to": {"place": "Васильків"}}, ...})
```

Names may be inflected ("на Носівку", "до Славутича"). `region` restricts the match to one oblast, `hint` (one name or a
list) only prefers it, and an unqualified village name that exists in several oblasts resolves to nothing rather than a
guess. A point field gets the place's centroid, a polygon field its area (a settlement's hromada), a line field the
segment between two places or `{"lon", "lat"}` ends. With `"required": true` an unresolved place drops the entity
instead of storing it without geometry.

## Shipped extractors

`extractors/` holds the extractors for the eight seeded entities (alert, target, track, explosion, impact,
airDefenseAction, launch, takeoff), written against the messages of the collected channels; `tests/test_extractors.py`
runs them on real message texts. The sandbox imports only the standard-library allowlist, so each stored extractor is
`common.py` followed by its own file. Store and enable them (idempotent; `--disable` stores them switched off):

```sh
python entity-extractor/extractors/install.py | docker exec -i puluj-g-postgis-1 psql -U puluj -d puluj
```

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
