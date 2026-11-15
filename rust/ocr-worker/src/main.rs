// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 StarChat contributors
//! StarChat OCR worker process: JSON Lines on stdout, diagnostics on stderr,
//! frames kept in memory only. Protocol-compatible with the Python worker,
//! plus `motion` messages that follow HUD sway between OCR ticks.
mod det;
mod engine;
mod geometry;
mod image;
mod motion;
mod rec;
mod text;
mod ticks;

use std::io::{BufRead, Write};
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Condvar, Mutex, MutexGuard};
use std::time::{Duration, Instant};

use serde_json::{json, Value};
use windows_capture::capture::{CaptureControl, Context, GraphicsCaptureApiHandler};
use windows_capture::frame::Frame;
use windows_capture::graphics_capture_api::InternalCaptureControl;
use windows_capture::settings::{ColorFormat, CursorCaptureSettings, DirtyRegionSettings, DrawBorderSettings,
                                MinimumUpdateIntervalSettings, SecondaryWindowSettings, Settings};
use windows_capture::window::Window;

use crate::det::Quad;
use crate::engine::Models;
use crate::image::Img;
use crate::motion::MotionTracker;
use crate::ticks::{Reader, TickClock, TickError, TickPipeline};

#[link(name = "user32")]
extern "system" {
    fn IsWindow(hwnd: *mut std::ffi::c_void) -> i32;
    fn FindWindowW(class: *const u16, title: *const u16) -> *mut std::ffi::c_void;
}
#[link(name = "kernel32")]
extern "system" {
    fn LoadLibraryW(name: *const u16) -> *mut std::ffi::c_void;
}

fn wide(text: &str) -> Vec<u16> {
    text.encode_utf16().chain(std::iter::once(0)).collect()
}

fn clock() -> f64 {
    static START: std::sync::OnceLock<Instant> = std::sync::OnceLock::new();
    START.get_or_init(Instant::now).elapsed().as_secs_f64()
}

fn emit(kind: &str, mut values: Value) {
    static WRITE: Mutex<()> = Mutex::new(());
    values["type"] = json!(kind);
    let line = values.to_string();
    let _guard = WRITE.lock().unwrap_or_else(|e| e.into_inner());
    let mut out = std::io::stdout().lock();
    let _ = out.write_all(line.as_bytes()).and_then(|_| out.write_all(b"\n")).and_then(|_| out.flush());
}

fn status(message: impl Into<String>) {
    emit("status", json!({ "message": message.into() }));
}

#[derive(Clone, PartialEq)]
struct Config {
    title: String,
    hwnd: u64,
    roi: [f64; 4], // x, y, w, h as fractions of the window
    interval_ms: u64,
}

impl Config {
    fn default() -> Self {
        Config { title: "Star Citizen".into(), hwnd: 0, roi: [0.01, 0.05, 0.36, 0.35], interval_ms: 200 }
    }
}

fn normalize_config(message: &Value, old: Option<&Config>) -> Result<Config, String> {
    let mut config = old.cloned().unwrap_or_else(Config::default);
    if let Some(v) = message.get("windowTitle") {
        config.title = v.as_str().map(str::to_string).unwrap_or_else(|| v.to_string());
    }
    if let Some(v) = message.get("windowHwnd") {
        config.hwnd = v.as_f64().ok_or("windowHwnd")?.max(0.0) as u64;
    }
    if let Some(v) = message.get("intervalMs") {
        config.interval_ms = v.as_f64().ok_or("intervalMs")? as i64 as u64;
    }
    if let Some(v) = message.get("roi") {
        for (slot, key) in config.roi.iter_mut().zip(["x", "y", "w", "h"]) {
            *slot = v.get(key).and_then(Value::as_f64).ok_or_else(|| format!("'{key}'"))?;
        }
    }
    config.title = config.title.trim().to_string();
    if config.title.is_empty() && config.hwnd == 0 {
        return Err("게임 창 제목 또는 HWND가 필요합니다. 데스크톱 캡처는 허용되지 않습니다.".into());
    }
    config.interval_ms = (config.interval_ms as i64).clamp(50, 10000) as u64;
    if !config.roi.iter().all(|v| v.is_finite()) {
        return Err("ROI에는 유한한 숫자가 필요합니다.".into());
    }
    config.roi[0] = config.roi[0].clamp(0.0, 0.999);
    config.roi[1] = config.roi[1].clamp(0.0, 0.999);
    config.roi[2] = config.roi[2].clamp(0.001, (1.0 - config.roi[0]).max(0.001));
    config.roi[3] = config.roi[3].clamp(0.001, (1.0 - config.roi[1]).max(0.001));
    Ok(config)
}

