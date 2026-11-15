// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 StarChat contributors
//! Chat-line text rules: header parsing, normalisation and fuzzy identity.
use regex::Regex;
use std::collections::HashMap;
use std::sync::OnceLock;

pub fn header() -> &'static Regex {
    static HEADER: OnceLock<Regex> = OnceLock::new();
    HEADER.get_or_init(|| Regex::new(r"^\s*(?:\[([^\]]*)\]\s*)?([A-Za-z0-9_.-]{1,80})\s*:\s*(.*)$").unwrap())
}

const KNOWN_CHANNELS: [&str; 5] = ["global", "local", "party", "org", "organization"];

/// Printable ASCII only, trimmed (the decoder's character set).
pub fn ascii(text: &str) -> String {
    text.chars().filter(|c| (32..=126).contains(&(*c as u32))).collect::<String>().trim().to_string()
}

/// difflib.SequenceMatcher(None, a, b, autojunk=False).ratio()
pub fn ratio(a: &str, b: &str) -> f64 {
    let (a, b): (Vec<char>, Vec<char>) = (a.chars().collect(), b.chars().collect());
    if a.is_empty() && b.is_empty() {
        return 1.0;
    }
    let mut b2j: HashMap<char, Vec<usize>> = HashMap::new();
    for (j, c) in b.iter().enumerate() {
        b2j.entry(*c).or_default().push(j);
    }
    let mut matches = 0usize;
    let mut queue = vec![(0usize, a.len(), 0usize, b.len())];
    while let Some((alo, ahi, blo, bhi)) = queue.pop() {
        // find_longest_match: earliest longest block in a, then earliest in b.
        let (mut best_i, mut best_j, mut best_size) = (alo, blo, 0usize);
        let mut previous: HashMap<usize, usize> = HashMap::new();
        for i in alo..ahi {
            let mut current: HashMap<usize, usize> = HashMap::new();
            if let Some(indices) = b2j.get(&a[i]) {
                for &j in indices {
                    if j < blo {
                        continue;
                    }
                    if j >= bhi {
                        break;
                    }
                    let k = if j > 0 { previous.get(&(j - 1)).copied().unwrap_or(0) } else { 0 } + 1;
                    current.insert(j, k);
                    if k > best_size {
                        best_i = i + 1 - k;
                        best_j = j + 1 - k;
                        best_size = k;
                    }
                }
            }
            previous = current;
        }
        if best_size > 0 {
            matches += best_size;
            if alo < best_i && blo < best_j {
                queue.push((alo, best_i, blo, best_j));
            }
            if best_i + best_size < ahi && best_j + best_size < bhi {
                queue.push((best_i + best_size, ahi, best_j + best_size, bhi));
            }
        }
    }
    2.0 * matches as f64 / (a.len() + b.len()) as f64
}

fn squash(text: &str) -> String {
    text.to_lowercase().split_whitespace().collect::<Vec<_>>().join(" ")
}

/// A long header must not make two short remarks (yes -> no) look identical.
pub fn same_text(a: &str, b: &str) -> bool {
    let (left, right) = (header().captures(a), header().captures(b));
    if left.is_some() != right.is_some() {
        return false;
    }
    let (mut a, mut b) = (a.to_string(), b.to_string());
    let headed = left.is_some();
    if let (Some(l), Some(r)) = (&left, &right) {
        let group = |c: &regex::Captures, i: usize| c.get(i).map_or("", |m| m.as_str()).to_string();
        let (ca, cb) = (group(l, 1).to_lowercase(), group(r, 1).to_lowercase());
        let (ha, hb) = (group(l, 2).to_lowercase(), group(r, 2).to_lowercase());
        let handle_same = ha == hb || (ha.len().min(hb.len()) >= 5 && ratio(&ha, &hb) >= 0.88);
        if !handle_same || (KNOWN_CHANNELS.contains(&ca.as_str()) && KNOWN_CHANNELS.contains(&cb.as_str()) && ca != cb) {
            return false;
        }
        a = group(l, 3);
        b = group(r, 3);
    }
    let (mut a, mut b) = (squash(&a), squash(&b));
    if headed && a.len().min(b.len()) >= 4 {
        let strip = |s: &str| s.chars().filter(|c| c.is_ascii_lowercase() || c.is_ascii_digit()).collect::<String>();
        a = strip(&a);
        b = strip(&b);
    }
    if a.len().min(b.len()) < 4 {
        return a == b;
    }
    ratio(&a, &b) >= 0.80
}

