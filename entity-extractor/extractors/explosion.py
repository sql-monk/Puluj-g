EXPLOSION_RE = re.compile(r"(?i)вибух|взрыв|бахнул|бахає|бабах|гучно|громко|детонац")
NOT_EXPLOSION_RE = re.compile(
    r"(?i)без\s+детонац|планов|підрив\w*\s+внп|можливо\s+чути|не\s+лякайтес|навчан|учени|не\s+було\s+вибух|"
    r"вибухов\w+\s+хвил|вибухівк|взрывчат|розмінуван"
)
DASH_PLACE_RE = re.compile(r"^(" + PLACE_LIST + r")\s*[-–—:]\s*(?:чути\s+)?(?:вибух|взрыв|гучно|громко)", re.I)
MAX_EXPLOSIONS = 6


def extract(message, write):
    source = (message.get("sourceCode") or "").lower()
    text = message.get("text") or ""
    if not EXPLOSION_RE.search(text) or NOT_EXPLOSION_RE.search(text) or is_long_read(message):
        return
    if source == "ukrainealarmsignal":
        # "⚠️ Шостка (Сумська обл.) ⏎ ЗМІ повідомляють про вибухи."
        lines = clean(text)
        match = re.match(r"^(?P<place>[^()]{3,80}?)\s*(?:\((?P<region>[^()]*обл[^()]*)\))?\s*$", lines[0] if lines else "")
        if match and re.search(r"(?i)повідомляють про вибух", text):
            place = match.group("place").strip()
            region = match.group("region")
            write("explosions", {
                "label": "Вибухи: " + place,
                "confidence": 0.8,
                "geometry": place_ref(place, [], region.strip() if region else None),
                "attributes": {"source": source, "reported": "media"},
            })
        return
    if source in ALERT_SOURCES:
        return
    written = 0
    for line, line_regions in lines_with_context(message):
        if written >= MAX_EXPLOSIONS or not EXPLOSION_RE.search(line) or NOT_EXPLOSION_RE.search(line):
            continue
        dashed = DASH_PLACE_RE.match(line)
        names = [name.strip() for name in LIST_SPLIT.split(dashed.group(1))] if dashed else [
            name for _, role, name in places_by_role(line) if role == "at"
        ]
        names = [name for name in names if is_place_name(name)]
        for name in names[:3]:
            write("explosions", {
                "label": "Вибух: " + name,
                "confidence": 0.7,
                "geometry": place_ref(name, hints(message, line_regions)),
                "attributes": {"source": source, "place": name, "regions": line_regions, "line": line[:300]},
            })
            written += 1