/// ROI fractions -> pixel rectangle (x, y, right, bottom) inside the frame.
fn crop_rect(width: u32, height: u32, roi: &[f64; 4]) -> (u32, u32, u32, u32) {
    let (w, h) = (width as f64, height as f64);
    let x = image::round_even(roi[0] * w).clamp(0.0, w - 1.0) as u32;
    let y = image::round_even(roi[1] * h).clamp(0.0, h - 1.0) as u32;
    let right = (image::round_even((roi[0] + roi[2]) * w).min(w) as u32).max(x + 1);
    let bottom = (image::round_even((roi[1] + roi[3]) * h).min(h) as u32).max(y + 1);
    (x, y, right, bottom)
}

/// Only the explicitly named game window; never a partial title or the desktop.
fn resolve_hwnd(config: &Config) -> Result<*mut std::ffi::c_void, String> {
    let hwnd = if config.hwnd != 0 {
        config.hwnd as usize as *mut std::ffi::c_void
    } else {
        unsafe { FindWindowW(std::ptr::null(), wide(&config.title).as_ptr()) }
    };
    if hwnd.is_null() || unsafe { IsWindow(hwnd) } == 0 {
        return Err("게임 창을 찾을 수 없습니다. 실행 후 다시 시도하세요.".into());
    }
    Ok(hwnd)
}

struct CapturedFrame {
    id: u64,
    at: f64,
    bgr: Img,
    gray: Img,
}

struct State {
    config: Option<Config>,
    revision: u64,
    force: bool,
    latest: Option<Arc<CapturedFrame>>,
    closed: bool,
    capture_generation: u64,
    last_copy: f64,
    frames: u64,
    motion: MotionTracker,
    last_motion: (u64, f64, f64),
    urgent: bool,
    /// Track keys the host asked to read again (manual re-translate).
    reread: Vec<String>,
    signalled: bool,
}

struct Shared {
    state: Mutex<State>,
    wake: Condvar,
    stop: AtomicBool,
}

impl Shared {
    fn lock(&self) -> MutexGuard<'_, State> {
        self.state.lock().unwrap_or_else(|e| e.into_inner())
    }

    fn notify(&self) {
        self.lock().signalled = true;
        self.wake.notify_all();
    }
}

struct Handler {
    shared: Arc<Shared>,
    generation: u64,
    track_interval: f64,
}

type HandlerError = Box<dyn std::error::Error + Send + Sync>;

impl GraphicsCaptureApiHandler for Handler {
    type Flags = (Arc<Shared>, u64, f64);
    type Error = HandlerError;

    fn new(context: Context<Self::Flags>) -> Result<Self, Self::Error> {
        let (shared, generation, track_interval) = context.flags;
        Ok(Handler { shared, generation, track_interval })
    }

    fn on_frame_arrived(&mut self, frame: &mut Frame, control: InternalCaptureControl) -> Result<(), Self::Error> {
        if self.shared.stop.load(Ordering::Relaxed) {
            control.stop();
            return Ok(());
        }
        let now = clock();
        let roi = {
            let state = self.shared.lock();
            if state.capture_generation != self.generation {
                drop(state);
                control.stop();
                return Ok(());
            }
            // Keep one frame only; never copy faster than the follower needs.
            if now - state.last_copy < self.track_interval {
                return Ok(());
            }
            match &state.config {
                Some(config) => config.roi,
                None => return Ok(()),
            }
        };
        // Only the chat ROI leaves the GPU, not the whole game frame.
        let (x, y, right, bottom) = crop_rect(frame.width(), frame.height(), &roi);
        let mut buffer = frame.buffer_crop(x, y, right, bottom)?;
        let (w, h, pitch) = (buffer.width() as usize, buffer.height() as usize, buffer.row_pitch() as usize);
        let bgr = image::bgra_to_bgr(buffer.as_raw_buffer(), pitch, w, h);
        let gray = image::gray_chat(&bgr);
        let mut message = None;
        {
            let mut state = self.shared.lock();
            if state.capture_generation != self.generation {
                return Ok(());
            }
            state.frames += 1;
            let id = state.frames;
            if let Some(offset) = state.motion.track(&gray, id) {
                let last = state.last_motion;
                if last.0 != offset.base || (last.1 - offset.dx).abs() >= 0.2 || (last.2 - offset.dy).abs() >= 0.2 {
                    state.last_motion = (offset.base, offset.dx, offset.dy);
                    message = Some(json!({ "base": offset.base, "dx": round1(offset.dx), "dy": round1(offset.dy) }));
                }
            } else if state.motion.lost {
                state.urgent = true; // Re-detect now instead of waiting for the tick.
            }
            state.latest = Some(Arc::new(CapturedFrame { id, at: now, bgr, gray }));
            state.last_copy = now;
            state.signalled = true;
        }
        self.shared.wake.notify_all();
        if let Some(message) = message {
            emit("motion", message);
        }
        Ok(())
    }

