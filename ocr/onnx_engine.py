# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 StarChat contributors
"""PP-OCRv6 small inference, without Paddle or vendor-specific GPU packages.

Resize, DB boxes and CTC decoding follow PaddleX's Apache-2.0 processors;
see licenses/PaddleX-LICENSE.txt and THIRD_PARTY_NOTICES.txt. The detection
parameters below preserve the actual StarChat 0.3.10 pipeline overrides.
"""
from pathlib import Path
import math
import os
import sys

import cv2
import numpy as np
import pyclipper
import yaml

ROOT = Path(__file__).resolve().parent


def session_options(ort):
    options = ort.SessionOptions()
    options.execution_mode = ort.ExecutionMode.ORT_SEQUENTIAL
    options.enable_mem_pattern = False  # Required by DirectML, including dynamic inputs.
    options.enable_cpu_mem_arena = False
    options.intra_op_num_threads = 2
    options.inter_op_num_threads = 1
    options.add_session_config_entry("session.intra_op.allow_spinning", "0")
    options.add_session_config_entry("session.inter_op.allow_spinning", "0")
    options.add_session_config_entry("ep.dml.enable_cpu_sync_spinning", "0")
    options.add_session_config_entry("ep.dml.disable_memory_arena", "1")
    options.log_severity_level = 3
    return options


def detection_tensor(image):
    h, w = image.shape[:2]
    # The previous PaddleOCR pipeline used limit_side_len=64, limit_type=min.
    ratio = max(1., 64. / min(h, w))
    rh, rw = int(h * ratio), int(w * ratio)
    if max(rh, rw) > 4000:
        ratio = 4000. / max(rh, rw)
        rh, rw = int(rh * ratio), int(rw * ratio)
    rh, rw = max(32, round(rh / 32) * 32), max(32, round(rw / 32) * 32)
    resized = image if (rh, rw) == (h, w) else cv2.resize(image, (rw, rh))
    values = resized.astype(np.float32)
    # Keep BGR order. The official normalization does not swap channels.
    values *= np.array([1. / 255 / .229, 1. / 255 / .224, 1. / 255 / .225], np.float32)
    values += np.array([-.485 / .229, -.456 / .224, -.406 / .225], np.float32)
    return np.ascontiguousarray(values.transpose(2, 0, 1)[None])


def mini_box(contour):
    rect = cv2.minAreaRect(np.asarray(contour, np.float32))
    points = sorted(cv2.boxPoints(rect), key=lambda p: p[0])
    left, right = sorted(points[:2], key=lambda p: p[1]), sorted(points[2:], key=lambda p: p[1])
    return np.array([left[0], right[0], right[1], left[1]], np.float32), min(rect[1])


def detection_boxes(prediction, original_shape):
    bitmap = np.asarray(prediction)[0, 0]
    if bitmap.ndim != 2 or not np.isfinite(bitmap).all():
        raise ValueError("Invalid ONNX detection output")
    height, width = bitmap.shape
    dest_h, dest_w = original_shape[:2]
    contours, _ = cv2.findContours((bitmap > .3).astype(np.uint8) * 255,
                                  cv2.RETR_LIST, cv2.CHAIN_APPROX_SIMPLE)
    boxes = []
    for contour in contours[:3000]:
        points, short_side = mini_box(contour)
        if short_side < 3:
            continue
        xmin, ymin = np.maximum(0, np.floor(points.min(axis=0))).astype(int)
        xmax, ymax = np.minimum([width - 1, height - 1], np.ceil(points.max(axis=0))).astype(int)
        if xmax < xmin or ymax < ymin:
            continue
        mask = np.zeros((ymax - ymin + 1, xmax - xmin + 1), np.uint8)
        cv2.fillPoly(mask, [(points - [xmin, ymin]).astype(np.int32)], 1)
        if cv2.mean(bitmap[ymin:ymax+1, xmin:xmax+1], mask)[0] < .6:
            continue
        perimeter = cv2.arcLength(points, True)
        if perimeter <= 0:
            continue
        offset = pyclipper.PyclipperOffset()
        offset.AddPath(points, pyclipper.JT_ROUND, pyclipper.ET_CLOSEDPOLYGON)
        expanded = offset.Execute(cv2.contourArea(points) * 1.5 / perimeter)
        if not expanded:
            continue
        box, short_side = mini_box(np.concatenate(expanded).reshape(-1, 1, 2))
        if short_side < 5:
            continue
        box = np.rint(box * np.array([dest_w / width, dest_h / height]))
        boxes.append(np.clip(box, [0, 0], [dest_w, dest_h]).astype(np.float32))
    return boxes


