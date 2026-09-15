"""Injects a demo situation through POST /api/admin/dev/ingest of the admin service. Usage: python scripts/dev-scenario.py"""
import json, urllib.request, datetime, sys
base = sys.argv[1] if len(sys.argv) > 1 else 'http://localhost:5268'
def post(path, body):
    req = urllib.request.Request(base + path, data=json.dumps(body).encode('utf-8'), headers={'Content-Type': 'application/json'})
    with urllib.request.urlopen(req, timeout=20) as r:
        return json.load(r)
now = datetime.datetime.now(datetime.timezone.utc)
def at(minutes_ago): return (now - datetime.timedelta(minutes=minutes_ago)).isoformat()
def alert(uid, title, minutes_ago, kind='alert.started'):
    return {'sourceCode': 'alerts_in_ua', 'publishedAt': at(minutes_ago), 'sourceMessageId': f'demo-{uid}:{kind}',
            'payload': {'kind': kind, 'at': at(minutes_ago),
                        'alert': {'id': uid, 'location_title': title, 'location_type': 'oblast', 'location_oblast': title,
                                  'alert_type': 'air_raid', 'started_at': at(minutes_ago)}}}
steps = [
    alert(9101, 'Київська область', 25),
    alert(9102, 'Чернігівська область', 30),
    alert(9103, 'Сумська область', 28),
    {'sourceCode': 'tg_kpszsu', 'text': 'Шахеди на Чернігівщині курсом на Київщину.', 'publishedAt': at(24)},
    {'sourceCode': 'tg_kpszsu', 'text': 'Група БпЛА на Сумщині у південно-західному напрямку.', 'publishedAt': at(22)},
    {'sourceCode': 'tg_kpszsu', 'text': 'БпЛА на півночі Київщини курсом на Київ.', 'publishedAt': at(6)},
    {'sourceCode': 'tg_kpszsu', 'text': 'Крилаті ракети через Кіровоградщину курсом на Вінниччину.', 'publishedAt': at(20)},
    {'sourceCode': 'tg_kpszsu', 'text': 'Крилата ракета на Вінниччині у західному напрямку.', 'publishedAt': at(2)},
    {'sourceCode': 'tg_kpszsu', 'text': 'Пуск балістики з Брянської області! Загроза для Чернігівщини!', 'publishedAt': at(1)},
]
for s in steps:
    print(post('/api/admin/dev/ingest', s))