    fn on_closed(&mut self) -> Result<(), Self::Error> {
        {
            let mut state = self.shared.lock();
            if state.capture_generation != self.generation {
                return Ok(());
            }
            state.closed = true;
            state.latest = None;
            state.signalled = true;
        }
        self.shared.wake.notify_all();
        Ok(())
    }
}

fn round1(v: f64) -> f64 {
    (v * 10.0).round() / 10.0
}

impl Reader for Models {
    fn detect(&mut self, image: &Img) -> Result<Vec<Quad>, String> {
        Models::detect(self, image)
    }
    fn recognize(&mut self, image: &Img, quads: &[Quad]) -> Result<Vec<(String, f64)>, String> {
        Models::recognize(self, image, quads)
    }
}

fn exe_dir() -> PathBuf {
    std::env::current_exe().ok().and_then(|p| p.parent().map(|p| p.to_path_buf())).unwrap_or_else(|| PathBuf::from("."))
}

fn load_models() -> Result<Models, String> {
    let dir = exe_dir();
    let candidates = [
        std::env::var_os("STARCHAT_ORT_DIR").map(PathBuf::from),
        Some(dir.clone()),
        Some(dir.join("runtime").join("Lib").join("site-packages").join("onnxruntime").join("capi")),
    ];
    let library = candidates.into_iter().flatten().find(|d| d.join("onnxruntime.dll").is_file())
        .ok_or("onnxruntime.dll을 찾을 수 없습니다.")?;
    // Windows ships an older DirectML in System32; bind the bundled one first.
    let directml = library.join("DirectML.dll");
    if directml.is_file() {
        unsafe { LoadLibraryW(wide(&directml.to_string_lossy()).as_ptr()) };
    }
    let environment = ort::init_from(library.join("onnxruntime.dll")).map_err(|e| e.to_string())?
        .with_name("starchat-ocr")
        .build()
        .map_err(|e| e.to_string())?;
    environment.set_log_level(ort::logging::LogLevel::Error);
    let root = std::env::var_os("STARCHAT_OCR_MODELS").map(PathBuf::from).unwrap_or_else(|| dir.join("models").join("onnx"));
    let mode = std::env::var("STARCHAT_OCR_DEVICE").unwrap_or_else(|_| "auto".into());
    Models::new(environment, root, &mode)
}

struct Worker {
    shared: Arc<Shared>,
    control: Option<CaptureControl<Handler, HandlerError>>,
    sequence: u64,
    track_interval: f64,
}

impl Worker {
    fn commands(shared: Arc<Shared>) {
        let mut input = std::io::stdin().lock();
        let mut bytes = Vec::new();
        loop {
            bytes.clear();
            match input.read_until(b'\n', &mut bytes) {
                Ok(0) | Err(_) => break,
                Ok(_) => {}
            }
            // The host's pipe encoding is not guaranteed: tolerate a BOM and non-UTF-8 bytes.
            let line = String::from_utf8_lossy(&bytes);
            let line = line.trim_start_matches('\u{feff}').trim();
            if line.is_empty() {
                continue;
            }
            let command: Value = match serde_json::from_str(line) {
                Ok(value) => value,
                Err(error) => {
                    status(format!("OCR 설정 오류: {error}"));
                    continue;
                }
            };
            match command.get("type").and_then(Value::as_str) {
                Some("stop") => break,
                Some("refresh") => {
                    shared.lock().force = true;
                    shared.notify();
                }
                Some("reread") => {
                    let keys: Vec<String> = command.get("keys").and_then(Value::as_array).into_iter().flatten()
                        .filter_map(|k| k.as_str().map(str::to_string)).take(64).collect();
                    shared.lock().reread.extend(keys);
                    shared.notify();
                }
                Some("config") => {
                    {
                        let mut state = shared.lock();
                        match normalize_config(&command, state.config.as_ref()) {
                            Ok(updated) => {
                                if state.config.as_ref() != Some(&updated) {
                                    state.config = Some(updated);
                                    state.revision += 1;
                                    state.force = true;
                                }
                            }
                            Err(error) => {
                                drop(state);
                                status(format!("OCR 설정 오류: {error}"));
                            }
                        }
                    }
                    shared.notify();
                }
                other => status(format!("알 수 없는 OCR 명령: {}", other.unwrap_or("None"))),
            }
        }
        shared.stop.store(true, Ordering::Relaxed);
        shared.notify();
    }

