# Shared helpers of the Puluj entity extractors. install.py prepends this file to every extractor, because the
# sandbox imports nothing but the standard-library allowlist. Geometry is never guessed here: an extractor names
# the place ({"place": ..., "region"/"hint": ...}) and the host resolves it against the places gazetteer.
import re

# Oblasts and the neighbouring regions messages name: canonical name (what the host resolves), then the stems of
# the "-щина" form and of the adjective that must be followed by "обл" ("харківськ... обл", "харьковск... обл").
REGIONS = (
    ("Вінницька обл.", "вінниччин|винничин", "вінницьк|винницк"),
    ("Волинська обл.", "волин[іьяю]", "волинськ|волынск"),
    ("Дніпропетровська обл.", "дніпропетровщин|днепропетровщин|дніпрощин", "дніпропетровськ|днепропетровск"),
    ("Донецька обл.", "донеччин|донетчин", "донецьк|донецк"),
    ("Житомирська обл.", "житомирщин", "житомирськ|житомирск"),
    ("Закарпатська обл.", "закарпатт", "закарпатськ|закарпатск"),
    ("Запорізька обл.", "запоріжчин|запорожчин", "запорізьк|запорожск"),
    ("Івано-Франківська обл.", "прикарпатт|франківщин", "івано-франківськ|ивано-франковск"),
    ("Київська обл.", "київщин|киевщин", "київськ|киевск"),
    ("Кіровоградська обл.", "кіровоградщин|кировоградщин|кропивниччин", "кіровоградськ|кировоградск"),
    ("Луганська обл.", "луганщин", "луганськ|луганск"),
    ("Львівська обл.", "львівщин|львовщин", "львівськ|львовск"),
    ("Миколаївська обл.", "миколаївщин|николаевщин", "миколаївськ|николаевск"),
    ("Одеська обл.", "одещин|одесчин|одесщин", "одеськ|одесск"),
    ("Полтавська обл.", "полтавщин", "полтавськ|полтавск"),
    ("Рівненська обл.", "рівненщин|ровенщин", "рівненськ|ровенск"),
    ("Сумська обл.", "сумщин", "сумськ|сумск"),
    ("Тернопільська обл.", "тернопільщин|тернопольщин", "тернопільськ|тернопольск"),
    ("Харківська обл.", "харківщин|харьковщин|слобожанщин", "харківськ|харьковск"),
    ("Херсонська обл.", "херсонщин", "херсонськ|херсонск"),
    ("Хмельницька обл.", "хмельниччин|хмельнитчин", "хмельницьк|хмельницк"),
    ("Черкаська обл.", "черкащин|черкасчин", "черкаськ|черкасск"),
    ("Чернівецька обл.", "буковин", "чернівецьк|черновицк"),
    ("Чернігівська обл.", "чернігівщин|черниговщин", "чернігівськ|черниговск"),
    ("АР Крим", "крим|крым", "кримськ|крымск"),
    ("Брянська область", "брянщин", "брянськ|брянск"),
    ("Курська область", "курщин", "курськ|курск"),
    ("Бєлгородська область", "бєлгородщин|белгородщин", "бєлгородськ|белгородск"),
)
REGION_PATTERNS = tuple(
    (
        name,
        re.compile(
            r"(?<![\w'])(?:(?:" + shchyna + r")\w*|(?:" + adjective + r")\w*\s+обл\w*\.?|(?:"
            + "|".join(stem[:5] for stem in adjective.split("|")) + r")\w*\.\s*обл\w*\.?)",  # "Черніг. обл"
            re.I,
        ),
    )
    for name, shchyna, adjective in REGIONS
)

