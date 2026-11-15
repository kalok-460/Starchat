// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 StarChat contributors
//! Small image kernels that replace the OpenCV calls of the Python worker:
//! colour CLAHE on Lab lightness, bilinear resize and quad rectification.
use std::sync::OnceLock;

/// Interleaved 8-bit image. `c` is 3 (BGR) or 1 (gray).
#[derive(Clone)]
pub struct Img {
    pub w: usize,
    pub h: usize,
    pub c: usize,
    pub data: Vec<u8>,
}

impl Img {
    pub fn new(w: usize, h: usize, c: usize) -> Self {
        Img { w, h, c, data: vec![0; w * h * c] }
    }
}

/// BGRA rows with an arbitrary pitch -> packed BGR.
pub fn bgra_to_bgr(src: &[u8], pitch: usize, w: usize, h: usize) -> Img {
    let mut out = Img::new(w, h, 3);
    for y in 0..h {
        let row = &src[y * pitch..y * pitch + w * 4];
        let dst = &mut out.data[y * w * 3..(y + 1) * w * 3];
        for (d, s) in dst.chunks_exact_mut(3).zip(row.chunks_exact(4)) {
            d[0] = s[0];
            d[1] = s[1];
            d[2] = s[2];
        }
    }
    out
}

/// BT.601 luma, the weights of cv2.COLOR_BGR2GRAY.
pub fn gray(image: &Img) -> Img {
    let mut out = Img::new(image.w, image.h, 1);
    for (d, s) in out.data.iter_mut().zip(image.data.chunks_exact(3)) {
        *d = ((s[0] as u32 * 1868 + s[1] as u32 * 9617 + s[2] as u32 * 4899 + 8192) >> 14) as u8;
    }
    out
}

struct LabTables {
    /// White-normalised X, Y, Z contribution of each B, G, R byte value.
    xyz: [[[f32; 256]; 3]; 3],
    cbrt: Vec<f32>, // Lab f(t), t in [0, 1]
    gamma: Vec<u8>, // linear [0,1] -> sRGB byte
}

const CBRT_N: usize = 1 << 14;
const GAMMA_N: usize = 1 << 14;

fn tables() -> &'static LabTables {
    static TABLES: OnceLock<LabTables> = OnceLock::new();
    TABLES.get_or_init(|| {
        // Rows: X, Y, Z. Columns: B, G, R (sRGB D65, as in OpenCV).
        const MATRIX: [[f32; 3]; 3] = [
            [0.180423 / 0.950456, 0.357580 / 0.950456, 0.412453 / 0.950456],
            [0.072169, 0.715160, 0.212671],
            [0.950227 / 1.088754, 0.119193 / 1.088754, 0.019334 / 1.088754],
        ];
        let mut xyz = [[[0f32; 256]; 3]; 3];
        for value in 0..256 {
            let x = value as f32 / 255.0;
            let linear = if x <= 0.04045 { x / 12.92 } else { ((x + 0.055) / 1.055).powf(2.4) };
            for axis in 0..3 {
                for channel in 0..3 {
                    xyz[axis][channel][value] = MATRIX[axis][channel] * linear;
                }
            }
        }
        let cbrt = (0..=CBRT_N)
            .map(|i| {
                let t = i as f32 / CBRT_N as f32;
                if t > 0.008856 { t.cbrt() } else { 7.787 * t + 16.0 / 116.0 }
            })
            .collect();
        let gamma = (0..=GAMMA_N)
            .map(|i| {
                let x = i as f32 / GAMMA_N as f32;
                let v = if x <= 0.0031308 { x * 12.92 } else { 1.055 * x.powf(1.0 / 2.4) - 0.055 };
                (v * 255.0 + 0.5).clamp(0.0, 255.0) as u8
            })
            .collect();
        LabTables { xyz, cbrt, gamma }
    })
}

#[inline]
fn lab_f(t: f32, table: &[f32]) -> f32 {
    table[((t * CBRT_N as f32 + 0.5) as usize).min(CBRT_N)]
}

#[inline]
fn lab_f_inv(f: f32) -> f32 {
    if f > 0.206893 { f * f * f } else { (f - 16.0 / 116.0) / 7.787 }
}