    fn end_capture(&mut self) {
        {
            let mut state = self.shared.lock();
            state.capture_generation += 1;
            state.latest = None;
            state.motion.clear();
        }
        if let Some(control) = self.control.take() {
            if let Err(error) = control.stop() {
                eprintln!("capture stop: {error}");
            }
        }
        self.shared.lock().latest = None;
    }

    fn begin_capture(&mut self, config: &Config) -> Result<(), String> {
        self.end_capture();
        let hwnd = resolve_hwnd(config)?;
        let generation = {
            let mut state = self.shared.lock();
            state.closed = false;
            state.last_copy = 0.0;
            state.capture_generation
        };
        let settings = Settings::new(
            Window::from_raw_hwnd(hwnd),
            CursorCaptureSettings::WithoutCursor,
            DrawBorderSettings::WithoutBorder,
            SecondaryWindowSettings::Default,
            MinimumUpdateIntervalSettings::Custom(Duration::from_secs_f64(self.track_interval)),
            DirtyRegionSettings::Default,
            ColorFormat::Bgra8,
            (self.shared.clone(), generation, self.track_interval * 0.75),
        );
        self.control = Some(Handler::start_free_threaded(settings).map_err(|e| e.to_string())?);
        status("게임 창 전용 WGC 캡처 연결됨");
        Ok(())
    }

