// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 StarChat contributors
//! The whole chat is tracked by position; a missed detection is verified
//! against the row's original glyph patch. Port of the Python LayoutTracker.
use crate::det::Quad;
use crate::image::{gray, round_even, warp_quad, Img};
use crate::text::{ascii, header, normalized_text, same_text};

#[derive(Clone)]
pub struct Row {
    /// Stable object identity (Python used the dict object itself).
    pub uid: u64,
    pub quad: Quad,
    /// x, y, w, h in the de-rotated layout frame.
    pub rect: [f64; 4],
    pub parts: Vec<(f64, f64)>,
    pub relative: [f64; 2],
    pub id: u64,
    pub version: u32,
    pub text: String,
    pub confidence: f64,
    pub seen: f64,
    pub hits: u32,
    pub present: bool,
    pub last_read: f64,
    pub dirty: bool,
    pub pending: String,
    pub pending_count: u32,
    pub appearance: Option<Vec<f32>>,
    /// The user asked for this row to be read again; the next result replaces the text outright.
    pub reread: bool,
}

pub struct Arranged {
    pub quad: Quad,
    pub rect: [f64; 4],
    pub parts: Vec<(f64, f64)>,
    pub relative: [f64; 2],
}

pub fn median(values: &mut [f64]) -> f64 {
    values.sort_by(|a, b| a.total_cmp(b));
    let n = values.len();
    if n == 0 {
        f64::NAN
    } else if n % 2 == 1 {
        values[n / 2]
    } else {
        (values[n / 2 - 1] + values[n / 2]) / 2.0
    }
}

fn norm(a: [f64; 2], b: [f64; 2]) -> f64 {
    ((b[0] - a[0]).powi(2) + (b[1] - a[1]).powi(2)).sqrt()
}

fn quad_angle(q: &Quad) -> f64 {
    (q[1][1] - q[0][1]).atan2(q[1][0] - q[0][0])
}

/// 96x16 zero-mean luminance patch of a row, or None when it has no contrast.
pub fn appearance(image: Option<&Img>, quad: &Quad) -> Option<Vec<f32>> {
    let image = image?;
    let sample = warp_quad(image, quad[0], quad[1], quad[3], 96, 16, false);
    let sample = if sample.c == 3 { gray(&sample) } else { sample };
    let mut values: Vec<f32> = sample.data.iter().map(|v| *v as f32).collect();
    let mean = values.iter().sum::<f32>() / values.len() as f32;
    let mut variance = 0f32;
    for v in values.iter_mut() {
        *v -= mean;
        variance += *v * *v;
    }
    if (variance / values.len() as f32).sqrt() >= 4.0 { Some(values) } else { None }
}

pub fn same_appearance(a: &Option<Vec<f32>>, b: &Option<Vec<f32>>) -> bool {
    let (Some(a), Some(b)) = (a, b) else { return false };
    let dot: f32 = a.iter().zip(b).map(|(x, y)| x * y).sum();
    let na = a.iter().map(|x| x * x).sum::<f32>().sqrt();
    let nb = b.iter().map(|x| x * x).sum::<f32>().sqrt();
    dot / (na * nb).max(1.0) >= 0.90
}

