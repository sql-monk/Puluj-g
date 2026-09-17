# Технічне завдання
## Сервіс збору VIINA 2.0: стеження за репозиторієм, розбір і копіювання даних у сховище Puluj

**Версія:** 1.0
**Дата:** 15.09.2026
**Пов'язані документи:** `Puluj.md` (продукт), `todo_dwh.md` (DWH, §9.1, §47–48, §68), `docs/README.md` (архітектура), `todo_map_viina_layer.md` (шар мапи — окреме ТЗ)

---

# 1. Призначення

Розробити компонент Puluj, який:

1. **стежить** за репозиторієм `https://github.com/zhukovyuri/VIINA` і помічає кожен новий реліз даних;
2. **завантажує** змінені файли (і лише їх), зберігає незмінну копію кожного отриманого файла (RAW);
3. **розбирає** CSV і GeoJSON у структуровані таблиці власної схеми `viina` у тій самій PostgreSQL/PostGIS, де живе Puluj;
4. **прив'язує** події до gazetteer Puluj (`places`) через `geonameid` і геометрію;
5. робить це **ідемпотентно** (повторний запуск нічого не дублює), **інкрементно** (оновлюються тільки змінені рядки) і з **повним походженням** (від рядка в БД до конкретного файла та його SHA-256).

Компонент не інтерпретує події VIINA як повітряні цілі (`Target`/`TargetTrack`) і не запускає кореляцію — це історичний подієвий шар, а не realtime-стрічка (див. §4.3). Відображення на мапі — окреме ТЗ.

---

# 2. Що таке VIINA (факти, перевірені 15.09.2026)

VIINA 2.0 (Violent Incident Information from News Articles) — база подій війни за повідомленнями українських і російських медіа. Web-scraper збирає новини кожні 6 годин, BERT-модель (`bert-base-slavic-cyrillic-upos`, fine-tuned) класифікує кожне повідомлення за типами подій і акторами, місця геокодуються (Yandex/OSM API, вручну перевірені), і результат викладається в GitHub.

Автори: Yuri Zhukov, Natalie Ayers (Harvard). Цитування: *Zhukov, Yuri and Natalie Ayers (2023). "VIINA 2.0: Violent Incident Information from News Articles on the 2022 Russian Invasion of Ukraine." Cambridge, MA: Harvard University.*

**Ліцензія: ODbL 1.0** — обов'язкова атрибуція; похідні бази лишаються відкритими. Атрибуцію треба показати в UI (мапа, деталі події) і зберегти в реєстрі джерел (`todo_dwh.md` §84).

## 2.1. Репозиторій

```text
Repo:            zhukovyuri/VIINA
Default branch:  main            (не master — raw-посилання на master для LFS повертають 404)
Data/            25 zip-архівів + 2 GeoJSON + PreviousVersions/ (VIINA 1.0, ігнорувати)
Diagnostics/     classification_report_bert.csv (якість класифікатора)
Figures/         картинки, ігнорувати
```

**Усі `*.zip` зберігаються через Git LFS** (`.gitattributes: *.zip filter=lfs`). У дереві git лежить лише pointer-файл (~130 байтів):

```text
version https://git-lfs.github.com/spec/v1
oid sha256:5199185a992283480cb9ab3bcd54dce11516bb9fd38bbd409965a80dcf2dbbfc
size 621665
```

Тобто `oid` = SHA-256 справжнього zip, а `size` — його розмір. Це і є найдешевший спосіб виявити зміну файла (§7).

## 2.2. Файли даних

Іменування: `Data/{dataset}_latest_{YYYY}.zip`, один CSV усередині з тією ж назвою (`event_1pd_latest_2026.zip` → `event_1pd_latest_2026.csv`). Файли поділені за календарними роками з 13.10.2024; рік визначається полем `date`.

| dataset | зміст | ключ |
|---|---|---|
| `event_info` | сирі повідомлення: час, місце, текст, джерело, URL | `event_id` |
| `event_labels` | BERT-класифікація тих самих повідомлень: ймовірності + бінарні індикатори | `event_id` |
| `event_1pd` | дедупліковані «одна подія на день»: агрегат повідомлень одного типу в одному місці за один день | `event_id_1pd` |
| `control` | територіальний контроль по населених пунктах GeoNames, щоденна панель | `geonameid + date` |
| `kontrol` | те саме по КАТОТТГ | `kod + date` |
| `gn_UA_tess.geojson` | 33 141 полігон (тесселяція) навколо населених пунктів GeoNames з атрибутами | `geonameid` |
| `katotth_UA_tess.geojson` | 29 724 полігони по КАТОТТГ | `kod` |

Розміри zip (з LFS-pointer-ів, 15.09.2026, байти):

| рік | event_info | event_labels | event_1pd | control | kontrol |
|---|---:|---:|---:|---:|---:|
| 2022 | 25 630 158 | 48 532 730 | 4 370 391 | 28 185 232 | 62 960 900 |
| 2023 | 11 674 601 | 22 671 294 | 2 299 419 | 32 119 788 | 73 325 880 |
| 2024 | 8 920 488 | 18 361 522 | 1 776 832 | 32 229 568 | 73 552 642 |
| 2025 | 5 406 297 | 9 895 882 | 998 353 | 32 174 993 | 73 438 357 |
| 2026 | 4 232 223 | 7 498 293 | 621 665 | 23 976 646 | 51 445 970 |

Разом ≈ 860 МБ zip. Розпаковані CSV 2026 року (за 256 днів): `event_info` 18,4 МБ / 36 094 рядки, `event_labels` 22 МБ / 35 844, `event_1pd` 5,9 МБ / 30 710, `control` **440 МБ / 8 484 096 рядків**, `kontrol` **495 МБ / 7 609 344**. Оцінка за всі роки: ≈ 0,5 млн подій, ≈ 55 млн рядків `control` і ≈ 50 млн `kontrol` — панелі контролю не можна зберігати як є, тільки згорнутими в інтервали (§11.4).

GeoJSON-файли лежать у git звичайно (не LFS): 31,7 МБ і 25,9 МБ.

## 2.3. Каденс оновлень

README обіцяє щоденне оновлення; фактично коміти «Data update» приходять раз на 1–3 дні (вересень 2026: 13, 11, 9, 7, 4; серпень: 31, 30, 29). Один реліз перезаписує **усі** файли `*_latest_*` (у т. ч. минулих років — переклас/перегеокодування), тому не можна припускати, що змінюється лише поточний рік. Останній `date` у файлі = день коміту; затримка між подією і появою в даних — від кількох годин до ~3 діб.

Кожен реліз має мітку `viina_version` (напр. `bert_20260913024050` — момент прогону BERT) у подієвих файлах і `vcontrol_version` (`20260913044905332209`) у файлах контролю.

---

# 3. Формат даних