    fn run(&mut self) -> Result<(), String> {
        status("PP-OCRv6 small 모델을 준비합니다.");
        let mut engine = load_models()?;
        let mut reported_device = engine.device.clone();
        let mut pipeline = TickPipeline::new();
        let mut tick_clock: Option<TickClock> = None;
        {
            let shared = self.shared.clone();
            std::thread::Builder::new().name("ocr-commands".into()).spawn(move || Worker::commands(shared)).map_err(|e| e.to_string())?;
        }
        emit("ready", json!({ "detector": "PP-OCRv6_small_det", "recognizer": "PP-OCRv6_small_rec", "mode": "geometry",
                              "language": "en", "device": engine.device, "runtime": "rust", "motion": true, "reread": true }));
        let mut active_revision = u64::MAX;
        let mut last_problem = String::new();
        let mut next_capture_attempt = 0.0f64;
        let mut last_frame = 0u64;
        let mut last_tick = 0.0f64;
        while !self.shared.stop.load(Ordering::Relaxed) {
            {
                let now = clock();
                let timeout = match &tick_clock {
                    Some(c) if c.next_tick > now => (c.next_tick - now).min(0.1),
                    _ => 0.1,
                };
                let mut state = self.shared.lock();
                if !state.signalled {
                    state = self.shared.wake.wait_timeout(state, Duration::from_secs_f64(timeout)).unwrap_or_else(|e| e.into_inner()).0;
                }
                state.signalled = false;
            }
            let now = clock();
            let (config, revision) = {
                let state = self.shared.lock();
                (state.config.clone(), state.revision)
            };
            let Some(config) = config else { continue };
            if revision != active_revision {
                self.end_capture();
                next_capture_attempt = 0.0;
                active_revision = revision;
                pipeline = TickPipeline::new();
                tick_clock = Some(TickClock::new(config.interval_ms as f64 / 1000.0, now));
            }
            let finished = self.control.as_ref().map_or(false, |c| c.is_finished());
            let closed = self.shared.lock().closed;
            if closed || finished {
                self.end_capture();
                self.shared.lock().closed = false;
                next_capture_attempt = now + 2.0;
                status("게임 창 연결이 종료되어 재연결을 기다립니다.");
            }
            if self.control.is_none() {
                if now < next_capture_attempt {
                    continue;
                }
                match self.begin_capture(&config) {
                    Ok(()) => {
                        pipeline = TickPipeline::new();
                        tick_clock = Some(TickClock::new(config.interval_ms as f64 / 1000.0, now));
                        last_problem.clear();
                    }
                    Err(problem) => {
                        if problem != last_problem {
                            status(format!("WGC 연결 대기: {problem}"));
                            last_problem = problem;
                        }
                        next_capture_attempt = now + 3.0;
                        continue;
                    }
                }
            }
            let (frame, frame_generation, urgent, force, rereads) = {
                let mut state = self.shared.lock();
                (state.latest.clone(), state.capture_generation, std::mem::take(&mut state.urgent), state.force, std::mem::take(&mut state.reread))
            };
            // A manual re-read runs on the next tick even if the picture has not changed.
            let reread = !rereads.is_empty();
            if reread {
                pipeline.layout.request_reread(&rereads);
            }
            let Some(frame) = frame else { continue };
            let Some(tick) = tick_clock.as_mut() else { continue };
            // The same pixels cannot produce new geometry.
            if frame.id == last_frame && !force && !reread {
                continue;
            }
            if reread && !tick.busy {
                tick.next_tick = tick.next_tick.min(now);
            }
            if urgent && !tick.busy && now - last_tick >= (tick.interval / 2.0).max(0.05) && tick.next_tick > now {
                tick.next_tick = now; // The follower lost the block: look again right away.
            }
            if !tick.begin(now) {
                continue;
            }
            let force = std::mem::take(&mut self.shared.lock().force);
            last_frame = frame.id;
            last_tick = now;
            let (width, height) = (frame.bgr.w, frame.bgr.h);
            let shared = self.shared.clone();
            let stopped = || shared.stop.load(Ordering::Relaxed);
            let cancelled = || {
                let state = shared.lock();
                stopped() || state.closed || state.revision != active_revision || frame_generation != state.capture_generation
            };
            let control_alive = self.control.as_ref().map_or(false, |c| !c.is_finished());
            let skipped = tick.skipped;
            let sequence = &mut self.sequence;
            let mut publish = |layout: &geometry::LayoutTracker, recognized: bool, detect_ms: f64, frame_age_ms: f64, metrics: Value, reference: bool| {
                let current = {
                    let state = shared.lock();
                    state.revision == active_revision && frame_generation == state.capture_generation && !state.closed && !stopped()
                };
                if !current || !control_alive {
                    return;
                }
                if resolve_hwnd(&config).is_err() {
                    shared.lock().closed = true;
                    return;
                }
                *sequence += 1;
                let (dx, dy) = {
                    let mut state = shared.lock();
                    if reference {
                        let quads: Vec<Quad> = layout.rows.iter().filter(|r| r.present).map(|r| r.quad).collect();
                        state.motion.set_reference(&frame.gray, frame.id, &quads);
                    }
                    let offset = state.motion.rebase(*sequence);
                    state.last_motion = (*sequence, offset.0, offset.1);
                    offset
                };
                let mut message = json!({
                    "full": true, "recognized": recognized, "width": width, "height": height, "lines": layout.lines(),
                    "revision": active_revision, "sequence": *sequence, "detectMs": round1(detect_ms),
                    "frameAgeMs": round1(frame_age_ms), "skippedTicks": skipped, "dx": round1(dx), "dy": round1(dy),
                });
                for (key, value) in metrics.as_object().into_iter().flatten() {
                    message[key] = value.clone();
                }
                emit("lines", message);
            };
            let image = image::preprocess(&frame.bgr);
            let captured_at = frame.at;
            let result = {
                let mut geometry_ready = |p: &TickPipeline| {
                    let completed = clock();
                    publish(&p.layout, false, (completed - now) * 1000.0, (completed - captured_at) * 1000.0,
                            json!({ "idleTicks": p.idle_ticks, "partialRuns": p.partial_runs, "fullReview": false, "recognizedRows": 0 }), true);
                };
                pipeline.process(&mut engine, &image, now, force, &mut geometry_ready, &cancelled)
            };
            tick.finish(clock());
            match result {
                Ok(result) => {
                    if engine.device != reported_device {
                        reported_device = engine.device.clone();
                        status("DirectML GPU를 사용할 수 없어 CPU OCR로 전환했습니다.");
                    }
                    let reread_done = std::mem::take(&mut pipeline.layout.reread_done);
                    if result.recognized || result.full_review || !reread_done.is_empty() {
                        publish(&pipeline.layout, result.recognized, 0.0, 0.0,
                                json!({ "idleTicks": result.idle_ticks, "partialRuns": result.partial_runs, "reread": reread_done,
                                        "fullReview": result.full_review, "recognizedRows": result.recognized_rows }), false);
                    }
                    last_problem.clear();
                }
                Err(TickError::Cancelled) => {}
                Err(TickError::Failed(problem)) => {
                    if problem != last_problem {
                        status(format!("OCR 처리 오류: {problem}"));
                        eprintln!("{problem}");
                        last_problem = problem;
                    }
                }
            }
        }
        self.end_capture();
        Ok(())
    }
}

