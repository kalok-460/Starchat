# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 StarChat contributors
"""StarChat OCR 전용 프로세스: stdout JSONL, 진단은 stderr, 프레임은 메모리에만 보관."""
from __future__ import annotations
import argparse
import ctypes
import json
import math
import os
from pathlib import Path
import sys
import threading
import time
import traceback

ROOT = Path(__file__).resolve().parent
# Bound CPU helper work; inference uses the bundled ONNX Runtime DirectML provider.
for key, value in {"OMP_NUM_THREADS": "1", "MKL_NUM_THREADS": "2"}.items():
    os.environ[key] = value
from geometry import LayoutTracker
from ticks import TickClock, TickPipeline, CancelledTick

PROTOCOL = None
WRITE_LOCK = threading.Lock()


def redirect_output():
    """네이티브 라이브러리의 printf까지 stderr로 돌려 JSON 프로토콜을 보호한다."""
    global PROTOCOL
    sys.stdout.flush()
    PROTOCOL = os.fdopen(os.dup(sys.stdout.fileno()), "w", encoding="utf-8", buffering=1)
    os.dup2(sys.stderr.fileno(), sys.stdout.fileno())
    sys.stdout = sys.stderr
    if hasattr(sys.stdin, "reconfigure"):
        sys.stdin.reconfigure(encoding="utf-8")


def emit(kind, **values):
    with WRITE_LOCK:
        PROTOCOL.write(json.dumps({"type": kind, **values}, ensure_ascii=False, allow_nan=False) + "\n")
        PROTOCOL.flush()


def preprocess(image):
    """Lab 명도에만 CLAHE를 적용하고 색을 유지한 BGR 3채널을 반환한다."""
    import cv2
    lab = cv2.cvtColor(image[:, :, :3], cv2.COLOR_BGR2LAB)
    lab[:, :, 0] = cv2.createCLAHE(clipLimit=2.0, tileGridSize=(8, 8)).apply(lab[:, :, 0])
    return cv2.cvtColor(lab, cv2.COLOR_LAB2BGR)


def crop_roi(frame, roi):
    height, width = frame.shape[:2]
    x = max(0, min(width - 1, round(roi["x"] * width)))
    y = max(0, min(height - 1, round(roi["y"] * height)))
    right = max(x + 1, min(width, round((roi["x"] + roi["w"]) * width)))
    bottom = max(y + 1, min(height, round((roi["y"] + roi["h"]) * height)))
    return frame[y:bottom, x:right].copy()


def parse_result(result, y_offset=0):
    """인식 결과의 다각형을 ROI 기준 경계상자로 변환한다."""
    result = result.get("res", result)
    lines = []
    polys = result.get("rec_polys", result.get("dt_polys", []))
    for text, score, poly in zip(result.get("rec_texts", []), result.get("rec_scores", []), polys):
        text = "".join(c for c in str(text) if 32 <= ord(c) <= 126).strip()
        confidence = float(score)
        if not text or not math.isfinite(confidence):
            continue
        xs = [float(p[0]) for p in poly]
        ys = [float(p[1]) for p in poly]
        if not all(math.isfinite(v) for v in xs + ys):
            continue
        left, top = max(0, math.floor(min(xs))), max(0, math.floor(min(ys)))
        lines.append({"text": text, "confidence": confidence, "x": left,
                      "y": top + y_offset, "w": max(1, math.ceil(max(xs)) - left),
                      "h": max(1, math.ceil(max(ys)) - top)})
    return sorted(lines, key=lambda item: (item["y"], item["x"]))


class Engine:
    def __init__(self):
        from onnx_engine import OnnxModels
        import cv2
        cv2.setNumThreads(1)
        self.model = OnnxModels()

    @property
    def device(self):
        return self.model.device

    def detect(self, image):
        """Detect boxes only; the recognition session is not executed here."""
        return self.model.detect(image)

    def recognize(self, image, rows):
        """검출을 반복하지 않고 필요한 행만 기울기를 펴서 영어 인식기에 보낸다."""
        import cv2
        import numpy as np
        if not rows:
            return []
        crops=[]
        for row in rows:
            q=row["quad"].astype(np.float32)
            w=max(4,min(8192,round(float(np.linalg.norm(q[1]-q[0])))))
            h=max(4,min(1024,round(float(np.linalg.norm(q[3]-q[0])))))
            target=np.array([[0,0],[w-1,0],[w-1,h-1],[0,h-1]],dtype=np.float32)
            crops.append(cv2.warpPerspective(image,cv2.getPerspectiveTransform(q,target),(w,h),
                                             borderMode=cv2.BORDER_REPLICATE))
        return self.model.recognize(crops)

    def read(self, image):
        image=preprocess(image)
        layout=LayoutTracker()
        layout.update(self.detect(image),(image.shape[1],image.shape[0]),time.monotonic())
        rows=layout.to_read(time.monotonic(),True)
        for row,(text,score) in zip(rows,self.recognize(image,rows)):
            layout.recognize(row,text,score,time.monotonic())
        return layout.lines()