# A channel's home region: a hint for the gazetteer, never a filter (its drones fly into the neighbours too).
SOURCE_REGIONS = {
    "tg_kremen_sv": "Полтавська обл.", "poltavaradar": "Полтавська обл.", "poltavaranger": "Полтавська обл.",
    "karkivw": "Харківська обл.", "kharkivlife": "Харківська обл.", "kharkov_media": "Харківська обл.",
    "nablydatel_dozor": "Харківська обл.", "place_kharkiv": "Харківська обл.", "tg_kuptg": "Харківська обл.",
    "tg_monitor1654": "Харківська обл.", "tg_tlknewsua": "Харківська обл.",
    "tg_odessaveter": "Одеська обл.", "tg_temporis_odesa": "Одеська обл.", "odessa_inform": "Одеська обл.",
    "odessa_knight": "Одеська обл.", "tg_renihub": "Одеська обл.",
    "tg_vanek_nikolaev": "Миколаївська обл.", "tg_korabely_media": "Миколаївська обл.",
    "nikalert": "Дніпропетровська обл.", "dnepr_nagladach": "Дніпропетровська обл.",
    "radar_dnipra": "Дніпропетровська обл.",
    "cherkasy_nebbo": "Черкаська обл.",
    "my_safety_chernigiv": "Чернігівська обл.", "northern_sich_ukr": "Чернігівська обл.",
    "phantomche": "Чернігівська обл.", "radarchernihiv": "Чернігівська обл.", "shahedchernihiv": "Чернігівська обл.",
    "tg_sumygo": "Сумська обл.", "tg_sumyregion": "Сумська обл.", "tg_svessainfo": "Сумська обл.",
    "tg_krolevetsnews": "Сумська обл.",
    "tg_zaborzp": "Запорізька обл.", "info_zp": "Запорізька обл.", "taktychna_rukavuchkaa": "Запорізька обл.",
    "tg_kherson_monitoring": "Херсонська обл.", "tg_kherson_non_drone": "Херсонська обл.",
    "kyiv_ova": "Київська обл.", "kyiv_kmva": "Київська обл.", "dangerouskiev": "Київська обл.",
    "tg_ukraineradar_24_7": "Київська обл.", "war_monitor": "Київська обл.", "eradarrua": "Київська обл.",
    "deraketaua": "Київська обл.",
}
# Structured alert feeds: the alert extractor reads them, the free-text extractors leave them alone.
ALERT_SOURCES = {"alerts_in_ua", "ukrainealarmsignal", "air_alert_ua"}
# Official channels write whole sentences; a bare "На Боярку" means a tracked target only in a radar channel.
OFFICIAL_SOURCES = {"kpszsu", "kyiv_ova", "kyiv_kmva"}

UPPER = "А-ЯЁІЇЄҐЫЭ"
LETTERS = "а-яёіїєґыэъ"
LOWER = LETTERS + "'ʼ’"
NAME = "[" + UPPER + "][" + LOWER + r"]+(?:-[" + UPPER + LOWER + "][" + LOWER + "]+)?"
# Two-word names whose second word people write in lower case: "Кривий ріг", "Біла церква".
PLACE = NAME + r"(?:\s+" + NAME + r"|\s+(?:ріг|рогу|розі|рог|церква|церкви|церкві|церкву))?"
PLACE_LIST = PLACE + r"(?:\s*(?:,|/|\\|\bта\b|\bі\b|\bй\b|\bи\b)\s*" + PLACE + ")*"
PLACE_RE = re.compile(PLACE)
LIST_SPLIT = re.compile(r"\s*(?:,|/|\\|\bта\b|\bі\b|\bй\b|\bи\b)\s*")
SETTLEMENT_PREFIX = r"(?:(?:м|с|смт|сел|н\.п|пгт|г)\.?\s*)?"

# Roles of a place in a sentence. Order matters only for readability; every match is kept with its position.
ROLE_PATTERNS = (
    ("to", r"(?:курсом|курс|вектором|вектор|рухається|летить|летять|летит|летят|прямує|прямують)\s+(?:на|до|в бік|у бік)"),
    ("to", r"курсом|курс|вектор"),
    ("to", r"(?:в|у)\s+напрямку(?:\s+на)?|в\s+напрямі|в\s+направлении|в\s+сторону|у\s+сторону|в\s+бік|у\s+бік"),
    ("to", r"на|до|к|ко"),
    ("from", r"від|з|із|зі|зо|от|со|с|из"),
    ("at", r"над|біля|повз|поблизу|поруч\s+з|неподалік|в\s+районі|у\s+районі|в\s+р-ні|в\s+р-н|в\s+район|у\s+район"),
    ("at", r"возле|около|мимо|в\s+районе|через|через\s+район|на\s+території|на\s+околиц\w*|по"),
    ("at", r"в|у|во"),
)
ROLE_RES = tuple(
    (role, re.compile(r"(?<![\w'])(?i:" + words + r")\s+" + SETTLEMENT_PREFIX + "(" + PLACE_LIST + ")"))
    for role, words in ROLE_PATTERNS
)