fn self_test() -> Result<(), String> {
    let check = |ok: bool, what: &str| if ok { Ok(()) } else { Err(what.to_string()) };
    // Colour CLAHE keeps hue order and is not a binarisation.
    let mut image = Img::new(96, 64, 3);
    for (i, p) in image.data.chunks_exact_mut(3).enumerate() {
        p.copy_from_slice(match i % 3 { 0 => &[30, 40, 120], 1 => &[30, 120, 40], _ => &[120, 30, 40] });
    }
    let processed = image::preprocess(&image);
    check(processed.w == 96 && processed.h == 64 && processed.c == 3, "preprocess shape")?;
    let argmax = |p: &[u8]| (0..3).max_by_key(|k| p[*k]).unwrap();
    let row = &processed.data[32 * 96 * 3..];
    check([argmax(&row[0..3]), argmax(&row[3..6]), argmax(&row[6..9])] == [2, 1, 0], "문자 색상을 유지해야 합니다.")?;
    let mut distinct = processed.data.clone();
    distinct.sort_unstable();
    distinct.dedup();
    check(distinct.len() > 3 && processed.data != image.data, "CLAHE를 적용하되 이진화하지 않습니다.")?;
    check(crop_rect(200, 100, &[0.2, 0.1, 0.5, 0.3]) == (40, 10, 140, 40), "crop")?;
    let mut dictionary: Vec<String> = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789[]:!?".chars().map(String::from).collect();
    dictionary.extend(["中".to_string(), "글".to_string()]);
    let english = rec::EnglishDecoder::new(dictionary)?;
    let classes = english.characters.len();
    let position = |c: &str| english.characters.iter().position(|x| x == c).unwrap();
    let mut values = vec![0f32; 3 * classes];
    values[position("中")] = 0.95;
    values[position("A")] = 0.4;
    values[classes] = 0.9;
    values[2 * classes + position(":")] = 0.8;
    let (text, score) = english.decode(&values, 3, classes)?;
    check(text == "A:" && (score - 0.6).abs() < 0.0001, "English decoder")?;
    check(text::ascii("中글").is_empty(), "ASCII filter")?;
    let roi = normalize_config(&json!({ "roi": { "x": 0.9, "y": 0.9, "w": 0.5, "h": 0.5 } }), None)?.roi;
    check(roi[2] < 0.11, "roi clamp")?;
    check(normalize_config(&json!({}), None)?.interval_ms == 200, "default interval")?;
    check(normalize_config(&json!({ "intervalMs": 1 }), None)?.interval_ms == 50, "minimum interval")?;
    check(normalize_config(&json!({ "roi": { "x": null, "y": 0, "w": 1, "h": 1 } }), None).is_err(), "NaN ROI accepted")?;
    let mut tick = TickClock::new(0.2, 0.0);
    check(tick.begin(0.0) && !tick.begin(0.25) && tick.skipped == 1, "busy tick skipped")?;
    tick.finish(0.95);
    check(!tick.busy && tick.next_tick > 0.95, "no replay of elapsed ticks")?;
    emit("self-test", json!({ "passed": true, "checks": 13, "runtime": "rust" }));
    Ok(())
}

#[link(name = "gdi32")]
extern "system" {
    fn CreateCompatibleDC(dc: *mut std::ffi::c_void) -> *mut std::ffi::c_void;
    fn CreateDIBSection(dc: *mut std::ffi::c_void, info: *const [u32; 10], usage: u32, bits: *mut *mut u8, section: *mut std::ffi::c_void, offset: u32) -> *mut std::ffi::c_void;
    fn SelectObject(dc: *mut std::ffi::c_void, object: *mut std::ffi::c_void) -> *mut std::ffi::c_void;
    fn DeleteObject(object: *mut std::ffi::c_void) -> i32;
    fn DeleteDC(dc: *mut std::ffi::c_void) -> i32;
    fn SetTextColor(dc: *mut std::ffi::c_void, color: u32) -> u32;
    fn SetBkMode(dc: *mut std::ffi::c_void, mode: i32) -> i32;
    fn CreateFontW(height: i32, width: i32, escapement: i32, orientation: i32, weight: i32, italic: u32, underline: u32, strike: u32,
                   charset: u32, precision: u32, clip: u32, quality: u32, pitch: u32, face: *const u16) -> *mut std::ffi::c_void;
    fn TextOutW(dc: *mut std::ffi::c_void, x: i32, y: i32, text: *const u16, length: i32) -> i32;
    fn GdiFlush() -> i32;
}

