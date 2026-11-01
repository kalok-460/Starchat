# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 StarChat contributors
"""Developer setup only: fetch pinned official models and verify every file."""
import hashlib
import json
import os
from pathlib import Path
import shutil
import urllib.request

ROOT = Path(__file__).resolve().parent


def download_models():
    manifest = json.loads((ROOT / "models-onnx.json").read_text(encoding="utf-8"))
    for kind, model in manifest["models"].items():
        destination = ROOT / "models" / "onnx" / kind
        destination.mkdir(parents=True, exist_ok=True)
        for name, info in model["files"].items():
            target = destination / name
            if target.is_file() and hashlib.sha256(target.read_bytes()).hexdigest() == info["sha256"]:
                continue
            url = "https://huggingface.co/{}/resolve/{}/{}".format(model["repository"], model["revision"], name)
            with urllib.request.urlopen(url, timeout=120) as response:
                content = response.read()
            if len(content) != info["bytes"] or hashlib.sha256(content).hexdigest() != info["sha256"]:
                raise RuntimeError("Model checksum mismatch: " + kind + "/" + name)
            temporary = target.with_suffix(target.suffix + ".download")
            temporary.write_bytes(content)
            os.replace(temporary, target)
        shutil.copy2(ROOT.parent / "licenses" / "PaddleX-LICENSE.txt", destination / "LICENSE")
        print("Verified PP-OCRv6 small ONNX " + kind, flush=True)


if __name__ == "__main__":
    download_models()