# Capitalised words that open a sentence or name a weapon, not a place.
NOT_PLACES = {
    "увага", "уважно", "відбій", "отбой", "загроза", "угроза", "тривога", "тревога", "ппо", "пво", "мінус", "минус",
    "шахед", "шахеди", "шахеды", "шахедів", "герань", "герані", "гербер", "гербери", "бпла", "бплa", "дрон", "дрони",
    "ракета", "ракети", "ракеты", "балістика", "балістики", "баллистика", "каб", "каби", "кабів", "реактивний",
    "реактивні", "реактив", "борт", "борти", "пуск", "пуски", "зліт", "виліт", "вилет", "взлет", "ціль", "цілі",
    "слідкуємо", "слідкую", "попередньо", "далі", "далее", "ще", "новий", "нова", "нові", "вже", "тихо",
    "україна", "україни", "україні", "україну", "україною", "україн", "рф", "росія", "росії", "россия",
    "тот", "чзв", "лбз", "зсу", "всу", "фпв", "бандероль", "бандеролі", "циркон", "калібр", "іскандер",
    "молнія", "ланцет", "орлан", "зала", "мопед", "мопеди", "підписатись", "підписатися", "підписуйтесь",
    "навігація", "канал", "чат", "бот", "радар", "монітор", "моніторинг", "резерв", "поддержать", "связь",
    "київщину", "область", "району", "район", "громада", "громади", "місто", "міста", "центр", "центру",
    "північ", "південь", "схід", "захід", "півночі", "півдня", "сходу", "заходу", "берег", "берега", "лівий",
    "правий", "лівому", "правому", "передмістя", "околиці", "окраїни", "акваторії", "акваторія", "море", "моря",
    "морі", "впав", "впала", "впали", "упал", "упали", "вихід", "виходи", "сховатись", "попереджаю", "будьте", "будь", "прямуйте", "перейдіть", "негайно", "ворог", "ворожі",
}


SIGNATURE_RE = re.compile(
    r"(?i)підписат|подписат|підпишись|написати нам|надіслати новину|поддержать|звʼязок|зв'язок з нами|навігація|"
    r"наш додаток|прислати фото|темный рыцарь|одесский ветер|skyops|tlkinst|канал со стрим|розвідка неба|"
    r"підтримати канал|радар дніпра|sumy go|донбас оперативний|абревіатура|тривога\s+нікополь|^nk\s*ua$"
)


def clean(text):
    """Text without links, hashtags, channel signatures and emoji noise; one entry per line."""
    text = re.sub(r"https?://\S+|t\.me/\S+|@\w+", " ", text or "")
    text = re.sub(r"[‼⁉←-⇿⌀-⏿☀-➿⬀-⯿🀀-🫿️‍⁠ㅤ]+", " ", text)
    lines = []
    for line in text.splitlines():
        line = re.sub(r"[ \t]+", " ", line).strip(" -–—•·*_|>")
        if line and not SIGNATURE_RE.search(line):
            lines.append(line)
    return lines


def regions_in(text):
    found = []
    for name, pattern in REGION_PATTERNS:
        match = pattern.search(text or "")
        if match:
            found.append((match.start(), name))
    return [name for _, name in sorted(found)]


def is_region_word(word):
    return any(pattern.fullmatch(word.strip()) for _, pattern in REGION_PATTERNS)


def is_place_name(name):
    words = name.lower().replace("’", "'").split()
    if not words or len(name) < 3 or words[0] in NOT_PLACES or words[-1] in NOT_PLACES:
        return False
    return not is_region_word(name) and not re.fullmatch(r"[" + UPPER + r"\-]+", name)


def places_by_role(line):
    """[(position, role, place)] for every place a line names after a preposition, in reading order."""
    taken = []
    found = []
    for role, pattern in ROLE_RES:
        for match in pattern.finditer(line):
            start, end = match.span(1)
            if any(start < other_end and end > other_start for other_start, other_end in taken):
                continue
            taken.append((start, end))
            for name in LIST_SPLIT.split(match.group(1)):
                name = name.strip()
                if is_place_name(name):
                    found.append((start, role, name))
    return sorted(found)


