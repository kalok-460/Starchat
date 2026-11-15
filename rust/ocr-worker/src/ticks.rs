// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 StarChat contributors
//! One OCR clock: serial detection/recognition, skipped busy ticks, count-based review.
use std::collections::HashMap;

use crate::det::Quad;
use crate::geometry::LayoutTracker;
use crate::image::Img;
use crate::text::{ascii, normalized_text};

pub struct TickClock {
    pub interval: f64,
    pub next_tick: f64,
    pub busy: bool,
    pub skipped: u64,
}

impl TickClock {
    pub fn new(interval: f64, now: f64) -> Self {
        TickClock { interval, next_tick: now, busy: false, skipped: 0 }
    }

    fn elapsed(&self, now: f64) -> u64 {
        (((now - self.next_tick + 1e-9) / self.interval).floor() as i64 + 1).max(0) as u64
    }

    pub fn begin(&mut self, now: f64) -> bool {
        let elapsed = self.elapsed(now);
        if elapsed == 0 {
            return false;
        }
        self.next_tick += elapsed as f64 * self.interval;
        self.skipped += if self.busy { elapsed } else { elapsed - 1 };
        if self.busy {
            return false;
        }
        self.busy = true;
        true
    }

    pub fn finish(&mut self, now: f64) {
        // Never replay elapsed deadlines after slow native inference.
        let elapsed = self.elapsed(now);
        self.next_tick += elapsed as f64 * self.interval;
        self.skipped += elapsed;
        self.busy = false;
    }
}

/// The inference backend seen by the pipeline (real models or a test double).
pub trait Reader {
    fn detect(&mut self, image: &Img) -> Result<Vec<Quad>, String>;
    fn recognize(&mut self, image: &Img, quads: &[Quad]) -> Result<Vec<(String, f64)>, String>;
}

pub enum TickError {
    Cancelled,
    Failed(String),
}

impl From<String> for TickError {
    fn from(error: String) -> Self {
        TickError::Failed(error)
    }
}

pub struct TickResult {
    pub recognized: bool,
    pub full_review: bool,
    pub recognized_rows: usize,
    pub idle_ticks: u32,
    pub partial_runs: u32,
}

pub struct TickPipeline {
    pub layout: LayoutTracker,
    pub idle_ticks: u32,
    pub partial_runs: u32,
    initial: bool,
}

impl TickPipeline {
    pub fn new() -> Self {
        TickPipeline { layout: LayoutTracker::new(), idle_ticks: 0, partial_runs: 0, initial: true }
    }

    fn read(&self, engine: &mut dyn Reader, image: &Img, uids: &[u64], cancelled: &dyn Fn() -> bool) -> Result<Vec<(String, f64)>, TickError> {
        let mut results = Vec::with_capacity(uids.len());
        // One captured image, sequential groups of <=2 rows.
        for group in uids.chunks(2) {
            if cancelled() {
                return Err(TickError::Cancelled);
            }
            let quads: Vec<Quad> = group.iter().filter_map(|uid| self.layout.index(*uid).map(|i| self.layout.rows[i].quad)).collect();
            let batch = engine.recognize(image, &quads)?;
            if batch.len() != group.len() {
                return Err(TickError::Failed("OCR returned an incomplete row batch".into()));
            }
            results.extend(batch);
        }
        if cancelled() {
            return Err(TickError::Cancelled);
        }
        Ok(results)
    }

