// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 StarChat contributors
//! PP-OCRv6 small inference on ONNX Runtime: DirectML with CPU fallback.
//!
//! DirectML specialises a session for the first input shape it sees and runs
//! every other shape through a slow path (measured: 9 ms vs 40+ ms). GPU
//! sessions are therefore created with fixed dimensions: one detector per ROI
//! size and one recogniser per padded-width bucket.
use std::collections::HashMap;
use std::path::{Path, PathBuf};

use ort::ep::directml::{DeviceFilter, PerformancePreference};
use ort::ep::{DirectML, CPU};
use ort::environment::Environment;
use ort::session::Session;
use ort::value::Tensor;

use crate::det::{detection_boxes, detection_size, detection_tensor, Quad};
use crate::image::{warp_quad, Img};
use crate::rec::{recognition_tensor, recognition_width, EnglishDecoder};

// Measured on an RTX 3070 (fixture window, 900x420 ROI, 10 rows), VRAM is the worker's peak:
//   arena off, 640 px buckets: detect 17.7 ms, 10 rows 86 ms, 179 MiB   <- default
//   arena on,  320 px buckets: detect 13.7 ms, 10 rows 58 ms, 402 MiB
//   Python worker 0.3.17:      detect 50 ms,   10 rows 721 ms, 219 MiB
// The game needs the VRAM more than OCR needs the last 4 ms. Overrides for experiments:
// STARCHAT_OCR_DML_ARENA=1, STARCHAT_OCR_REC_BUCKET, STARCHAT_OCR_REC_SESSIONS.
const REC_BUCKET: usize = 640;
const MAX_REC_SESSIONS: usize = 4;

pub struct Models {
    environment: Environment,
    root: PathBuf,
    mode: String,
    gpu: bool,
    pub device: String,
    pub fallback_reason: String,
    decoder: EnglishDecoder,
    det: Option<((usize, usize), Session)>,
    rec: HashMap<usize, (u64, Session)>,
    uses: u64,
    buffer: Vec<f32>,
    arena: bool,
    bucket: usize,
    max_sessions: usize,
}

fn character_dictionary(path: &Path) -> Result<Vec<String>, String> {
    let text = std::fs::read_to_string(path).map_err(|e| format!("{}: {e}", path.display()))?;
    let documents = yaml_rust2::YamlLoader::load_from_str(&text).map_err(|e| e.to_string())?;
    let list = documents.first().map(|d| &d["PostProcess"]["character_dict"]).and_then(|v| v.as_vec())
        .ok_or("character_dict is missing from inference.yml")?;
    Ok(list.iter().map(|v| match v {
        yaml_rust2::Yaml::String(s) => s.clone(),
        yaml_rust2::Yaml::Integer(i) => i.to_string(),
        yaml_rust2::Yaml::Real(r) => r.clone(),
        yaml_rust2::Yaml::Boolean(b) => b.to_string(),
        yaml_rust2::Yaml::Null => "~".to_string(),
        _ => String::new(),
    }).collect())
}

impl Models {
    pub fn new(environment: Environment, root: PathBuf, mode: &str) -> Result<Self, String> {
        let mode = mode.to_lowercase();
        if !["auto", "cpu", "gpu"].contains(&mode.as_str()) {
            return Err("STARCHAT_OCR_DEVICE must be auto, cpu or gpu".into());
        }
        let decoder = EnglishDecoder::new(character_dictionary(&root.join("rec").join("inference.yml"))?)?;
        let mut models = Models {
            environment, root, mode: mode.clone(), gpu: mode != "cpu", device: "cpu".into(),
            fallback_reason: String::new(), decoder, det: None, rec: HashMap::new(), uses: 0, buffer: Vec::new(),
            arena: std::env::var("STARCHAT_OCR_DML_ARENA").map_or(false, |v| v == "1"),
            bucket: std::env::var("STARCHAT_OCR_REC_BUCKET").ok().and_then(|v| v.parse().ok()).filter(|v| (32..=3200).contains(v)).unwrap_or(REC_BUCKET),
            max_sessions: std::env::var("STARCHAT_OCR_REC_SESSIONS").ok().and_then(|v| v.parse().ok()).filter(|v| *v >= 1).unwrap_or(MAX_REC_SESSIONS),
        };
        if models.gpu {
            // Probe once so an unusable GPU is reported at start-up, as before.
            match models.session("rec", &[("DynamicDimension.0", 1), ("DynamicDimension.1", models.bucket as i64)]) {
                Ok(session) => {
                    models.rec.insert(models.bucket, (0, session));
                    models.device = "DirectML GPU".into();
                }
                Err(error) => {
                    if mode == "gpu" {
                        return Err(error);
                    }
                    eprintln!("DirectML initialization failed; using CPU: {error}");
                    models.fallback_reason = error;
                    models.gpu = false;
                }
            }
        }
        if !models.gpu {
            models.load_cpu()?;
        }
        Ok(models)
    }

    fn session(&self, kind: &str, dimensions: &[(&str, i64)]) -> Result<Session, String> {
        let build = || -> ort::Result<Session> {
            let mut builder = Session::builder(&self.environment)?
                .with_parallel_execution(false)?
                .with_memory_pattern(false)? // Required by DirectML.
                .with_intra_threads(2)?
                .with_inter_threads(1)?
                .with_intra_op_spinning(false)?
                .with_inter_op_spinning(false)?
                .with_config_entry("ep.dml.enable_cpu_sync_spinning", "0")?
                .with_config_entry("ep.dml.disable_memory_arena", if self.arena { "0" } else { "1" })?;
            if self.gpu {
                for (name, size) in dimensions {
                    builder = builder.with_dimension_override(name, *size)?;
                }
                builder = builder.with_execution_providers([
                    DirectML::default()
                        .with_performance_preference(PerformancePreference::HighPerformance)
                        .with_device_filter(DeviceFilter::Gpu)
                        .build()
                        .error_on_failure(),
                    CPU::default().build(),
                ])?;
            } else {
                builder = builder.with_execution_providers([CPU::default().build()])?;
            }
            builder.commit_from_file(self.root.join(kind).join("inference.onnx"))
        };
        build().map_err(|e| e.to_string())
    }