def recognition_tensor(image):
    h, w = image.shape[:2]
    width = min(3200, max(320, int(48 * w / h)))
    resized_width = min(width, math.ceil(48 * w / h))
    values = cv2.resize(image, (resized_width, 48)).astype(np.float32).transpose(2, 0, 1)
    values = (values / 255 - .5) / .5
    padded = np.zeros((1, 3, 48, width), np.float32)
    padded[0, :, :, :resized_width] = values
    return padded


class EnglishDecoder:
    """Mask before argmax; keep original probabilities rather than renormalizing."""
    def __init__(self, characters):
        self.characters = ["blank"] + list(characters) + [" "]
        self.allowed = np.array([0] + [i for i, c in enumerate(self.characters[1:], 1)
            if c and all(32 <= ord(ch) <= 126 for ch in c)], dtype=np.int64)
        if not all(c in self.characters for c in "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789"):
            raise ValueError("English CTC vocabulary is unavailable")

    def __call__(self, prediction):
        prediction = np.asarray(prediction)
        if prediction.ndim != 3 or prediction.shape[0] != 1 or prediction.shape[2] != len(self.characters):
            raise ValueError("ONNX recognition vocabulary/shape mismatch")
        values = prediction[0][:, self.allowed]
        if not np.isfinite(values).all():
            raise ValueError("Invalid ONNX recognition output")
        chosen = values.argmax(axis=-1)
        indices = self.allowed[chosen]
        probabilities = values[np.arange(len(chosen)), chosen]
        keep = np.ones(len(indices), dtype=bool)
        keep[1:] = indices[1:] != indices[:-1]
        keep &= indices != 0
        text = "".join(self.characters[i] for i in indices[keep])
        score = float(probabilities[keep].mean()) if keep.any() else 0.
        return text, score


class OnnxModels:
    def __init__(self, mode=None, model_root=None, ort=None):
        if ort is None:
            import onnxruntime as ort
        self.ort = ort
        self.mode = (mode or os.environ.get("STARCHAT_OCR_DEVICE", "auto")).lower()
        if self.mode not in ("auto", "cpu", "gpu"):
            raise ValueError("STARCHAT_OCR_DEVICE must be auto, cpu or gpu")
        self.root = Path(model_root or ROOT / "models" / "onnx")
        with (self.root / "rec" / "inference.yml").open(encoding="utf-8") as stream:
            config = yaml.safe_load(stream)
        self.decoder = EnglishDecoder(config["PostProcess"]["character_dict"])
        self.sessions = {}
        self.device = "cpu"
        self.fallback_reason = ""
        wants_gpu = self.mode != "cpu" and "DmlExecutionProvider" in ort.get_available_providers()
        if self.mode == "gpu" and not wants_gpu:
            raise RuntimeError("DirectML execution provider unavailable")
        if wants_gpu:
            try:
                self._load(True)
                return
            except Exception as error:
                self.sessions.clear()
                if self.mode == "gpu":
                    raise
                self.fallback_reason = str(error)
                print("DirectML initialization failed; using CPU: " + str(error), file=sys.stderr)
        self._load(False)

    def _load(self, gpu):
        providers = [("DmlExecutionProvider", {"performance_preference": "high_performance", "device_filter": "gpu"}),
                     "CPUExecutionProvider"] if gpu else ["CPUExecutionProvider"]
        sessions = {}
        for kind in ("det", "rec"):
            session = self.ort.InferenceSession(str(self.root / kind / "inference.onnx"),
                sess_options=session_options(self.ort), providers=providers, enable_fallback=False)
            if gpu and "DmlExecutionProvider" not in session.get_providers():
                raise RuntimeError("DirectML model initialization fell back to CPU")
            # ORT may otherwise recreate the provider silently after a run failure.
            session.disable_fallback()
            sessions[kind] = session
        self.sessions = sessions
        self.device = "DirectML GPU" if gpu else "cpu"

    def run(self, kind, tensor):
        session = self.sessions[kind]
        try:
            return session.run(None, {session.get_inputs()[0].name: tensor})[0]
        except Exception as error:
            if self.mode != "auto" or self.device == "cpu":
                raise
            # A driver/device loss changes both models once, retaining the tracker.
            self.sessions.clear()
            self.fallback_reason = str(error)
            print("DirectML execution failed; using CPU: " + str(error), file=sys.stderr)
            self._load(False)
            session = self.sessions[kind]
            return session.run(None, {session.get_inputs()[0].name: tensor})[0]

    def detect(self, image):
        return detection_boxes(self.run("det", detection_tensor(image)), image.shape)

    def recognize(self, crops):
        return [self.decoder(self.run("rec", recognition_tensor(crop))) for crop in crops]
