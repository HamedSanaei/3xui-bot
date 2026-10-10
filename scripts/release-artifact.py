#!/usr/bin/env python3
"""Validate completed test runs and immutable, data-free Linux application archives."""
import argparse
import hashlib
import io
import json
import os
from pathlib import Path, PurePosixPath
import re
import stat
import tarfile
import xml.etree.ElementTree as ET

MAX_BYTES = 2 * 1024 * 1024 * 1024
MAX_FILES = 20000


def require(condition, message):
    if not condition:
        raise ValueError(message)


def safe_path(name):
    parts = name.split('/')
    require(name and not name.startswith('/') and '\\' not in name,
            'unsafe archive path')
    require(all(p not in ('', '.', '..') for p in parts), 'unsafe archive path component')
    require(all(re.fullmatch(r'[A-Za-z0-9_.@+ -]+', p) for p in parts), 'unsupported archive path')
    return PurePosixPath(name)


def allowed(name):
    path = safe_path(name)
    parts = [p.lower() for p in path.parts]
    base = parts[-1]
    require(not any(p in ('data', 'telemetry', 'adminbot.tests', '.git', '.ssh', 'scripts', 'obj') for p in parts),
            'protected or non-runtime content in artifact')
    require(not any('test' in p and (p.endswith('.dll') or p.endswith('.pdb')) for p in parts),
            'test assembly in artifact')
    require(not any(p.startswith('.env') or 'token' in p or 'secret' in p for p in parts),
            'secret-bearing path in artifact')
    require(not (base.startswith(('configuration', 'appsettings')) or
                 '.db' in base or base.endswith(('.cs', '.csproj', '.sln', '.trx', '.key', '.pem', '.pfx'))),
            'configuration, database, source or secret in artifact')


def stream_digest(stream):
    checksum = hashlib.sha256()
    while True:
        block = stream.read(1024 * 1024)
        if not block:
            return checksum.hexdigest()
        checksum.update(block)


def file_digest(path):
    with Path(path).open('rb') as stream:
        return stream_digest(stream)


def pack(root, sha, archive):
    require(re.fullmatch(r'[0-9a-f]{40}', sha), 'invalid commit SHA')
    root = Path(root)
    require(root.is_dir() and not root.is_symlink(), 'publish root must be a real directory')
    records = {}
    total = 0
    for directory, dirs, files in os.walk(root, followlinks=False):
        for entry in dirs + files:
            path = Path(directory) / entry
            require(not path.is_symlink(), 'symlink in published output')
            allowed(path.relative_to(root).as_posix())
        for entry in sorted(files):
            path = Path(directory) / entry
            info = path.stat()
            require(stat.S_ISREG(info.st_mode) and info.st_nlink == 1, 'non-regular or hardlinked published file')
            name = path.relative_to(root).as_posix()
            total += info.st_size
            require(total <= MAX_BYTES and len(records) < MAX_FILES, 'artifact exceeds size/count limit')
            records[name] = {'size': info.st_size, 'sha256': file_digest(path),
                             'mode': 0o755 if info.st_mode & 0o111 else 0o644}
    require('Adminbot' in records and records['Adminbot']['mode'] == 0o755, 'missing executable')
    require(all(p in records for p in ('Adminbot.dll', 'Adminbot.deps.json', 'Adminbot.runtimeconfig.json')),
            'incomplete runtime output')
    manifest = {'format': 1, 'commit': sha, 'runtime': 'linux-x64', 'files': records}
    with tarfile.open(archive, 'w:gz', format=tarfile.USTAR_FORMAT) as tar:
        data = json.dumps(manifest, sort_keys=True, separators=(',', ':')).encode()
        item = tarfile.TarInfo('release.json')
        item.size, item.mode = len(data), 0o644
        tar.addfile(item, io.BytesIO(data))
        for name, record in sorted(records.items()):
            item = tarfile.TarInfo('publish/' + name)
            item.size, item.mode = record['size'], record['mode']
            with (root / name).open('rb') as stream:
                require(stream_digest(stream) == record['sha256'], 'publish changed while packing')
                stream.seek(0)
                tar.addfile(item, stream)
    print(file_digest(archive))


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, 'duplicate manifest key')
        result[key] = value
    return result


