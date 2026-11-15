// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 StarChat contributors
//! Fast follower for HUD sway between OCR ticks.
//!
//! The chat block sways with the player's head while detection runs only a few
//! times per second. On every captured frame the bright glyph pixels of the
//! last detected rows are matched against the new frame by a small integer
//! shift search, which costs well under a millisecond and needs no model.
//! The result is one shared (dx, dy) relative to the last published positions.
use std::collections::VecDeque;

use crate::det::Quad;
use crate::image::Img;

const MAX_POINTS: usize = 3000;
const NEAR_X: i32 = 4;
const NEAR_Y: i32 = 6;

struct Point {
    x: i32,
    y: i32,
    value: i32,
}

pub struct Offset {
    pub dx: f64,
    pub dy: f64,
    /// Sequence of the `lines` message these offsets are relative to.
    pub base: u64,
}

pub struct MotionTracker {
    points: Vec<Point>,
    size: (usize, usize),
    row_height: f64,
    /// Integer estimate the next search starts from.
    guess: (i32, i32),
    current: (f64, f64),
    base: u64,
    /// (frame id, offset) relative to the current reference.
    history: VecDeque<(u64, (f64, f64))>,
    pub lost: bool,
    active: bool,
}

impl MotionTracker {
    pub fn new() -> Self {
        MotionTracker { points: Vec::new(), size: (0, 0), row_height: 16.0, guess: (0, 0), current: (0.0, 0.0),
                        base: 0, history: VecDeque::new(), lost: false, active: false }
    }

    pub fn clear(&mut self) {
        *self = MotionTracker::new();
    }

    /// Offset to send along with a `lines` message published as `sequence`.
    pub fn rebase(&mut self, sequence: u64) -> (f64, f64) {
        self.base = sequence;
        if self.active { self.current } else { (0.0, 0.0) }
    }

    /// New reference: `gray` is the frame the tick detected on (`frame_id`),
    /// `quads` are the rows found in it.
    pub fn set_reference(&mut self, gray: &Img, frame_id: u64, quads: &[Quad]) {
        // Motion that happened while the model was running carries over.
        let latest = self.history.back().map(|h| h.1);
        let at_frame = self.history.iter().find(|h| h.0 == frame_id).map(|h| h.1);
        let carried = match (self.active && !self.lost, latest, at_frame) {
            (true, Some(latest), Some(at_frame)) => (latest.0 - at_frame.0, latest.1 - at_frame.1),
            _ => (0.0, 0.0),
        };
        self.history.clear();
        self.points.clear();
        self.size = (gray.w, gray.h);
        let mut heights = Vec::new();
        let mut candidates: Vec<Point> = Vec::new();
        for q in quads {
            let x0 = q.iter().map(|p| p[0]).fold(f64::MAX, f64::min).floor().max(0.0) as usize;
            let y0 = q.iter().map(|p| p[1]).fold(f64::MAX, f64::min).floor().max(0.0) as usize;
            let x1 = (q.iter().map(|p| p[0]).fold(f64::MIN, f64::max).ceil() as usize).min(gray.w);
            let y1 = (q.iter().map(|p| p[1]).fold(f64::MIN, f64::max).ceil() as usize).min(gray.h);
            if x1 <= x0 + 4 || y1 <= y0 + 4 {
                continue;
            }
            heights.push(((q[3][0] - q[0][0]).powi(2) + (q[3][1] - q[0][1]).powi(2)).sqrt());
            // Glyphs are the bright strokes of the row: keep pixels well above
            // the row's own median brightness.
            let mut histogram = [0u32; 256];
            for y in y0..y1 {
                for v in &gray.data[y * gray.w + x0..y * gray.w + x1] {
                    histogram[*v as usize] += 1;
                }
            }
            let total = ((x1 - x0) * (y1 - y0)) as u32;
            let percentile = |fraction: f64| {
                let mut sum = 0u32;
                for (value, count) in histogram.iter().enumerate() {
                    sum += count;
                    if sum as f64 >= total as f64 * fraction {
                        return value as i32;
                    }
                }
                255
            };
            let (middle, bright) = (percentile(0.5), percentile(0.97));
            if bright - middle < 24 {
                continue; // No usable contrast in this row.
            }
            let threshold = middle + (bright - middle) * 6 / 10;
            for y in y0..y1 {
                for x in x0..x1 {
                    let value = gray.data[y * gray.w + x] as i32;
                    if value >= threshold {
                        candidates.push(Point { x: x as i32, y: y as i32, value });
                    }
                }
            }
        }
        let step = (candidates.len() / MAX_POINTS).max(1);
        self.points = candidates.into_iter().step_by(step).collect();
        self.row_height = if heights.is_empty() { 16.0 } else { crate::geometry::median(&mut heights) };
        self.active = self.points.len() >= 40;
        self.lost = false;
        self.current = if self.active { carried } else { (0.0, 0.0) };
        self.guess = (self.current.0.round() as i32, self.current.1.round() as i32);
    }

    fn cost(&self, gray: &Img, dx: i32, dy: i32) -> Option<f64> {
        let (w, h) = (gray.w as i32, gray.h as i32);
        let (mut sum, mut count) = (0u64, 0u32);
        for p in &self.points {
            let (x, y) = (p.x + dx, p.y + dy);
            if x < 0 || y < 0 || x >= w || y >= h {
                continue;
            }
            sum += (p.value - gray.data[(y * w + x) as usize] as i32).unsigned_abs() as u64;
            count += 1;
        }
        if (count as usize) * 2 < self.points.len() { None } else { Some(sum as f64 / count as f64) }
    }

