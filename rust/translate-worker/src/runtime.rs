// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 StarChat contributors
//! Pinned, consent-only model installation. Translation itself never downloads.
use std::fs::{self, File};
use std::io::{Read, Write};
use std::os::windows::ffi::OsStrExt;
use std::os::windows::fs::OpenOptionsExt;
use std::path::{Path, PathBuf};

use serde_json::{json, Map, Value};
use sha2::{Digest, Sha256};

const FORMAT: i64 = 2;

#[link(name = "kernel32")]
extern "system" {
    fn GetDiskFreeSpaceExW(path: *const u16, available: *mut u64, total: *mut u64, free: *mut u64) -> i32;
}

pub fn model_root() -> PathBuf {
    match std::env::var_os("STARCHAT_LOCAL_MODEL_ROOT") {
        Some(root) if !root.is_empty() => PathBuf::from(root),
        _ => PathBuf::from(std::env::var_os("LOCALAPPDATA").unwrap_or_default()).join("StarChat").join("models").join("nllb-200-distilled-600m-ct2-int8"),
    }
}

/// model.json sits beside the executable (the `translation` folder).
pub fn manifest() -> Result<Value, String> {
    let directory = std::env::current_exe().ok().and_then(|p| p.parent().map(Path::to_path_buf)).unwrap_or_default();
    let text = fs::read_to_string(directory.join("model.json")).map_err(|e| e.to_string())?;
    serde_json::from_str(&text).map_err(|e| e.to_string())
}

fn digest(path: &Path) -> std::io::Result<String> {
    let mut file = File::open(path)?;
    let mut hasher = Sha256::new();
    let mut block = vec![0u8; 1024 * 1024];
    loop {
        let read = file.read(&mut block)?;
        if read == 0 {
            break;
        }
        hasher.update(&block[..read]);
    }
    Ok(hasher.finalize().iter().map(|b| format!("{b:02x}")).collect())
}

fn files(manifest: &Value) -> Map<String, Value> {
    manifest.get("files").and_then(Value::as_object).cloned().unwrap_or_default()
}

pub fn installed(manifest: &Value, root: &Path, verify: bool) -> bool {
    let check = || -> Option<bool> {
        let stamp: Value = serde_json::from_str(&fs::read_to_string(root.join("installed.json")).ok()?).ok()?;
        if stamp.get("format")?.as_i64()? != FORMAT || stamp.get("revision")? != manifest.get("revision")? || stamp.get("engine")?.as_str()? != "ctranslate2" {
            return Some(false);
        }
        let expected = files(manifest);
        let recorded = stamp.get("files")?.as_object()?;
        if recorded.len() != expected.len() || !recorded.keys().all(|k| expected.contains_key(k)) {
            return Some(false);
        }
        for (name, info) in recorded {
            if expected.get(name)? != info {
                return Some(false);
            }
            let path = root.join(name);
            if !path.is_file() || fs::metadata(&path).ok()?.len() != info.get("bytes")?.as_u64()? {
                return Some(false);
            }
            if verify && digest(&path).ok()? != info.get("sha256")?.as_str()? {
                return Some(false);
            }
        }
        Some(true)
    };
    check().unwrap_or(false)
}

fn free_space(path: &Path) -> u64 {
    let wide: Vec<u16> = path.as_os_str().encode_wide().chain(std::iter::once(0)).collect();
    let mut available = 0u64;
    if unsafe { GetDiskFreeSpaceExW(wide.as_ptr(), &mut available, std::ptr::null_mut(), std::ptr::null_mut()) } == 0 {
        return u64::MAX;
    }
    available
}

/// Errors are the short codes the host translates for the user.
pub fn install(progress: &mut dyn FnMut(Value)) -> Result<(), String> {
    let manifest = manifest().map_err(|_| "install_failed")?;
    let root = model_root();
    if installed(&manifest, &root, true) {
        progress(json!({ "type": "done" }));
        return Ok(());
    }
    fs::create_dir_all(&root).map_err(|_| "install_failed")?;
    // An exclusively opened file is released by the OS even when the cancel button kills us.
    let _lock = fs::OpenOptions::new().create(true).append(true).share_mode(0).open(root.join(".install.lock")).map_err(|_| "install_failed")?;
    install_locked(&manifest, &root, progress)
}

