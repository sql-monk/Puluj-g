from functools import lru_cache

from psycopg.conninfo import make_conninfo
from pydantic import AliasChoices, Field
from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_file=".env", extra="ignore", case_sensitive=False, populate_by_name=True)

    database_url: str = Field(
        default="postgresql://puluj:puluj@localhost:5442/puluj",
        validation_alias="DATABASE_URL",
    )
    connection_string: str | None = Field(default=None, validation_alias="ConnectionStrings__Puluj")
    admin_token: str | None = Field(
        default=None,
        validation_alias=AliasChoices("ADMIN_TOKEN", "Admin__Token"),
    )
    service_token: str | None = Field(
        default=None,
        validation_alias=AliasChoices("ENTITY_EXTRACTOR_TOKEN", "EntityExtractor__Token"),
    )
    concurrency: int = Field(
        default=4,
        ge=1,
        le=64,
        validation_alias=AliasChoices("ENTITY_EXTRACTOR_CONCURRENCY", "EntityExtractor__Concurrency"),
    )
    default_timeout_ms: int = Field(default=5_000, ge=100, le=60_000)
    max_output_bytes: int = Field(default=256_000, ge=1_024, le=10_000_000)
    max_memory_mb: int = Field(default=256, ge=32, le=2_048)
    llm_enabled: bool = Field(default=False, validation_alias="Llm__Enabled")
    llm_provider: str = Field(default="Anthropic", validation_alias="Llm__Provider")
    llm_model: str = Field(default="claude-opus-5", validation_alias="Llm__Model")
    llm_base_url: str | None = Field(default=None, validation_alias="Llm__BaseUrl")
    anthropic_api_key: str | None = Field(default=None, validation_alias="ANTHROPIC_API_KEY")
    openai_api_key: str | None = Field(default=None, validation_alias="OPENAI_API_KEY")
    anthropic_base_url: str = "https://api.anthropic.com"

    @property
    def postgres_dsn(self) -> str:
        value = self.connection_string or self.database_url
        if ";" not in value:
            return value
        pairs: dict[str, str] = {}
        for part in _split_connection_string(value):
            if "=" in part:
                key, item = part.split("=", 1)
                pairs[key.strip().lower()] = _unquote_connection_value(item.strip())
        return make_conninfo(
            host=pairs.get("host", "localhost"),
            port=pairs.get("port", "5432"),
            dbname=pairs.get("database", pairs.get("initial catalog", "puluj")),
            user=pairs.get("username", pairs.get("user id", "puluj")),
            password=pairs.get("password", "puluj"),
        )


def _split_connection_string(value: str) -> list[str]:
    parts: list[str] = []
    current: list[str] = []
    quote: str | None = None
    index = 0
    while index < len(value):
        character = value[index]
        if quote is not None:
            current.append(character)
            if character == quote:
                if index + 1 < len(value) and value[index + 1] == quote:
                    current.append(value[index + 1])
                    index += 1
                else:
                    quote = None
        elif character in {"'", '"'}:
            quote = character
            current.append(character)
        elif character == ";":
            if current:
                parts.append("".join(current))
                current = []
        else:
            current.append(character)
        index += 1
    if quote is not None:
        raise ValueError("unterminated quoted value in ConnectionStrings__Puluj")
    if current:
        parts.append("".join(current))
    return parts


def _unquote_connection_value(value: str) -> str:
    if len(value) >= 2 and value[0] == value[-1] and value[0] in {"'", '"'}:
        quote = value[0]
        return value[1:-1].replace(quote * 2, quote)
    return value


@lru_cache
def get_settings() -> Settings:
    return Settings()
