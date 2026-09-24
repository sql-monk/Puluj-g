"""Place references: an extractor names a place, the host finds its geometry in the ``places`` gazetteer.

Extractors run without database access, so a geometry field may carry a reference instead of GeoJSON:

* point / polygon field: ``{"place": "Кременчук", "region": "Полтавська обл."}``; ``region`` restricts the match to
  one oblast (or a foreign region), ``hint`` (one name or a list) only prefers it, ``levels`` limits
  ``places.level`` (1 oblast, 2 raion, 3 hromada, 4-6 settlements);
* line field: ``{"from": <reference>, "to": <reference>}`` where each end is a place reference or ``{"lon", "lat"}``.

``"required": true`` (top level) drops the whole entity when the place cannot be resolved, instead of storing it
without geometry.

Messages inflect names ("на Носівку", "до Славутича", "у Кременчуці"), while ``places.name_variants`` holds the
lower-cased nominative and its stem ("носівк", "носівка"). The host turns each name into the small set of forms the
inflected word could have come from; ``ee_place_geometry`` picks the best gazetteer match inside the database.
"""

from __future__ import annotations

import re
from itertools import product
from typing import Any

MAX_CANDIDATES = 64
_ENDINGS = (
    "ого", "ому", "ими", "ами", "ями", "ові", "еві", "ою", "ею", "ій", "ий", "ої", "ах", "ях", "ом", "ем",
    "ам", "ям", "их", "ім", "ый", "ой", "ая", "ое", "ую", "ей",
    "а", "я", "у", "ю", "і", "ї", "е", "є", "о", "и", "ь", "ы",
)
_REFILLS = ("", "а", "я", "и", "і", "ь", "о", "е")
# Vowel and consonant alternations of oblique cases: Харкова/Харків, Ірпеня/Ірпінь, Кременчуці/Кременчук.
_ALTERNATIONS = (("ов", "ів"), ("ев", "ів"), ("єв", "їв"), ("ен", "ін"), ("ц", "к"), ("з", "г"))
_WORD = re.compile(r"[\w'ʼ’\-.]+", re.UNICODE)
_APOSTROPHES = str.maketrans({"’": "'", "ʼ": "'", "`": "'"})
_PREFIXES = {"м.", "м", "с.", "с", "смт", "смт.", "сел.", "селище", "село", "місто", "г.", "пгт", "н.п.", "нп"}
_REFERENCE_KEYS = {"place", "region", "hint", "levels", "required"}
_POINT_KEYS = {"lon", "lat"}


def is_place_reference(kind: str, value: Any) -> bool:
    if not isinstance(value, dict) or "type" in value:
        return False
    if kind == "line":
        return "from" in value and "to" in value
    return "place" in value


def requires_geometry(kind: str, value: Any) -> bool:
    """``"required": true`` in a reference: without a resolved geometry the entity is not written at all."""
    return is_place_reference(kind, value) and value.get("required") is True


def place_reference(kind: str, value: dict[str, Any], field_name: str) -> dict[str, Any]:
    """The JSON ``ee_place_geometry`` resolves; raises ``ValueError`` for a malformed reference."""
    if not isinstance(value.get("required", False), bool):
        raise ValueError(f"field {field_name!r} required must be a boolean")
    if kind == "line":
        unknown = sorted(set(value) - {"from", "to", "required"})
        if unknown:
            raise ValueError(f"field {field_name!r} line reference has unknown keys: {', '.join(unknown)}")
        return {"from": _end(value["from"], field_name), "to": _end(value["to"], field_name)}
    return _place(value, field_name)


def _end(value: Any, field_name: str) -> dict[str, Any]:
    if isinstance(value, dict) and set(value) == _POINT_KEYS:
        return _coordinates(value, field_name)
    if isinstance(value, dict) and "place" in value:
        return _place({key: item for key, item in value.items() if key != "required"}, field_name)
    raise ValueError(f"field {field_name!r} line ends must be place references or {{lon, lat}}")


def _coordinates(value: dict[str, Any], field_name: str) -> dict[str, Any]:
    lon, lat = value["lon"], value["lat"]
    if not all(isinstance(item, (int, float)) and not isinstance(item, bool) for item in (lon, lat)):
        raise ValueError(f"field {field_name!r} lon/lat must be numbers")
    if not (-180 <= lon <= 180 and -90 <= lat <= 90):
        raise ValueError(f"field {field_name!r} lon/lat are out of range")
    return {"lon": float(lon), "lat": float(lat)}