    /// Best shift in a window around `centre`: (dx, dy, cost, median cost).
    fn search(&self, gray: &Img, centre: (i32, i32), rx: i32, ry: i32) -> Option<(i32, i32, f64, f64)> {
        let mut costs = Vec::with_capacity(((2 * rx + 1) * (2 * ry + 1)) as usize);
        let mut best: Option<(i32, i32, f64)> = None;
        for dy in centre.1 - ry..=centre.1 + ry {
            for dx in centre.0 - rx..=centre.0 + rx {
                let Some(cost) = self.cost(gray, dx, dy) else { continue };
                costs.push(cost);
                // Prefer the smaller move among equal costs.
                let closer = |b: &(i32, i32, f64)| (dx - centre.0).abs() + (dy - centre.1).abs() < (b.0 - centre.0).abs() + (b.1 - centre.1).abs();
                if best.as_ref().map_or(true, |b| cost < b.2 - 1e-9 || ((cost - b.2).abs() <= 1e-9 && closer(b))) {
                    best = Some((dx, dy, cost));
                }
            }
        }
        let (dx, dy, cost) = best?;
        Some((dx, dy, cost, crate::geometry::median(&mut costs)))
    }

    /// Track one captured frame. Returns the offset when it is trustworthy.
    pub fn track(&mut self, gray: &Img, frame_id: u64) -> Option<Offset> {
        if !self.active || self.size != (gray.w, gray.h) {
            return None;
        }
        // A distinct minimum: glyphs on glyphs is far cheaper than glyphs on background.
        let distinct = |r: &(i32, i32, f64, f64)| r.2 < 0.62 * r.3 && r.3 - r.2 >= 6.0;
        let mut found = self.search(gray, self.guess, NEAR_X, NEAR_Y).filter(distinct);
        if found.is_none() {
            // A new chat line scrolls the block by a whole row in one frame.
            let wide = (self.row_height * 1.6).ceil() as i32 + NEAR_Y;
            found = self.search(gray, self.guess, NEAR_X + 4, wide).filter(distinct);
        }
        let Some((dx, dy, cost, _)) = found else {
            self.lost = true;
            return None;
        };
        // Parabolic sub-pixel refinement on both axes.
        let refine = |minus: Option<f64>, plus: Option<f64>| match (minus, plus) {
            (Some(a), Some(b)) if a + b - 2.0 * cost > 1e-6 => (0.5 * (a - b) / (a + b - 2.0 * cost)).clamp(-0.5, 0.5),
            _ => 0.0,
        };
        let fx = refine(self.cost(gray, dx - 1, dy), self.cost(gray, dx + 1, dy));
        let fy = refine(self.cost(gray, dx, dy - 1), self.cost(gray, dx, dy + 1));
        self.lost = false;
        self.guess = (dx, dy);
        self.current = (dx as f64 + fx, dy as f64 + fy);
        if self.history.len() >= 128 {
            self.history.pop_front();
        }
        self.history.push_back((frame_id, self.current));
        Some(Offset { dx: self.current.0, dy: self.current.1, base: self.base })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn frame(shift: (i32, i32)) -> Img {
        let mut image = Img::new(320, 120, 1);
        for (i, v) in image.data.iter_mut().enumerate() {
            *v = 30 + ((i * 7) % 11) as u8; // textured dark background
        }
        for row in 0..3 {
            for glyph in 0..20 {
                for y in 0..10 {
                    for x in 0..5 {
                        if (x + y + glyph) % 3 != 0 {
                            let px = 20 + glyph * 12 + x + shift.0;
                            let py = 20 + row * 30 + y as i32 + shift.1;
                            if px >= 0 && py >= 0 && px < 320 && py < 120 {
                                image.data[(py * 320 + px) as usize] = 230;
                            }
                        }
                    }
                }
            }
        }
        image
    }

    fn rows() -> Vec<Quad> {
        (0..3).map(|r| {
            let y = 18.0 + r as f64 * 30.0;
            [[16.0, y], [264.0, y], [264.0, y + 14.0], [16.0, y + 14.0]]
        }).collect()
    }

    #[test]
    fn follows_small_sway_and_row_scroll() {
        let mut tracker = MotionTracker::new();
        tracker.set_reference(&frame((0, 0)), 1, &rows());
        tracker.rebase(7);
        let offset = tracker.track(&frame((2, -3)), 2).expect("small sway");
        assert_eq!((offset.dx.round() as i32, offset.dy.round() as i32, offset.base), (2, -3, 7));
        let offset = tracker.track(&frame((3, -5)), 3).expect("continued sway");
        assert_eq!((offset.dx.round() as i32, offset.dy.round() as i32), (3, -5));
        let offset = tracker.track(&frame((3, -22)), 4).expect("row scroll");
        assert_eq!(offset.dy.round() as i32, -22);
    }

    #[test]
    fn carries_motion_across_a_new_reference_and_reports_loss() {
        let mut tracker = MotionTracker::new();
        tracker.set_reference(&frame((0, 0)), 1, &rows());
        tracker.track(&frame((0, 2)), 2);
        tracker.track(&frame((0, 5)), 3);
        // Detection ran on frame 2; frame 3 has since moved 3 px further.
        tracker.set_reference(&frame((0, 2)), 2, &rows().iter().map(|q| q.map(|p| [p[0], p[1] + 2.0])).collect::<Vec<_>>());
        let carried = tracker.rebase(8);
        assert!(carried.0.abs() < 0.3 && (carried.1 - 3.0).abs() < 0.3, "{carried:?}");
        let mut blank = Img::new(320, 120, 1);
        blank.data.fill(40);
        assert!(tracker.track(&blank, 4).is_none() && tracker.lost);
    }
}