/// Black GDI text on white, as a BGR image (the installer's inference check).
fn render_text(text: &str, w: usize, h: usize) -> Result<Img, String> {
    unsafe {
        // BITMAPINFOHEADER: 32-bit top-down DIB.
        let header: [u32; 10] = [40, w as u32, (-(h as i32)) as u32, 1 | (32 << 16), 0, 0, 0, 0, 0, 0];
        let dc = CreateCompatibleDC(std::ptr::null_mut());
        let mut bits: *mut u8 = std::ptr::null_mut();
        let bitmap = CreateDIBSection(dc, &header, 0, &mut bits, std::ptr::null_mut(), 0);
        if dc.is_null() || bitmap.is_null() || bits.is_null() {
            return Err("GDI bitmap unavailable".into());
        }
        std::slice::from_raw_parts_mut(bits, w * h * 4).fill(255);
        let font = CreateFontW(44, 0, 0, 0, 400, 0, 0, 0, 0, 0, 0, 4, 0, wide("Arial").as_ptr());
        let old_bitmap = SelectObject(dc, bitmap);
        let old_font = SelectObject(dc, font);
        SetBkMode(dc, 1);
        SetTextColor(dc, 0);
        let characters = wide(text);
        TextOutW(dc, 15, (h as i32 - 44) / 2, characters.as_ptr(), characters.len() as i32 - 1);
        GdiFlush();
        let image = image::bgra_to_bgr(std::slice::from_raw_parts(bits, w * h * 4), w * 4, w, h);
        SelectObject(dc, old_font);
        SelectObject(dc, old_bitmap);
        DeleteObject(font);
        DeleteObject(bitmap);
        DeleteDC(dc);
        Ok(image)
    }
}

fn check() -> Result<(), String> {
    let mut engine = load_models()?;
    let image = render_text("[Global] Pilot: Hello Star Citizen", 900, 120)?;
    let start = Instant::now();
    let processed = image::preprocess(&image);
    let mut pipeline = TickPipeline::new();
    pipeline.process(&mut engine, &processed, 10.0, true, &mut |_| {}, &|| false).map_err(|e| match e {
        TickError::Failed(m) => m,
        TickError::Cancelled => "cancelled".into(),
    })?;
    let lines = pipeline.layout.lines();
    if !lines.iter().any(|l| l["text"].as_str().map_or(false, |t| t.contains("Hello"))) {
        return Err(format!("Synthetic OCR inference failed: {}", Value::Array(lines)));
    }
    emit("check", json!({ "passed": true, "model": "PP-OCRv6_small", "device": engine.device, "language": "en", "characterSet": "ASCII",
                          "runtime": "rust", "elapsedMs": start.elapsed().as_millis() as u64, "lines": lines }));
    Ok(())
}