Усі CSV: UTF-8, кома-роздільник, заголовок у першому рядку, RFC 4180 (значення з комами/лапками/переносами в подвійних лапках; поле `text` може містити лапки і, ймовірно, переноси рядків — парсер має це витримувати). Дата `YYYYMMDD`, час `HH:MM`. Часовий пояс у документації **не вказано** (припущення: локальний час публікації, Europe/Kyiv; §10.4).

## 3.1. `event_info` — 23 колонки (заголовок дослівно)

```text
viina_version,event_id,event_id_1pd,date,time,geonameid,feature_code,asciiname,
ADM1_NAME,ADM1_CODE,ADM2_NAME,ADM2_CODE,longitude,latitude,GEO_PRECISION,GEO_API,
location,address,report_id,source,url,text,lang
```

| колонка | опис | спостережено (2026) |
|---|---|---|
| `viina_version` | мітка релізу | `bert_20260913024050` |
| `event_id` | унікальний id повідомлення | унікальний у файлі, без дублів |
| `event_id_1pd` | id дедуплікованої події | посилання на `event_1pd` |
| `date` | дата повідомлення | `20260101`…`20260913` |
| `time` | час повідомлення | `00:00` у 47 % рядків (= час невідомий), решта — реальні хвилини |
| `geonameid` | id населеного пункту GeoNames | числовий; збігається з `places.external_key = 'geonames:{id}'` у Puluj |
| `feature_code` | тип пункту GeoNames | PPLA, PPL, PPLC, PPLA2, PPLX, PPLQ, PPLH, PPLA3, PPLW; **порожній** у 0,7 % |
| `asciiname` | назва ASCII | `Bilka`, `Lutsk` |
| `ADM1_NAME`, `ADM1_CODE` | область (англ. назва, числовий код GAUL) | `Odessa`/`1800`, `Volyn`/`2623` |
| `ADM2_NAME`, `ADM2_CODE` | район (**старі, дореформені** райони GAUL) | `Ivanivs'kyi`/`15842` |
| `longitude`, `latitude` | координати | завжди заповнені; для `GEO_PRECISION=ADM1` це точка репрезентативного пункту, а не самої події |
| `GEO_PRECISION` | точність геокодування | `ADM3` 83 %, `ADM1` 14 %, `STREET` 1,6 %, `ADM2` 0,7 % |
| `GEO_API` | чим геокодовано | `Yandex` 90 %, `OSM` 10 %, `liveuamap`, `meduza` |
| `location` | порядковий номер локації в повідомленні | одне повідомлення з N локаціями → N рядків з різними `event_id` |
| `address` | адреса від геокодера | `"Украина, Одесская область"` |
| `report_id` | id статті-джерела | кілька `event_id` можуть мати один `report_id` |
| `source` | код медіа | 2026: pravdaua, nv, liga, unian, liveuamap, interfaxua, espreso, militarnyy, meduza, ng (README перелічує 15 за всі роки) |
| `url` | посилання на статтю | |
| `text` | заголовок/опис | оригінальна мова |
| `lang` | мова | `ua` 84 %, `ru` 16 % |

## 3.2. `event_labels` — 61 колонка

```text
viina_version,event_id,event_id_1pd,date,geonameid,
t_mil,a_rus,a_ukr,a_rus_init,a_ukr_init,a_civ,a_other,
t_aad,t_airstrike,t_airalert,t_uav,t_armor,t_arrest,t_artillery,t_control,t_firefight,
t_ied,t_raid,t_occupy,t_property,t_cyber,t_hospital,t_milcas,t_civcas,t_retreat,
t_mil_b,a_rus_b,a_ukr_b,a_rus_init_b,a_ukr_init_b,a_civ_b,a_other_b,
t_aad_b,t_airstrike_b,t_airalert_b,t_uav_b,t_armor_b,t_arrest_b,t_artillery_b,t_control_b,t_firefight_b,
t_ied_b,t_raid_b,t_occupy_b,t_property_b,t_cyber_b,t_hospital_b,t_milcas_b,t_civcas_b,t_retreat_b,
t_loc,t_san,t_loc_b,t_san_b,tid
```

Кожна ознака у двох варіантах: ймовірність (float 0–1, напр. `2.92e-05`) і бінарний індикатор `_b` (0/1; поріг підібрано за F1). `t_loc_b`, `t_san_b` і `tid` подекуди порожні. `tid` — недокументований числовий стовпець; зберігати як є, не інтерпретувати.

27 ознак:

| ознака | значення | AUC (BERT) |
|---|---|---|
| `t_mil` | подія про війну/військові дії | 0,969 |
| `t_loc` | є посилання на конкретне місце | 0,980 |
| `t_san` | згадка санкцій | 0,882 |
| `t_aad` | ППО (Бук, ПЗРК) | 0,923 |
| `t_airstrike` | авіаудар, бомбардування, удар вертольота | 0,995 |
| `t_uav` | БпЛА | 0,938 |
| `t_airalert` | повітряна тривога | 1,000 |
| `t_armor` | танковий бій/штурм | 0,991 |
| `t_arrest` | арешт, полон | 0,935 |
| `t_artillery` | артилерія, міномети, РСЗВ | 0,957 |
| `t_control` | встановлення/заява про контроль над пунктом | 0,931 |
| `t_firefight` | стрілецький бій | 0,991 |
| `t_ied` | СВП, міна, вибух | — |
| `t_raid` | рейд/штурм ДРГ, спецпризначенців | 0,977 (1 позитивний приклад у тесті) |
| `t_occupy` | окупація території/будівлі | 0,890 (1 приклад) |
| `t_property` | руйнування майна/інфраструктури | 0,958 |
| `t_cyber` | кібератаки | **0,754 — нижче порогу 0,80, автори радять не використовувати** |
| `t_hospital` | удари по лікарнях/гумконвоях | — |
| `t_milcas` | військові втрати | 0,938 |
| `t_civcas` | цивільні втрати | 0,958 |
| `t_retreat` | відступ | 0,992 |
| `a_rus` / `a_ukr` | участь РФ / України | 0,948 / 0,886 |
| `a_rus_init` / `a_ukr_init` | ініціатор РФ / Україна | 0,943 / 0,950 |
| `a_civ` | ініціатор — цивільні | 0,878 |
| `a_other` | третя сторона (США, ЄС, Червоний Хрест) | 0,939 |

Розподіл бінарних міток у `event_1pd` 2026 (для оцінки обсягів шару): t_mil 17 980, t_property 8 983, t_civcas 5 462, t_uav 4 901, t_artillery 2 762, t_control 2 310, t_firefight 1 957, t_airstrike 1 573, t_raid 1 562, t_arrest 1 380, t_milcas 1 219, t_occupy 1 099, t_aad 1 056, t_ied 482, t_armor 307, t_airalert 142, t_hospital 56, t_cyber 1.

