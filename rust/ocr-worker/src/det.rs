// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 StarChat contributors
//! PP-OCRv6 detection pre/post-processing (DB boxes), following PaddleX's
//! Apache-2.0 processors with the StarChat pipeline overrides
//! (limit_side_len=64/min, thresh .3, box_thresh .6, unclip 1.5).
use crate::image::{resize, round_even, Img};

pub type Quad = [[f64; 2]; 4];

/// Network input size for an image: both sides are multiples of 32.
pub fn detection_size(w: usize, h: usize) -> (usize, usize) {
    let ratio = (64.0 / w.min(h) as f64).max(1.0);
    let (mut rh, mut rw) = ((h as f64 * ratio) as usize, (w as f64 * ratio) as usize);
    if rh.max(rw) > 4000 {
        let ratio = 4000.0 / rh.max(rw) as f64;
        rh = (rh as f64 * ratio) as usize;
        rw = (rw as f64 * ratio) as usize;
    }
    let snap = |v: usize| (round_even(v as f64 / 32.0) as usize * 32).max(32);
    (snap(rw), snap(rh))
}

/// NCHW float tensor. Channel order stays BGR, as in the official normalization.
pub fn detection_tensor(image: &Img, out: &mut Vec<f32>) -> (usize, usize) {
    let (rw, rh) = detection_size(image.w, image.h);
    let resized;
    let source = if (rw, rh) == (image.w, image.h) {
        image
    } else {
        resized = resize(image, rw, rh);
        &resized
    };
    const SCALE: [f32; 3] = [1.0 / 255.0 / 0.229, 1.0 / 255.0 / 0.224, 1.0 / 255.0 / 0.225];
    const SHIFT: [f32; 3] = [-0.485 / 0.229, -0.456 / 0.224, -0.406 / 0.225];
    let plane = rw * rh;
    out.clear();
    out.resize(plane * 3, 0.0);
    let (b, rest) = out.split_at_mut(plane);
    let (g, r) = rest.split_at_mut(plane);
    for (i, p) in source.data.chunks_exact(3).enumerate() {
        b[i] = p[0] as f32 * SCALE[0] + SHIFT[0];
        g[i] = p[1] as f32 * SCALE[1] + SHIFT[1];
        r[i] = p[2] as f32 * SCALE[2] + SHIFT[2];
    }
    (rw, rh)
}

fn cross(o: [f64; 2], a: [f64; 2], b: [f64; 2]) -> f64 {
    (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])
}

fn convex_hull(points: &mut Vec<[f64; 2]>) -> Vec<[f64; 2]> {
    points.sort_by(|a, b| a[0].total_cmp(&b[0]).then(a[1].total_cmp(&b[1])));
    points.dedup();
    if points.len() < 3 {
        return points.clone();
    }
    let mut hull: Vec<[f64; 2]> = Vec::with_capacity(points.len() * 2);
    for &p in points.iter() {
        while hull.len() >= 2 && cross(hull[hull.len() - 2], hull[hull.len() - 1], p) <= 0.0 {
            hull.pop();
        }
        hull.push(p);
    }
    let lower = hull.len() + 1;
    for &p in points.iter().rev().skip(1) {
        while hull.len() >= lower && cross(hull[hull.len() - 2], hull[hull.len() - 1], p) <= 0.0 {
            hull.pop();
        }
        hull.push(p);
    }
    hull.pop();
    hull
}