/// OpenCV's CLAHE(clipLimit=2, tileGridSize=8x8) on one 8-bit plane.
fn clahe(plane: &[u8], w: usize, h: usize) -> Vec<u8> {
    const TILES: usize = 8;
    let pad_w = (TILES - w % TILES) % TILES;
    let pad_h = (TILES - h % TILES) % TILES;
    let (pw, ph) = (w + pad_w, h + pad_h);
    let (tw, th) = (pw / TILES, ph / TILES);
    // BORDER_REFLECT_101 for the padded right/bottom strips.
    let reflect = |i: usize, n: usize| if i < n { i } else { (2 * n).saturating_sub(i + 2).min(n - 1) };
    let area = (tw * th) as i64;
    let clip = ((2.0 * area as f64 / 256.0) as i64).max(1);
    let scale = 255.0f32 / area as f32;
    let mut luts = vec![0u8; TILES * TILES * 256];
    for ty in 0..TILES {
        for tx in 0..TILES {
            let mut hist = [0i64; 256];
            for y in ty * th..(ty + 1) * th {
                let sy = reflect(y, h);
                for x in tx * tw..(tx + 1) * tw {
                    hist[plane[sy * w + reflect(x, w)] as usize] += 1;
                }
            }
            let mut clipped = 0i64;
            for v in hist.iter_mut() {
                if *v > clip {
                    clipped += *v - clip;
                    *v = clip;
                }
            }
            let batch = clipped / 256;
            let mut residual = clipped - batch * 256;
            for v in hist.iter_mut() {
                *v += batch;
            }
            if residual > 0 {
                let step = ((256 / residual) as usize).max(1);
                let mut i = 0;
                while i < 256 && residual > 0 {
                    hist[i] += 1;
                    residual -= 1;
                    i += step;
                }
            }
            let lut = &mut luts[(ty * TILES + tx) * 256..(ty * TILES + tx + 1) * 256];
            let mut sum = 0i64;
            for (i, v) in hist.iter().enumerate() {
                sum += *v;
                lut[i] = (sum as f32 * scale).round().clamp(0.0, 255.0) as u8;
            }
        }
    }
    // Per-column interpolation terms are shared by every row.
    let (inv_tw, inv_th) = (1.0 / tw as f32, 1.0 / th as f32);
    let columns: Vec<(usize, usize, f32)> = (0..w)
        .map(|x| {
            let f = x as f32 * inv_tw - 0.5;
            let t1 = f.floor();
            let a = f - t1;
            let lo = (t1 as i64).clamp(0, TILES as i64 - 1) as usize;
            let hi = (t1 as i64 + 1).clamp(0, TILES as i64 - 1) as usize;
            (lo, hi, a)
        })
        .collect();
    let mut out = vec![0u8; w * h];
    for y in 0..h {
        let f = y as f32 * inv_th - 0.5;
        let t1 = f.floor();
        let ya = f - t1;
        let y1 = (t1 as i64).clamp(0, TILES as i64 - 1) as usize;
        let y2 = (t1 as i64 + 1).clamp(0, TILES as i64 - 1) as usize;
        let (top, bottom) = (&luts[y1 * TILES * 256..], &luts[y2 * TILES * 256..]);
        let row = &plane[y * w..(y + 1) * w];
        let dst = &mut out[y * w..(y + 1) * w];
        for x in 0..w {
            let (x1, x2, xa) = columns[x];
            let v = row[x] as usize;
            let a = top[x1 * 256 + v] as f32;
            let b = top[x2 * 256 + v] as f32;
            let c = bottom[x1 * 256 + v] as f32;
            let d = bottom[x2 * 256 + v] as f32;
            let value = (a * (1.0 - xa) + b * xa) * (1.0 - ya) + (c * (1.0 - xa) + d * xa) * ya;
            dst[x] = (value + 0.5).clamp(0.0, 255.0) as u8;
        }
    }
    out
}

/// The game's chat colours as B, G, R scaled so the largest channel is 1:
/// global #FFFFFF, party #00FF4E, whisper #7E3EFF, channel label #8DCCFF.
const CHAT_COLOURS: [[f32; 3]; 4] = [
    [1.0, 1.0, 1.0],
    [78.0 / 255.0, 1.0, 0.0],
    [1.0, 62.0 / 255.0, 126.0 / 255.0],
    [1.0, 204.0 / 255.0, 141.0 / 255.0],
];
const COLOUR_FLOOR: f32 = 0.2;

