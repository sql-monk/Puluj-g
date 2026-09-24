ORIGIN_REGION_RE = re.compile(r"(?<![\w'])(?:від|з|із|зі|от|из|со|с)\s+(\S+(?:\s+\S+)?)", re.I)
MAX_TRACKS = 8


def origin_region(line):
    """ "з Брянщини", "від Брянської" — a region is a coarse but honest start of a route."""
    for match in ORIGIN_REGION_RE.finditer(line):
        for name, pattern in REGION_PATTERNS:
            if pattern.match(match.group(1)) or re.match(r"(?i)" + name.split(" ")[0][:-2], match.group(1)):
                return match.start(), name
    return None


def extract(message, write):
    source = (message.get("sourceCode") or "").lower()
    if source in ALERT_SOURCES or is_long_read(message):
        return
    message_kind = target_type(message.get("text") or "")
    written = 0
    for line, line_regions in lines_with_context(message):
        if written >= MAX_TRACKS or NEGATIVE_RE.search(line):
            continue
        mentions = places_by_role(line)
        destination = next(((position, name) for position, role, name in mentions if role == "to"), None)
        if destination is None:
            continue
        start = next(
            ((position, name, False) for position, role, name in mentions
             if role in ("from", "at") and position < destination[0] and name != destination[1]),
            None,
        )
        if start is None:
            region = origin_region(line)
            if region is None or region[0] > destination[0]:
                continue
            start = (region[0], region[1], True)
        kind = target_type(line) or message_kind
        if kind is None:
            if not MOVEMENT_RE.search(line):
                continue
            kind = ("unknown", "Ціль")
        code, label = kind
        region_hints = hints(message, line_regions)
        origin = {"place": start[1]} if start[2] else place_ref(start[1], region_hints)
        write("tracks", {
            "label": label + ": " + start[1] + " → " + destination[1],
            "geometry": {"from": origin, "to": place_ref(destination[1], region_hints), "required": True},
            "attributes": {
                "targetType": code,
                "from": start[1],
                "to": destination[1],
                "count": count_in(line),
                "regions": line_regions,
                "line": line[:300],
            },
        })
        written += 1