/// Minimum-area enclosing rectangle: (corners, short side).
fn min_area_rect(hull: &[[f64; 2]]) -> Option<(Quad, f64)> {
    if hull.len() < 3 {
        return None;
    }
    let mut best: Option<(f64, Quad, f64)> = None;
    for i in 0..hull.len() {
        let (a, b) = (hull[i], hull[(i + 1) % hull.len()]);
        let length = ((b[0] - a[0]).powi(2) + (b[1] - a[1]).powi(2)).sqrt();
        if length < 1e-9 {
            continue;
        }
        let (ux, uy) = ((b[0] - a[0]) / length, (b[1] - a[1]) / length);
        let (mut lo_u, mut hi_u, mut lo_v, mut hi_v) = (f64::MAX, f64::MIN, f64::MAX, f64::MIN);
        for p in hull {
            let u = p[0] * ux + p[1] * uy;
            let v = -p[0] * uy + p[1] * ux;
            lo_u = lo_u.min(u);
            hi_u = hi_u.max(u);
            lo_v = lo_v.min(v);
            hi_v = hi_v.max(v);
        }
        let area = (hi_u - lo_u) * (hi_v - lo_v);
        if best.as_ref().map_or(true, |b| area < b.0 - 1e-9) {
            let corner = |u: f64, v: f64| [u * ux - v * uy, u * uy + v * ux];
            let quad = [corner(lo_u, lo_v), corner(hi_u, lo_v), corner(hi_u, hi_v), corner(lo_u, hi_v)];
            best = Some((area, quad, (hi_u - lo_u).min(hi_v - lo_v)));
        }
    }
    best.map(|b| (b.1, b.2))
}

/// PaddleX get_mini_boxes ordering: top-left, top-right, bottom-right, bottom-left.
fn ordered(quad: Quad) -> Quad {
    let mut points = quad;
    points.sort_by(|a, b| a[0].total_cmp(&b[0]));
    let (mut left, mut right) = ([points[0], points[1]], [points[2], points[3]]);
    left.sort_by(|a, b| a[1].total_cmp(&b[1]));
    right.sort_by(|a, b| a[1].total_cmp(&b[1]));
    [left[0], right[0], right[1], left[1]]
}

fn expand(quad: Quad, distance: f64) -> Quad {
    // Offsetting a rectangle with round joins and taking its minimum-area
    // rectangle equals pushing every side outwards by `distance`.
    let centre = [(quad[0][0] + quad[2][0]) / 2.0, (quad[0][1] + quad[2][1]) / 2.0];
    let unit = |a: [f64; 2], b: [f64; 2]| {
        let n = ((b[0] - a[0]).powi(2) + (b[1] - a[1]).powi(2)).sqrt().max(1e-9);
        ([(b[0] - a[0]) / n, (b[1] - a[1]) / n], n)
    };
    let (u, lu) = unit(quad[0], quad[1]);
    let (v, lv) = unit(quad[0], quad[3]);
    let (hu, hv) = (lu / 2.0 + distance, lv / 2.0 + distance);
    let corner = |a: f64, b: f64| [centre[0] + u[0] * a + v[0] * b, centre[1] + u[1] * a + v[1] * b];
    [corner(-hu, -hv), corner(hu, -hv), corner(hu, hv), corner(-hu, hv)]
}