/// Brightness kept by a pixel: 1 when its hue is one of the chat colours (at any
/// brightness), falling with the cube of the distance to the nearest of them,
/// never below `COLOUR_FLOOR`.
///
/// A lamp-lit panel behind the chat is as bright as the text, so lightness
/// alone cannot separate them; its colour can. Measured on a recording of white
/// chat over a lit orange panel with the background pushed 45% brighter: 4 of
/// 90 lines read with plain CLAHE, 58 with a white-only weight, 73 with this
/// one. Translucent party/whisper text over sky or foliage is only read with
/// the palette weight.
#[inline]
fn chat_weight(b: u8, g: u8, r: u8) -> f32 {
    let max = b.max(g).max(r);
    if max == 0 {
        return COLOUR_FLOOR;
    }
    let scale = 1.0 / max as f32;
    let (b, g, r) = (b as f32 * scale, g as f32 * scale, r as f32 * scale);
    let mut nearest = 1.0f32;
    for colour in &CHAT_COLOURS {
        let distance = (b - colour[0]).abs().max((g - colour[1]).abs()).max((r - colour[2]).abs());
        nearest = nearest.min(distance);
    }
    let closeness = 1.0 - nearest;
    (closeness * closeness * closeness).max(COLOUR_FLOOR)
}

/// Dim pixels that are not chat-coloured (see `chat_weight`); hue is unchanged.
pub fn suppress_background(image: &Img) -> Img {
    let mut out = Img::new(image.w, image.h, 3);
    for (d, s) in out.data.chunks_exact_mut(3).zip(image.data.chunks_exact(3)) {
        let w = chat_weight(s[0], s[1], s[2]);
        d[0] = (s[0] as f32 * w + 0.5) as u8;
        d[1] = (s[1] as f32 * w + 0.5) as u8;
        d[2] = (s[2] as f32 * w + 0.5) as u8;
    }
    out
}

/// Text strength for the motion follower: brightest channel x chat weight, so
/// white, green, purple and sky-blue glyphs are all bright strokes.
pub fn gray_chat(image: &Img) -> Img {
    let mut out = Img::new(image.w, image.h, 1);
    for (d, s) in out.data.iter_mut().zip(image.data.chunks_exact(3)) {
        *d = (s[0].max(s[1]).max(s[2]) as f32 * chat_weight(s[0], s[1], s[2]) + 0.5) as u8;
    }
    out
}

/// Background suppression, then BGR -> Lab, CLAHE on lightness only, back to BGR.
pub fn preprocess(image: &Img) -> Img {
    let suppressed = suppress_background(image);
    let image = &suppressed;
    let t = tables();
    let n = image.w * image.h;
    let mut light = vec![0u8; n];
    let mut fa = vec![0f32; n]; // fX - fY
    let mut fb = vec![0f32; n]; // fY - fZ
    for (i, p) in image.data.chunks_exact(3).enumerate() {
        let (b, g, r) = (p[0] as usize, p[1] as usize, p[2] as usize);
        let fx = lab_f(t.xyz[0][0][b] + t.xyz[0][1][g] + t.xyz[0][2][r], &t.cbrt);
        let fy = lab_f(t.xyz[1][0][b] + t.xyz[1][1][g] + t.xyz[1][2][r], &t.cbrt);
        let fz = lab_f(t.xyz[2][0][b] + t.xyz[2][1][g] + t.xyz[2][2][r], &t.cbrt);
        // 116 f(Y) - 16 also covers the linear segment (903.3 Y).
        light[i] = ((116.0 * fy - 16.0) * 2.55 + 0.5).clamp(0.0, 255.0) as u8;
        fa[i] = fx - fy;
        fb[i] = fy - fz;
    }
    let equalized = clahe(&light, image.w, image.h);
    let mut out = Img::new(image.w, image.h, 3);
    let gscale = GAMMA_N as f32;
    for (i, p) in out.data.chunks_exact_mut(3).enumerate() {
        if equalized[i] == light[i] {
            p.copy_from_slice(&image.data[i * 3..i * 3 + 3]);
            continue;
        }
        let l = equalized[i] as f32 / 2.55;
        let fy = (l + 16.0) / 116.0;
        let y = if l > 7.9996 { fy * fy * fy } else { l / 903.3 };
        let fy = if l > 7.9996 { fy } else { 7.787 * y + 16.0 / 116.0 };
        let x = lab_f_inv(fy + fa[i]) * 0.950456;
        let z = lab_f_inv(fy - fb[i]) * 1.088754;
        let r = 3.240479 * x - 1.537150 * y - 0.498535 * z;
        let g = -0.969256 * x + 1.875991 * y + 0.041556 * z;
        let b = 0.055648 * x - 0.204043 * y + 1.057311 * z;
        p[0] = t.gamma[(b.clamp(0.0, 1.0) * gscale) as usize];
        p[1] = t.gamma[(g.clamp(0.0, 1.0) * gscale) as usize];
        p[2] = t.gamma[(r.clamp(0.0, 1.0) * gscale) as usize];
    }
    out
}

