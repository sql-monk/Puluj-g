import httpx

from app.config import Settings
from app.main import create_app
from app.models import EntityDefinition, EntityField
from app.repository import ProcessingClaim
from tests.fakes import FakeRepository


async def test_extract_returns_scalar_json() -> None:
    repository = FakeRepository()
    repository.claim = ProcessingClaim(10, "completed", 1)
    app = create_app(repository, Settings(admin_token="secret"))

    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://test") as client:
        response = await client.post(
            "/extract",
            json={"deliveryId": "11111111-1111-1111-1111-111111111111", "rawMessageId": 1, "sourceId": 1, "text": "x"},
        )

    assert response.status_code == 200
    assert response.json() == 1


async def test_extract_requires_dedicated_service_token_when_configured() -> None:
    repository = FakeRepository()
    repository.claim = ProcessingClaim(10, "completed", 1)
    app = create_app(repository, Settings(admin_token="admin-secret", service_token="delivery-secret"))
    payload = {
        "deliveryId": "11111111-1111-1111-1111-111111111111",
        "rawMessageId": 1,
        "sourceId": 1,
        "text": "x",
    }

    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://test") as client:
        missing = await client.post("/extract", json=payload)
        admin_token = await client.post(
            "/extract", headers={"x-entity-extractor-token": "admin-secret"}, json=payload
        )
        accepted = await client.post(
            "/extract", headers={"x-entity-extractor-token": "delivery-secret"}, json=payload
        )

    assert missing.status_code == 401
    assert admin_token.status_code == 401
    assert accepted.status_code == 200


async def test_admin_validation_requires_token_and_reports_diagnostic() -> None:
    app = create_app(FakeRepository(), Settings(admin_token="secret"))

    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://test") as client:
        unauthorized = await client.post("/admin/validate", json={"code": "def extract(: pass"})
        response = await client.post(
            "/admin/validate",
            headers={"x-admin-token": "secret"},
            json={"code": "def extract(: pass"},
        )

    assert unauthorized.status_code == 401
    assert response.status_code == 200
    assert response.json()["valid"] is False
    assert response.json()["diagnostics"][0]["line"] == 1


async def test_admin_test_validates_alias_and_fields_without_committing() -> None:
    repository = FakeRepository()
    repository.definitions = [
        EntityDefinition(
            entity_definition_id=1,
            entity_name="explosion",
            table_name="ee_explosions",
            fields=[EntityField(name="label", type="text", required=True)],
        )
    ]
    app = create_app(repository, Settings(admin_token="secret"))

    async with httpx.AsyncClient(transport=httpx.ASGITransport(app=app), base_url="http://test") as client:
        accepted = await client.post(
            "/admin/test",
            headers={"x-admin-token": "secret"},
            json={
                "code": "def extract(message, write):\n    write('explosions', {'label': message['text']})",
                "message": {"text": "Вибух"},
                "timeoutMs": 15000,
            },
        )
        unknown_field = await client.post(
            "/admin/test",
            headers={"x-admin-token": "secret"},
            json={
                "code": "def extract(message, write):\n    write('explosions', {'unknown': 1})",
                "message": {"text": "Вибух"},
                "timeoutMs": 15000,
            },
        )
        unknown_table = await client.post(
            "/admin/test",
            headers={"x-admin-token": "secret"},
            json={
                "code": "def extract(message, write):\n    write('legacy_events', {'label': 'x'})",
                "message": {},
                "timeoutMs": 15000,
            },
        )

    assert accepted.json()["error"] is None
    assert accepted.json()["result"] == 1
    assert unknown_field.json()["result"] is None
    assert "unknown fields" in unknown_field.json()["error"]
    assert unknown_table.json()["result"] is None
    assert "not registered and enabled" in unknown_table.json()["error"]
    assert repository.committed == []