def bare_places(line):
    """A line that is nothing but place names ("Бровари", "Видубичі Печерськ", "Пекарі/Хмільна")."""
    body = re.sub(r"[!?.‼️⚠️❗]+", " ", line).strip()
    if not body or len(body) > 60 or re.search(r"\d{3,}|(?<![\w'])[" + LETTERS + "]{3,}", body):
        return []
    names = [name.strip() for name in LIST_SPLIT.split(body) if name.strip()]
    if not names or not all(PLACE_RE.fullmatch(name) for name in names):
        return []
    return [name for name in names if is_place_name(name)]


COUNT_RE = re.compile(r"(?<!\d)(\d{1,3})\s*(?:х|x|×|шт)?\s*(?=[^\d]{0,25}(?:бпла|шахед|дрон|ракет|реактив|мопед|герань|гербер|бандерол|каб|балістик|балистик|крилат|калібр|беспилот|безпілот))", re.I)


def count_in(line):
    match = COUNT_RE.search(line)
    return int(match.group(1)) if match else None


# Target classes; the codes carry the words the public map's filters look for (uav/drone/shahed, cruise,
# ballistic, aircraft), the label is what a person reads.
TARGET_TYPES = (
    ("ballistic_missile", "Балістика", r"балісти|баллисти|іскандер|искандер|кн-23|kn-23|с-300|с-400|кинджал|кинжал"),
    ("cruise_missile", "Циркон", r"циркон|онікс|оникс"),
    ("cruise_missile", "Бандероль", r"бандерол"),
    ("cruise_missile", "Крилата ракета", r"крилат|крылат|калібр|калибр|х-101|х-555|х-59|х-69|х-22|х-32|kh-101|мгкр|герань-5"),
    ("guided_bomb", "КАБ", r"(?<![\w])каб(?:и|ів|ы|ов|ами)?(?![\w])|кабов|керован\w* авіаційн|управляем\w* авиацион|авіабомб"),
    ("fpv_drone", "FPV-дрон", r"фпв|fpv"),
    ("jet_drone", "Реактивний БпЛА", r"реактивн\w*|реактив\b|герань-3|герань-4|🏍"),
    ("recon_drone", "Розвідувальний БпЛА", r"розвідувальн\w* бпла|розвідник|орлан|zala|зала\b|суперкам|supercam|молні"),
    ("geran_drone", "Герань", r"герань|geran"),
    ("gerbera_drone", "Гербера", r"гербер|gerbera"),
    ("shahed_drone", "Шахед", r"шахед|шахід|шах(?:ів|и)(?!\w)|shahed|мопед|🛵"),
    ("uav", "БпЛА", r"бпла|бпл\b|бплa|безпілот|беспилот|дрон|дроны|дрони|бла\b"),
    ("missile", "Ракета", r"ракет|ракети|ракеты"),
    ("aircraft", "Авіація", r"су-34|су-35|су-30|су-25|су-24|міг-31|миг-31|ту-22|ту-95|ту-160|тактичн\w* авіаці|тактическ\w* авиаци"),
)
TARGET_TYPE_RES = tuple((code, label, re.compile(pattern, re.I)) for code, label, pattern in TARGET_TYPES)


def target_type(text):
    for code, label, pattern in TARGET_TYPE_RES:
        if pattern.search(text or ""):
            return code, label
    return None


NEGATIVE_RE = re.compile(
    r"(?i)не\s+фіксу|не\s+фиксиру|без\s+(?:подальшої\s+)?фіксац|відбій|отбой|мінус|минус|збит|сбит|знищен|уничтож|"
    r"втрачен|потерян|не\s+актив|немає|нема\b|загроз\w*\s+(?:застосування|пусків|балістики)|возможн|можлив|ймовірн\w*\s+пуск"
)


# Words that make a bare "на X" a movement even when the message never names the weapon.
MOVEMENT_RE = re.compile(
    r"(?i)курс|вектор|лет[иі]т|летять|летят|рухаєт|заліта|виліта|пролітає|пролетів|прямує|повз|розверта|кружля|"
    r"маневру|залетел|вылетел|движет|направля|мимо|в сторону|в бік|у бік|напрямку"
)


def hints(message, line_regions):
    """Regions to prefer, most specific first: the line's own, then the channel's home region."""
    found = list(line_regions)
    home = SOURCE_REGIONS.get((message.get("sourceCode") or "").lower())
    if home and home not in found:
        found.append(home)
    return found


