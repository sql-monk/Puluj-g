"""The shipped extractors (extractors/*.py) against messages taken from the live Telegram and alert feeds."""

import sys
from pathlib import Path

import pytest

from app.runtime import _execute_blocking, _safe_builtins, validate_code

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "extractors"))
import install  # noqa: E402


def run(name: str, text: str | None = None, source: str = "tg_monitor_ukr", payload=None) -> list[tuple[str, dict]]:
    code = install.build(name)
    namespace = {"__name__": "extractor", "__builtins__": _safe_builtins()}
    exec(compile(code, name, "exec"), namespace, namespace)
    writes: list[tuple[str, dict]] = []
    message = {"sourceCode": source, "text": text, "rawPayload": payload, "publishedAt": "2026-09-24T03:00:00Z"}
    namespace["extract"](message, lambda table, values: writes.append((table, values)))
    return writes


@pytest.mark.parametrize("name", list(install.EXTRACTORS))
def test_every_extractor_passes_the_sandbox_policy(name: str) -> None:
    assert validate_code(install.build(name)).valid


def test_an_extractor_runs_in_the_real_sandbox() -> None:
    result = _execute_blocking(
        install.build("target"),
        {"sourceCode": "kharkov_media", "text": "❗️4х БпЛА курсом на Андріївку\n2х БпЛА курсом на Ізюм"},
        10_000,
        1_000_000,
        512,
    )
    assert result.error is None, result.stderr
    assert [write.values["geometry"]["place"] for write in result.writes] == ["Андріївку", "Ізюм"]


def test_alerts_in_ua_payload_is_an_area_state() -> None:
    payload = {
        "at": "2026-09-24T03:26:57.66+00:00",
        "kind": "alert.finished",
        "alert": {"id": 266486, "alert_type": "air_raid", "started_at": "2026-09-24T03:06:26.643Z",
                  "location_uid": "99", "location_type": "raion", "location_title": "Подільський район",
                  "location_oblast": "Одеська область", "alert_level": "yellow", "threats": []},
    }
    [(table, values)] = run("alert", source="alerts_in_ua", payload=payload)
    assert table == "alerts"
    assert values["status"] == "ended"
    assert values["occurredAt"] == "2026-09-24T03:26:57.66+00:00"
    assert values["geometry"] == {"place": "Подільський район", "required": True, "region": "Одеська область"}
    assert values["stateKey"] == "alert:подільський район|одеськ"


def test_alarm_signal_lists_share_the_state_key_of_the_structured_feed() -> None:
    writes = run("alert", "🟢 Відбій тривоги\nПодільський район (Одеська обл.)\nІзмаїльський район (Одеська обл.)",
                 source="ukrainealarmsignal")
    assert [values["stateKey"] for _, values in writes] == [
        "alert:подільський район|одеськ", "alert:ізмаїльський район|одеськ"]
    assert {values["status"] for _, values in writes} == {"ended"}


def test_alarm_signal_threat_over_a_city() -> None:
    [(_, values)] = run("alert", "🛸 Чугуїв (Харківська обл.)\nЗагроза застосування БПЛА. Перейдіть в укриття!",
                        source="ukrainealarmsignal")
    assert (values["alertType"], values["status"], values["label"]) == ("drone_threat", "active", "Чугуїв")


def test_official_dash_alert() -> None:
    [(_, values)] = run("alert", "🟡 Бучанський район — повітряна тривога, жовтий рівень: Дронова загроза (жовтий рівень)",
                        source="kyiv_ova")
    assert values["status"] == "active"
    assert values["attributes"]["level"] == "yellow"
    assert values["stateKey"] == "alert:бучанський район|київськ"


def test_targets_follow_region_headers_and_courses() -> None:
    writes = run("target", "Чернігівщина:\nБпЛА на Чернігів\nБпЛА на Носівку\n\nПолтавщина:\nБпЛА на Глобине",
                 source="raketa_trevoga")
    places = [(values["geometry"]["place"], values["geometry"]["hint"][0]) for _, values in writes]
    assert places == [("Чернігів", "Чернігівська обл."), ("Носівку", "Чернігівська обл."), ("Глобине", "Полтавська обл.")]
    assert {values["targetType"] for _, values in writes} == {"uav"}


def test_target_types_carry_the_map_filter_words() -> None:
    [(_, jet)] = run("target", "🛵 Реактивні БпЛА на півночі Чернігівщини ➡️ курсом на Славутич.", source="kpszsu")
    [(_, ballistic)] = run("target", "Пара балістик на Київ", source="tg_operatyvnohlep")
    assert jet["targetType"] == "jet_drone" and jet["label"] == "Реактивний БпЛА → Славутич"
    assert ballistic["targetType"] == "ballistic_missile"


