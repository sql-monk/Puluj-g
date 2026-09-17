"""Generates docs/diagrams/*.drawio from compact Python descriptions.

Edit the DIAGRAMS dict below, run `python docs/diagrams/build.py`, then `node docs/diagrams/export.mjs` for PNGs.
Keeping the source here (instead of only the XML) makes the diagrams reviewable and reproducible.
"""
from __future__ import annotations
import html
import pathlib

OUT = pathlib.Path(__file__).parent

# ---- palette -------------------------------------------------------------------------------------------------
BOX = "rounded=1;whiteSpace=wrap;html=1;fontSize=12;strokeWidth=1.2;"
NOTE = "text;html=1;align=left;verticalAlign=top;whiteSpace=wrap;fontSize=11;fontColor=#555555;"
GROUP = "rounded=1;whiteSpace=wrap;html=1;verticalAlign=top;align=left;spacingLeft=8;spacingTop=2;fontStyle=1;fontSize=13;dashed=1;strokeWidth=1.2;"
EDGE = "edgeStyle=orthogonalEdgeStyle;rounded=1;html=1;endArrow=block;endFill=1;strokeWidth=1.5;fontSize=11;"
DB = "shape=cylinder3;whiteSpace=wrap;html=1;boundedLbl=1;backgroundOutline=1;size=12;fontSize=12;strokeWidth=1.2;"
DIAMOND = "rhombus;whiteSpace=wrap;html=1;fontSize=12;strokeWidth=1.2;"

C = {
    "src": "fillColor=#fff2cc;strokeColor=#d6b656;",     # sources / input
    "worker": "fillColor=#dae8fc;strokeColor=#6c8ebf;",  # worker
    "db": "fillColor=#e1d5e7;strokeColor=#9673a6;",      # database
    "api": "fillColor=#d5e8d4;strokeColor=#82b366;",     # api
    "web": "fillColor=#ffe6cc;strokeColor=#d79b00;",     # browser
    "grey": "fillColor=#f5f5f5;strokeColor=#666666;",
    "red": "fillColor=#f8cecc;strokeColor=#b85450;",
}


class D:
    def __init__(self, name: str, title: str):
        self.name, self.title = name, title
        self.cells: list[str] = []
        self.n = 1

    def _id(self) -> str:
        self.n += 1
        return f"c{self.n}"

    def box(self, id: str, x: int, y: int, w: int, h: int, label: str, style: str = BOX, color: str = "grey") -> str:
        st = style + C.get(color, color)
        self.cells.append(
            f'<mxCell id="{id}" value="{html.escape(label, quote=True)}" style="{st}" vertex="1" parent="1">'
            f'<mxGeometry x="{x}" y="{y}" width="{w}" height="{h}" as="geometry"/></mxCell>')
        return id

    def group(self, id: str, x: int, y: int, w: int, h: int, label: str, color: str) -> str:
        return self.box(id, x, y, w, h, label, GROUP, color)

    def note(self, x: int, y: int, w: int, h: int, label: str) -> str:
        return self.box(self._id(), x, y, w, h, label, NOTE, "")

    def edge(self, src: str, dst: str, label: str = "", style: str = "", exit=None, entry=None, dashed=False) -> None:
        st = EDGE + style
        if exit:
            st += f"exitX={exit[0]};exitY={exit[1]};exitDx=0;exitDy=0;"
        if entry:
            st += f"entryX={entry[0]};entryY={entry[1]};entryDx=0;entryDy=0;"
        if dashed:
            st += "dashed=1;"
        self.cells.append(
            f'<mxCell id="{self._id()}" value="{html.escape(label, quote=True)}" style="{st}" edge="1" parent="1" '
            f'source="{src}" target="{dst}"><mxGeometry relative="1" as="geometry"/></mxCell>')

    def write(self) -> None:
        body = "".join(self.cells)
        xml = (f'<mxfile host="puluj" modified="2026-09-11T00:00:00.000Z" agent="build.py" version="24.0.0">'
               f'<diagram id="{self.name}" name="{html.escape(self.title, quote=True)}"><mxGraphModel dx="1200" dy="800" grid="1" '
               f'gridSize="10" guides="1" tooltips="1" connect="1" arrows="1" fold="1" page="1" pageScale="1" pageWidth="1400" '
               f'pageHeight="900" math="0" shadow="0"><root><mxCell id="0"/><mxCell id="1" parent="0"/>{body}</root>'
               f'</mxGraphModel></diagram></mxfile>')
        (OUT / f"{self.name}.drawio").write_text(xml, encoding="utf-8")
        print("wrote", self.name)