pub fn normalized_text(text: &str) -> String {
    static DETACHED: OnceLock<Regex> = OnceLock::new();
    static ISOLATED: OnceLock<Regex> = OnceLock::new();
    let format = |c: &regex::Captures| {
        // Non-English channel text cannot be recognized by the English decoder.
        // Do not turn its ASCII hallucinations into channel IDs.
        let channel = c.get(1).map_or("", |m| m.as_str());
        let channel = if KNOWN_CHANNELS.contains(&channel.to_lowercase().as_str()) { channel } else { "" };
        format!("[{}] {}: {}", channel, &c[2], &c[3])
    };
    if let Some(c) = header().captures(text) {
        return format(&c);
    }
    if text.starts_with('[') || text.starts_with(']') {
        // The English decoder can lose the closing bracket of a Korean channel.
        // Recover a leading handle: boundary, never join it to the previous
        // speaker's continuation line.
        let candidate = match text.split_once(']') {
            Some((_, rest)) => rest.to_string(),
            None => text.trim_start_matches('[').to_string(),
        };
        // A detached dot after the channel is common in the small model.
        let detached = DETACHED.get_or_init(|| Regex::new(r"^\s*[.,;|]+\s+").unwrap());
        let candidate = detached.replace(&candidate, "").to_string();
        if let Some(c) = header().captures(candidate.trim_start()) {
            return format(&c);
        }
    }
    // A Korean channel can be read as a short fragment such as 'IL,' with the
    // brackets lost. Only for a body-less "ID:" header, split the fragment at
    // a separator that cannot be part of a valid handle.
    let isolated = ISOLATED.get_or_init(|| Regex::new(r"^\s*[A-Za-z\[\]|]{1,6}[,;|]\s*([A-Za-z0-9_.-]{2,80})\s*:\s*$").unwrap());
    if let Some(c) = isolated.captures(text) {
        let candidate = format!("{}:", &c[1]);
        if let Some(c) = header().captures(&candidate) {
            return format(&c);
        }
    }
    text.to_string()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ratio_matches_difflib() {
        assert!((ratio("abcd", "bcde") - 0.75).abs() < 1e-12);
        assert!((ratio("bobinhed", "bobinned") - 0.875).abs() < 1e-12);
        assert!((ratio("hello world", "hallo wurld") - 9.0 * 2.0 / 22.0).abs() < 1e-12);
        assert_eq!(ratio("", ""), 1.0);
    }

    #[test]
    fn normalizes_headers() {
        assert_eq!(normalized_text("[Global] Pilot: Hello"), "[Global] Pilot: Hello");
        assert_eq!(normalized_text("[IL] Pilot: Hello"), "[] Pilot: Hello");
        assert_eq!(normalized_text("[] . Pilot: Hello"), "[] Pilot: Hello");
        assert_eq!(normalized_text("[IL . Pilot: Hello"), "[IL . Pilot: Hello");
        assert_eq!(normalized_text("IL, Pilot:"), "[] Pilot: ");
        assert_eq!(normalized_text("just a continuation"), "just a continuation");
    }

    #[test]
    fn compares_remarks() {
        assert!(same_text("[Global] Pilot: yes", "[Global] Pilot: yes"));
        assert!(!same_text("[Global] Pilot: yes", "[Global] Pilot: no"));
        assert!(!same_text("[Global] PilotA: hello there", "[Global] PilotB: hello there"));
        assert!(same_text("[Global] Pilot: Meet me at the spaceport", "[Global] Pilot: Meet me at the spacepart"));
        assert!(!same_text("[Global] Pilot: hello", "hello"));
    }
}
