MAX_TARGETS = 12


def extract(message, write):
    source = (message.get("sourceCode") or "").lower()
    if source in ALERT_SOURCES or is_long_read(message):
        return
    raw = message.get("text") or ""
    message_kind = target_type(raw)
    context = lines_with_context(message)
    written = 0
    for line, line_regions in context:
        if written >= MAX_TARGETS or NEGATIVE_RE.search(line):
            continue
        kind = target_type(line) or message_kind
        mentions = places_by_role(line)
        positions = [(name, "at") for _, role, name in mentions if role == "at"]
        heading = [(name, "to") for _, role, name in mentions if role == "to"]
        chosen = positions[:1] + heading if positions else heading
        bare = False
        if not mentions and len(context) <= 3:
            # Radar channels follow one target with bare names: "Бровари", "Пекарі/Хмільна".
            chosen = [(name, "at") for name in bare_places(line)]
            bare = True
        if not chosen:
            continue
        if kind is None:
            # Without a weapon word only movement ("курс Чабанка") or a radar channel's short update
            # ("На Боярку", "Бровари") names a target.
            short = bare or (len(context) <= 2 and len(line) <= 60)
            if not MOVEMENT_RE.search(line) and not (short and source not in OFFICIAL_SOURCES):
                continue
            kind = ("unknown", "Ціль")
        code, label = kind
        count = count_in(line)
        origins = [name for _, role, name in mentions if role == "from"]
        for name, role in chosen[:5]:
            write("targets", {
                "label": (label + " → " + name) if role == "to" else (label + ": " + name),
                "targetType": code,
                "status": "active",
                "geometry": place_ref(name, hints(message, line_regions)),
                "attributes": {
                    "place": name,
                    "role": "heading" if role == "to" else "position",
                    "count": count,
                    "from": origins[0] if origins else None,
                    "regions": line_regions,
                    "line": line[:300],
                },
            })
            written += 1
            if written >= MAX_TARGETS:
                break
