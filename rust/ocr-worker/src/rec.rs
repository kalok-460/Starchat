// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 StarChat contributors
//! PP-OCRv6 recognition tensor and the English-only CTC decoder.
use crate::image::{resize, Img};

/// Natural network width of a crop (the Python worker's `width`).
pub fn recognition_width(crop: &Img) -> usize {
    ((48.0 * crop.w as f64 / crop.h as f64) as usize).clamp(320, 3200)
}

/// (1, 3, 48, `width`) tensor; the resized crop is left-aligned, rest is zero.
pub fn recognition_tensor(crop: &Img, width: usize, out: &mut Vec<f32>) {
    let natural = recognition_width(crop);
    let resized_width = natural.min((48.0 * crop.w as f64 / crop.h as f64).ceil() as usize).max(1).min(width);
    let resized = resize(crop, resized_width, 48);
    let plane = 48 * width;
    out.clear();
    out.resize(plane * 3, 0.0);
    for y in 0..48 {
        for x in 0..resized_width {
            let p = &resized.data[(y * resized_width + x) * 3..];
            for k in 0..3 {
                out[k * plane + y * width + x] = (p[k] as f32 / 255.0 - 0.5) / 0.5;
            }
        }
    }
}

/// Mask before argmax; keep original probabilities rather than renormalizing.
pub struct EnglishDecoder {
    pub characters: Vec<String>,
    allowed: Vec<usize>,
}

impl EnglishDecoder {
    pub fn new(dictionary: Vec<String>) -> Result<Self, String> {
        let mut characters = Vec::with_capacity(dictionary.len() + 2);
        characters.push("blank".to_string());
        characters.extend(dictionary);
        characters.push(" ".to_string());
        let mut allowed = vec![0usize];
        allowed.extend(characters.iter().enumerate().skip(1)
            .filter(|(_, c)| !c.is_empty() && c.chars().all(|ch| (32..=126).contains(&(ch as u32))))
            .map(|(i, _)| i));
        let required = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        if !required.chars().all(|c| characters.iter().any(|x| x.len() == 1 && x.starts_with(c))) {
            return Err("English CTC vocabulary is unavailable".into());
        }
        Ok(EnglishDecoder { characters, allowed })
    }

    /// `values` is (steps, classes) row-major.
    pub fn decode(&self, values: &[f32], steps: usize, classes: usize) -> Result<(String, f64), String> {
        if classes != self.characters.len() || values.len() != steps * classes {
            return Err("ONNX recognition vocabulary/shape mismatch".into());
        }
        let mut text = String::new();
        let (mut sum, mut count, mut previous) = (0f64, 0usize, usize::MAX);
        for step in 0..steps {
            let row = &values[step * classes..(step + 1) * classes];
            let (mut best, mut best_value) = (0usize, f32::NEG_INFINITY);
            for &index in &self.allowed {
                let v = row[index];
                if !v.is_finite() {
                    return Err("Invalid ONNX recognition output".into());
                }
                if v > best_value {
                    best_value = v;
                    best = index;
                }
            }
            if best != previous && best != 0 {
                text.push_str(&self.characters[best]);
                sum += best_value as f64;
                count += 1;
            }
            previous = best;
        }
        Ok((text, if count > 0 { sum / count as f64 } else { 0.0 }))
    }
}