/// Remove the tilt and group the detected fragments of one line.
pub fn arrange(polygons: &[Quad]) -> Vec<Arranged> {
    let quads: Vec<&Quad> = polygons.iter()
        .filter(|p| p.iter().all(|c| c[0].is_finite() && c[1].is_finite()))
        .filter(|p| norm(p[0], p[1]) >= 3.0 && norm(p[0], p[3]) >= 3.0)
        .collect();
    if quads.is_empty() {
        return Vec::new();
    }
    let mut angles: Vec<f64> = quads.iter().map(|q| quad_angle(q)).collect();
    let angle = median(&mut angles);
    // Accept the ~5 degree HUD rotation; vertical signs are not chat rows.
    if angle.abs() > 12f64.to_radians() {
        return Vec::new();
    }
    let (cos, sin) = (angle.cos(), angle.sin());
    let mut bounds: Vec<[f64; 4]> = quads.iter().map(|q| {
        let flat = q.map(|p| [p[0] * cos + p[1] * sin, -p[0] * sin + p[1] * cos]);
        [flat.iter().map(|p| p[0]).fold(f64::MAX, f64::min), flat.iter().map(|p| p[1]).fold(f64::MAX, f64::min),
         flat.iter().map(|p| p[0]).fold(f64::MIN, f64::max), flat.iter().map(|p| p[1]).fold(f64::MIN, f64::max)]
    }).collect();
    let height = median(&mut bounds.iter().map(|b| b[3] - b[1]).collect::<Vec<_>>());
    bounds.sort_by(|a, b| ((a[1] + a[3]) / 2.0).total_cmp(&((b[1] + b[3]) / 2.0)).then(a[0].total_cmp(&b[0])));
    let mut groups: Vec<(f64, Vec<[f64; 4]>)> = Vec::new();
    for b in bounds {
        let center = (b[1] + b[3]) / 2.0;
        let index = match groups.iter().rposition(|g| (g.0 - center).abs() <= 0.48 * height) {
            Some(index) => index,
            None => {
                groups.push((center, Vec::new()));
                groups.len() - 1
            }
        };
        groups[index].1.push(b);
        groups[index].0 = median(&mut groups[index].1.iter().map(|p| (p[1] + p[3]) / 2.0).collect::<Vec<_>>());
    }
    let mut result: Vec<Arranged> = groups.into_iter().take(64).map(|(_, mut parts)| {
        parts.sort_by(|a, b| a[0].total_cmp(&b[0]));
        let x = parts.iter().map(|p| p[0]).fold(f64::MAX, f64::min);
        let y = parts.iter().map(|p| p[1]).fold(f64::MAX, f64::min);
        let right = parts.iter().map(|p| p[2]).fold(f64::MIN, f64::max);
        let bottom = parts.iter().map(|p| p[3]).fold(f64::MIN, f64::max);
        let (w, h) = (right - x, bottom - y);
        let back = |p: [f64; 2]| [p[0] * cos - p[1] * sin, p[0] * sin + p[1] * cos];
        Arranged {
            quad: [back([x, y]), back([right, y]), back([right, bottom]), back([x, bottom])],
            rect: [x, y, w, h],
            parts: parts.iter().map(|p| ((p[0] - x) / w, (p[2] - p[0]) / w)).collect(),
            relative: [0.0, 0.0],
        }
    }).collect();
    result.sort_by(|a, b| a.rect[1].total_cmp(&b.rect[1]).then(a.rect[0].total_cmp(&b.rect[0])));
    // The left edge of the topmost row is the shared reference point.
    let anchor = [result[0].rect[0], result[0].rect[1]];
    for r in result.iter_mut() {
        r.relative = [r.rect[0] - anchor[0], r.rect[1] - anchor[1]];
    }
    result
}