/// cv2.resize(..., INTER_LINEAR): half-pixel centres, edge clamp, no antialiasing.
pub fn resize(image: &Img, nw: usize, nh: usize) -> Img {
    if nw == image.w && nh == image.h {
        return image.clone();
    }
    let c = image.c;
    let mut out = Img::new(nw, nh, c);
    let axis = |n: usize, m: usize| -> Vec<(usize, usize, f32)> {
        let scale = m as f32 / n as f32;
        (0..n)
            .map(|d| {
                let f = ((d as f32 + 0.5) * scale - 0.5).max(0.0);
                let i = (f as usize).min(m - 1);
                (i, (i + 1).min(m - 1), f - i as f32)
            })
            .collect()
    };
    let xs = axis(nw, image.w);
    let ys = axis(nh, image.h);
    for (dy, &(y0, y1, fy)) in ys.iter().enumerate() {
        let top = &image.data[y0 * image.w * c..];
        let bottom = &image.data[y1 * image.w * c..];
        let dst = &mut out.data[dy * nw * c..(dy + 1) * nw * c];
        for (dx, &(x0, x1, fx)) in xs.iter().enumerate() {
            for k in 0..c {
                let a = top[x0 * c + k] as f32;
                let b = top[x1 * c + k] as f32;
                let cc = bottom[x0 * c + k] as f32;
                let d = bottom[x1 * c + k] as f32;
                let v = (a + (b - a) * fx) * (1.0 - fy) + (cc + (d - cc) * fx) * fy;
                dst[dx * c + k] = (v + 0.5) as u8;
            }
        }
    }
    out
}

/// Rectify the parallelogram (q0 -> q1 along x, q0 -> q3 along y) to w x h.
/// `replicate` selects BORDER_REPLICATE, otherwise outside pixels are black.
pub fn warp_quad(image: &Img, q0: [f64; 2], q1: [f64; 2], q3: [f64; 2], w: usize, h: usize, replicate: bool) -> Img {
    let c = image.c;
    let mut out = Img::new(w, h, c);
    let ux = [(q1[0] - q0[0]) / (w.max(2) - 1) as f64, (q1[1] - q0[1]) / (w.max(2) - 1) as f64];
    let vy = [(q3[0] - q0[0]) / (h.max(2) - 1) as f64, (q3[1] - q0[1]) / (h.max(2) - 1) as f64];
    let (iw, ih) = (image.w as i64, image.h as i64);
    let fetch = |x: i64, y: i64, k: usize| -> f32 {
        if replicate {
            image.data[(y.clamp(0, ih - 1) * iw + x.clamp(0, iw - 1)) as usize * c + k] as f32
        } else if x < 0 || y < 0 || x >= iw || y >= ih {
            0.0
        } else {
            image.data[(y * iw + x) as usize * c + k] as f32
        }
    };
    for dy in 0..h {
        let bx = q0[0] + vy[0] * dy as f64;
        let by = q0[1] + vy[1] * dy as f64;
        for dx in 0..w {
            let sx = bx + ux[0] * dx as f64;
            let sy = by + ux[1] * dx as f64;
            let (x0, y0) = (sx.floor(), sy.floor());
            let (fx, fy) = ((sx - x0) as f32, (sy - y0) as f32);
            let (x0, y0) = (x0 as i64, y0 as i64);
            let dst = &mut out.data[(dy * w + dx) * c..(dy * w + dx + 1) * c];
            if x0 >= 0 && y0 >= 0 && x0 + 1 < iw && y0 + 1 < ih {
                let p = (y0 * iw + x0) as usize * c;
                let q = p + image.w * c;
                for k in 0..c {
                    let a = image.data[p + k] as f32;
                    let b = image.data[p + c + k] as f32;
                    let cc = image.data[q + k] as f32;
                    let d = image.data[q + c + k] as f32;
                    dst[k] = ((a + (b - a) * fx) * (1.0 - fy) + (cc + (d - cc) * fx) * fy + 0.5) as u8;
                }
            } else {
                for k in 0..c {
                    let a = fetch(x0, y0, k);
                    let b = fetch(x0 + 1, y0, k);
                    let cc = fetch(x0, y0 + 1, k);
                    let d = fetch(x0 + 1, y0 + 1, k);
                    dst[k] = ((a + (b - a) * fx) * (1.0 - fy) + (cc + (d - cc) * fx) * fy + 0.5) as u8;
                }
            }
        }
    }
    out
}

/// Python's round(): ties go to the even integer.
pub fn round_even(v: f64) -> f64 {
    let r = v.round();
    if (v - v.trunc()).abs() == 0.5 && r % 2.0 != 0.0 { r - v.signum() } else { r }
}