fn install_locked(manifest: &Value, root: &Path, progress: &mut dyn FnMut(Value)) -> Result<(), String> {
    fn other<E>(_: E) -> String {
        "install_failed".to_string()
    }
    let staging = root.join(".download");
    fs::create_dir_all(&staging).map_err(other)?;
    let expected = files(manifest);
    let size = |info: &Value| info.get("bytes").and_then(Value::as_u64).unwrap_or(0);
    let hash = |info: &Value| info.get("sha256").and_then(Value::as_str).unwrap_or("").to_string();
    let total: u64 = expected.values().map(size).sum();
    let required: u64 = expected.iter().filter(|(name, _)| !staging.join(name).is_file()).map(|(_, info)| size(info)).sum::<u64>() + 32 * 1024 * 1024;
    if free_space(root) < required {
        return Err("disk_space".into());
    }
    // Tests may point this at a local server; the default is the pinned repository revision.
    let base = std::env::var("STARCHAT_MODEL_BASE_URL").unwrap_or_else(|_| format!(
        "https://huggingface.co/{}/resolve/{}", manifest["repository"].as_str().unwrap_or(""), manifest["revision"].as_str().unwrap_or("")));
    let agent = ureq::AgentBuilder::new().timeout_connect(std::time::Duration::from_secs(60)).timeout_read(std::time::Duration::from_secs(60)).build();
    let mut completed = 0u64;
    for (name, info) in &expected {
        let path = staging.join(name);
        let ready = path.is_file() && fs::metadata(&path).map(|m| m.len()).unwrap_or(0) == size(info) && digest(&path).ok().as_deref() == Some(hash(info).as_str());
        if !ready {
            let part = staging.join(format!("{name}.part"));
            let response = agent.get(&format!("{base}/{name}")).set("User-Agent", "StarChat-ModelInstaller/1.0").call().map_err(other)?;
            let mut reader = response.into_reader();
            let mut output = File::create(&part).map_err(other)?;
            let mut block = vec![0u8; 1024 * 1024];
            let mut read = 0u64;
            loop {
                let count = reader.read(&mut block).map_err(other)?;
                if count == 0 {
                    break;
                }
                read += count as u64;
                if read > size(info) {
                    return Err("download_size".into());
                }
                output.write_all(&block[..count]).map_err(other)?;
                progress(json!({ "type": "progress", "bytes": completed + read, "total": total }));
            }
            output.flush().map_err(other)?;
            drop(output);
            if read != size(info) || digest(&part).map_err(other)? != hash(info) {
                return Err("download_checksum".into());
            }
            fs::rename(&part, &path).map_err(other)?;
        }
        completed += size(info);
        progress(json!({ "type": "progress", "bytes": completed, "total": total }));
    }
    progress(json!({ "type": "preparing" }));
    let mut inventory = Map::new();
    for name in expected.keys() {
        let (source, destination) = (staging.join(name), root.join(name));
        inventory.insert(name.clone(), json!({ "bytes": fs::metadata(&source).map_err(other)?.len(), "sha256": digest(&source).map_err(other)? }));
        if destination.exists() {
            fs::remove_file(&destination).map_err(other)?;
        }
        fs::rename(&source, &destination).map_err(other)?;
    }
    let stamp = json!({ "format": FORMAT, "revision": manifest["revision"], "engine": "ctranslate2", "files": inventory });
    let temporary = root.join("installed.json.new");
    fs::write(&temporary, serde_json::to_string_pretty(&stamp).map_err(|e| e.to_string())? + "\n").map_err(other)?;
    let published = root.join("installed.json");
    if published.exists() {
        fs::remove_file(&published).map_err(other)?;
    }
    fs::rename(&temporary, &published).map_err(other)?; // Publish only after every file passes.
    progress(json!({ "type": "done" }));
    Ok(())
}
