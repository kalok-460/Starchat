# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 StarChat contributors
"""Pinned, consent-only model installation. Translation itself never downloads."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import urllib.request

MANIFEST = json.loads(Path(__file__).with_name('model.json').read_text('utf-8'))
FORMAT = 2


def model_root():
    override = os.environ.get('STARCHAT_LOCAL_MODEL_ROOT')
    return Path(override) if override else Path(os.environ['LOCALAPPDATA']) / 'StarChat' / 'models' / 'nllb-200-distilled-600m-ct2-int8'


def digest(path):
    h = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            h.update(block)
    return h.hexdigest()


def installed(root=None, verify=False):
    root = root or model_root()
    try:
        stamp = json.loads((root / 'installed.json').read_text('utf-8'))
        if stamp['format'] != FORMAT or stamp['revision'] != MANIFEST['revision'] or stamp['engine'] != 'ctranslate2':
            return False
        expected = set(MANIFEST['files'])
        if set(stamp['files']) != expected:
            return False
        for name, info in stamp['files'].items():
            path = root / name
            original = MANIFEST['files'].get(name)
            if original and info != original:
                return False
            if not path.is_file() or path.stat().st_size != info['bytes']:
                return False
            if verify and digest(path) != info['sha256']:
                return False
        return True
    except (OSError, ValueError, KeyError, TypeError):
        return False


def install(progress, root=None, opener=urllib.request.urlopen):
    root = root or model_root()
    if installed(root, verify=True):
        progress({'type': 'done'})
        return
    root.mkdir(parents=True, exist_ok=True)
    # A process-owned OS lock is released even when the cancel button kills us.
    import msvcrt
    with (root / '.install.lock').open('a+b') as lock:
        if lock.tell() == 0:
            lock.write(b'0'); lock.flush()
        lock.seek(0)
        msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
        _install_locked(root, progress, opener)


def _install_locked(root, progress, opener):
    staging = root / '.download'
    staging.mkdir(exist_ok=True)
    total = sum(v['bytes'] for v in MANIFEST['files'].values())
    required = sum(v['bytes'] for n, v in MANIFEST['files'].items()
                   if not (staging / n).is_file()) + 32 * 1024 * 1024
    if shutil.disk_usage(root).free < required:
        raise OSError('disk_space')
    completed = 0
    for name, info in MANIFEST['files'].items():
        path = staging / name
        path.parent.mkdir(parents=True, exist_ok=True)
        if not (path.is_file() and path.stat().st_size == info['bytes'] and digest(path) == info['sha256']):
            part = path.with_name(path.name + '.part')
            url = 'https://huggingface.co/{}/resolve/{}/{}'.format(MANIFEST['repository'], MANIFEST['revision'], name)
            request = urllib.request.Request(url, headers={'User-Agent': 'StarChat-ModelInstaller/1.0'})
            read = 0
            with opener(request, timeout=60) as response, part.open('wb') as output:
                for chunk in iter(lambda: response.read(1024 * 1024), b''):
                    read += len(chunk)
                    if read > info['bytes']:
                        raise ValueError('download_size')
                    output.write(chunk)
                    progress({'type': 'progress', 'bytes': completed + read, 'total': total})
            if read != info['bytes'] or digest(part) != info['sha256']:
                raise ValueError('download_checksum')
            part.replace(path)
        completed += info['bytes']
        progress({'type': 'progress', 'bytes': completed, 'total': total})
    progress({'type': 'preparing'})
    names = list(MANIFEST['files'])
    inventory = {}
    for name in names:
        src, dest = staging / name, root / name
        inventory[name] = {'bytes': src.stat().st_size, 'sha256': digest(src)}
        dest.parent.mkdir(parents=True, exist_ok=True)
        src.replace(dest)
    stamp = {'format': FORMAT, 'revision': MANIFEST['revision'], 'engine': 'ctranslate2', 'files': inventory}
    temp = root / 'installed.json.new'
    temp.write_text(json.dumps(stamp, indent=2) + '\n', encoding='utf-8')
    temp.replace(root / 'installed.json')  # Publish only after every file passes.
    progress({'type': 'done'})