# ---- 1. system overview -------------------------------------------------------------------------------------
def overview() -> None:
    d = D("01-overview", "Огляд системи")
    d.group("gS", 20, 40, 200, 250, "Джерела (OSINT)", "src")
    d.box("alerts", 40, 80, 160, 50, "alerts.in.ua<br><font style='font-size:10px'>REST, опитування 30 с</font>", color="src")
    d.box("tg", 40, 145, 160, 50, "Telegram-канали<br><font style='font-size:10px'>MTProto, live + історія</font>", color="src")
    d.box("rss", 40, 210, 160, 50, "RSS / web<br><font style='font-size:10px'>(інтерфейс готовий)</font>", color="src")

    d.group("gW", 280, 40, 300, 470, "Puluj.Worker", "worker")
    d.box("col", 300, 80, 260, 50, "Collectors + Supervisor<br><font style='font-size:10px'>кожен колектор — окрема задача, backoff при збої</font>", color="worker")
    d.box("ing", 300, 150, 260, 50, "RawMessageIngestor<br><font style='font-size:10px'>INSERT … ON CONFLICT DO NOTHING (ідемпотентно)</font>", color="worker")
    d.box("prs", 300, 220, 260, 60, "Normalizer → RuleParser (+LLM fallback)<br>→ TargetBuilder<br><font style='font-size:10px'>текст → факти → Target</font>", color="worker")
    d.box("cor", 300, 300, 260, 60, "Deduplicator → Correlator<br><font style='font-size:10px'>Target → TargetTrack + Revision</font>", color="worker")
    d.box("wd", 300, 380, 260, 50, "TrackWatchdog / IndexProvider<br><font style='font-size:10px'>закриття треків за таймаутом; кеш таксономії та газетира</font>", color="worker")
    d.note(300, 440, 260, 60, "Один процес, кілька фонових служб. Обробка одного повідомлення — одна транзакція; після COMMIT публікується NOTIFY.")

    d.box("db", 640, 120, 220, 330, "PostgreSQL + PostGIS<br><br>raw_messages<br>targets · target_links<br>target_tracks<br>air_alerts · app_settings<br>places · таксономія · sources<br><br><font style='font-size:10px'>єдине джерело істини<br>ролі: puluj (owner, Worker) · puluj_reader (Api) · puluj_admin (Admin)</font>", DB, "db")

    d.group("gA", 920, 40, 240, 300, "Puluj.Api (роль puluj_reader — лише читання)", "api")
    d.box("rest", 940, 80, 200, 50, "REST /api/*<br><font style='font-size:10px'>local :5267 · Docker :8090→8080</font>", color="api")
    d.box("hub", 940, 150, 200, 50, "SignalR /hubs/map<br><font style='font-size:10px'>TrackUpserted, TrackClosed, AlertChanged</font>", color="api")
    d.box("lst", 940, 220, 200, 50, "LISTEN puluj_events<br><font style='font-size:10px'>по події дочитує сутність з БД</font>", color="api")
    d.box("spa", 940, 285, 200, 40, "static: wwwroot (збірка web/)", color="api")

    d.group("gB", 1220, 40, 200, 300, "Браузер", "web")
    d.box("map", 1240, 80, 160, 60, "Карта (MapLibre)<br><font style='font-size:10px'>тривоги, район, шлях, прогноз</font>", color="web")
    d.box("eta", 1240, 160, 160, 60, "ETA / fade<br><font style='font-size:10px'>рахується локально; точка користувача не покидає браузер</font>", color="web")
    d.box("hist", 1240, 240, 160, 50, "Історія / деталі<br><font style='font-size:10px'>replay, джерела</font>", color="web")

    d.group("gM", 920, 370, 240, 210, "Puluj.Admin (роль puluj_admin)", "worker")
    d.box("adm", 940, 410, 200, 50, "REST /api/admin/*<br><font style='font-size:10px'>local :5268 · Docker :8091→8081</font>", color="worker")
    d.box("ops", 940, 475, 200, 50, "Стан компонентів<br><font style='font-size:10px'>heartbeat Worker, /api/health Api, колектори, БД</font>", color="worker")
    d.box("adms", 940, 535, 200, 35, "static: admin.html (збірка web/)", color="worker")
    d.box("logs", 640, 480, 220, 60, "logs/ (спільна тека)<br><font style='font-size:10px'>worker-, api-, admin-&lt;день&gt;.log (Serilog)</font>", color="grey")
    d.box("padm", 1240, 400, 160, 60, "Адмін-панель<br><font style='font-size:10px'>налаштування, статистика, логи</font>", color="web")

    d.edge("alerts", "col", exit=(1, 0.5), entry=(0, 0.3))
    d.edge("tg", "col", exit=(1, 0.5), entry=(0, 0.7))
    d.edge("col", "ing")
    d.edge("ing", "prs")
    d.edge("prs", "cor")
    d.edge("cor", "wd")
    d.edge("ing", "db", "RawMessage", exit=(1, 0.5), entry=(0, 0.2))
    d.edge("prs", "db", "Target", exit=(1, 0.5), entry=(0, 0.45))
    d.edge("cor", "db", "Track, Revision, NOTIFY", exit=(1, 0.5), entry=(0, 0.7))
    d.edge("db", "rest", "SQL", exit=(1, 0.3), entry=(0, 0.5))
    d.edge("db", "lst", "NOTIFY {type,id}", exit=(1, 0.75), entry=(0, 0.5), dashed=True)
    d.edge("lst", "hub", exit=(0.5, 0), entry=(0.5, 1))
    d.edge("rest", "map", "JSON", exit=(1, 0.5), entry=(0, 0.5))
    d.edge("hub", "map", "WebSocket", exit=(1, 0.5), entry=(0, 0.9), dashed=True)
    d.edge("db", "adm", "SQL (read/write app_settings, sources)", exit=(1, 0.95), entry=(0, 0.5))
    d.edge("logs", "adm", "tail", exit=(1, 0.5), entry=(0, 0.9), dashed=True)
    d.edge("adm", "padm", "JSON", exit=(1, 0.5), entry=(0, 0.5))
    d.edge("ops", "rest", "GET /api/health", exit=(0.5, 0), entry=(0.5, 1), dashed=True)
    d.write()