def place_ref(name, region_hints, region=None):
    """A place the host resolves; ``required`` drops the whole entity when the gazetteer does not know it."""
    reference = {"place": name, "required": True}
    if region:
        reference["region"] = region
    elif region_hints:
        reference["hint"] = region_hints
    return reference


def lines_with_context(message):
    """(line, regions) pairs: a region header ("Чернігівщина:", "Харьковская область.") carries to the lines under it."""
    context = []
    result = []
    for line in clean(message.get("text")):
        regions = regions_in(line)
        remainder = line
        for _, pattern in REGION_PATTERNS:
            remainder = pattern.sub(" ", remainder)
        if regions and len(re.sub(r"[\W\d_]+", "", remainder)) <= 3:
            context = regions
            continue
        result.append((line, regions or context))
    return result


def is_long_read(message):
    """Digests, news and reposts: past a few lines of radar talk the prepositions stop meaning movement."""
    text = message.get("text") or ""
    return len(text) > 700 or text.count("\n") > 14


# Launch areas and airfields outside the gazetteer (Russia, occupied Crimea, the seas): public approximate
# coordinates. Order matters: the first pattern found in a line wins.
SITES = (
    ("Приморсько-Ахтарськ", 38.17, 46.05, r"ахтарськ|ахтарск"),
    ("Цимбулова (Орел)", 36.08, 52.97, r"цимбулов|орл[аіуе]\b|орел\b|орёл|орла\b"),
    ("Курськ", 36.30, 51.75, r"курськ|курск|халін|халин"),
    ("Міллерово", 40.37, 48.95, r"міл+[еє]ров|мил+еров"),
    ("Шаталово", 32.48, 54.34, r"шаталов"),
    ("Брянськ", 34.18, 53.21, r"брянськ|брянск|брянщин"),
    ("Навля", 34.50, 52.83, r"навл[яіі]"),
    ("Бєлгород", 36.59, 50.60, r"бєлгород|белгород"),
    ("Воронеж", 39.22, 51.62, r"воронеж|балтимор"),
    ("Ліпецьк", 39.60, 52.70, r"ліпецьк|липецк"),
    ("Таганрог", 38.85, 47.20, r"таганрог"),
    ("Єйськ", 38.21, 46.68, r"єйськ|ейск"),
    ("Морозовськ", 41.79, 48.31, r"морозовськ|морозовск"),
    ("Донецьк", 37.74, 48.07, r"донецьк\b|донецьк[аіу]\b|донецк\b|донецка\b|донецке\b"),
    ("Енгельс", 46.21, 51.48, r"енгельс|энгельс"),
    ("Оленья", 33.46, 68.15, r"олень[яи]|оленегорськ|оленегорск"),
    ("Моздок", 44.59, 43.79, r"моздок"),
    ("Шайковка", 34.00, 54.23, r"шайковк"),
    ("Саваслейка", 42.31, 55.44, r"саваслейк"),
    ("Борисоглібськ", 42.18, 51.37, r"борисогл[іе]бськ|борисоглебск"),
    ("Бельбек", 33.57, 44.69, r"бельбек"),
    ("Саки", 33.60, 45.09, r"\bсак[иах]\b|сакськ"),
    ("Гвардійське", 33.98, 45.12, r"гвардійськ|гвардейск"),
    ("Джанкой", 34.39, 45.70, r"джанкой"),
    ("Крим", 34.10, 45.30, r"криму|крим\b|крыма|крым\b"),
    ("Чорне море", 31.50, 44.30, r"чорн\w+ мор|черн\w+ мор|акваторі"),
    ("Азовське море", 36.60, 46.20, r"азовськ\w+ мор|азовск\w+ мор"),
    ("Каспійське море", 49.50, 42.50, r"каспі|касписк|каспийск"),
    ("Мачулищі", 27.58, 53.78, r"мачулищ"),
)
SITE_RES = tuple((name, lon, lat, re.compile(pattern, re.I)) for name, lon, lat, pattern in SITES)


def site_in(text):
    """(name, lon, lat) of the first launch area or airfield a text names."""
    for name, lon, lat, pattern in SITE_RES:
        if pattern.search(text or ""):
            return name, lon, lat
    return None


def destinations(line):
    return [name for _, role, name in places_by_role(line) if role == "to"]