## 3.3. `event_1pd` — 44 колонки

```text
viina_version,event_id_1pd,date,n_reports,event_ids,sources,geonameid,feature_code,asciiname,
ADM1_NAME,ADM1_CODE,ADM2_NAME,ADM2_CODE,longitude,latitude,GEO_PRECISION,
t_mil_b,a_rus_b,a_ukr_b,a_rus_init_b,a_ukr_init_b,a_civ_b,a_other_b,
t_aad_b,t_airstrike_b,t_airalert_b,t_uav_b,t_armor_b,t_arrest_b,t_artillery_b,t_control_b,t_firefight_b,
t_ied_b,t_raid_b,t_occupy_b,t_property_b,t_cyber_b,t_hospital_b,t_milcas_b,t_civcas_b,t_retreat_b,
t_loc_b,t_san_b,tid
```

`n_reports` — скільки повідомлень злито (90 % = 1, до 10+); `event_ids` — список через `", "` (`"2717862, 2717863, 2717864"`); `sources` — список кодів джерел (може бути один код на всі). Лише бінарні мітки, без ймовірностей.

Правило DWH (`todo_dwh.md` §9.1): `event_1pd` — це **підказка дедуплікації від джерела**, не заміна сирим подіям. Одиниця зберігання — `event_id`; `event_id_1pd` — атрибут групи.

## 3.4. `control` / `kontrol` — 8 колонок

```text
geonameid,date,status_wiki,status_boost,status_dsm,status_isw,status,vcontrol_version
kod,      date,status_wiki,status_boost,status_dsm,status_isw,status,vcontrol_version
```

Статуси: `UA` | `RU` | `CONTESTED` (`status` — «голосування більшості» wiki/boost/dsm, нічия → DeepStateMap; ISW оновлюється рідше). 2026: UA 7 014 963, RU 1 461 400, CONTESTED 7 733 рядків. Один рядок на **кожен пункт на кожен день** (33 142 пункти × 256 днів). `kod` — код КАТОТТГ (`UA01000000000013043`).

## 3.5. GeoJSON

`gn_UA_tess.geojson`: `MultiPolygon` на пункт, властивості GeoNames (`geonameid` як float `461727.0`, `name`, `asciiname`, `alternatenames`, `latitude`, `longitude`, `feature_code`, `admin1_code`…, `population`, `timezone`) + GAUL `ADM1_NAME/ADM1_CODE/ADM2_NAME/ADM2_CODE`. `katotth_UA_tess.geojson`: `Polygon`, `kod`, `osm_id`, українські назви `name`, `ADM1_NAME_ALT`, `ADM2_NAME_ALT`, `admin_level`. CRS84 (lon, lat).

---

# 4. Місце в архітектурі Puluj

## 4.1. Новий колектор

```text
src/Puluj.Collectors/Viina/
    ViinaCollector.cs          ICollector: цикл «перевірити → завантажити змінене → розібрати → записати»
    ViinaOptions.cs            Collectors:Viina:*
    ViinaRelease.cs            перелік файлів релізу, pointer-и, порівняння з БД
    LfsClient.cs               pointer → oid/size → завантаження вмісту (media URL, batch API як fallback)
    Parsing/
        CsvReader.cs           потоковий RFC-4180 reader (без завантаження файла в пам'ять)
        EventInfoRow.cs, EventLabelsRow.cs, Event1pdRow.cs, ControlRow.cs  типізовані рядки + валідація
    Loading/
        EventLoader.cs         upsert подій/міток/1pd по хешу рядка
        ControlLoader.cs       згортання щоденної панелі в інтервали, повне перезавантаження року
        TessLoader.cs          GeoJSON → viina.gn_cell / viina.katottg_cell
        PlaceResolver.cs       geonameid / ST_Contains → places.place_id
```

Реєстрація за зразком `AlertsInUa`:

- `CollectorNames.Viina = "viina"`, `WorkerOptions.Viina = "viina"` (роль процесу), `AllRoles` доповнити;
- `DependencyInjection.AddPulujCollectors`: `services.Configure<ViinaOptions>`, `AddHttpClient(ViinaCollector.HttpClientName)` з `AddStandardResilienceHandler()` і таймаутом, достатнім для 75 МБ (`Timeout = 10 хв`, потокове читання);
- `CollectorSupervisor`: додати `IOptionsMonitor<ViinaOptions>` до сигнатури перезапуску (як `alertsOptions`/`telegramOptions`);
- `deploy/docker-compose.yml`: сервіс `collector-viina` (`Worker__Roles: viina`), том `viinaraw` для RAW-архіву;
- `Program.cs` Worker: `if (roles.Contains(WorkerOptions.Viina)) collectors.Add(CollectorNames.Viina)`.

Локально (`dev-run.ps1`, ролі порожні = усі) колектор запускається разом з рештою.

## 4.2. Джерело в реєстрі

`data/sources.json` (upsert за `code`, БД — власник рядка):

```json
{
  "code": "viina",
  "name": "VIINA 2.0 (Zhukov & Ayers, Harvard)",
  "type": "Dataset",
  "url": "https://github.com/zhukovyuri/VIINA",
  "trustLevel": 0.7,
  "priority": 30,
  "enabled": true,
  "pollingInterval": "06:00:00",
  "config": { "collector": "viina", "repo": "zhukovyuri/VIINA", "branch": "main", "license": "ODbL-1.0",
              "attribution": "VIINA 2.0 — Zhukov & Ayers (Harvard), ODbL" }
}
```

Додати `SourceType.Dataset = 5` до `Puluj.Domain.Enums.SourceType` (зберігається як int — міграція схеми не потрібна; `SourcesEditor` в адмінці має показувати новий тип). `Handles(source)`: `options.Enabled && source.Type == Dataset && config.collector == "viina"`.

## 4.3. Чому не через `raw_messages` → `Target`

Конвеєр Puluj (`RawMessage → RawMessageProcessor → Target → Correlator → TargetTrack`) створений для realtime-повідомлень про повітряні цілі з хвилинною точністю. VIINA — це ~0,5 млн історичних повідомлень з точністю до дня (47 % без часу), 21 тип подій, з яких лише `t_uav`/`t_airstrike`/`t_aad`/`t_airalert` дотичні до предмета `Target`, і жодного з них не можна відправляти в кореляцію треків та ETA. Тому:

- один запис `raw_messages` **на файл-реліз** не створюється, і по одному на подію — теж; аналог `RawMessage` для датасету — рядок `viina.snapshot` (файл + SHA-256 + версія), а RAW-вміст лежить у файловому архіві (§9);
- події живуть у власній схемі `viina` і не проходять через `Processing`;
- **міст у `Target`** (вибіркове створення `Target` з `IdentificationMethod.Structured` для подій `STREET`/`ADM3` типів `t_uav`/`t_airstrike` з реальним часом) — окремий етап поза цим ТЗ (§21, MVP+2), і тільки з прапорцем, який кореляція ігнорує.