# ---- 2. processing pipeline ---------------------------------------------------------------------------------
def pipeline() -> None:
    d = D("02-pipeline", "Шлях повідомлення")
    x, w = 380, 300
    d.box("raw", x, 30, w, 50, "RawMessage (Pending)<br><font style='font-size:10px'>оригінальний текст/payload, незмінний</font>", color="db")
    d.box("kind", x + 70, 110, 160, 70, "структурований<br>payload?", DIAMOND, "grey")
    d.box("alert", 60, 115, 240, 60, "AlertsInUaHandler<br><font style='font-size:10px'>AirAlert + Target(AirRaidAlert/AlertCancelled), без NLP</font>", color="worker")
    d.box("norm", x, 210, w, 60, "Normalizer<br><font style='font-size:10px'>NFC, emoji→слова, lower-case, речення → сегменти, токени</font>", color="worker")
    d.box("rule", x, 300, w, 70, "RuleParser<br><font style='font-size:10px'>aliases (стеми) · газетир (місце + роль: з / на / курсом на) · напрямок · кількість · тип події · hedge</font>", color="worker")
    d.box("any", x + 70, 400, 160, 70, "є факти?", DIAMOND, "grey")
    d.box("llm", 60, 405, 240, 60, "LlmParser (fallback)<br><font style='font-size:10px'>лише коли правила мовчать, а текст схожий на загрозу; тільки коди з таксономії</font>", color="worker")
    d.box("bld", x, 500, w, 70, "TargetBuilder<br><font style='font-size:10px'>рівень класифікації + впевненість (обмежена довірою джерела); район ≠ точка; напрямок; metadata «чому»</font>", color="worker")
    d.box("obs", x, 600, w, 50, "Target", color="db")
    d.box("dedup", x, 680, w, 60, "Deduplicator<br><font style='font-size:10px'>те саме місце/клас/подія ±3 хв з іншого повідомлення → дублікат (зберігається)</font>", color="worker")
    d.box("corr", x, 770, w, 70, "Correlator<br><font style='font-size:10px'>скоринг проти активних треків: час · простір · напрямок · клас ≥ 0.6 → приєднати, інакше новий трек</font>", color="worker")
    d.box("trk", x, 870, w, 60, "TargetTrack + TargetTrackRevision<br><font style='font-size:10px'>ревізія = знімок для історії (час події)</font>", color="db")
    d.box("ntf", x, 960, w, 50, "COMMIT → NOTIFY puluj_events → SignalR", color="api")

    d.box("err", 760, 300, 240, 70, "Помилка стадії<br><font style='font-size:10px'>rollback, processing_errors, attempts++; після 3 спроб — Failed. Решта повідомлень не чекає</font>", color="red")
    d.note(760, 500, 260, 90, "Кожне повідомлення може дати 0..N targets (по одному на сегмент/загрозу). Повідомлення без фактів — теж результат (метрика parser.unmatched).")
    d.note(760, 680, 260, 110, "Тривоги/відбої з тексту не створюють AirAlert (це робить лише alerts.in.ua), але «відбій» закриває треки в області.")

    d.edge("raw", "kind")
    d.edge("kind", "alert", "так", exit=(0, 0.5), entry=(1, 0.5))
    d.edge("kind", "norm", "ні")
    d.edge("norm", "rule")
    d.edge("rule", "any")
    d.edge("any", "llm", "ні", exit=(0, 0.5), entry=(1, 0.5))
    d.edge("any", "bld", "так")
    d.edge("llm", "bld", exit=(0.6, 1), entry=(0, 0.5))
    d.edge("alert", "obs", exit=(0.15, 1), entry=(0, 0.5))
    d.edge("bld", "obs")
    d.edge("obs", "dedup")
    d.edge("dedup", "corr")
    d.edge("corr", "trk")
    d.edge("trk", "ntf")
    d.edge("rule", "err", exit=(1, 0.5), entry=(0, 0.5), dashed=True)
    d.write()


