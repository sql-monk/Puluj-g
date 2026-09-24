LAUNCH_RE = re.compile(r"(?i)пуск|запуск|запущен|запустил|стартувал|старт\s+(?:ракет|балістик)|виходи\s+балістик|вихід\s+балістик")
NOT_LAUNCH_RE = re.compile(
    r"(?i)загроз\w*\s+(?:пуск|застосуван|балістик)|угроз\w*|можлив|возможн|ймовірн\w*\s+пуск|підготовк|"
    r"не\s+(?:було|фіксу)|без\s+пуск|пускові\s+установк|мережу\s+пуск|покинул\w*\s+пускові|йде\s+на\s+пуск|"
    r"заход\w*\s+на\s+пуск|на\s+пускові"
)
BOARD_LINE_RE = re.compile(r"^(?P<site>[^—\-–]{3,40}?)\s*[—–-]\s*пуск(?P<rest>[^\n]*)$", re.I)
MAX_LAUNCHES = 8


def sites_in(text):
    """Every launch area a line names, in reading order: "з Орла 2 групи, Шаталово 3 групи та Ахтарська"."""
    found = []
    for name, lon, lat, pattern in SITE_RES:
        match = pattern.search(text or "")
        if match:
            found.append((match.start(), name, lon, lat))
    return [(name, lon, lat) for _, name, lon, lat in sorted(found)]


def write_launch(write, message, line, site, kind):
    code, label = kind or ("unknown", "ціль невідома")
    heading = destinations(line)
    write("launches", {
        "label": "Пуск: " + label + " · " + site[0] + (" → " + heading[0] if heading else ""),
        "launchType": code,
        # The launch site; where the weapons head, when the message says, stays an attribute.
        "geometry": {"type": "Point", "coordinates": [site[1], site[2]]},
        "attributes": {"site": site[0], "to": heading[0] if heading else None, "count": count_in(line),
                       "source": message.get("sourceCode"), "line": line[:300]},
    })


def extract(message, write):
    source = (message.get("sourceCode") or "").lower()
    text = message.get("text") or ""
    if source in ALERT_SOURCES or not LAUNCH_RE.search(text) or is_long_read(message):
        return
    message_kind = target_type(text)
    written = 0
    for line, _ in lines_with_context(message):
        board = BOARD_LINE_RE.match(line)
        if board:
            # PoltavaRanger's launch board: "Курськ — пуск реактивних БпЛА 23:56"; a "підготовка" line is no launch.
            sites = sites_in(board.group("site"))[:1]
            kind = target_type(board.group("rest")) or message_kind
        elif LAUNCH_RE.search(line) and not NOT_LAUNCH_RE.search(line):
            sites = sites_in(line)
            kind = target_type(line) or message_kind
        else:
            continue
        for site in sites:
            if written >= MAX_LAUNCHES:
                return
            write_launch(write, message, line, site, kind)
            written += 1
