"""Exports raw_messages to monthly gzipped NDJSON files (one JSON object per line, sorted by published_at).

Reads the live DB through `docker exec <container> psql` in short raw_message_id batches, so no long
transaction or lock is held on raw_messages while the collector inserts. Rows above the max id seen at
start are skipped, which makes the dump a consistent cut.

Usage: python scripts/export-raw-messages.py <out-dir> [--container puluj-g-postgis-1] [--batch 100000]
Output: raw_messages-YYYY-MM.ndjson.gz per month of published_at (UTC), sources.json, manifest.json.
"""
import argparse, gzip, hashlib, json, os, subprocess, sys, time

COLUMNS = """r.raw_message_id, r.source_id, s.code as source_code, r.source_message_id, r.source_message_key,
    r.source_revision, r.published_at, r.received_at, r.url, r.hash, r.raw_text, r.raw_payload"""


def psql(container, sql):
    cmd = ['docker', 'exec', '-e', 'PGTZ=UTC', '-e', 'PGCLIENTENCODING=UTF8', container,
           'psql', '-U', 'puluj', '-d', 'puluj', '-X', '-q', '-A', '-t', '-v', 'ON_ERROR_STOP=1', '-c', sql]
    res = subprocess.run(cmd, capture_output=True)
    if res.returncode != 0:
        sys.exit(f'psql failed: {res.stderr.decode("utf-8", "replace")}')
    return res.stdout


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('out')
    ap.add_argument('--container', default='puluj-g-postgis-1')
    ap.add_argument('--batch', type=int, default=100_000)
    a = ap.parse_args()
    parts = os.path.join(a.out, 'parts')
    os.makedirs(parts, exist_ok=True)
    for stale in os.listdir(parts):
        os.remove(os.path.join(parts, stale))

    lo, hi = map(int, psql(a.container, 'select min(raw_message_id), max(raw_message_id) from raw_messages').decode().strip().split('|'))
    print(f'raw_message_id {lo}..{hi}', flush=True)

    # Pass 1: stream batches by primary key, append each row to its month's unsorted part file.
    # Line prefix "<published_at>\t<id>\t" is the sort key for pass 2; JSON never holds a raw tab.
    files, total, started = {}, 0, time.time()
    for start in range(lo, hi + 1, a.batch):
        end = min(start + a.batch, hi + 1)
        sql = f"""select to_char(t.published_at at time zone 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.US') || E'\\t'
                         || t.raw_message_id || E'\\t' || row_to_json(t)::text
                  from (select {COLUMNS} from raw_messages r join sources s using (source_id)
                        where r.raw_message_id >= {start} and r.raw_message_id < {end}) t"""
        for line in psql(a.container, sql).splitlines():
            month = line[:7].decode()
            f = files.get(month)
            if f is None:
                f = files[month] = open(os.path.join(parts, f'{month}.tsv'), 'ab')
            f.write(line + b'\n')
            total += 1
        print(f'  ids < {end}: {total} rows, {time.time() - started:.0f}s', flush=True)
    for f in files.values():
        f.close()

    # Pass 2: sort each month by (published_at, raw_message_id) and gzip it.
    manifest = {'exported_at': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()), 'max_raw_message_id': hi,
                'total_rows': total, 'files': []}
    for month in sorted(files):
        part = os.path.join(parts, f'{month}.tsv')
        with open(part, 'rb') as f:
            rows = [l.rstrip(b'\n').split(b'\t', 2) for l in f]
        rows.sort(key=lambda r: (r[0], int(r[1])))
        name = f'raw_messages-{month}.ndjson.gz'
        path = os.path.join(a.out, name)
        with gzip.open(path, 'wb', compresslevel=6) as g:
            for r in rows:
                g.write(r[2] + b'\n')
        sha = hashlib.sha256()
        with open(path, 'rb') as f:
            for chunk in iter(lambda: f.read(1 << 20), b''):
                sha.update(chunk)
        manifest['files'].append({'name': name, 'month': month, 'rows': len(rows),
                                  'bytes': os.path.getsize(path), 'sha256': sha.hexdigest()})
        os.remove(part)
        print(f'  {name}: {len(rows)} rows, {os.path.getsize(path) / 1e6:.1f} MB', flush=True)
    os.rmdir(parts)

    sources = psql(a.container, """select coalesce(json_agg(t order by t.source_id), '[]') from (
        select source_id, code, name, type, url, trust_level, priority, enabled, config from sources) t""")
    with open(os.path.join(a.out, 'sources.json'), 'wb') as f:
        f.write(sources.strip() + b'\n')
    with open(os.path.join(a.out, 'manifest.json'), 'w', encoding='utf-8') as f:
        json.dump(manifest, f, ensure_ascii=False, indent=2)
    print(f'done: {total} rows in {len(files)} files', flush=True)


if __name__ == '__main__':
    main()