# ---- 3. data model ------------------------------------------------------------------------------------------
def datamodel() -> None:
    d = D("03-data-model", "Модель даних і provenance")
    T = "swimlane;fontStyle=1;childLayout=stackLayout;horizontal=1;startSize=26;horizontalStack=0;resizeParent=1;resizeParentMax=0;resizeLast=0;collapsible=0;marginBottom=0;html=1;fontSize=12;strokeWidth=1.2;"
    R = "text;strokeColor=none;fillColor=none;align=left;verticalAlign=middle;spacingLeft=6;spacingRight=4;overflow=hidden;rotatable=0;points=[[0,0.5],[1,0.5]];portConstraint=eastwest;html=1;fontSize=11;"

    def table(id: str, x: int, y: int, w: int, title: str, rows: list[str], color: str) -> None:
        h = 26 + 22 * len(rows)
        d.box(id, x, y, w, h, title, T, color)
        for i, r in enumerate(rows):
            rid = f"{id}_r{i}"
            d.cells.append(f'<mxCell id="{rid}" value="{html.escape(r, quote=True)}" style="{R}" vertex="1" parent="{id}">'
                           f'<mxGeometry y="{26 + 22 * i}" width="{w}" height="22" as="geometry"/></mxCell>')

    table("sources", 40, 40, 220, "sources", ["code, name, type", "trust_level 0..1, priority", "config jsonb (channel, …), secrets jsonb"], "src")
    table("raw", 40, 170, 220, "raw_messages", ["source_id, source_message_id ★", "published_at / received_at", "raw_text, raw_payload jsonb", "hash ★, processing_status, attempts"], "db")
    table("obs", 340, 120, 260, "targets", ["raw_message_id, source_id, segment_text", "event_type, observed_at", "category / class / family / model", "model_/classification_confidence, confidence", "location_kind, location (centroid), accuracy_km", "location_place_id, origin_/destination_place_id", "direction_deg, direction_kind, direction_confidence", "duplicate_of_target_id", "parser_metadata jsonb (правила, спани)"], "db")
    table("tto", 680, 200, 220, "track_targets", ["target_track_id, target_id", "sequence", "association_confidence 0..1", "association_reason jsonb"], "db")
    table("trk", 980, 120, 240, "target_tracks", ["status, closed_reason", "category / class / family / model", "first_seen_at, last_seen_at, updated_at", "last_location*, track_geometry", "direction_*, object_count", "track_confidence, target_count", "distinct_source_count, last_source_id"], "db")
    table("rev", 980, 340, 240, "target_track_revisions", ["target_track_id, revision_at ★", "повна копія стану треку", "target_id (тригер)"], "db")
    table("places", 340, 380, 260, "places (газетир)", ["external_key, name, name_variants[]", "level: Region · District (район) · Hromada", "· City…Village · NamedArea; parent_id", "geometry, centroid, radius_km"], "grey")
    table("tax", 680, 520, 220, "таксономія", ["target_categories → classes", "→ families → models", "metadata: speed, fade, window", "target_model_aliases (стеми)"], "grey")
    table("alerts", 40, 320, 220, "air_alerts", ["place_id, alert_type", "started_at, ended_at", "source_alert_id ★"], "db")
    table("links", 680, 40, 220, "target_links (m : n)", ["from_target_id → to_target_id", "probability 0..1 (сума на ціль ≤ 1)", "distance_km, minutes_apart,", "heading_diff_deg, required_minutes"], "db")
    table("srcstat", 40, 470, 220, "source_daily_stats / source_copies", ["source_id, day: targets, copies,", "copied_by, lead_seconds_sum", "copier → original, day: count, delay"], "src")

    d.edge("sources", "raw", "1 : n", exit=(0.5, 1), entry=(0.5, 0))
    d.edge("raw", "obs", "1 : 0..n", exit=(1, 0.5), entry=(0, 0.5))
    d.edge("obs", "tto", "1 : 0..1", exit=(1, 0.5), entry=(0, 0.5))
    d.edge("obs", "links", "тригер: кінематика", exit=(1, 0.15), entry=(0, 0.5), dashed=True)
    d.edge("obs", "srcstat", "тригер: дублі", exit=(0.05, 1), entry=(1, 0.3), dashed=True)
    d.edge("tto", "trk", "n : 1", exit=(1, 0.5), entry=(0, 0.5))
    d.edge("trk", "rev", "1 : n (append-only)", exit=(0.5, 1), entry=(0.5, 0))
    d.edge("obs", "places", "location / origin / destination", exit=(0.5, 1), entry=(0.5, 0), dashed=True)
    d.edge("obs", "tax", "класифікація", exit=(0.9, 1), entry=(0.5, 0), dashed=True)
    d.edge("trk", "tax", exit=(0, 0.85), entry=(1, 0.5), dashed=True)
    d.edge("alerts", "places", exit=(1, 0.5), entry=(0, 0.5), dashed=True)
    d.edge("raw", "alerts", "start/end", exit=(0.5, 1), entry=(0.5, 0), dashed=True)
    d.note(40, 590, 260, 90, "★ — ключ ідемпотентності / replay.<br>Ланцюжок походження: карта → track → track_targets → target → raw_message → source → URL оригіналу. Нічого не видаляється; дублікати лишаються.")
    d.write()