/// Reads a raw BGR file through the full pipeline; used for parity checks and timing.
fn read_file(path: &str, w: usize, h: usize, repeat: usize) -> Result<(), String> {
    let data = std::fs::read(path).map_err(|e| e.to_string())?;
    if data.len() != w * h * 3 {
        return Err("raw BGR size mismatch".into());
    }
    let raw = Img { w, h, c: 3, data };
    let mut engine = load_models()?;
    let time = |f: &mut dyn FnMut() -> Result<(), String>, n: usize| -> Result<(f64, f64), String> {
        let mut samples = Vec::new();
        for _ in 0..n {
            let start = Instant::now();
            f()?;
            samples.push(start.elapsed().as_secs_f64() * 1000.0);
        }
        let worst = samples.iter().copied().fold(0.0, f64::max);
        Ok((round1(geometry::median(&mut samples)), round1(worst)))
    };
    let image = image::preprocess(&raw);
    let boxes = engine.detect(&image)?;
    let mut pipeline = TickPipeline::new();
    let never = || false;
    pipeline.process(&mut engine, &image, 10.0, true, &mut |_| {}, &never).map_err(|e| match e {
        TickError::Failed(m) => m,
        TickError::Cancelled => "cancelled".into(),
    })?;
    let lines = pipeline.layout.lines();
    let quads: Vec<Quad> = pipeline.layout.rows.iter().filter(|r| r.present).map(|r| r.quad).collect();
    let mut report = json!({ "device": engine.device, "boxes": boxes, "lines": lines, "rows": quads.len() });
    if repeat > 0 {
        report["preprocessMs"] = json!(time(&mut || { image::preprocess(&raw); Ok(()) }, repeat)?);
        report["detectMs"] = json!(time(&mut || engine.detect(&image).map(|_| ()), repeat)?);
        report["recognizeAllMs"] = json!(time(&mut || engine.recognize(&image, &quads).map(|_| ()), repeat.min(10))?);
        report["recognizeOneMs"] = json!(time(&mut || engine.recognize(&image, &quads[..quads.len().min(1)]).map(|_| ()), repeat)?);
        let mut now = 20.0;
        report["steadyTickMs"] = json!(time(&mut || {
            now += 0.2;
            pipeline.process(&mut engine, &image, now, false, &mut |_| {}, &never).map(|_| ()).map_err(|_| "tick".to_string())
        }, repeat)?);
        let gray = image::gray_chat(&raw);
        let mut tracker = MotionTracker::new();
        tracker.set_reference(&gray, 1, &quads);
        let mut id = 1;
        report["motionTrackMs"] = json!(time(&mut || { id += 1; tracker.track(&gray, id); Ok(()) }, repeat)?);
    }
    emit("read", report);
    Ok(())
}

/// Runs numbered raw BGR frames (frame_0000.bgr ...) through the tick pipeline
/// at 200 ms steps and reports the tracked lines of every tick.
fn replay(directory: &str, w: usize, h: usize, count: usize) -> Result<(), String> {
    let mut engine = load_models()?;
    let mut pipeline = TickPipeline::new();
    let mut ticks = Vec::new();
    for index in 0..count {
        let data = std::fs::read(std::path::Path::new(directory).join(format!("frame_{index:04}.bgr"))).map_err(|e| e.to_string())?;
        if data.len() != w * h * 3 {
            return Err("raw BGR size mismatch".into());
        }
        let image = image::preprocess(&Img { w, h, c: 3, data });
        pipeline.process(&mut engine, &image, 10.0 + index as f64 * 0.2, index == 0, &mut |_| {}, &|| false).map_err(|e| match e {
            TickError::Failed(m) => m,
            TickError::Cancelled => "cancelled".into(),
        })?;
        ticks.push(Value::Array(pipeline.layout.lines().iter().map(|l| json!([l["boxId"], l["version"], l["text"], l["y"]])).collect()));
    }
    emit("replay", json!({ "device": engine.device, "ticks": ticks }));
    Ok(())
}

fn main() {
    let arguments: Vec<String> = std::env::args().skip(1).collect();
    let result = match arguments.first().map(String::as_str) {
        Some("--self-test") => self_test(),
        Some("--check") => check(),
        Some("--replay") if arguments.len() >= 5 => replay(&arguments[1], arguments[2].parse().unwrap_or(0), arguments[3].parse().unwrap_or(0), arguments[4].parse().unwrap_or(0)),
        Some("--read") if arguments.len() >= 4 => read_file(&arguments[1], arguments[2].parse().unwrap_or(0), arguments[3].parse().unwrap_or(0),
                                                             arguments.get(4).and_then(|v| v.parse().ok()).unwrap_or(0)),
        Some(other) => Err(format!("unknown argument {other}")),
        None => {
            let track_ms: f64 = std::env::var("STARCHAT_OCR_TRACK_MS").ok().and_then(|v| v.parse().ok()).unwrap_or(16.0);
            let shared = Arc::new(Shared {
                state: Mutex::new(State {
                    config: None, revision: 0, force: true, latest: None, closed: false, capture_generation: 0, last_copy: 0.0,
                    frames: 0, motion: MotionTracker::new(), last_motion: (0, 0.0, 0.0), urgent: false, reread: Vec::new(), signalled: false,
                }),
                wake: Condvar::new(),
                stop: AtomicBool::new(false),
            });
            Worker { shared, control: None, sequence: 0, track_interval: track_ms.clamp(8.0, 200.0) / 1000.0 }.run()
        }
    };
    if let Err(error) = result {
        emit("status", json!({ "message": format!("OCR 시작 실패: {error}"), "fatal": true }));
        eprintln!("{error}");
        std::process::exit(1);
    }
    // Capture and command threads may still be parked in native calls.
    std::process::exit(0);
}