def test_radar_channel_bare_names_are_positions_but_verbs_and_denials_are_not() -> None:
    assert [v["geometry"]["place"] for _, v in run("target", "Пекарі/Хмільна", source="cherkasy_nebbo")] == ["Пекарі", "Хмільна"]
    assert run("target", "Не фіксується.", source="tg_veselyy_pivden") == []
    assert run("target", "Відбій усім тихой ночи 🫡", source="karkivw") == []
    assert run("target", "🦇 Темный Рыцарь", source="odessa_knight") == []


def test_capitalised_prepositions_and_lower_case_second_words() -> None:
    [(_, course)] = run("target", "Курс Чабанка.", source="odessa_knight")
    [(_, city)] = run("target", "Ціль на Кривий ріг", source="radar_dnipra")
    assert course["geometry"]["place"] == "Чабанка"
    assert city["geometry"]["place"] == "Кривий ріг"


def test_active_extractors_keep_target_but_never_generate_retired_tracks() -> None:
    writes = [write for name in install.EXTRACTORS
              for write in run(name, "🛵 Від Узина курсом на Васильків", source="eradarrua")]
    assert "track" not in install.EXTRACTORS
    assert any(table == "targets" for table, _ in writes)
    assert not any(table in {"track", "tracks", "ee_tracks"} for table, _ in writes)


@pytest.mark.parametrize("enabled", [True, False])
def test_repeated_installation_always_retires_tracks(enabled: bool) -> None:
    first = install.sql(enabled)
    second = install.sql(enabled)
    assert first == second
    assert "UPDATE ee_extractors SET enabled = false WHERE name = 'track';" in first
    assert "WHERE entity_name = 'track' OR table_name = 'ee_tracks';" in first
    assert "VALUES ('track'," not in first
    assert "DELETE " not in first and "DROP " not in first
    assert f", {str(enabled).lower()}, 20, 5000)" in first


def test_media_reported_explosions() -> None:
    [(_, values)] = run("explosion", "⚠️ Шостка (Сумська обл.)\nЗМІ повідомляють про вибухи. Будьте обережні!",
                        source="ukrainealarmsignal")
    assert values["geometry"] == {"place": "Шостка", "required": True, "region": "Сумська обл."}
    assert run("explosion", "Сьогодні з 10:00 проводитимуться планові підриви ВНП, можливо чути вибухи.") == []
    [(_, dashed)] = run("explosion", "💥 Кривий Ріг - вибухи", source="tg_povitryanatrivogaaa")
    assert dashed["geometry"]["place"] == "Кривий Ріг"


def test_impacts_need_a_weapon_not_the_weather() -> None:
    [(_, values)] = run("impact", "Зафіксовано падіння ворожого безпілотника на території Чернігова.", source="phantomche")
    assert values["geometry"]["place"] == "Чернігова"
    assert run("impact", "Також у різних районах міста фіксуються випадки падіння гілок і дерев у Харкові.") == []


def test_air_defense_needs_a_place() -> None:
    [(table, values)] = run("airDefenseAction", "📡Біля Києва дорозвідка, попередньо збиття.", source="taktychna_rukavuchkaa")
    assert table == "air_defense_actions" and values["geometry"]["place"] == "Києва"
    assert run("airDefenseAction", "Мінус", source="odessa_knight") == []


def test_launches_are_points_at_every_named_site() -> None:
    writes = run("launch", "Пуски шахів з Пр. Ахтарська (14 груп) та Орла (2 групи)", source="tg_sectorv666")
    assert [values["attributes"]["site"] for _, values in writes] == ["Приморсько-Ахтарськ", "Цимбулова (Орел)"]
    assert writes[0][1]["geometry"] == {"type": "Point", "coordinates": [38.17, 46.05]}
    assert writes[0][1]["launchType"] == "shahed_drone"
    assert run("launch", "‼ Загроза балістики з Міллєрово", source="tg_sectorv666") == []


def test_takeoff_needs_an_aircraft() -> None:
    [(_, values)] = run("takeoff", "В повітрі відмічається 1х борт Ту-95мс з аеродрому «Енгельс-2».", source="strategicaviation")
    assert values["aircraftType"] == "Ту-95МС"
    assert values["geometry"]["coordinates"] == [46.21, 51.48]
    assert run("takeoff", "дрон вилетів з Києва до Василькова.", source="northern_sich_ukr") == []