/// Order-preserving matching on width, row order and relative position.
pub fn assignments(old: &[Row], new: &[Arranged]) -> Vec<(usize, usize)> {
    if old.is_empty() || new.is_empty() {
        return Vec::new();
    }
    let (n, m) = (old.len(), new.len());
    let h = median(&mut new.iter().map(|r| r.rect[3]).collect::<Vec<_>>()).max(4.0);
    // Detector height jitter is not a scale measurement.
    let mut scale = 1.0;
    if n == m {
        let candidate = median(&mut old.iter().zip(new).map(|(a, b)| b.rect[2] / a.rect[2].max(3.0)).collect::<Vec<_>>());
        if (0.94..=1.06).contains(&candidate) {
            scale = candidate;
        }
    }
    let at = |i: usize, j: usize| i * m + j;
    let mut widths = vec![0f64; n * m];
    let mut shape = vec![false; n * m];
    let (mut dx, mut dy) = (vec![0f64; n * m], vec![0f64; n * m]);
    for (i, a) in old.iter().enumerate() {
        for (j, b) in new.iter().enumerate() {
            let (ow, nw) = (a.rect[2] * scale, b.rect[2]);
            widths[at(i, j)] = (ow - nw).abs() / ow.max(nw);
            let mut ok = (ow - nw).abs() <= (0.09 * ow.max(nw)).max(5.0);
            // One line splitting into fragments is not proof of new text.
            if ok && a.parts.len() == b.parts.len() && a.parts.len() > 1 {
                let worst = a.parts.iter().zip(&b.parts)
                    .map(|(p, q)| (p.0 - q.0).abs().max((p.1 - q.1).abs()))
                    .fold(0f64, f64::max);
                if worst > 0.10 {
                    ok = false;
                }
            }
            shape[at(i, j)] = ok;
            dx[at(i, j)] = b.relative[0] - a.relative[0] * scale;
            dy[at(i, j)] = b.relative[1] - a.relative[1] * scale;
        }
    }
    // Vote for shared offsets in half-row steps (Counter.most_common order).
    let mut votes: Vec<((i64, i64), usize)> = Vec::new();
    for index in 0..n * m {
        if shape[index] {
            let key = (round_even(dx[index] / (h * 0.5)) as i64, round_even(dy[index] / (h * 0.5)) as i64);
            match votes.iter_mut().find(|v| v.0 == key) {
                Some(v) => v.1 += 1,
                None => votes.push((key, 1)),
            }
        }
    }
    votes.sort_by(|a, b| b.1.cmp(&a.1)); // stable: ties keep first-seen order
    let mut offsets = vec![(0.0, 0.0)];
    offsets.extend(votes.iter().take(16).map(|((x, y), _)| (*x as f64 * h * 0.5, *y as f64 * h * 0.5)));
    let (mut best, mut best_score) = (Vec::new(), -1.0f64);
    let mut dp = vec![0f64; (n + 1) * (m + 1)];
    let mut pair = vec![-1f64; n * m];
    let mut valid = vec![false; n * m];
    for (tx, ty) in offsets {
        let d = |i: usize, j: usize| i * (m + 1) + j;
        dp.iter_mut().for_each(|v| *v = 0.0);
        for i in (0..n).rev() {
            for j in (0..m).rev() {
                let index = at(i, j);
                let nw = new[j].rect[2];
                let ok = shape[index] && (dy[index] - ty).abs() <= h * 0.65 && (dx[index] - tx).abs() <= (h * 0.8).max(nw * 0.05);
                valid[index] = ok;
                pair[index] = if ok {
                    let distance = (dy[index] - ty).abs() / h + (dx[index] - tx).abs() / h.max(nw * 0.12);
                    2.0 - 0.25 * distance - widths[index] + dp[d(i + 1, j + 1)]
                } else {
                    -1.0
                };
                dp[d(i, j)] = pair[index].max(dp[d(i + 1, j)]).max(dp[d(i, j + 1)]);
            }
        }
        let score = dp[0] - 0.0001 * (tx.abs() + ty.abs());
        if score <= best_score {
            continue;
        }
        let mut pairs = Vec::new();
        let (mut i, mut j) = (0, 0);
        while i < n && j < m {
            let index = at(i, j);
            if valid[index] && (dp[d(i, j)] - pair[index]).abs() < 1e-7 {
                pairs.push((i, j));
                i += 1;
                j += 1;
            } else if dp[d(i + 1, j)] >= dp[d(i, j + 1)] {
                i += 1;
            } else {
                j += 1;
            }
        }
        best = pairs;
        best_score = score;
    }
    best
}

/// Least-squares affine map (3x2) from `src` points to `dst` points.
fn fit_affine(src: &[[f64; 2]], dst: &[[f64; 2]]) -> Option<[[f64; 2]; 3]> {
    let mut ata = [[0f64; 3]; 3];
    let mut atb = [[0f64; 2]; 3];
    for (s, d) in src.iter().zip(dst) {
        let row = [s[0], s[1], 1.0];
        for i in 0..3 {
            for j in 0..3 {
                ata[i][j] += row[i] * row[j];
            }
            atb[i][0] += row[i] * d[0];
            atb[i][1] += row[i] * d[1];
        }
    }
    // Gauss-Jordan with partial pivoting.
    let mut a = [[0f64; 5]; 3];
    for i in 0..3 {
        a[i][..3].copy_from_slice(&ata[i]);
        a[i][3] = atb[i][0];
        a[i][4] = atb[i][1];
    }
    for col in 0..3 {
        let pivot = (col..3).max_by(|x, y| a[*x][col].abs().total_cmp(&a[*y][col].abs()))?;
        if a[pivot][col].abs() < 1e-9 {
            return None;
        }
        a.swap(col, pivot);
        for row in 0..3 {
            if row != col {
                let factor = a[row][col] / a[col][col];
                for k in col..5 {
                    a[row][k] -= factor * a[col][k];
                }
            }
        }
    }
    Some([0, 1, 2].map(|i| [a[i][3] / a[i][i], a[i][4] / a[i][i]]))
}