Схема `viina` = «staging» у термінах `todo_dwh.md` (§7–8, там вона названа `stg_viina`); коли з'явиться DWH, ці таблиці або переносяться туди 1:1, або DWH читає їх напряму.

---

# 5. Конфігурація

`appsettings.json` Worker-а (розділ `Collectors:Viina`; ключі перекриваються з адмінки через `app_settings`, як і для інших колекторів):

```jsonc
"Viina": {
  "Enabled": false,
  "Repo": "zhukovyuri/VIINA",
  "Branch": "main",
  "PollingInterval": "06:00:00",       // перевірка pointer-ів; сам реліз виходить раз на 1–3 дні
  "Datasets": ["event_info", "event_labels", "event_1pd", "control", "kontrol"],
  "Years": null,                        // null = 2022..поточний рік; або явний список
  "LoadTessellation": true,             // gn_UA_tess / katotth_UA_tess
  "RawDirectory": "data/raw/viina",     // RAW-архів (у Docker — том /app/data/raw/viina)
  "KeepRawVersions": 0,                 // 0 = зберігати всі версії кожного файла; N = останні N
  "GitHubToken": null,                  // необов'язково; лише для GitHub API (commits/contents), raw і LFS працюють без нього
  "SourceTimeZone": "Europe/Kyiv",      // як тлумачити date+time (§10.4)
  "ForceReload": null                   // "2024" | "event_labels" | "event_labels_latest_2024" — одноразове повне перезавантаження, скидається після виконання
}
```

`Runtime:Viina:*` в `app_settings` (пише колектор, читає адмінка): `LastCheckAt`, `LastReleaseVersion`, `LastReleaseAt`, `CheckNow` (прапорець від кнопки «Перевірити зараз»), `Status` (JSON: по кожному файлу oid/rows/тривалість/помилка).

---

# 6. Стан колектора

- `collector_states` (через `CollectorStateStore`, `source_id` джерела `viina`): `LastPolledAt`, `LastSuccessAt`, `LastError`, `ConsecutiveFailures`, `LastMessageAt` = найпізніший `date` у даних, `Cursor` = JSON `{ "files": { "event_info_latest_2026.zip": { "oid": "...", "size": 4232223, "snapshotId": 17 }, ... }, "gnTessSha": "...", "katottgTessSha": "..." }`.
- `viina.snapshot` — по одному рядку на кожен завантажений і розібраний файл (§9.2).
- Прогрес усередині релізу (щоб рестарт посередині 25 файлів продовжив, а не почав спочатку): файл вважається обробленим тільки після коміту транзакції завантаження і оновлення `Cursor.files[...]`; незавершені файли будуть повторені при наступному циклі, бо їх oid у `Cursor` ще старий.

---

# 7. Виявлення змін

Кожен цикл (`PollingInterval` або `CheckNow`):

1. Для кожного файла з `Datasets × Years` — `GET https://raw.githubusercontent.com/{repo}/{branch}/Data/{file}.zip` (130 байтів, без автентифікації, без лімітів GitHub API). Розпарсити pointer: рядок `oid sha256:<64 hex>` і `size <int>`. Якщо відповідь не починається з `version https://git-lfs.github.com/spec/v1` — це або звичайний файл (репозиторій перестав використовувати LFS: тоді SHA-256 рахуємо самі з тіла), або помилка (HTML 404) → `LastError`, файл пропускається.
2. Порівняти `oid` з `Cursor.files[file].oid`. Збіг → файл не змінився, нічого не завантажувати. Нове/інше → у список на завантаження.
3. GeoJSON (не LFS): `HEAD` на raw-URL, порівняти `ETag`; або `GET /repos/{repo}/contents/Data/gn_UA_tess.geojson` (поле `sha`, потребує GitHub API — 60 запитів/год без токена, достатньо).
4. Додатково (інформативно, для адмінки та логів): `GET /repos/{repo}/commits?path=Data&per_page=1` → дата останнього релізу; не обов'язково для роботи.
5. Якщо змінено хоча б один файл — це «реліз»: створити `viina.release` (§9.2), обробляти файли в порядку `event_info → event_labels → event_1pd → control → kontrol → geojson` (мітки і 1pd посилаються на події; тесселяція — довідник).

Умовний `If-None-Match` на raw для pointer-ів не потрібен (130 байтів), але `HttpClient` має вмикати `AutomaticDecompression`.

---

# 8. Завантаження

Основний шлях (перевірено 15.09.2026, 200 OK, `ETag` = oid, `Last-Modified`):

```text
GET https://media.githubusercontent.com/media/{repo}/{branch}/Data/{file}.zip
```