def normalize_config(message, old=None):
    config = dict(old or {"windowTitle": "Star Citizen", "windowHwnd": 0,
                         "roi": {"x": 0.01, "y": 0.05, "w": 0.36, "h": 0.35},
                         "intervalMs": 200})
    for key in ("windowTitle", "windowHwnd", "intervalMs", "roi"):
        if key in message:
            config[key] = message[key]
    config["windowTitle"] = str(config["windowTitle"]).strip()
    config["windowHwnd"] = max(0, int(config["windowHwnd"]))
    if not config["windowTitle"] and not config["windowHwnd"]:
        raise ValueError("게임 창 제목 또는 HWND가 필요합니다. 데스크톱 캡처는 허용되지 않습니다.")
    config["intervalMs"] = max(50, min(10000, int(config["intervalMs"])))
    config.pop("fullIntervalMs", None)  # Legacy settings do not create a second clock.
    roi = {key: float(config["roi"][key]) for key in ("x", "y", "w", "h")}
    if not all(math.isfinite(value) for value in roi.values()):
        raise ValueError("ROI에는 유한한 숫자가 필요합니다.")
    roi["x"], roi["y"] = max(0.0, min(.999, roi["x"])), max(0.0, min(.999, roi["y"]))
    roi["w"] = max(.001, min(1 - roi["x"], roi["w"]))
    roi["h"] = max(.001, min(1 - roi["y"], roi["h"]))
    config["roi"] = roi
    return config


def resolve_hwnd(config):
    """제목 부분일치나 화면 캡처로 대체하지 않고 명시한 게임 창만 선택한다."""
    user32 = ctypes.windll.user32
    user32.IsWindow.argtypes = [ctypes.c_void_p]
    user32.FindWindowW.argtypes = [ctypes.c_wchar_p, ctypes.c_wchar_p]
    user32.FindWindowW.restype = ctypes.c_void_p
    hwnd = config["windowHwnd"] or user32.FindWindowW(None, config["windowTitle"])
    if not hwnd or not user32.IsWindow(hwnd):
        raise RuntimeError("게임 창을 찾을 수 없습니다. 실행 후 다시 시도하세요.")
    return int(hwnd)