pub struct LayoutTracker {
    pub rows: Vec<Row>,
    pub next_id: u64,
    next_uid: u64,
    pub hold_seconds: f64,
    pub size: Option<(usize, usize)>,
    pub identity: String,
    pub warmed: bool,
    /// Track keys whose requested re-read finished since the last publish.
    pub reread_done: Vec<String>,
}

fn random_identity() -> String {
    use std::hash::{BuildHasher, Hasher};
    let mut out = String::new();
    for salt in 0..2u64 {
        let mut hasher = std::collections::hash_map::RandomState::new().build_hasher();
        hasher.write_u64(salt);
        hasher.write_u128(std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map_or(0, |d| d.as_nanos()));
        out.push_str(&format!("{:016x}", hasher.finish()));
    }
    out
}

impl LayoutTracker {
    pub fn new() -> Self {
        LayoutTracker { rows: Vec::new(), next_id: 0, next_uid: 0, hold_seconds: 1.8, size: None, identity: random_identity(), warmed: false, reread_done: Vec::new() }
    }

    fn track_key(&self, row: &Row) -> String {
        format!("{}:{}:{}", self.identity, row.id, row.version)
    }

    /// Flag the rows with these track keys for an unconditional re-read. Keys that no
    /// longer exist are reported as done so the host does not wait for them.
    pub fn request_reread(&mut self, keys: &[String]) -> bool {
        let mut any = false;
        for key in keys {
            let found = (0..self.rows.len()).find(|i| self.rows[*i].present && self.track_key(&self.rows[*i]) == *key);
            match found {
                Some(index) => {
                    self.rows[index].reread = true;
                    any = true;
                }
                None => self.reread_done.push(key.clone()),
            }
        }
        any
    }

    pub fn index(&self, uid: u64) -> Option<usize> {
        self.rows.iter().position(|r| r.uid == uid)
    }

