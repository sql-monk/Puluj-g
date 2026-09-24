AIR_DEFENSE_RE = re.compile(
    r"(?i)(?<![\w])(?:ппо|пво)(?![\w])|збит|збиття|сбит|знищен|уничтож|перехоплен|перехват|мобільн\w+\s+вогнев|"
    r"працю\w*\s+(?:сили\s+)?(?:ппо|пво)|(?<![\w])(?:мінус|минус)(?![\w])"
)
NOT_AIR_DEFENSE_RE = re.compile(r"(?i)можлив\w*\s+робот|не\s+знімайте|не\s+фотографуйте|правил")
MAX_ACTIONS = 4


def extract(message, write):
    source = (message.get("sourceCode") or "").lower()
    text = message.get("text") or ""
    if source in ALERT_SOURCES or not AIR_DEFENSE_RE.search(text) or is_long_read(message):
        return
    context = lines_with_context(message)
    written = 0
    for line, line_regions in context:
        if written >= MAX_ACTIONS or not AIR_DEFENSE_RE.search(line) or NOT_AIR_DEFENSE_RE.search(line):
            continue
        names = [name for _, role, name in places_by_role(line) if role in ("at", "to")]
        if not names and len(context) <= 2:
            names = bare_places(AIR_DEFENSE_RE.sub(" ", line))
        kind = target_type(line) or target_type(text)
        for name in names[:2]:
            write("air_defense_actions", {
                "label": "ППО: " + name,
                "confidence": 0.8 if re.search(r"(?i)збит|сбит|знищен|уничтож|перехоплен", line) else 0.6,
                "geometry": place_ref(name, hints(message, line_regions)),
                "attributes": {
                    "source": source,
                    "place": name,
                    "targetType": kind[0] if kind else None,
                    "regions": line_regions,
                    "line": line[:300],
                },
            })
            written += 1