def _place(value: dict[str, Any], field_name: str) -> dict[str, Any]:
    unknown = sorted(set(value) - _REFERENCE_KEYS)
    if unknown:
        raise ValueError(f"field {field_name!r} place reference has unknown keys: {', '.join(unknown)}")
    name = value.get("place")
    if not isinstance(name, str) or not name.strip():
        raise ValueError(f"field {field_name!r} place must be a non-empty string")
    reference: dict[str, Any] = {"names": name_candidates(name)}
    region = value.get("region")
    if region is not None:
        if not isinstance(region, str):
            raise ValueError(f"field {field_name!r} region must be a string")
        reference["regions"] = region_candidates(region)
    hint = value.get("hint")
    if hint is not None:
        hinted = [hint] if isinstance(hint, str) else hint
        if not isinstance(hinted, list) or not all(isinstance(item, str) for item in hinted):
            raise ValueError(f"field {field_name!r} hint must be a string or a list of strings")
        reference["hints"] = list(dict.fromkeys(form for item in hinted[:4] for form in region_candidates(item)))
    levels = value.get("levels")
    if levels is not None:
        if not isinstance(levels, list) or not all(isinstance(item, int) and 0 <= item <= 7 for item in levels):
            raise ValueError(f"field {field_name!r} levels must be a list of integers 0..7")
        reference["levels"] = levels
    return reference


def words(value: str) -> list[str]:
    normalized = value.translate(_APOSTROPHES).lower().replace("ё", "е")
    tokens = [token.strip(".-'") if token not in _PREFIXES else "" for token in _WORD.findall(normalized)]
    return [_generic(token) for token in tokens if token and token not in _PREFIXES]


def _generic(token: str) -> str:
    """Variants spell the generic part of a name one way: "харківськ обл", "чугуївськ район"."""
    if token.startswith(("област", "обл")):
        return "обл"
    if token.startswith(("район", "р-н")):
        return "район"
    return token


def word_stems(word: str) -> list[str]:
    """The word and what is left of it without a case ending: multi-word variants are stored as stems."""
    stems: list[str] = [word]
    for ending in _ENDINGS:
        if word.endswith(ending) and len(word) - len(ending) >= 3:
            stems.append(word[: -len(ending)])
    if word.endswith("ок") and len(word) >= 5:
        stems.append(word[:-2] + "к")  # genitive plural: "Ріпок" of Ріпки
    for stem in list(stems[1:]):
        for alternation, nominative in _ALTERNATIONS:
            if stem.endswith(alternation):
                stems.append(stem[: -len(alternation)] + nominative)
    return list(dict.fromkeys(stems))


def word_forms(word: str) -> list[str]:
    """Forms a (possibly inflected) single word could have in ``name_variants``, most literal first."""
    stems = word_stems(word)
    forms = [word, *(stem + refill for stem in stems[1:] for refill in _REFILLS)]
    return list(dict.fromkeys(form for form in forms if len(form) >= 3))


def name_candidates(value: str) -> list[str]:
    tokens = words(value)[:4]
    if not tokens:
        return []
    if len(tokens) == 1:
        return word_forms(tokens[0])[:MAX_CANDIDATES]
    per_word = [word_stems(token)[:6] for token in tokens]
    candidates = [" ".join(parts) for parts in product(*per_word)]
    return list(dict.fromkeys([" ".join(tokens), *candidates]))[:MAX_CANDIDATES]


def region_candidates(value: str) -> list[str]:
    """Region names match on the full phrase ("харківськ обл") or its first word alone ("харківськ", "брянщин")."""
    tokens = [token for token in words(value) if token not in {"рф", "росія", "білорусь"}]
    if not tokens:
        return []
    candidates = name_candidates(" ".join(tokens))
    candidates.extend(word_forms(tokens[0]))
    if len(tokens) > 1 and tokens[1].startswith("обл"):
        candidates.extend(f"{form} обл" for form in word_forms(tokens[0]))
    return list(dict.fromkeys(candidates))[: MAX_CANDIDATES * 2]