Резервний шлях (той, що використовує сам git-lfs; працює завжди, поки об'єкт є у сховищі):

```text
POST https://github.com/{repo}.git/info/lfs/objects/batch
Accept: application/vnd.git-lfs+json
Content-Type: application/vnd.git-lfs+json
{"operation":"download","transfers":["basic"],"objects":[{"oid":"<oid>","size":<size>}]}
→ objects[0].actions.download.href   (підписаний S3-URL, дійсний 1 годину; expires_at у відповіді)
```

Вимоги:

- потокове збереження у `RawDirectory/tmp/{file}.{oid}.part`, після завершення — перевірка `Content-Length == size` і **SHA-256 файла == oid**; невідповідність → видалити, повторити (до 3 разів з експоненційною паузою), потім `LastError`;
- підтримка `Range`/докачування не обов'язкова (найбільший файл 75 МБ);
- 429/5xx → `Retry-After`/backoff (стандартний resilience handler);
- одночасно не більше 2 завантажень; загальний трафік одного релізу ≤ 1 ГБ;
- після перевірки файл переміщується в архів (§9.1) і **більше ніколи не змінюється**.

---

# 9. RAW-архів і походження

## 9.1. Файлова розкладка

```text
{RawDirectory}/
  releases/{releaseId}-{yyyyMMdd-HHmm}/         # один каталог на реліз, файли що змінилися
      event_info_latest_2026.{oid[:12]}.zip
      event_info_latest_2026.pointer            # оригінальний LFS-pointer як отримано
      gn_UA_tess.{sha[:12]}.geojson
      manifest.json                             # {file, oid, size, sha256, url, downloadedAt, httpEtag, httpLastModified}
```

Zip не розпаковується на диск: CSV читається потоком з архіву (`ZipArchive` → `Stream`). Retention: `KeepRawVersions` (типово всі; при 860 МБ на реліз і ~10 релізів/місяць — ~8 ГБ/міс, якщо змінюються всі файли; фактично лише змінені). Це «RAW FOREVER» з `todo_dwh.md` §93 — відкладена до появи S3/MinIO річ; поки що диск/том.

## 9.2. Таблиці походження

```sql
CREATE SCHEMA viina;

CREATE TABLE viina.release (
    release_id      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    detected_at     timestamptz NOT NULL,
    completed_at    timestamptz,
    viina_version   text,                 -- з подієвих файлів (bert_…)
    vcontrol_version text,                -- з файлів контролю
    upstream_commit text,                 -- sha коміту GitHub, якщо отримали
    upstream_commit_at timestamptz,
    files_changed   int NOT NULL,
    status          text NOT NULL         -- running | completed | failed | partial
);

CREATE TABLE viina.snapshot (             -- один завантажений файл (аналог RawMessage для датасету)
    snapshot_id     bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    release_id      bigint NOT NULL REFERENCES viina.release,
    file_name       text NOT NULL,        -- event_info_latest_2026.zip
    dataset         text NOT NULL,        -- event_info | event_labels | event_1pd | control | kontrol | gn_tess | katottg_tess
    year            int,
    lfs_oid         text,                 -- sha256 з pointer-а (для zip)
    sha256          text NOT NULL,        -- перевірений хеш вмісту
    size_bytes      bigint NOT NULL,
    source_url      text NOT NULL,
    http_etag       text, http_last_modified timestamptz,
    downloaded_at   timestamptz NOT NULL,
    raw_path        text NOT NULL,        -- відносно RawDirectory
    row_count       int,                  -- рядків у CSV / features у GeoJSON
    rows_inserted   int, rows_updated int, rows_unchanged int, rows_removed int, rows_rejected int,
    parsed_at       timestamptz,
    duration_ms     int,
    error           text,
    UNIQUE (file_name, sha256)
);
```

`viina.snapshot(file_name, sha256)` унікальний: той самий вміст двічі не розбирається, навіть якщо pointer «змінився» туди-назад.

---

# 10. Розбір

## 10.1. CSV

- Власний або бібліотечний потоковий reader з підтримкою: лапок, подвоєних лапок, ком і `\n`/`\r\n` всередині лапок, BOM, порожніх кінцевих полів; без читання файла в пам'ять цілком (control 440 МБ).
- Заголовок порівнюється з очікуваним набором (§3). **Дрейф схеми** (`todo_dwh.md` §61): нова колонка → попередження, її значення складаються в `extra jsonb`; зникла або перейменована обов'язкова колонка / зміна порядку відомих колонок, що ламає типи → файл **не завантажується**, `snapshot.error`, `release.status = partial`, помилка в адмінці. Обов'язкові: ключі, `date`, координати, `GEO_PRECISION`, усі `*_b`.
- Кожен рядок валідується: `event_id` int64 > 0; `date` → `DATE`; `longitude ∈ [-180;180]`, `latitude ∈ [-90;90]` і в межах прямокутника «Україна + сусіди» (lon 20–45, lat 42–56 — поза ним попередження, не reject); `GEO_PRECISION ∈ {STREET, ADM3, ADM2, ADM1}` (інше → зберегти як текст, `location_kind = Unknown`); ймовірності ∈ [0;1]; `*_b ∈ {0,1,''}`.
- Невалідний рядок → `viina.rejected_row (snapshot_id, line_no, reason, raw_line)`, лічильник `rows_rejected`; файл продовжує оброблятися. Поріг: якщо відхилено > 5 % рядків — файл не комітиться, `error`.

## 10.2. Хеш рядка

`record_hash = sha256(канонічний рядок усіх колонок у порядку заголовка, як у файлі)`. Порівняння з хешем у БД визначає insert / update / unchanged без порівняння колонок.

## 10.3. Мітки

`event_labels` розкладаються в **вузьку** таблицю `viina.event_label (event_id, label, probability, flag)` — 27 рядків на подію (≈ 13,5 млн рядків за всі роки; з `smallint` кодом мітки і `real` — прийнятно) **і** у широку денормалізовану маску в `viina.event` (`labels_mask bigint` — біт на кожну `*_b = 1`, `actors_mask smallint`) для швидких фільтрів мапи. Довідник міток — `viina.label (label_id smallint, code text, name_uk text, auc real, group_code text)`, заповнюється seed-файлом `data/viina/labels.json` (коди, українські назви, AUC з README, група для мапи — див. ТЗ мапи §5).

## 10.4. Час

- `date` → `event_date date`.
- `time` → `time_raw text`; `reported_at timestamptz` = `date + time` у `SourceTimeZone` → UTC; `time_precision` = `Minute`, якщо `time ≠ '00:00'`, інакше `Day` (тоді `reported_at` = початок доби за Kyiv). Значення `00:00` вважати невідомим часом — це припущення, зафіксоване в коді коментарем і в цьому ТЗ; якщо автори підтвердять інше, змінюється лише ця функція.
- Не змішувати `reported_at` (публікація) і «час події» — VIINA дає лише перший (`todo_dwh.md` §51).

## 10.5. GeoJSON

Потоковий розбір (`System.Text.Json` `Utf8JsonReader` по features; 32 МБ можна і в пам'ять, але тримати ≤ 200 МБ RSS). `geonameid` приходить як float `461727.0` → int. Геометрія → `geometry(MultiPolygon,4326)` (Polygon загортається в MultiPolygon), `ST_IsValid` → інакше `ST_MakeValid`, невиправне → reject.

---

# 11. Модель даних

Усе в схемі `viina`, snake_case (EF `UseSnakeCaseNamingConvention`, як у решті проєкту), сутності в `Puluj.Domain/Entities/Viina/*.cs`, конфігурації в `Puluj.Infrastructure/Persistence/Configurations/Viina/*`, одна міграція `AddViina`. Масові записи — не через EF, а `NpgsqlBinaryImporter` (`COPY … FROM STDIN (FORMAT BINARY)`) у тимчасову таблицю + `INSERT … ON CONFLICT` (за прецедентом `RawMessageIngestor`, який пише сирим `NpgsqlCommand`).

## 11.1. `viina.event` (з `event_info` + маски з `event_labels`)

```sql
CREATE TABLE viina.event (
    event_id        bigint PRIMARY KEY,               -- VIINA event_id
    event_id_1pd    bigint,
    report_id       bigint,
    location_index  smallint,
    event_date      date NOT NULL,
    time_raw        text,
    reported_at     timestamptz NOT NULL,
    time_precision  smallint NOT NULL,                -- 1 Minute | 2 Day
    geonameid       int,
    feature_code    text,
    asciiname       text,
    adm1_name text, adm1_code int, adm2_name text, adm2_code int,
    location        geography(Point,4326) NOT NULL,   -- як у файлі
    geo_precision   text NOT NULL,                    -- STREET | ADM3 | ADM2 | ADM1
    location_kind   smallint NOT NULL,                -- Puluj LocationKind: Point | City | District | Region | Unknown
    place_id        int REFERENCES places,            -- розв'язане місце (§12)
    region_place_id int REFERENCES places,            -- область (завжди, якщо знайдено)
    geo_api         text,
    address         text,
    source_code     text NOT NULL,
    url             text,
    text            text NOT NULL,
    lang            text,
    labels_mask     bigint NOT NULL DEFAULT 0,        -- біти t_* (_b = 1)
    actors_mask     smallint NOT NULL DEFAULT 0,      -- біти a_*
    is_military     boolean NOT NULL DEFAULT false,   -- t_mil_b
    viina_version   text NOT NULL,
    record_hash     bytea NOT NULL,
    labels_hash     bytea,
    first_snapshot_id bigint NOT NULL REFERENCES viina.snapshot,
    last_snapshot_id  bigint NOT NULL REFERENCES viina.snapshot,
    removed_snapshot_id bigint REFERENCES viina.snapshot,  -- подія зникла з файла upstream (soft delete)
    extra           jsonb                              -- невідомі колонки при дрейфі схеми
) PARTITION BY RANGE (event_date);
-- партиції по роках: viina.event_2022 … viina.event_2026 (+ автоматично наступний рік)

CREATE INDEX ON viina.event USING gist (location);
CREATE INDEX ON viina.event (event_date);
CREATE INDEX ON viina.event (event_id_1pd);
CREATE INDEX ON viina.event (place_id);
CREATE INDEX ON viina.event (region_place_id, event_date);
CREATE INDEX ON viina.event USING gin (to_tsvector('simple', text));   -- пошук по тексту (опційно)
```

## 11.2. `viina.event_label`

```sql
CREATE TABLE viina.event_label (
    event_id    bigint NOT NULL,
    label_id    smallint NOT NULL REFERENCES viina.label,
    probability real,
    flag        boolean,
    PRIMARY KEY (event_id, label_id)
);
```

Оновлюється разом з `viina.event.labels_hash`: якщо хеш рядка `event_labels` не змінився — 27 рядків не переписуються.

## 11.3. `viina.event_1pd`

```sql
CREATE TABLE viina.event_1pd (
    event_id_1pd  bigint PRIMARY KEY,
    event_date    date NOT NULL,
    n_reports     int NOT NULL,
    event_ids     bigint[] NOT NULL,
    sources       text[] NOT NULL,
    geonameid int, geo_precision text, location geography(Point,4326), place_id int, region_place_id int,
    labels_mask bigint NOT NULL, actors_mask smallint NOT NULL, is_military boolean NOT NULL,
    tid int, viina_version text NOT NULL, record_hash bytea NOT NULL,
    first_snapshot_id bigint NOT NULL, last_snapshot_id bigint NOT NULL, removed_snapshot_id bigint
);
CREATE INDEX ON viina.event_1pd (event_date);
CREATE INDEX ON viina.event_1pd USING gist (location);
```

## 11.4. Контроль території — інтервали замість щоденної панелі

```sql
CREATE TABLE viina.control_span (
    geonameid    int NOT NULL,
    from_date    date NOT NULL,
    to_date      date NOT NULL,          -- включно; останній день файла для «поточного» інтервалу
    status       text NOT NULL,          -- UA | RU | CONTESTED
    status_wiki text, status_boost text, status_dsm text, status_isw text,
    vcontrol_version text NOT NULL,
    snapshot_id  bigint NOT NULL REFERENCES viina.snapshot,
    PRIMARY KEY (geonameid, from_date)
);
CREATE INDEX ON viina.control_span (from_date, to_date);
CREATE INDEX ON viina.control_span (status, from_date, to_date);

CREATE TABLE viina.katottg_control_span ( kod text NOT NULL, …те саме…, PRIMARY KEY (kod, from_date) );
```

Алгоритм: файл року читається відсортовано по (`geonameid`, `date`) — файл уже так упорядкований; якщо ні, сортувати на стороні БД у тимчасовій таблиці. Сусідні дні з однаковим кортежем п'яти статусів зливаються в один інтервал. Очікувано ≤ 1 % рядків стають інтервалами (для 2026: 8,5 млн → десятки тисяч). Оскільки файл року — **повна** панель, завантаження року робиться як `DELETE WHERE from_date BETWEEN 1 січня AND 31 грудня року AND geonameid у файлі` + `INSERT` в одній транзакції; інтервали, що перетинають межу року, лишаються двома записами (по одному на рік) — це допустимо і просто.

Запит «хто контролював пункт на дату D»: `WHERE geonameid = ? AND from_date <= D AND to_date >= D` (`todo_dwh.md` §68–69). Для карти — `ST_Union` тесселяційних полігонів за статусом на дату (кешується сервісом мапи, див. ТЗ мапи §8.4).

## 11.5. Тесселяція

```sql
CREATE TABLE viina.gn_cell (
    geonameid int PRIMARY KEY, name text, asciiname text, alternatenames text,
    feature_code text, population int, admin1_code text, adm1_name text, adm1_code int, adm2_name text, adm2_code int,
    centroid geography(Point,4326) NOT NULL, geometry geometry(MultiPolygon,4326) NOT NULL,
    place_id int REFERENCES places, snapshot_id bigint NOT NULL
);
CREATE INDEX ON viina.gn_cell USING gist (geometry);
CREATE TABLE viina.katottg_cell ( kod text PRIMARY KEY, name text, osm_id bigint, admin_level text, adm1_name_alt text, adm2_name_alt text,
    centroid geography(Point,4326), geometry geometry(Polygon,4326) NOT NULL, place_id int, snapshot_id bigint NOT NULL );
```

Повне перезавантаження при зміні файла (upsert за ключем, видалення зниклих).

## 11.6. Довідники

`viina.label` (§10.3), `viina.source (code, name, lang, homepage)` — 15 медіа з README (seed `data/viina/sources.json`; невідомий код у даних додається автоматично з `name = code`).

---

# 12. Прив'язка до gazetteer Puluj

`GazetteerSeeder` імпортує **всі** населені пункти `UA.txt` GeoNames у `places` з `external_key = 'geonames:{id}'` — той самий ідентифікатор, що і `geonameid` VIINA. Тому:

| `GEO_PRECISION` | `location_kind` | `place_id` | `region_place_id` |
|---|---|---|---|
| `STREET` | `Point` | пункт за `geonameid` (якщо є) | `ST_Contains(oblast.geometry, point)` |
| `ADM3` | `City` / `Town` / `Village` за `places.level` | пункт за `geonameid`; fallback — найближчий `places` у радіусі 3 км від координат | те саме |
| `ADM2` | `District` | район `places.level = District`, що містить точку (COD-AB райони 2020 р.; `ADM2_CODE` VIINA — старі райони GAUL, ним **не** користуватися) | те саме |
| `ADM1` | `Region` | область, що містить точку (координати VIINA для ADM1 — репрезентативний пункт біля центру області, не подія) | = `place_id` |
| інше / порожній `feature_code` без збігу | `Unknown` | null | за точкою, якщо знайдено |

Розв'язання робиться одним SQL після COPY у тимчасову таблицю (`UPDATE … FROM places WHERE external_key = 'geonames:' || geonameid`, потім spatial join для ADM1/ADM2 і `region_place_id`), не по одному рядку з C#. Статистика нерозв'язаних `geonameid` (список різних значень і кількість) — у лог і `snapshot.error`-примітки; якщо нерозв'язано > 2 % ADM3-рядків — попередження в адмінці (ймовірно, gazetteer не завантажений: `scripts/gazetteer/download.ps1`).

Правило Puluj (`Puluj.md` §6): **нечітке місце не перетворюється на точку**. `location` зберігає координати з файла (це походження), але споживачі зобов'язані дивитися на `location_kind`; для `Region`/`District` карта малює полігон/лічильник, не маркер (ТЗ мапи §6.3).

---

# 13. Ідемпотентність і версії рядків

- Ключ ідентичності: `event_id` (події), `event_id_1pd`, `(geonameid|kod, from_date)` (контроль), `geonameid|kod` (клітинки).
- Повторний розбір того самого файла (той самий `sha256`) не виконується (`snapshot` unique).
- Новий реліз: для кожного рядка — `record_hash` збігається → лише `last_snapshot_id` не чіпати (нічого не писати); не збігається → `UPDATE` усіх колонок + `last_snapshot_id`; відсутній у БД → `INSERT`; є в БД для цього року, але відсутній у файлі → `removed_snapshot_id` (рядок лишається: походження не втрачається; споживачі фільтрують `removed_snapshot_id IS NULL`). Повернувся у пізнішому релізі → `removed_snapshot_id = NULL`.
- Історія попередніх значень рядка **не** зберігається в БД (MVP): попередній стан завжди відновлюється з RAW-архіву за `first/last_snapshot_id`. Повна SCD2-історія рядків — завдання DWH (`todo_dwh.md` §48).
- `ForceReload` (§5) ігнорує збіг `oid`/`sha256` і хешів рядків для вказаного року/датасету.
- Кожен файл — одна транзакція; реліз загалом — ні (частковий успіх допустимий: `release.status = partial`, наступний цикл доробить файли, чиї `oid` у `Cursor` не оновлені).

---

# 14. Цикл роботи (псевдокод)

```text
RunAsync(sources):
  loop until cancelled:
    wait PollingInterval  або  Runtime:Viina:CheckNow == true (перевіряти прапорець кожні 15 с)
    MarkPolled
    files = Datasets × Years (+ geojson)
    changed = [f for f in files if pointer(f).oid != cursor.files[f].oid]        // §7
    if changed.empty: MarkSuccess(cursor); continue
    release = INSERT viina.release(running, files_changed = |changed|)
    for f in order(event_info → event_labels → event_1pd → control → kontrol → geojson):
        try:
            path = download(f) with sha256 == oid                                 // §8
            snap = INSERT viina.snapshot
            BEGIN
              COPY csv → temp table (streaming, validation, rejected rows)         // §10
              resolve places                                                       // §12
              upsert by record_hash / rebuild year spans                           // §11, §13
              UPDATE snapshot counters
            COMMIT
            cursor.files[f] = {oid, size, snapshotId}; MarkSuccess(cursor)        // після коміту
            metrics, log
        catch ex: snapshot.error = ex; MarkFailure; continue with next file
    release.status = all ok ? completed : partial
    NOTIFY puluj_events {type: ViinaUpdated, id: release_id}                        // §16
    Runtime:Viina:LastReleaseVersion/At, Status
```

Один інстанс колектора (як `processor`): захист — `pg_advisory_xact_lock(hash('viina'))` навколо релізу, щоб два контейнери з роллю `viina` не працювали одночасно.

---

# 15. Продуктивність

Цілі на розробницькій машині (PostGIS у Docker, SSD):

| операція | обсяг | ціль |
|---|---|---|
| перевірка pointer-ів | 27 запитів | < 10 с |
| `event_info` 2022 | ~220 тис. рядків, 25 МБ zip | < 60 с разом із розв'язанням місць |
| `event_labels` 2022 | ~220 тис. × 27 міток | < 90 с |
| `control` рік | 8,5–12 млн рядків, 440+ МБ CSV | < 4 хв, RSS процесу < 500 МБ |
| повний перший backfill (25 файлів) | ≈ 860 МБ | < 40 хв |
| інкрементний реліз (змінилися файли 2026) | | < 5 хв |

Засоби: потоковий CSV, `COPY BINARY`, `work_mem` для сортування у сесії (`SET LOCAL work_mem = '256MB'`), індекси на партиціях, `ANALYZE` після завантаження року, відсутність EF change tracking на масових шляхах.

---

# 16. Спостережуваність, події, адмінка

- **Метрики** (`PulujMetrics`, OpenTelemetry): `viina.files.checked`, `viina.files.downloaded{dataset}`, `viina.bytes.downloaded`, `viina.rows{dataset,result=inserted|updated|unchanged|removed|rejected}`, `viina.release.duration_ms`, `viina.data.lag_days` (сьогодні − max `event_date`).
- **Логи** Serilog: одна інформаційна стрічка на файл (rows/inserted/updated/…/duration), попередження на дрейф схеми та нерозв'язані місця, помилки з винятком; рядки-відмови — лише лічильник у логу, самі рядки в `viina.rejected_row`.
- **NOTIFY** `puluj_events`: новий `PulujEventType.ViinaUpdated` (`Id = release_id`). `NotifyBridge` в Api транслює у SignalR `ViinaUpdated(meta)` (використання — ТЗ мапи §8.6); Api також інвалідує свій кеш контрольних полігонів.
- **Health**: `CollectorsHealthCheck` уже читає `collector_states`; для VIINA «нездоровий» = `ConsecutiveFailures ≥ 3` **або** `LastSuccessAt` старше 3 діб (реліз щонайбільше раз на 3 дні + запас), **або** лаг даних > 7 днів.
- **Адмін-панель** (`Puluj.Admin`, розділ «Налаштування → Джерела → VIINA» і «Моніторинг → Колектори»): увімкнути/вимкнути, інтервал, набір датасетів і років, `LoadTessellation`, `KeepRawVersions`; таблиця файлів з `oid[:12]`, датою, рядками, тривалістю, помилкою; кнопки «Перевірити зараз» (`Runtime:Viina:CheckNow = true`) і «Перезавантажити…» (`ForceReload`); покриття даних: перший/останній `event_date`, кількість подій по роках, лаг у днях, версія релізу. DTO — у `Puluj.Contracts/AdminDtos.cs` (`ViinaStatusDto`, `ViinaFileStatusDto`).

---

# 17. Обробка помилок

| ситуація | дія |
|---|---|
| raw pointer 404 / HTML | файл пропущено, `LastError`, наступний файл; якщо 404 для **всіх** файлів — можливо, змінилась гілка/розкладка: `ConsecutiveFailures++`, помилка в адмінці «структура репозиторію змінилася» |
| media URL 404, batch API OK | працювати через batch (fallback вбудований) |
| sha256 ≠ oid | повтор до 3 разів, потім помилка файла |
| zip без очікуваного CSV / кілька CSV | взяти файл із назвою `{file}.csv`, інакше помилка «unexpected archive layout» |
| дрейф схеми | §10.1 |
| > 5 % відхилених рядків | файл не комітиться |
| нерозв'язані `geonameid` > 2 % | попередження, завантаження триває |
| помилка БД посеред файла | транзакція відкочується, `Cursor` не оновлено, повтор у наступному циклі |
| диск: `RawDirectory` недоступний або < 2 ГБ вільного | реліз не починається, помилка в адмінці |

Помилки одного колектора ніколи не зачіпають інші (`CollectorSupervisor` перезапускає з backoff).

---

# 18. Безпека і ліцензія

- Жодних секретів не потрібно; `GitHubToken` необов'язковий і зберігається як `sources.secrets` (`github_token`) за загальним правилом — ніколи не повертається в браузер.
- Вихідний трафік тільки на `raw.githubusercontent.com`, `media.githubusercontent.com`, `github.com`, `api.github.com`, `*.githubusercontent.com`/S3 (підписані LFS-URL). Врахувати у firewall/egress-правилах Docker-хоста.
- ODbL: атрибуція в UI і в `sources.config.attribution`; при передачі даних VIINA далі (API третім особам) — та сама ліцензія. Медіа-тексти (`text`) — це заголовки/уривки новин з відкритих сайтів; зберігаються як є (`todo_dwh.md` §72), переклад не робиться.
- Api читає схему `viina` роллю `puluj_reader` — міграція `AddViina` має видати `GRANT USAGE ON SCHEMA viina` і `SELECT` на таблиці (`ALTER DEFAULT PRIVILEGES` з `AddDbRoles` стосується лише схеми `public` — перевірити й додати для `viina`).

---

# 19. Тестування

- **Fixtures**: `tests/data/viina/` — усічені файли по 200–500 рядків кожного датасету (з реальних 2026, з рядками усіх `GEO_PRECISION`, з `text` у лапках і з комою, з порожніми `t_loc_b`/`tid`, з `n_reports > 1`), маленький `gn_UA_tess` на 20 features, LFS-pointer-файл, і «релізи» v1/v2 одного файла з доданим/зміненим/видаленим рядком.
- **Unit** (`Puluj.Processing.Tests` або новий `Puluj.Collectors.Tests`): парсер pointer-а; CSV reader на крайових випадках; валідація рядка; обчислення `record_hash` стабільне; згортання панелі контролю в інтервали (включно з межами року і одноденними змінами); розбір `event_ids` списку; мапінг `GEO_PRECISION → LocationKind`; тлумачення `time = 00:00`.
- **Integration** (`Puluj.Integration.Tests`, Testcontainers PostGIS, як `PipelineTests`): seed gazetteer з тестових файлів → завантажити fixture-реліз v1 через `ViinaCollector` з підміненим `HttpMessageHandler` (без мережі) → перевірити кількості, `place_id` для кожного `GEO_PRECISION`, маски міток; завантажити v2 → перевірити inserted/updated/unchanged/removed і незмінність `first_snapshot_id`; повторити v2 → усе `unchanged`, `snapshot` не дублюється; змоделювати дрейф схеми → файл відхилено, реліз `partial`.
- Тести не ходять у GitHub (правило `todo_dwh.md` §89).

---

# 20. Етапи

**MVP (це ТЗ):** `event_info`, `event_labels`, `event_1pd` за всі роки; RAW-архів; `viina.snapshot/release`; прив'язка до `places`; адмінка; NOTIFY; тести.

**MVP+1:** `control`/`kontrol` як інтервали; тесселяції; попередньо обчислені полігони контролю на дату для карти (ТЗ мапи §8.4).

**MVP+2:** міст у `Target` для вибраних типів з реальним часом (`IdentificationMethod.Structured`, `IdentificationSource = "viina"`, прапорець «історичний, не корелювати»); перенесення схеми в DWH (`stg_viina`) і S3 для RAW.

---

# 21. Критерії приймання

1. Після `dev-run.ps1 -ResetDb` з `Collectors:Viina:Enabled = true` протягом 40 хв у `viina.event` є всі події 2022–2026, `viina.snapshot` містить 15 рядків (3 датасети × 5 років) без `error`, у `collector_states` — `LastSuccessAt` і `Cursor` з 15 oid.
2. Повторний цикл без змін upstream не завантажує жодного байта (лише pointer-и) і не пише в `viina.*`.
3. Для 2026: ≥ 98 % рядків з `GEO_PRECISION = ADM3` мають `place_id`; 100 % рядків з `ADM1` мають `region_place_id` і `location_kind = Region`.
4. Кожна подія веде до файла: `event.last_snapshot_id → snapshot.raw_path`, файл існує, його `sha256` збігається з записом.
5. Реліз, у якому змінився один файл, обробляється < 5 хв і породжує рівно один `ViinaUpdated`.
6. Вимкнення колектора в адмінці зупиняє його без рестарту процесу; увімкнення — продовжує з `Cursor`.
7. Падіння мережі посеред релізу лишає БД консистентною (файли — атомарно), наступний цикл доробляє решту.
8. `dotnet test` зелений, у т. ч. інтеграційний сценарій v1 → v2 → v2.

---

# 22. Відкриті питання (потребують рішення до або під час реалізації)

1. Часовий пояс `time` у VIINA — запит авторам або емпірична перевірка по кількох подіях з відомим часом (напр. масовані атаки). До з'ясування — Europe/Kyiv.
2. Чи потрібні `kontrol`/КАТОТТГ у Puluj зараз (gazetteer Puluj на КАТОТТГ не спирається; `places.katottg_code` заповнений лише частково)? Пропозиція: завантажувати як інтервали (дешево), полігони КАТОТТГ — за потреби.
3. Глибина RAW-архіву на диску до появи S3/MinIO: усі версії (≈ до 8 ГБ/міс у гіршому разі) чи останні 3.
4. Чи виносити колектор в окремий контейнер за замовчуванням, чи тримати в `processor`-подібному одному процесі (пам'ять при розборі `control` до 500 МБ).
