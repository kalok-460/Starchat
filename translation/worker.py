# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 StarChat contributors
import argparse
import json
import os
import sys
from pathlib import Path

# Embedded Python uses an isolated ._pth; explicitly add only our script folder.
sys.path.insert(0, str(Path(__file__).resolve().parent))
from runtime import installed, install, model_root

# Bound native pools before importing CTranslate2/NumPy; yield while idle.
for key, value in {'OMP_NUM_THREADS':'4', 'MKL_NUM_THREADS':'4',
                   'OPENBLAS_NUM_THREADS':'1', 'OMP_WAIT_POLICY':'PASSIVE',
                   'KMP_BLOCKTIME':'0'}.items():
    os.environ[key] = value


def emit(value):
    print(json.dumps(value, ensure_ascii=True), flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--install', action='store_true')
    args = parser.parse_args()
    if args.install:
        try:
            install(emit)
            return 0
        except Exception as exc:
            # Never echo server responses, source texts, credentials or URLs.
            code = str(exc) if str(exc) in {'disk_space', 'download_size', 'download_checksum'} else 'install_failed'
            emit({'type': 'error', 'code': code})
            return 1
    if not installed():
        emit({'type': 'error', 'code': 'not_installed'})
        return 1
    engine = None
    for line in sys.stdin:
        try:
            request = json.loads(line)
            if engine is None:
                from engine import Engine
                engine = Engine(model_root())
            result = engine.translate(str(request['text']), str(request['source']), str(request['target']))
            emit({'type': 'result', 'text': result, 'backend': engine.backend})
        except Exception as exc:
            code = str(exc) if str(exc) in {'language', 'too_long'} else 'inference_failed'
            if engine is None and isinstance(exc, (ImportError, OSError)):
                code = 'runtime_missing'
            emit({'type': 'error', 'code': code})
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