/// DB post-processing of a probability map (`width` x `height`) into quads in
/// the coordinates of the original `dest_w` x `dest_h` image.
pub fn detection_boxes(bitmap: &[f32], width: usize, height: usize, dest_w: usize, dest_h: usize) -> Result<Vec<Quad>, String> {
    if bitmap.len() != width * height || bitmap.iter().any(|v| !v.is_finite()) {
        return Err("Invalid ONNX detection output".into());
    }
    let mut labels = vec![0u32; width * height];
    let mut stack: Vec<usize> = Vec::new();
    let mut boxes = Vec::new();
    let mut next = 0u32;
    for start in 0..width * height {
        if bitmap[start] <= 0.3 || labels[start] != 0 {
            continue;
        }
        next += 1;
        if next > 3000 {
            break;
        }
        // 8-connected component; its hull only needs each row's extremes.
        let mut spans: Vec<(usize, usize, usize)> = Vec::new(); // y, min x, max x
        let (mut top, mut bottom) = (usize::MAX, 0usize);
        labels[start] = next;
        stack.push(start);
        let mut rows: std::collections::BTreeMap<usize, (usize, usize)> = std::collections::BTreeMap::new();
        while let Some(index) = stack.pop() {
            let (x, y) = (index % width, index / width);
            top = top.min(y);
            bottom = bottom.max(y);
            let entry = rows.entry(y).or_insert((x, x));
            entry.0 = entry.0.min(x);
            entry.1 = entry.1.max(x);
            for ny in y.saturating_sub(1)..=(y + 1).min(height - 1) {
                for nx in x.saturating_sub(1)..=(x + 1).min(width - 1) {
                    let n = ny * width + nx;
                    if labels[n] == 0 && bitmap[n] > 0.3 {
                        labels[n] = next;
                        stack.push(n);
                    }
                }
            }
        }
        spans.extend(rows.iter().map(|(y, (a, b))| (*y, *a, *b)));
        let mut points: Vec<[f64; 2]> = Vec::with_capacity(spans.len() * 2);
        for (y, a, b) in &spans {
            points.push([*a as f64, *y as f64]);
            points.push([*b as f64, *y as f64]);
        }
        let hull = convex_hull(&mut points);
        let Some((quad, short_side)) = min_area_rect(&hull) else { continue };
        if short_side < 3.0 {
            continue;
        }
        let quad = ordered(quad);
        // Score: mean probability inside the (integer-truncated) box polygon.
        let xmin = quad.iter().map(|p| p[0]).fold(f64::MAX, f64::min).floor().max(0.0) as usize;
        let ymin = quad.iter().map(|p| p[1]).fold(f64::MAX, f64::min).floor().max(0.0) as usize;
        let xmax = (quad.iter().map(|p| p[0]).fold(f64::MIN, f64::max).ceil() as usize).min(width - 1);
        let ymax = (quad.iter().map(|p| p[1]).fold(f64::MIN, f64::max).ceil() as usize).min(height - 1);
        if xmax < xmin || ymax < ymin {
            continue;
        }
        let polygon: Vec<[f64; 2]> = quad.iter().map(|p| [(p[0] - xmin as f64).trunc(), (p[1] - ymin as f64).trunc()]).collect();
        let (mut sum, mut count) = (0f64, 0usize);
        for y in ymin..=ymax {
            for x in xmin..=xmax {
                let p = [(x - xmin) as f64, (y - ymin) as f64];
                let (mut positive, mut negative) = (false, false);
                for k in 0..4 {
                    let c = cross(polygon[k], polygon[(k + 1) % 4], p);
                    positive |= c > 1e-9;
                    negative |= c < -1e-9;
                }
                if !(positive && negative) {
                    sum += bitmap[y * width + x] as f64;
                    count += 1;
                }
            }
        }
        if count == 0 || sum / (count as f64) < 0.6 {
            continue;
        }
        let side = |a: [f64; 2], b: [f64; 2]| ((b[0] - a[0]).powi(2) + (b[1] - a[1]).powi(2)).sqrt();
        let perimeter: f64 = (0..4).map(|k| side(quad[k], quad[(k + 1) % 4])).sum();
        if perimeter <= 0.0 {
            continue;
        }
        let area = 0.5 * (0..4).map(|k| quad[k][0] * quad[(k + 1) % 4][1] - quad[(k + 1) % 4][0] * quad[k][1]).sum::<f64>().abs();
        // pyclipper receives integer coordinates.
        let truncated: Quad = [0, 1, 2, 3].map(|k| [quad[k][0].trunc(), quad[k][1].trunc()]);
        let expanded = ordered(expand(truncated, area * 1.5 / perimeter));
        let short = side(expanded[0], expanded[1]).min(side(expanded[0], expanded[3]));
        if short < 5.0 {
            continue;
        }
        let (sx, sy) = (dest_w as f64 / width as f64, dest_h as f64 / height as f64);
        boxes.push(expanded.map(|p| [round_even(p[0] * sx).clamp(0.0, dest_w as f64), round_even(p[1] * sy).clamp(0.0, dest_h as f64)]));
    }
    Ok(boxes)
}