    pub fn update(&mut self, polygons: &[Quad], size: (usize, usize), now: f64, image: Option<&Img>) {
        if self.size != Some(size) {
            self.rows.clear();
            self.size = Some(size);
            self.warmed = false;
        }
        let new = arrange(polygons);
        let mut old = std::mem::take(&mut self.rows);
        let mut pairs = assignments(&old, &new);
        // A width/fragment change at the same row requests verification without
        // destroying the old text. Never use this fallback for a measured scroll.
        let shift = if pairs.is_empty() { 0.0 } else { median(&mut pairs.iter().map(|&(i, j)| new[j].rect[1] - old[i].rect[1]).collect::<Vec<_>>()) };
        let new_height = if new.is_empty() { 0.0 } else { median(&mut new.iter().map(|r| r.rect[3]).collect::<Vec<_>>()) };
        let mut dirty: Vec<usize> = Vec::new();
        if !old.is_empty() && !new.is_empty() && shift.abs() < 0.7 * new_height {
            let mut used_i: Vec<usize> = pairs.iter().map(|p| p.0).collect();
            let mut used_j: Vec<usize> = pairs.iter().map(|p| p.1).collect();
            for (j, b) in new.iter().enumerate() {
                if used_j.contains(&j) {
                    continue;
                }
                let mut candidate: Option<(f64, usize)> = None;
                for (i, a) in old.iter().enumerate() {
                    if used_i.contains(&i) {
                        continue;
                    }
                    let (ar, br) = (a.rect, b.rect);
                    let ratio = br[2] / ar[2].max(3.0);
                    if ((ar[1] + ar[3] / 2.0) + shift - (br[1] + br[3] / 2.0)).abs() < ar[3].max(br[3]) * 0.65
                        && (ar[0] - br[0]).abs() < br[3].max(12.0)
                        && ratio > 0.5 && ratio < 2.0
                    {
                        let key = (ar[1] + shift - br[1]).abs();
                        if candidate.map_or(true, |c| key < c.0) {
                            candidate = Some((key, i));
                        }
                    }
                }
                if let Some((_, i)) = candidate {
                    pairs.push((i, j));
                    used_i.push(i);
                    used_j.push(j);
                    dirty.push(i);
                }
            }
            pairs.sort();
        }
        let used_old: Vec<usize> = pairs.iter().map(|p| p.0).collect();
        let used_new: Vec<usize> = pairs.iter().map(|p| p.1).collect();
        let min_used_old = used_old.iter().copied().min();
        let max_used_new = used_new.iter().copied().max();
        let shift = if pairs.is_empty() { 0.0 } else { median(&mut pairs.iter().map(|&(i, j)| new[j].rect[1] - old[i].rect[1]).collect::<Vec<_>>()) };
        let scrolled = pairs.len() >= 2
            && min_used_old.unwrap_or(0) > 0
            && (0..new.len()).any(|j| !used_new.contains(&j) && j > max_used_new.unwrap_or(0))
            && shift < -0.7 * new_height;
        let mut transform = None;
        if !pairs.is_empty() {
            // One shared rotation/translation/small scale for the whole block.
            let src: Vec<[f64; 2]> = pairs.iter().flat_map(|&(i, _)| old[i].quad).collect();
            let dst: Vec<[f64; 2]> = pairs.iter().flat_map(|&(_, j)| new[j].quad).collect();
            if let Some(affine) = fit_affine(&src, &dst) {
                let determinant = affine[0][0] * affine[1][1] - affine[0][1] * affine[1][0];
                if determinant > 0.65 && determinant < 1.5 {
                    transform = Some(affine);
                }
            }
        }
        let mut new: Vec<Option<Arranged>> = new.into_iter().map(Some).collect();
        let mut taken: Vec<Option<Row>> = old.drain(..).map(Some).collect();
        let mut updated: Vec<Row> = Vec::new();
        for &(i, j) in &pairs {
            let mut item = taken[i].take().unwrap();
            let fresh = new[j].take().unwrap();
            item.quad = fresh.quad;
            item.rect = fresh.rect;
            item.parts = fresh.parts;
            item.relative = fresh.relative;
            item.seen = now;
            item.hits += 1;
            item.present = true;
            if dirty.contains(&i) {
                item.dirty = true;
            }
            item.appearance = appearance(image, &item.quad);
            updated.push(item);
        }
        for fresh in new.into_iter().flatten() {
            self.next_id += 1;
            self.next_uid += 1;
            let appearance = appearance(image, &fresh.quad);
            updated.push(Row {
                uid: self.next_uid, quad: fresh.quad, rect: fresh.rect, parts: fresh.parts, relative: fresh.relative,
                id: self.next_id, version: 0, text: String::new(), confidence: 0.0, seen: now, hits: 1, present: true,
                last_read: 0.0, dirty: false, pending: String::new(), pending_count: 0, appearance, reread: false,
            });
        }
        for (i, item) in taken.into_iter().enumerate() {
            let Some(mut item) = item else { continue };
            if scrolled && i < min_used_old.unwrap_or(0) {
                continue;
            }
            // One missed detection does not erase a translation: apply the
            // motion estimated from the remaining rows.
            if let Some(t) = &transform {
                item.quad = item.quad.map(|p| [p[0] * t[0][0] + p[1] * t[1][0] + t[2][0], p[0] * t[0][1] + p[1] * t[1][1] + t[2][1]]);
            }
            item.present = false;
            // Missing detection is not evidence that text disappeared. Verify the
            // old glyph patch after applying the shared motion (no extra OCR).
            let sample = appearance(image, &item.quad);
            if same_appearance(&item.appearance, &sample) {
                item.seen = now;
            }
            if now - item.seen > self.hold_seconds {
                continue;
            }
            let top = item.quad.iter().map(|p| p[1]).fold(f64::MAX, f64::min);
            let bottom = item.quad.iter().map(|p| p[1]).fold(f64::MIN, f64::max);
            if bottom < 0.0 || top > size.1 as f64 {
                continue;
            }
            updated.push(item);
        }
        self.rows = updated;
        // Held rows are re-aligned in the same frame to avoid accumulated error.
        if !self.rows.is_empty() {
            let angle = median(&mut self.rows.iter().map(|r| quad_angle(&r.quad)).collect::<Vec<_>>());
            let (cos, sin) = (angle.cos(), angle.sin());
            for r in self.rows.iter_mut() {
                let p = r.quad.map(|p| [p[0] * cos + p[1] * sin, -p[0] * sin + p[1] * cos]);
                let (x0, x1) = (p.iter().map(|v| v[0]).fold(f64::MAX, f64::min), p.iter().map(|v| v[0]).fold(f64::MIN, f64::max));
                let (y0, y1) = (p.iter().map(|v| v[1]).fold(f64::MAX, f64::min), p.iter().map(|v| v[1]).fold(f64::MIN, f64::max));
                r.rect = [x0, y0, x1 - x0, y1 - y0];
            }
            // Row order must be decided in de-rotated coordinates.
            self.rows.sort_by(|a, b| a.rect[1].total_cmp(&b.rect[1]).then(a.rect[0].total_cmp(&b.rect[0])));
            self.rows.truncate(64);
            let anchor = [self.rows[0].rect[0], self.rows[0].rect[1]];
            for r in self.rows.iter_mut() {
                r.relative = [r.rect[0] - anchor[0], r.rect[1] - anchor[1]];
            }
        }
    }