def verify(archive, sha, checksum, destination):
    require(re.fullmatch(r'[0-9a-f]{40}', sha) and re.fullmatch(r'[0-9a-f]{64}', checksum), 'invalid SHA/checksum')
    path = Path(archive)
    require(path.is_file() and not path.is_symlink() and path.stat().st_size <= MAX_BYTES, 'unsafe archive file')
    require(file_digest(path) == checksum, 'archive checksum mismatch')
    # Open the same descriptor for validation and extraction; never use tar.extract/all on untrusted members.
    with tarfile.open(path, 'r:gz') as tar:
        names = set()
        total = 0
        for member in tar:
            safe_path(member.name)
            require(member.name not in names, 'duplicate archive entry')
            require(member.isfile() and not member.linkname and not member.pax_headers,
                    'archive links/directories/special entries are forbidden')
            require(member.mode in (0o644, 0o755), 'unsafe file mode')
            require(0 <= member.size <= MAX_BYTES, 'unsafe member size')
            names.add(member.name)
            total += member.size
            require(total <= MAX_BYTES and len(names) <= MAX_FILES + 1, 'archive exceeds size/count limit')
        require('release.json' in names, 'missing manifest')
        item = tar.getmember('release.json')
        require(item.size <= 8 * 1024 * 1024, 'manifest too large')
        manifest = json.loads(tar.extractfile(item).read(), object_pairs_hook=unique_object)
        require(set(manifest) == {'format', 'commit', 'runtime', 'files'} and manifest['format'] == 1 and
                manifest['commit'] == sha and manifest['runtime'] == 'linux-x64', 'manifest identity mismatch')
        records = manifest['files']
        require(isinstance(records, dict) and records, 'empty/invalid file manifest')
        require(names == {'release.json'} | {'publish/' + n for n in records}, 'archive/manifest content mismatch')
        require(all(p in records for p in ('Adminbot', 'Adminbot.dll', 'Adminbot.deps.json', 'Adminbot.runtimeconfig.json')),
                'incomplete runtime output')
        for name, record in records.items():
            allowed(name)
            require(isinstance(record, dict) and set(record) == {'size', 'mode', 'sha256'}, 'invalid file record')
            member = tar.getmember('publish/' + name)
            require(member.size == record['size'] and member.mode == record['mode'], 'file size/mode mismatch')
            require(stream_digest(tar.extractfile(member)) == record['sha256'], 'file checksum mismatch')
        require(records['Adminbot']['mode'] == 0o755, 'application is not executable')
        # Reject a file used as another file's parent before any filesystem writes.
        for name in records:
            require(not any(parent.as_posix() in records for parent in PurePosixPath(name).parents if str(parent) != '.'),
                    'file/directory path collision')
        if destination:
            target = Path(destination)
            require(not target.exists() and not target.is_symlink(), 'extraction destination must be absent')
            require(target.parent.resolve(strict=True) == target.parent.absolute(),
                    'extraction parent path contains a symlink or traversal')
            target.mkdir(mode=0o700)
            for name, record in records.items():
                output = target / name
                output.parent.mkdir(parents=True, exist_ok=True)
                with output.open('xb') as stream, tar.extractfile('publish/' + name) as source:
                    while True:
                        block = source.read(1024 * 1024)
                        if not block:
                            break
                        stream.write(block)
                output.chmod(record['mode'])
    print('Verified immutable release for ' + sha)


def tests(discovery, results):
    text = Path(discovery).read_text(encoding='utf-8-sig')
    marker = 'The following Tests are available:'
    require(text.count(marker) == 1, 'test discovery did not complete')
    expected = [line.strip() for line in text.split(marker, 1)[1].splitlines() if line.strip()]
    require(expected, 'no discovered tests')
    files = list(Path(results).glob('*.trx'))
    require(len(files) == 1, 'expected exactly one completed TRX report')
    root = ET.parse(files[0]).getroot()
    ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    summary = root.find('t:ResultSummary', ns)
    require(summary is not None and summary.get('outcome') == 'Completed', 'test run incomplete/aborted')
    counters = summary.find('t:Counters', ns)
    require(counters is not None, 'missing test counters')
    count = len(expected)
    require(all(int(counters.get(k, '-1')) == count for k in ('total', 'executed', 'passed')),
            'not every discovered test passed')
    require(all(int(counters.get(k, '0')) == 0 for k in
                ('failed', 'error', 'timeout', 'aborted', 'inconclusive', 'notExecuted', 'disconnected', 'warning')),
            'non-passing test outcomes')
    outcomes = root.findall('t:Results/t:UnitTestResult', ns)
    require(len(outcomes) == count and all(r.get('outcome') == 'Passed' for r in outcomes), 'incomplete result set')
    print(f'All {count} discovered tests completed and passed')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)
    pack_args = sub.add_parser('pack')
    pack_args.add_argument('root')
    pack_args.add_argument('sha')
    pack_args.add_argument('archive')
    verify_args = sub.add_parser('verify')
    verify_args.add_argument('archive')
    verify_args.add_argument('sha')
    verify_args.add_argument('checksum')
    verify_args.add_argument('--extract')
    test_args = sub.add_parser('tests')
    test_args.add_argument('discovery')
    test_args.add_argument('results')
    args = parser.parse_args()
    try:
        if args.command == 'pack':
            pack(args.root, args.sha, args.archive)
        elif args.command == 'verify':
            verify(args.archive, args.sha, args.checksum, args.extract)
        else:
            tests(args.discovery, args.results)
    except (ValueError, OSError, tarfile.TarError, ET.ParseError, TypeError, KeyError) as error:
        parser.exit(64, f'Release refused: {error}\n')


if __name__ == '__main__':
    main()