# ---- 4. correlation decision flow ---------------------------------------------------------------------------
def correlation() -> None:
    d = D("04-correlation", "Кореляція нового Target")
    x, w = 420, 300
    d.box("in", x, 30, w, 50, "Target (TargetObserved, з категорією)", color="db")
    d.box("dup", x + 60, 110, 180, 80, "є таке саме за ±3 хв<br>з іншого повідомлення?", DIAMOND, "grey")
    d.box("dupY", 40, 120, 300, 70, "Дублікат<br><font style='font-size:10px'>duplicate_of = оригінал; прив'язати до його треку (provenance); інше джерело → +1 до впевненості оригіналу</font>", color="worker")
    d.box("cand", x, 230, w, 80, "Кандидати: активні треки тієї ж категорії,<br>сумісний клас, last_seen ±2 год<br><font style='font-size:10px'>без якоря (ні район, ні пункт) → треку немає; трек, який у цьому ж повідомленні вже продовжила інша ціль, не бере</font>", color="worker")
    d.box("score", x, 320, w, 110, "Скоринг кожного кандидата<br><font style='font-size:10px'>0.30·час (вікно класу) + 0.35·простір (розрив між площами якорів, полігони областей, ≤ швидкість×Δt + 30 км) + 0.15·напрямок + 0.20·клас/модель<br>= 0 якщо стрибок неможливий, немає якоря або те саме джерело за &lt; 8 хв назвало інший пункт</font>", color="worker")
    d.box("thr", x + 60, 470, 180, 80, "найкращий ≥ 0.6 ?", DIAMOND, "grey")
    d.box("new", 40, 480, 300, 60, "Новий TargetTrack<br><font style='font-size:10px'>association_confidence = 1</font>", color="worker")
    d.box("att", x, 590, w, 80, "Приєднати до треку<br><font style='font-size:10px'>класифікація лише уточнюється; новіший → last_location, шлях, напрямок; старіший → тільки provenance; association_reason зберігає розклад балів</font>", color="worker")
    d.box("conf", x, 700, w, 60, "track_confidence = найкраща target<br>+1 за ≥2 джерела, +1 за ≥3 повідомлення", color="worker")
    d.box("rev", x, 790, w, 50, "Revision (час події) → NOTIFY TrackUpserted", color="db")
    d.box("links", 800, 520, 280, 110, "БД, тригер на targets: target_links<br><font style='font-size:10px'>попередники у вікні класу, які могли долетіти на крейсерській швидкості (розрив між площами, розворот до 4 хв); ймовірності нормовані до суми ≤ 1; дубль = 1. Сліди на карті — лише з цих зв'язків. Той самий тригер веде копії джерел і їх рейтинг.</font>", color="db")

    d.box("close1", 800, 230, 280, 70, "Відбій тривоги / «загроза минула»<br><font style='font-size:10px'>закриває активні треки в тій області (Cancelled)</font>", color="red")
    d.box("close2", 800, 320, 280, 70, "TrackWatchdog (щохвилини)<br><font style='font-size:10px'>без оновлень 2 × вікно класу → Closed(timeout)</font>", color="red")
    d.note(800, 420, 280, 80, "Вікна класів: БпЛА 60 хв, крилаті 25 хв, балістика 10 хв (TargetClass.Metadata, змінюється в БД).")

    d.edge("in", "dup")
    d.edge("dup", "dupY", "так", exit=(0, 0.5), entry=(1, 0.5))
    d.edge("dup", "cand", "ні")
    d.edge("cand", "score")
    d.edge("score", "thr")
    d.edge("thr", "new", "ні", exit=(0, 0.5), entry=(1, 0.5))
    d.edge("thr", "att", "так")
    d.edge("att", "conf")
    d.edge("new", "conf", exit=(0.5, 1), entry=(0, 0.5))
    d.edge("dupY", "conf", exit=(1, 0.8), entry=(0, 0.2))
    d.edge("conf", "rev")
    d.write()


