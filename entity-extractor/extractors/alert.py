ALERT_TYPES = (
    ("recon_drone_activity", r"розвідувальн"),
    ("guided_bomb_threat", r"каб|керован\w* авіаційн"),
    ("ballistic_threat", r"балісти"),
    ("missile_threat", r"ракетн|ракет"),
    ("drone_threat", r"бпла|дрон|шахед"),
    ("shelling_threat", r"обстріл|артилер"),
    ("air_raid", r"тривог"),
)
PLACE_LINE_RE = re.compile(r"^(?P<place>[^()]{3,80}?)\s*\((?P<region>[^()]*обл[^()]*)\)\s*$")
BARE_ALERT_PLACES = {"Київ", "Севастополь"}
DASH_ALERT_RE = re.compile(
    r"^(?P<place>[" + UPPER + r"][^—\n]{2,80}?)\s+—\s+(?P<status>повітряна тривога|відбій повітряної тривоги)"
    r"(?:,\s*(?P<level>жовтий|червоний)\s+рівень)?",
    re.I,
)


def state_key(place, region):
    """One key per area, whichever feed reports it: the map shows only the latest state of each key."""
    place = re.sub(r"^(?:м|с|смт)\.\s*", "", place.strip().lower().replace("’", "'").replace("ʼ", "'"))
    region_word = (region or "").strip().lower().split(" ")[0] if region else ""
    return "alert:" + re.sub(r"\s+", " ", place) + "|" + region_word.rstrip("аяоеіи.")


def alert_type(text):
    lowered = (text or "").lower()
    for code, pattern in ALERT_TYPES:
        if re.search(pattern, lowered):
            return code
    return "air_raid"


def level_of(text):
    lowered = (text or "").lower()
    if "червон" in lowered or "🔴" in (text or ""):
        return "red"
    if "жовт" in lowered or "🟡" in (text or ""):
        return "yellow"
    return None


def write_alert(write, place, region, status, kind, occurred_at, attributes, hint=None):
    reference = {"place": place, "required": True}
    if region:
        reference["region"] = region
    if hint and not region:
        reference["hint"] = hint
    values = {
        "label": place,
        "alertType": kind,
        "status": status,
        "stateKey": state_key(place, region or (hint[0] if hint else None)),
        "geometry": reference,
        "attributes": attributes,
    }
    if occurred_at:
        values["occurredAt"] = occurred_at
    write("alerts", values)


def from_alerts_in_ua(message, write):
    payload = message.get("rawPayload") or {}
    alert = payload.get("alert") or {}
    kind = payload.get("kind") or ""
    title = alert.get("location_title")
    if not title or kind not in ("alert.started", "alert.finished", "alert.updated"):
        return
    ended = kind == "alert.finished"
    location_type = alert.get("location_type") or ""
    region = None if location_type == "oblast" else alert.get("location_oblast")
    occurred = payload.get("at") if ended else (alert.get("started_at") or payload.get("at"))
    threats = alert.get("threats") or []
    write_alert(
        write,
        title,
        region,
        "ended" if ended else "active",
        alert.get("alert_type") or "air_raid",
        occurred,
        {
            "source": "alerts_in_ua",
            "alertId": alert.get("id"),
            "locationUid": alert.get("location_uid"),
            "locationType": location_type,
            "level": alert.get("alert_level"),
            "threats": [threat.get("threat_type") for threat in threats if isinstance(threat, dict)],
        },
    )


def from_alarm_signal(message, write):
    """@UkraineAlarmSignal: "🚨 Чугуївський район (Харківська обл.) ⏎ Повітряна тривога." and multi-area lists."""
    text = message.get("text") or ""
    if re.search(r"(?i)ЗМІ повідомляють|мапа тривог|станом на|планов", text):
        return
    places = []
    status_lines = []
    for line in clean(text):
        match = PLACE_LINE_RE.match(line)
        if match:
            places.append((match.group("place").strip(), match.group("region").strip()))
        elif line in BARE_ALERT_PLACES:
            places.append((line, None))
        else:
            status_lines.append(line)
    status_text = " ".join(status_lines)
    if not places or not re.search(r"(?i)тривог|загроз|активніст|рівень", status_text):
        return
    ended = bool(re.search(r"(?i)відбій", status_text))
    kind = "air_raid" if ended else alert_type(status_text)
    level = None if ended else level_of(text)
    for place, region in places[:40]:
        write_alert(write, place, region, "ended" if ended else "active", kind, None,
                    {"source": "ukrainealarmsignal", "level": level, "status": status_text[:200]})


def from_air_alert(message, write):
    """@air_alert_ua: "🔴 09:53 Повітряна тривога в Корюківський район ⏎ ... ⏎ #Корюківський_район"."""
    text = message.get("text") or ""
    first = (text.splitlines() or [""])[0]
    if not re.search(r"(?i)тривог|загроз|атак|обстріл|каб|відбій", first):
        return
    tag = re.search(r"#(\S+)\s*$", text.strip())
    place = None
    if tag:
        place = tag.group(1).replace("_", " ").split(" та ")[0]
        place = re.sub(r"^м\s+", "", place)
        if re.search(r"[а-яіїє][А-ЯІЇЄ]", place):
            place = None  # "#БілгородДністровський_район" lost its hyphen: read the sentence instead
    if place is None:
        spelled = re.search(r"\bв\s+(?:м\.\s*)?([" + UPPER + r"][^.!\n]+?(?:район|громада|область))", first)
        place = spelled.group(1) if spelled else None
    if not place:
        return
    ended = bool(re.search(r"(?i)відбій", first)) or "🟢" in first
    write_alert(write, place.strip(), None, "ended" if ended else "active",
                "air_raid" if ended else alert_type(first), None,
                {"source": "air_alert_ua", "level": None if ended else level_of(first), "status": first[:200]})


def from_dash_lines(message, write):
    """Official channels: "🟡 Бучанський район — повітряна тривога, жовтий рівень: Дронова загроза"."""
    for line in clean(message.get("text"))[:3]:
        match = DASH_ALERT_RE.match(line)
        if not match:
            continue
        ended = match.group("status").lower().startswith("відбій")
        level = (match.group("level") or "").lower()
        detail = line[match.end():]
        write_alert(
            write,
            match.group("place").strip(),
            None,
            "ended" if ended else "active",
            "air_raid" if ended or not detail.strip() else alert_type(detail),
            None,
            {
                "source": message.get("sourceCode"),
                "level": None if ended or not level else "red" if level.startswith("черв") else "yellow",
            },
            hints(message, []),
        )
        return


def extract(message, write):
    source = (message.get("sourceCode") or "").lower()
    if source == "alerts_in_ua":
        from_alerts_in_ua(message, write)
    elif source == "ukrainealarmsignal":
        from_alarm_signal(message, write)
    elif source == "air_alert_ua":
        from_air_alert(message, write)
    else:
        from_dash_lines(message, write)