class Worker:
    def __init__(self):
        self.lock = threading.Lock()
        self.stop = threading.Event()
        self.wake = threading.Event()
        self.config = None
        self.revision = 0
        self.force = True
        self.latest = None
        self.capture = None
        self.control = None
        self.closed = False
        self.last_copy = 0.0
        self.capture_generation = 0
        self.sequence = 0

    def commands(self):
        try:
            for line in sys.stdin:
                try:
                    command = json.loads(line)
                    kind = command.get("type")
                    if kind == "stop":
                        break
                    if kind == "refresh":
                        with self.lock:
                            self.force = True
                        self.wake.set()
                    elif kind == "config":
                        with self.lock:
                            updated = normalize_config(command, self.config)
                            if updated != self.config:
                                self.config = updated
                                self.revision += 1
                                self.force = True
                        self.wake.set()
                    else:
                        emit("status", message="알 수 없는 OCR 명령: " + str(kind))
                except (ValueError, TypeError, KeyError) as error:
                    emit("status", message="OCR 설정 오류: " + str(error))
        finally:
            self.stop.set()
            self.wake.set()

    def end_capture(self):
        with self.lock:
            self.capture_generation += 1
            self.latest = None
        control, self.control = self.control, None
        if control is not None:
            try:
                control.stop()
            except Exception:
                traceback.print_exc(file=sys.stderr)
        self.capture = None
        with self.lock:
            self.latest = None

    def begin_capture(self, config):
        from windows_capture import WindowsCapture
        self.end_capture()
        hwnd = resolve_hwnd(config)
        self.closed = False
        self.last_copy = 0
        generation = self.capture_generation
        capture = WindowsCapture(cursor_capture=False, draw_border=False,
                                 monitor_index=None, window_hwnd=hwnd,
                                 minimum_update_interval=50)  # Fresh frames; OCR still has only its one tick clock.

        @capture.event
        def on_frame_arrived(frame, capture_control):
            if self.stop.is_set() or generation != self.capture_generation:
                capture_control.stop()
                return
            now = time.monotonic()
            # 한 장만 유지하고 OCR보다 빠른 프레임 복사를 제한한다.
            if now - self.last_copy < min(.1, self.config["intervalMs"] / 1000):
                return
            with self.lock:
                if generation != self.capture_generation:
                    return
                roi = dict(self.config["roi"])
                self.latest = crop_roi(frame.frame_buffer, roi)
                self.last_copy = now
            self.wake.set()

        @capture.event
        def on_closed():
            with self.lock:
                if generation != self.capture_generation:
                    return
                self.closed = True
                self.latest = None
            self.wake.set()

        self.capture = capture
        self.control = capture.start_free_threaded()
        emit("status", message="게임 창 전용 WGC 캡처 연결됨")

    def run(self):
        emit("status", message="PP-OCRv6 small 모델을 준비합니다.")
        engine = Engine()
        reported_device = engine.device
        pipeline = TickPipeline(engine)
        clock = None
        import windows_capture  # stdin 읽기 스레드 전에 라이브러리 초기화를 마친다.
        threading.Thread(target=self.commands, daemon=True, name="ocr-commands").start()
        emit("ready", detector="PP-OCRv6_small_det", recognizer="PP-OCRv6_small_rec", mode="geometry", language="en", device=engine.device)
        active_revision = -1
        layout = pipeline.layout
        last_problem = ""
        next_capture_attempt = 0.0
        frame_generation = -1
        width = height = 0

        def publish(recognized, detect_ms=0, frame_age_ms=0, **metrics):
            with self.lock:
                current = (self.revision == active_revision and frame_generation == self.capture_generation
                           and not self.closed and not self.stop.is_set())
            if current and self.control is not None and not self.control.is_finished():
                try:
                    resolve_hwnd(config)
                except RuntimeError:
                    self.closed = True
                    return
                self.sequence += 1
                emit("lines", full=True, recognized=recognized, width=width, height=height,
                     lines=layout.lines(), revision=active_revision, sequence=self.sequence,
                     detectMs=round(detect_ms, 1), frameAgeMs=round(frame_age_ms, 1),
                     skippedTicks=clock.skipped if clock else 0, **metrics)
        try:
            while not self.stop.is_set():
                self.wake.wait(.1 if clock is None or clock.next_tick <= time.monotonic() else min(.1, clock.next_tick-time.monotonic()))
                self.wake.clear()
                now = time.monotonic()
                with self.lock:
                    config = self.config
                    revision = self.revision
                    force = self.force
                if config is None:
                    continue
                if revision != active_revision:
                    self.end_capture()
                    next_capture_attempt = 0
                    active_revision = revision
                    pipeline = TickPipeline(engine)
                    layout = pipeline.layout
                    clock = TickClock(config["intervalMs"]/1000, now)
                if self.closed or (self.control and self.control.is_finished()):
                    self.end_capture()
                    self.closed = False
                    next_capture_attempt = now + 2
                    emit("status", message="게임 창 연결이 종료되어 재연결을 기다립니다.")
                if self.control is None:
                    if now < next_capture_attempt:
                        continue
                    try:
                        self.begin_capture(config)
                        pipeline = TickPipeline(engine)
                        layout = pipeline.layout
                        clock = TickClock(config["intervalMs"]/1000, now)
                        last_problem = ""
                    except Exception as error:
                        problem = str(error)
                        if problem != last_problem:
                            emit("status", message="WGC 연결 대기: " + problem)
                            last_problem = problem
                        next_capture_attempt = now + 3
                        continue
                with self.lock:
                    frame = self.latest
                    frame_generation = self.capture_generation
                    captured_at = self.last_copy
                if frame is None or not clock.begin(now):
                    continue
                with self.lock:
                    force = self.force
                    self.force = False
                height, width = frame.shape[:2]
                def cancelled():
                    return (self.stop.is_set() or self.closed or self.revision != active_revision
                            or frame_generation != self.capture_generation)
                def geometry_ready():
                    completed = time.monotonic()
                    publish(False, (completed-now)*1000, (completed-captured_at)*1000,
                            idleTicks=pipeline.idle_ticks, partialRuns=pipeline.partial_runs,
                            fullReview=False, recognizedRows=0)
                try:
                    image = preprocess(frame)
                    result = pipeline.process(image, now, force, geometry_ready, cancelled)
                    clock.finish(time.monotonic())
                    if engine.device != reported_device:
                        reported_device = engine.device
                        emit("status", message="DirectML GPU를 사용할 수 없어 CPU OCR로 전환했습니다.")
                    if result["recognized"] or result["fullReview"]:
                        publish(**result)
                    last_problem = ""
                except CancelledTick:
                    pass
                except Exception as error:
                    problem = str(error)
                    if problem != last_problem:
                        emit("status", message="OCR 처리 오류: " + problem)
                        traceback.print_exc(file=sys.stderr)
                        last_problem = problem
                finally:
                    if clock.busy:
                        clock.finish(time.monotonic())
        finally:
            self.end_capture()


