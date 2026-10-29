"""Verify every shipped file against SHA256SUMS and optionally test its ZIP."""
import argparse
import hashlib
import json
from pathlib import Path
from zipfile import ZipFile


def sha256(path):
    value = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            value.update(block)
    return value.hexdigest()


parser = argparse.ArgumentParser()
parser.add_argument('--root', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--archive', type=Path)
args = parser.parse_args()
root = args.root.resolve(strict=True)
listed = set()
failures = []
for line in (root / 'SHA256SUMS.txt').read_text(encoding='utf-8-sig').splitlines():
    expected, relative = line.split('  ', 1)
    path = (root / relative).resolve()
    if not path.is_relative_to(root) or relative in listed:
        raise SystemExit('Unsafe or duplicate manifest path: ' + relative)
    listed.add(relative)
    if not path.is_file() or sha256(path) != expected.lower():
        failures.append(relative)
actual = {p.relative_to(root).as_posix() for p in root.rglob('*') if p.is_file()}
unexpected = sorted(actual - listed - {'SHA256SUMS.txt'})
result = {'status': 'passed' if not failures and not unexpected else 'failed',
          'root': str(root), 'verifiedFiles': len(listed),
          'mismatches': failures, 'unexpectedFiles': unexpected}
if args.archive:
    with ZipFile(args.archive) as archive:
        corrupt = archive.testzip()
        result['zipEntries'] = len(archive.infolist())
    result['archiveSha256'] = sha256(args.archive)
    result['archiveBytes'] = args.archive.stat().st_size
    result['zipCorruptEntry'] = corrupt
    if corrupt:
        result['status'] = 'failed'
args.output.parent.mkdir(parents=True, exist_ok=True)
args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
print(json.dumps(result, ensure_ascii=False))
raise SystemExit(0 if result['status'] == 'passed' else 1)
