#!/usr/bin/env python3
import csv
import hashlib
import re
from pathlib import Path

LOG = Path('artifacts/build-warning-inventory.log')
CSV_ALL = Path('artifacts/build-warning-inventory.csv')
CSV_UNIQ = Path('artifacts/build-warning-inventory.unique.csv')

PAT = re.compile(r'^(?P<File>/.+?)\((?P<Line>\d+),(?P<Column>\d+)\): warning (?P<WarningCode>[A-Z]{2}\d+): (?P<Message>.+?) \[(?P<Project>.+?)\]$')
FIELDS = ['Project','WarningCode','File','Line','Column','Message','RawWarningLine']

if not LOG.exists():
    raise SystemExit(f'missing log: {LOG}')

rows = []
with LOG.open('r', encoding='utf-8', errors='ignore') as f:
    for line in f:
        raw = line.rstrip('\n')
        m = PAT.match(raw)
        if not m:
            continue
        d = m.groupdict()
        d['RawWarningLine'] = raw
        rows.append(d)

CSV_ALL.parent.mkdir(parents=True, exist_ok=True)
with CSV_ALL.open('w', encoding='utf-8', newline='') as f:
    w = csv.DictWriter(f, fieldnames=FIELDS)
    w.writeheader()
    for r in rows:
        w.writerow({k: r[k] for k in FIELDS})

seen = set()
uniq = []
for r in rows:
    key = (r['File'], r['Line'], r['Column'], r['WarningCode'], r['Message'])
    if key in seen:
        continue
    seen.add(key)
    uniq.append(r)

with CSV_UNIQ.open('w', encoding='utf-8', newline='') as f:
    w = csv.DictWriter(f, fieldnames=FIELDS)
    w.writeheader()
    for r in uniq:
        w.writerow({k: r[k] for k in FIELDS})

sha = hashlib.sha256(LOG.read_bytes()).hexdigest()
print(f'log={LOG}')
print(f'log_sha256={sha}')
print(f'raw_warning_count={len(rows)}')
print(f'unique_warning_count={len(uniq)}')
print(f'unique_warning_codes={len({r["WarningCode"] for r in uniq})}')
print(f'wrote={CSV_ALL}')
print(f'wrote={CSV_UNIQ}')