# ---- 5. realtime + history ----------------------------------------------------------------------------------
def realtime() -> None:
    d = D("05-realtime-history", "Реальний час і історія")
    cols = {"w": ("Worker", 60), "p": ("PostgreSQL", 360), "a": ("Api", 660), "b": ("Браузер", 960)}
    for k, (name, x) in cols.items():
        d.box(f"h{k}", x, 20, 200, 40, name, color={"w": "worker", "p": "db", "a": "api", "b": "web"}[k])
        d.cells.append(f'<mxCell id="l{k}" style="endArrow=none;dashed=1;html=1;strokeColor=#999999;" edge="1" parent="1">'
                       f'<mxGeometry relative="1" as="geometry"><mxPoint x="{x + 100}" y="60" as="sourcePoint"/><mxPoint x="{x + 100}" y="560" as="targetPoint"/></mxGeometry></mxCell>')

    def step(id: str, k: str, y: int, label: str, color: str) -> None:
        x = cols[k][1]
        d.box(id, x, y, 200, 44, label, color=color)

    step("s1", "w", 90, "1. COMMIT транзакції повідомлення", "worker")
    step("s2", "p", 90, "2. NOTIFY puluj_events {type,id}", "db")
    step("s3", "a", 90, "3. LISTEN → SELECT track/alert", "api")
    step("s4", "b", 90, "4. SignalR TrackUpserted → store → карта", "web")
    d.edge("s1", "s2", exit=(1, 0.5), entry=(0, 0.5))
    d.edge("s2", "s3", exit=(1, 0.5), entry=(0, 0.5), dashed=True)
    d.edge("s3", "s4", exit=(1, 0.5), entry=(0, 0.5), dashed=True)

    step("r1", "b", 190, "reconnect / старт сторінки", "web")
    step("r2", "a", 190, "GET /api/snapshot (live)", "api")
    d.edge("r1", "r2", exit=(0, 0.5), entry=(1, 0.5))
    d.note(60, 190, 260, 44, "події за час розриву не буферизуються — клієнт просто перечитує стан")

    step("t1", "b", 290, "кожні 15 с: tick()", "web")
    d.note(60, 290, 260, 60, "fade та ETA залежать від часу і рахуються в браузері — сервер не бере участі")

    step("h1", "b", 380, "слайдер історії → at = T", "web")
    step("h2", "a", 380, "GET /api/snapshot?at=T", "api")
    step("h3", "p", 380, "DISTINCT ON (track) … revision_at ≤ T", "db")
    d.edge("h1", "h2", exit=(0, 0.5), entry=(1, 0.5))
    d.edge("h2", "h3", exit=(0, 0.5), entry=(1, 0.5))
    step("h4", "b", 450, "карта показує стан на T; SignalR ігнорується", "web")
    d.edge("h3", "h4", exit=(0.5, 1), entry=(0, 0.5))
    d.note(60, 450, 260, 90, "Ревізії штампуються часом події (observed_at), тому дочитана з історії Telegram стрічка відтворюється так, як розгорталася, а не як оброблялася.")
    d.write()


