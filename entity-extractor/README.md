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