def self_test():
    import numpy as np
    image = np.tile(np.array([[[30, 40, 120], [30, 120, 40], [120, 30, 40]]], dtype=np.uint8), (64, 32, 1))
    processed = preprocess(image)
    assert processed.shape == image.shape and processed.dtype == np.uint8
    assert processed[32, :3].argmax(-1).tolist() == [2, 1, 0], "문자 색상을 유지해야 합니다."
    assert len(set(processed.ravel().tolist())) > 3 and not np.array_equal(processed, image), "CLAHE를 적용하되 이진화하지 않습니다."
    frame = np.zeros((100, 200, 4), dtype=np.uint8)
    assert crop_roi(frame, {"x": .2, "y": .1, "w": .5, "h": .3}).shape == (30, 100, 4)
    result = {"rec_texts": ["[Global] Pilot: Hello"], "rec_scores": [.95],
              "rec_polys": [[[3, 4], [90, 3], [91, 17], [4, 18]]]}
    lines = parse_result(result)
    assert lines[0]["x"] == 3 and lines[0]["y"] == 3 and lines[0]["h"] == 15
    from onnx_engine import EnglishDecoder
    english = EnglishDecoder(list("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789[]:!?") + ["中", "글"])
    values = np.zeros((1, 3, len(english.characters)), dtype=np.float32)
    values[0, 0, english.characters.index("中")] = .95
    values[0, 0, english.characters.index("A")] = .4
    values[0, 1, 0] = .9
    values[0, 2, english.characters.index(":")] = .8
    text, score = english(values)
    assert text == "A:" and abs(score - .6) < .0001
    assert values[0, 0, english.characters.index("中")] > .9, "Do not modify predictor-owned buffers"
    assert parse_result({"rec_texts": ["中글"], "rec_scores": [.99], "rec_polys": result["rec_polys"]}) == []
    assert normalize_config({"roi": {"x": .9, "y": .9, "w": .5, "h": .5}})["roi"]["w"] < .11
    assert normalize_config({})["intervalMs"] == 200
    assert normalize_config({"intervalMs": 1})["intervalMs"] == 50
    assert normalize_config({"intervalMs": 50})["intervalMs"] == 50
    try:
        normalize_config({"roi": {"x": float("nan"), "y": 0, "w": 1, "h": 1}})
        raise AssertionError("NaN ROI accepted")
    except ValueError:
        pass
    # 같은 설정을 반복 수신해도 캡처 재시작을 유발하는 revision은 한 번만 바뀐다.
    import io
    saved_input = sys.stdin
    try:
        sys.stdin = io.StringIO((json.dumps({"type": "config", "windowTitle": "Synthetic fixture"}) + "\n") * 1000 + '{"type":"stop"}\n')
        worker = Worker()
        worker.commands()
        assert worker.revision == 1
        assert worker.stop.is_set()
        generation = worker.capture_generation
        worker.end_capture()
        assert worker.capture_generation == generation + 1 and worker.latest is None
    finally:
        sys.stdin = saved_input
    emit("self-test", passed=True, checks=16)


def check():
    import cv2
    import numpy as np
    import windows_capture
    engine = Engine()
    image = np.full((120, 900, 3), 255, dtype=np.uint8)
    cv2.putText(image, "[Global] Pilot: Hello Star Citizen", (15, 70), cv2.FONT_HERSHEY_SIMPLEX, 1.2, (0, 0, 0), 2, cv2.LINE_AA)
    start = time.monotonic()
    lines = engine.read(image)
    if not any("Hello" in item["text"] for item in lines):
        raise RuntimeError("Synthetic OCR inference failed: " + repr(lines))
    assert all(all(32 <= ord(c) <= 126 for c in item["text"]) for item in lines)
    emit("check", passed=True, model="PP-OCRv6_small", device=engine.device, language="en", characterSet="ASCII", elapsedMs=round((time.monotonic()-start)*1000), lines=lines)


if __name__ == "__main__":
    redirect_output()
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    try:
        if args.self_test:
            self_test()
        elif args.check:
            check()
        else:
            Worker().run()
    except Exception as error:
        emit("status", message="OCR 시작 실패: " + str(error), fatal=True)
        traceback.print_exc(file=sys.stderr)
        sys.exit(1)