# ---- 6. code-level building blocks -------------------------------------------------------------------------
def codeclasses() -> None:
    d = D("06-code-classes", "Ключові класи та межі проєктів")
    d.group("host", 30, 30, 245, 590, "Puluj.Worker / hosting", "worker")
    d.box("init", 50, 75, 205, 65, "DatabaseInitializer : IHostedService<br><font style='font-size:10px'>advisory lock → MigrateAsync → seed → exit для ролі migrate</font>", color="worker")
    d.box("collect", 50, 165, 205, 55, "Collectors / Supervisor<br><font style='font-size:10px'>Telegram, AlertsInUa → IncomingMessage</font>", color="worker")
    d.box("claims", 50, 245, 205, 55, "RawMessageClaims<br><font style='font-size:10px'>Pending → InProgress, SKIP LOCKED</font>", color="worker")
    d.box("proc", 50, 325, 205, 70, "RawMessageProcessor<br><font style='font-size:10px'>одна транзакція; parse поза store-lock, sinks під lock</font>", color="worker")
    d.box("watch", 50, 425, 205, 65, "TrackWatchdog : BackgroundService<br><font style='font-size:10px'>закриває за class window; revision + event</font>", color="worker")
    d.box("analytics", 50, 520, 205, 65, "Analytics Worker / AnalysisRunner<br><font style='font-size:10px'>окрема схема analytics, читає raw_messages</font>", color="worker")

    d.group("infra", 335, 30, 255, 590, "Infrastructure", "db")
    d.box("ingest", 355, 75, 215, 65, "RawMessageIngestor<br><font style='font-size:10px'>idempotent INSERT … ON CONFLICT DO NOTHING</font>", color="db")
    d.box("context", 355, 170, 215, 65, "PulujDbContext : DbContext<br><font style='font-size:10px'>EF entities, migrations, configurations</font>", color="db")
    d.box("queue", 355, 265, 215, 55, "RawMessageQueue<br><font style='font-size:10px'>processor polling / wake-up</font>", color="db")
    d.box("notify", 355, 350, 215, 65, "INotifyPublisher<br><font style='font-size:10px'>NOTIFY puluj_events тільки після COMMIT</font>", color="db")
    d.box("settings", 355, 445, 215, 55, "SettingsStore<br><font style='font-size:10px'>app_settings перекриває config/env</font>", color="db")
    d.box("domain", 355, 530, 215, 65, "Domain entities<br><font style='font-size:10px'>RawMessage · Target · TargetTrack · Revision · Source</font>", color="db")

    d.group("processing", 650, 30, 300, 590, "Puluj.Processing", "worker")
    d.box("norm", 670, 75, 260, 50, "INormalizer / Normalizer", color="worker")
    d.box("parse", 670, 150, 260, 65, "IParser → RuleParser / LlmParser<br><font style='font-size:10px'>факти з тексту; LLM лише fallback</font>", color="worker")
    d.box("build", 670, 240, 260, 55, "TargetBuilder<br><font style='font-size:10px'>ParsedFact → Target</font>", color="worker")
    d.box("sink", 670, 320, 260, 65, "ITargetSink<br><font style='font-size:10px'>DeduplicationSink · CorrelationSink · TextAlertSink</font>", color="worker")
    d.box("corr", 670, 410, 260, 65, "Correlator (pure static)<br><font style='font-size:10px'>AnchorOf · Score · SelectBestTrack</font>", color="worker")
    d.box("indexes", 670, 510, 260, 65, "IndexProvider : BackgroundService / IIndexes<br><font style='font-size:10px'>taxonomy + gazetteer кеш</font>", color="worker")

    d.group("edge", 1010, 30, 300, 590, "HTTP / UI", "api")
    d.box("api", 1030, 85, 260, 65, "Puluj.Api<br><font style='font-size:10px'>REST + LISTEN + SignalR; роль puluj_reader</font>", color="api")
    d.box("admin", 1030, 185, 260, 65, "Puluj.Admin<br><font style='font-size:10px'>settings / ops / logs; роль puluj_admin</font>", color="api")
    d.box("web", 1030, 290, 260, 65, "web/ React + MapLibre<br><font style='font-size:10px'>map, history, ETA; admin SPA</font>", color="web")
    d.note(1030, 400, 260, 105, "Стрілки показують головні runtime-залежності, а не повний DI-граф. Межі проєктів підказують, де розміщувати нову логіку: домен — без інфраструктури; EF/SQL — Infrastructure; текст і кореляція — Processing.")

    # Keep only the local data-flow edges here. Runtime communication across API/UI is documented in diagram 05;
    # avoiding every DI relation keeps this code-level map legible.
    d.edge("collect", "ingest", "IncomingMessage", exit=(1, .5), entry=(0, .5))
    d.edge("ingest", "context", "write", exit=(.5, 1), entry=(.5, 0))
    d.edge("claims", "queue", "claim", exit=(1, .5), entry=(0, .5))
    d.edge("queue", "proc", "raw id", exit=(0, .7), entry=(1, .35))
    d.edge("norm", "parse")
    d.edge("parse", "build")
    d.edge("build", "sink")
    d.edge("sink", "corr")
    d.write()


