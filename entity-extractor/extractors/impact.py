IMPACT_RE = re.compile(
    r"(?i)влучан|влучив|влучил|приліт|прилет|прилетіл|прилетел|попадан|падіння|падение|уламк|обломк|"
    r"(?:дрон|бпла|шахед|ракета|безпілотник)\w*\s+впа|впав\s+дрон|пошкоджен|поврежден|зруйнован|разрушен"
)
WEAPON_RE = re.compile(
    r"(?i)дрон|бпла|шахед|безпілот|беспилот|ракет|каб|бомб|бандерол|герань|удар|атак|обстріл|обстрел|ворож|"
    r"рашист|окупант|влучан|приліт|прилет|попадан"
)
NOT_IMPACT_RE = re.compile(r"(?i)гілок|дерев|погодн|негод|без\s+детонац|навчан")
MAX_IMPACTS = 4


def extract(message, write):
    source = (message.get("sourceCode") or "").lower()
    text = message.get("text") or ""
    if source in ALERT_SOURCES or not IMPACT_RE.search(text) or not WEAPON_RE.search(text) or NOT_IMPACT_RE.search(text):
        return
    if len(text) > 1500:
        return
    written = 0
    for line, line_regions in lines_with_context(message):
        if written >= MAX_IMPACTS or not IMPACT_RE.search(line):
            continue
        names = [name for _, role, name in places_by_role(line) if role == "at"]
        for name in names[:2]:
            write("impacts", {
                "label": "Влучання: " + name,
                "confidence": 0.7 if re.search(r"(?i)влучан|приліт|прилет|попадан", line) else 0.5,
                "geometry": place_ref(name, hints(message, line_regions)),
                "attributes": {"source": source, "place": name, "regions": line_regions, "line": line[:300]},
            })
            written += 1
