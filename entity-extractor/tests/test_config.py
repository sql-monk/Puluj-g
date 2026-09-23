from psycopg.conninfo import conninfo_to_dict

from app.config import Settings


def test_dotnet_connection_string_preserves_special_characters() -> None:
    settings = Settings(
        connection_string=(
            'Host=db.internal;Port=5439;Database="puluj/data";'
            'Username="ee@worker";Password="p@ss word/with?chars;and"'
        )
    )

    parsed = conninfo_to_dict(settings.postgres_dsn)

    assert parsed["host"] == "db.internal"
    assert parsed["port"] == "5439"
    assert parsed["dbname"] == "puluj/data"
    assert parsed["user"] == "ee@worker"
    assert parsed["password"] == "p@ss word/with?chars;and"


def test_llm_environment_maps_to_configuration_keys(monkeypatch) -> None:
    monkeypatch.setenv("Llm__Enabled", "true")
    monkeypatch.setenv("Llm__OpenAI__Model", "compose-model")

    environment = {key.lower(): value for key, value in Settings(_env_file=None).llm_environment.items()}

    assert environment["llm:enabled"] == "true"
    assert environment["llm:openai:model"] == "compose-model"