# ---- 7. Docker ports and first empty-database deploy ---------------------------------------------------------
def deployment() -> None:
    d = D("07-deployment", "Puluj-G: Docker-порти та перший запуск порожньої БД")
    d.group("host", 30, 30, 260, 560, "Host / оператор", "web")
    d.box("cmd", 55, 80, 210, 65, "docker compose -p puluj-g<br>-f deploy/docker-compose.yml up --build", color="web")
    d.box("browser", 55, 185, 210, 80, "Браузер<br><font style='font-size:10px'>карта localhost:8090<br>адмінка localhost:8091</font>", color="web")
    d.box("diag", 55, 310, 210, 70, "Локальна діагностика БД<br><font style='font-size:10px'>localhost:5442 (psql, tests)</font>", color="web")
    d.note(55, 425, 210, 100, "Ці порти не перетинаються з базовим Puluj на 5432 / 8080 / 8081. Локальний запуск без Docker використовує 5267 / 5268 / 5269.")

    d.group("compose", 350, 30, 700, 560, "Compose project puluj-g (окрема network і managed volumes)", "grey")
    d.box("db", 380, 95, 235, 95, "postgis<br><font style='font-size:10px'>container :5432<br>volume puluj-g_pgdata<br>healthcheck pg_isready</font>", DB, "db")
    d.box("mig", 700, 95, 290, 95, "migrate (one-shot)<br><font style='font-size:10px'>DatabaseInitializer: advisory lock → 18+ EF migrations → roles → seed → exit 0</font>", color="worker")
    d.box("services", 700, 250, 290, 145, "Після migrate = service_completed_successfully<br><br>collector-telegram · collector-alerts<br>processor × N · api · admin · analytics", color="worker")
    d.box("api", 380, 265, 235, 55, "api: container :8080<br>host :8090", color="api")
    d.box("admin", 380, 345, 235, 55, "admin: container :8081<br>host :8091", color="api")
    d.box("intern", 380, 430, 610, 70, "Усі контейнери підключаються як Host=postgis;Port=5432<br><font style='font-size:10px'>внутрішній порт не змінювався; змінено лише host publishing</font>", color="grey")

    d.group("state", 1110, 30, 260, 560, "Стан нової БД", "db")
    d.box("empty", 1135, 85, 210, 55, "Новий порожній том", color="db")
    d.box("schema", 1135, 175, 210, 75, "Схема + PostGIS<br><font style='font-size:10px'>__EFMigrationsHistory, таблиці, функції, ролі</font>", color="db")
    d.box("seed", 1135, 285, 210, 75, "Початкові дані<br><font style='font-size:10px'>таксономія, джерела, газетир</font>", color="db")
    d.box("ready", 1135, 400, 210, 65, "Готово для сервісів<br><font style='font-size:10px'>api/admin/collectors/processor запускаються</font>", color="db")

    d.edge("cmd", "db", "створює volume", exit=(1, .3), entry=(0, .5))
    d.edge("db", "mig", "healthy", exit=(1, .5), entry=(0, .5))
    d.edge("mig", "services", "exit 0")
    d.edge("db", "api", "SQL", exit=(.5, 1), entry=(.5, 0))
    d.edge("db", "admin", "SQL", exit=(.5, 1), entry=(.5, 0))
    d.edge("browser", "api", "HTTP :8090", exit=(1, .3), entry=(0, .5))
    d.edge("browser", "admin", "HTTP :8091", exit=(1, .7), entry=(0, .5))
    d.edge("diag", "db", "TCP :5442", exit=(1, .5), entry=(0, .7), dashed=True)
    d.edge("empty", "schema", "migrations")
    d.edge("schema", "seed", "seeders")
    d.edge("seed", "ready", "migrate exits 0")
    d.edge("mig", "schema", exit=(1, .4), entry=(0, .5), dashed=True)
    d.edge("services", "ready", exit=(1, .7), entry=(0, .5), dashed=True)
    d.write()


DIAGRAMS = [overview, pipeline, datamodel, correlation, realtime, codeclasses, deployment]

if __name__ == "__main__":
    for fn in DIAGRAMS:
        fn()