    /// uids of the rows to read. New boxes are read after a second detection.
    pub fn to_read(&self, now: f64, force: bool) -> Vec<u64> {
        self.rows.iter().filter(|r| {
            r.present && (r.reread || ((!r.text.is_empty() || r.hits >= 2 || !self.warmed)
                && (force || ((r.text.is_empty() || r.dirty || !r.pending.is_empty()) && r.hits >= 2 && now - r.last_read >= 0.5))))
        }).map(|r| r.uid).collect()
    }

    pub fn recognize(&mut self, uid: u64, text: &str, confidence: f64, now: f64) {
        let Some(index) = self.index(uid) else { return };
        let reread = std::mem::take(&mut self.rows[index].reread);
        if reread {
            let key = self.track_key(&self.rows[index]);
            self.reread_done.push(key);
        }
        let row = &mut self.rows[index];
        row.last_read = now;
        let text = ascii(text);
        if text.is_empty() || !confidence.is_finite() || confidence < 0.60 {
            return;
        }
        if reread {
            // A manual re-read replaces the text without the two-reading confirmation; the
            // track key is unchanged so the host updates the same chat message in place.
            row.text = normalized_text(&text);
            row.confidence = confidence;
            row.pending.clear();
            row.pending_count = 0;
            row.dirty = false;
            self.warmed = true;
            return;
        }
        let previous = row.text.clone();
        let text = normalized_text(&text);
        // A small spelling change at the same place must not shake the display.
        let same = !previous.is_empty() && same_text(&previous, &text);
        if !previous.is_empty() && !same {
            // A clipped header must not turn into a continuation of another
            // speaker. Keep the recognized header until replacement is confirmed.
            if header().is_match(&previous) && !header().is_match(&text) {
                row.pending.clear();
                row.pending_count = 0;
                return;
            }
            if !row.pending.is_empty() && same_text(&row.pending, &text) {
                row.pending_count += 1;
            } else {
                row.pending = text.clone();
                row.pending_count = 1;
            }
            if row.pending_count < 2 {
                return;
            }
            row.version += 1;
        }
        if previous.is_empty() || !same {
            row.text = text;
        }
        self.warmed = true;
        let row = &mut self.rows[index];
        row.pending.clear();
        row.pending_count = 0;
        row.dirty = false;
        row.confidence = row.confidence.max(confidence);
        // Only after a successful read does a new track remove the stale row it covers.
        let bounds = |q: &Quad| (
            q.iter().map(|p| p[0]).fold(f64::MAX, f64::min), q.iter().map(|p| p[1]).fold(f64::MAX, f64::min),
            q.iter().map(|p| p[0]).fold(f64::MIN, f64::max), q.iter().map(|p| p[1]).fold(f64::MIN, f64::max));
        let (x1, y1, x2, y2) = bounds(&row.quad);
        self.rows.retain(|r| {
            if r.uid == uid || r.present {
                return true;
            }
            let (a, b, c, d) = bounds(&r.quad);
            let intersection = (x2.min(c) - x1.max(a)).max(0.0) * (y2.min(d) - y1.max(b)).max(0.0);
            intersection / ((x2 - x1) * (y2 - y1)).min((c - a) * (d - b)).max(1.0) <= 0.45
        });
    }

    pub fn lines(&self) -> Vec<serde_json::Value> {
        self.rows.iter().filter(|r| !r.text.is_empty()).map(|r| {
            let q = &r.quad;
            serde_json::json!({
                "text": r.text, "confidence": r.confidence, "boxId": r.id, "version": r.version,
                "trackKey": format!("{}:{}:{}", self.identity, r.id, r.version),
                "x": q[0][0], "y": q[0][1], "w": norm(q[0], q[1]), "h": norm(q[0], q[3]),
                "angle": quad_angle(q).to_degrees(), "layoutX": r.rect[0], "layoutY": r.rect[1],
            })
        }).collect()
    }
}