    pub fn process(&mut self, engine: &mut dyn Reader, image: &Img, now: f64, force: bool,
                   geometry_ready: &mut dyn FnMut(&TickPipeline), cancelled: &dyn Fn() -> bool) -> Result<TickResult, TickError> {
        if cancelled() {
            return Err(TickError::Cancelled);
        }
        let size = (image.w, image.h);
        if self.layout.size != Some(size) {
            self.initial = true;
            self.idle_ticks = 0;
            self.partial_runs = 0;
        }
        let boxes = engine.detect(image)?;
        self.layout.update(&boxes, size, now, Some(image));
        if cancelled() {
            return Err(TickError::Cancelled);
        }
        let present: Vec<u64> = self.layout.rows.iter().filter(|r| r.present).map(|r| r.uid).collect();
        let changed = self.layout.rows.iter().any(|r| r.present && (r.text.is_empty() || r.dirty));
        self.idle_ticks = if changed { 0 } else { self.idle_ticks + 1 };
        geometry_ready(self); // Publish positions and counters before reading text.
        let mut full = force || self.initial || self.idle_ticks >= 10 || self.partial_runs >= 10;
        let candidates: Vec<u64> = if full {
            Vec::new()
        } else {
            self.layout.to_read(now, false).into_iter()
                .filter(|uid| self.layout.index(*uid).map_or(false, |i| self.layout.rows[i].text.is_empty() || self.layout.rows[i].dirty || self.layout.rows[i].reread))
                .collect()
        };
        let results = self.read(engine, image, &candidates, cancelled)?;
        if !candidates.is_empty() {
            self.partial_runs += 1;
        }
        // The tenth partial pass contributes its fresh results to the full pass.
        full = full || self.partial_runs >= 10;
        if full {
            let mut already: HashMap<u64, (String, f64)> = candidates.iter().copied().zip(results).collect();
            let remaining: Vec<u64> = present.iter().copied().filter(|uid| !already.contains_key(uid)).collect();
            let extra = self.read(engine, image, &remaining, cancelled)?;
            already.extend(remaining.iter().copied().zip(extra));
            let results: Vec<(String, f64)> = present.iter().map(|uid| already.remove(uid).unwrap_or_default()).collect();
            reconcile_full(&mut self.layout, &present, &results, now);
            self.idle_ticks = 0;
            self.partial_runs = 0;
            self.initial = false;
        } else {
            for (uid, (text, score)) in candidates.iter().zip(&results) {
                self.layout.recognize(*uid, text, *score, now);
            }
        }
        let rows = if full { present.len() } else { candidates.len() };
        Ok(TickResult { recognized: rows > 0, full_review: full, recognized_rows: rows, idle_ticks: self.idle_ticks, partial_runs: self.partial_runs })
    }
}

/// Move only unique exact source identities; preserve current detected geometry.
///
/// Ambiguous/fuzzy readings retain the existing confirmation safeguards. This is
/// an atomic whole-frame correction, not a per-row guessed rotation/relocation.
pub fn reconcile_full(layout: &mut LayoutTracker, rows: &[u64], results: &[(String, f64)], now: f64) {
    let key = |text: &str| text.to_lowercase().split_whitespace().collect::<Vec<_>>().join(" ");
    struct Source { id: u64, version: u32, text: String, confidence: f64, last_read: f64, dirty: bool, pending: String, pending_count: u32 }
    let known: Vec<Source> = layout.rows.iter().filter(|r| !r.text.is_empty()).map(|r| Source {
        id: r.id, version: r.version, text: r.text.clone(), confidence: r.confidence, last_read: r.last_read,
        dirty: r.dirty, pending: r.pending.clone(), pending_count: r.pending_count,
    }).collect();
    let mut counts: HashMap<String, usize> = HashMap::new();
    for source in &known {
        *counts.entry(key(&source.text)).or_default() += 1;
    }
    let sources: HashMap<String, &Source> = known.iter().filter(|s| counts[&key(&s.text)] == 1).map(|s| (key(&s.text), s)).collect();
    let valid: Vec<String> = results.iter().map(|(text, score)| {
        if score.is_finite() && *score >= 0.60 { normalized_text(&ascii(text)) } else { String::new() }
    }).collect();
    let mut found: HashMap<String, usize> = HashMap::new();
    for text in valid.iter().filter(|t| !t.is_empty()) {
        *found.entry(key(text)).or_default() += 1;
    }
    let matches: HashMap<u64, &Source> = rows.iter().zip(&valid)
        .filter(|(_, text)| !text.is_empty() && found[&key(text)] == 1)
        .filter_map(|(uid, text)| sources.get(&key(text)).map(|s| (*uid, *s)))
        .collect();
    let used: Vec<u64> = matches.values().map(|s| s.id).collect();
    for uid in rows {
        let Some(index) = layout.index(*uid) else { continue };
        if let Some(source) = matches.get(uid) {
            let row = &mut layout.rows[index];
            row.id = source.id;
            row.version = source.version;
            row.text = source.text.clone();
            row.confidence = source.confidence;
            row.last_read = source.last_read;
            row.dirty = source.dirty;
            row.pending = source.pending.clone();
            row.pending_count = source.pending_count;
        } else if used.contains(&layout.rows[index].id) {
            layout.next_id += 1;
            let id = layout.next_id;
            let row = &mut layout.rows[index];
            row.id = id;
            row.version = 0;
            row.text.clear();
            row.confidence = 0.0;
            row.last_read = 0.0;
            row.dirty = false;
            row.pending.clear();
            row.pending_count = 0;
        }
    }
    layout.rows.retain(|r| r.present || !used.contains(&r.id));
    for (uid, (text, score)) in rows.iter().zip(results) {
        layout.recognize(*uid, text, *score, now);
    }
}
