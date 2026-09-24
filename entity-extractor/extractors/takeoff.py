TAKEOFF_RE = re.compile(
    r"(?i)зл[іе]т|злетів|злетіл|взлет|взлёт|вил[іе]т|вилетів|вилетіл|вылет|піднял|поднял|в\s+повітрі|в\s+воздухе|"
    r"активніст\w*\s+(?:тактичн|стратегічн)|активност\w*\s+(?:тактическ|стратегическ)"
)
AIRCRAFT = (
    ("Ту-22М3", r"ту-?22"),
    ("Ту-95МС", r"ту-?95"),
    ("Ту-160", r"ту-?160"),
    ("МіГ-31К", r"м[іи]г-?31"),
    ("Су-34", r"су-?34"),
    ("Су-35", r"су-?35"),
    ("Су-30", r"су-?30"),
    ("Су-25", r"су-?25"),
    ("Су-24", r"су-?24"),
    ("Іл-76", r"[іи]л-?76"),
    ("А-50", r"а-?50\b"),
    ("Тактична авіація", r"тактичн\w* авіаці|тактическ\w* авиаци|авіагруп|авиагрупп|саральот|самольот|самолет|борт"),
    ("Стратегічна авіація", r"стратегічн\w* авіаці|стратегическ\w* авиаци|стратег"),
)
AIRCRAFT_RES = tuple((label, re.compile(pattern, re.I)) for label, pattern in AIRCRAFT)
NOT_TAKEOFF_RE = re.compile(r"(?i)не\s+актив|неактив|не\s+зафіксовано|відсутн|не\s+фіксу|попередження\s+про|посадк")
MAX_TAKEOFFS = 4


def aircraft_in(text):
    for label, pattern in AIRCRAFT_RES:
        if pattern.search(text or ""):
            return label
    return None


def extract(message, write):
    source = (message.get("sourceCode") or "").lower()
    text = message.get("text") or ""
    if source in ALERT_SOURCES or not TAKEOFF_RE.search(text) or not aircraft_in(text) or is_long_read(message):
        return
    written = 0
    for line, line_regions in lines_with_context(message):
        if written >= MAX_TAKEOFFS or not TAKEOFF_RE.search(line) or NOT_TAKEOFF_RE.search(line):
            continue
        aircraft = aircraft_in(line) or aircraft_in(text)
        # A drone "вилітає на Київщину" is no takeoff: only an aircraft named in the message makes one.
        if aircraft is None or (target_type(line) or ("", ""))[0] in ("jet_drone", "shahed_drone", "geran_drone", "gerbera_drone", "recon_drone", "uav", "fpv_drone"):
            continue
        site = site_in(line)
        values = {
            "label": "Зліт: " + aircraft + (" · " + site[0] if site else ""),
            "aircraftType": aircraft,
            "attributes": {"airfield": site[0] if site else None, "count": count_in(line),
                           "source": source, "line": line[:300]},
        }
        if site:
            values["geometry"] = {"type": "Point", "coordinates": [site[1], site[2]]}
        write("takeoffs", values)
        written += 1
