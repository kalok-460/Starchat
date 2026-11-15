// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 StarChat contributors
//! StarChat local translation worker: one JSON request per stdin line, one
//! JSON reply per stdout line. `--install` downloads the pinned model.
mod engine;
mod runtime;

use std::io::{BufRead, Write};

use serde_json::{json, Value};

fn emit(value: Value) {
    let mut out = std::io::stdout().lock();
    let _ = writeln!(out, "{value}").and_then(|_| out.flush());
}

fn main() {
    // Bound native pools before CTranslate2 starts; yield while idle.
    for (key, value) in [("OMP_NUM_THREADS", "4"), ("MKL_NUM_THREADS", "4"), ("OPENBLAS_NUM_THREADS", "1"), ("OMP_WAIT_POLICY", "PASSIVE"), ("KMP_BLOCKTIME", "0")] {
        std::env::set_var(key, value);
    }
    if std::env::args().any(|a| a == "--install") {
        if let Err(code) = runtime::install(&mut |message| emit(message)) {
            // Never echo server responses, source texts, credentials or URLs.
            let code = if ["disk_space", "download_size", "download_checksum"].contains(&code.as_str()) { code } else { "install_failed".into() };
            emit(json!({ "type": "error", "code": code }));
            std::process::exit(1);
        }
        return;
    }
    let installed = runtime::manifest().map(|m| runtime::installed(&m, &runtime::model_root(), false)).unwrap_or(false);
    if !installed {
        emit(json!({ "type": "error", "code": "not_installed" }));
        std::process::exit(1);
    }
    let mut translator: Option<engine::Engine> = None;
    let mut input = std::io::stdin().lock();
    let mut bytes = Vec::new();
    loop {
        bytes.clear();
        match input.read_until(b'\n', &mut bytes) {
            Ok(0) | Err(_) => break,
            Ok(_) => {}
        }
        let line = String::from_utf8_lossy(&bytes);
        let line = line.trim_start_matches('\u{feff}').trim();
        if line.is_empty() {
            continue;
        }
        let field = |request: &Value, key: &str| request.get(key).and_then(Value::as_str).map(str::to_string);
        let reply = match serde_json::from_str::<Value>(line).ok().and_then(|r| Some((field(&r, "text")?, field(&r, "source")?, field(&r, "target")?))) {
            None => json!({ "type": "error", "code": "inference_failed" }),
            Some((text, source, target)) => {
                if translator.is_none() {
                    match engine::Engine::new(&runtime::model_root()) {
                        Ok(created) => translator = Some(created),
                        Err(error) => {
                            eprintln!("{error}");
                            emit(json!({ "type": "error", "code": "runtime_missing" }));
                            continue;
                        }
                    }
                }
                let translator = translator.as_mut().unwrap();
                match translator.translate(&text, &source, &target) {
                    Ok(result) => json!({ "type": "result", "text": result, "backend": translator.backend }),
                    Err(engine::Failure::Language) => json!({ "type": "error", "code": "language" }),
                    Err(engine::Failure::TooLong) => json!({ "type": "error", "code": "too_long" }),
                    Err(engine::Failure::Other(error)) => {
                        eprintln!("{error}");
                        json!({ "type": "error", "code": "inference_failed" })
                    }
                }
            }
        };
        emit(reply);
    }
}