    fn load_cpu(&mut self) -> Result<(), String> {
        self.gpu = false;
        self.det = None;
        self.rec.clear();
        self.det = Some(((0, 0), self.session("det", &[])?));
        let session = self.session("rec", &[])?;
        self.rec.insert(0, (0, session));
        self.device = "cpu".into();
        Ok(())
    }

    /// A driver/device loss changes both models once; the caller keeps its tracker.
    fn fall_back(&mut self, error: String) -> Result<(), String> {
        if self.mode != "auto" || !self.gpu {
            return Err(error);
        }
        eprintln!("DirectML execution failed; using CPU: {error}");
        self.fallback_reason = error;
        self.load_cpu()
    }

    fn run_det(&mut self, w: usize, h: usize) -> Result<(Vec<f32>, usize, usize), String> {
        let key = if self.gpu { (w, h) } else { (0, 0) };
        if self.det.as_ref().map(|d| d.0) != Some(key) {
            self.det = None; // Release the previous ROI's session first.
            let session = self.session("det", &[("DynamicDimension.0", 1), ("DynamicDimension.1", h as i64), ("DynamicDimension.2", w as i64)])?;
            self.det = Some((key, session));
        }
        let tensor = Tensor::from_array(([1usize, 3, h, w], std::mem::take(&mut self.buffer))).map_err(|e| e.to_string())?;
        let session = &mut self.det.as_mut().unwrap().1;
        let outputs = session.run(ort::inputs![tensor]).map_err(|e| e.to_string())?;
        let (shape, values) = outputs[0].try_extract_tensor::<f32>().map_err(|e| e.to_string())?;
        if shape.len() != 4 {
            return Err("Invalid ONNX detection output".into());
        }
        Ok((values.to_vec(), shape[3] as usize, shape[2] as usize))
    }

    /// Detect boxes only; the recognition session is not executed here.
    pub fn detect(&mut self, image: &Img) -> Result<Vec<Quad>, String> {
        let mut buffer = std::mem::take(&mut self.buffer);
        let (w, h) = detection_tensor(image, &mut buffer);
        debug_assert_eq!((w, h), detection_size(image.w, image.h));
        self.buffer = buffer;
        let (bitmap, bw, bh) = match self.run_det(w, h) {
            Ok(result) => result,
            Err(error) => {
                self.fall_back(error)?;
                let mut buffer = std::mem::take(&mut self.buffer);
                detection_tensor(image, &mut buffer);
                self.buffer = buffer;
                self.run_det(w, h)?
            }
        };
        detection_boxes(&bitmap[..bw * bh], bw, bh, image.w, image.h)
    }

    fn run_rec(&mut self, crop: &Img) -> Result<(String, f64), String> {
        let natural = recognition_width(crop);
        let (key, width) = if self.gpu {
            let bucket = (natural.div_ceil(self.bucket) * self.bucket).max(320);
            (bucket, bucket)
        } else {
            (0, natural)
        };
        self.uses += 1;
        if !self.rec.contains_key(&key) {
            if self.rec.len() >= self.max_sessions {
                if let Some(oldest) = self.rec.iter().min_by_key(|(_, v)| v.0).map(|(k, _)| *k) {
                    self.rec.remove(&oldest);
                }
            }
            let session = self.session("rec", &[("DynamicDimension.0", 1), ("DynamicDimension.1", width as i64)])?;
            self.rec.insert(key, (self.uses, session));
        }
        let mut buffer = std::mem::take(&mut self.buffer);
        recognition_tensor(crop, width, &mut buffer);
        let tensor = Tensor::from_array(([1usize, 3, 48, width], buffer)).map_err(|e| e.to_string())?;
        let entry = self.rec.get_mut(&key).unwrap();
        entry.0 = self.uses;
        let outputs = entry.1.run(ort::inputs![tensor]).map_err(|e| e.to_string())?;
        let (shape, values) = outputs[0].try_extract_tensor::<f32>().map_err(|e| e.to_string())?;
        if shape.len() != 3 || shape[0] != 1 {
            return Err("ONNX recognition vocabulary/shape mismatch".into());
        }
        self.decoder.decode(values, shape[1] as usize, shape[2] as usize)
    }

    /// Rectify each row quad and read it with the English recogniser.
    pub fn recognize(&mut self, image: &Img, quads: &[Quad]) -> Result<Vec<(String, f64)>, String> {
        let mut results = Vec::with_capacity(quads.len());
        for q in quads {
            let length = |a: [f64; 2], b: [f64; 2]| ((b[0] - a[0]).powi(2) + (b[1] - a[1]).powi(2)).sqrt();
            let w = (crate::image::round_even(length(q[0], q[1])) as usize).clamp(4, 8192);
            let h = (crate::image::round_even(length(q[0], q[3])) as usize).clamp(4, 1024);
            let crop = warp_quad(image, q[0], q[1], q[3], w, h, true);
            let result = match self.run_rec(&crop) {
                Ok(result) => result,
                Err(error) => {
                    self.fall_back(error)?;
                    self.run_rec(&crop)?
                }
            };
            results.push(result);
        }
        Ok(results)
    }
}
