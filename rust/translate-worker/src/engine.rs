// SPDX-License-Identifier: GPL-3.0-only
// Copyright (C) 2026 StarChat contributors
//! NLLB-200 distilled-600M: CPU INT8, four threads, one translation at a time.
use std::collections::HashSet;
use std::path::Path;
use std::sync::OnceLock;

use ct2rs_sys::{ComputeType, Config, Device, TranslationOptions, Translator};
use regex::Regex;
use sentencepiece::SentencePieceProcessor;

// Local translation tuning (the user's adjusted values; see AI_HANDOFF.md §5).
// Change them here and rebuild with rust\build.ps1, then fully restart StarChat.
pub const BEAM_SIZE: usize = 2;
pub const REPETITION_PENALTY: f32 = 1.1;
pub const MAX_DECODING_LENGTH: usize = 150;
pub const NO_REPEAT_NGRAM_SIZE: usize = 3;
pub const DISABLE_UNK: bool = true;
pub const MIN_LENGTH_INPUT_TOKENS: usize = 5;
pub const MIN_OUTPUT_RATIO: f64 = 0.9;
pub const INTRA_THREADS: usize = 4;

pub fn expand_game_terms(text: &str) -> String {
    static TERMS: OnceLock<Regex> = OnceLock::new();
    let terms = TERMS.get_or_init(|| Regex::new(r"(?i)\b(?:dog\s*fighting|dog\s*fight|strafing|strafe|ty|np)\b").unwrap());
    terms.replace_all(text, |c: &regex::Captures| {
        let key: String = c[0].to_lowercase().chars().filter(|ch| !ch.is_whitespace()).collect();
        match key.as_str() {
            "ty" => "thank you",
            "np" => "no problem",
            "dogfighting" | "dogfight" => "aerial combat",
            "strafe" => "move sideways",
            "strafing" => "moving sideways",
            _ => "",
        }.to_string()
    }).into_owned()
}

pub fn minimum_decoding_length(input_tokens: usize) -> usize {
    if input_tokens < MIN_LENGTH_INPUT_TOKENS {
        return 1; // Keep CT2's default for short messages.
    }
    // CT2 counts the forced target-language prefix in its returned sequence.
    // Add that one token so it cannot satisfy part of the minimum text length.
    (MAX_DECODING_LENGTH - 1).min((input_tokens as f64 * MIN_OUTPUT_RATIO).ceil() as usize + 1)
}

/// ISO codes used by app settings -> NLLB language+script tags.
const LANGUAGES: &[(&str, &str)] = &[
    ("en", "eng_Latn"), ("ko", "kor_Hang"), ("ja", "jpn_Jpan"), ("zh", "zho_Hans"), ("de", "deu_Latn"), ("fr", "fra_Latn"),
    ("es", "spa_Latn"), ("ru", "rus_Cyrl"), ("uk", "ukr_Cyrl"), ("ar", "arb_Arab"), ("he", "heb_Hebr"), ("fa", "pes_Arab"),
    ("hi", "hin_Deva"), ("bn", "ben_Beng"), ("th", "tha_Thai"), ("vi", "vie_Latn"), ("id", "ind_Latn"), ("ms", "zsm_Latn"),
    ("tr", "tur_Latn"), ("it", "ita_Latn"), ("pt", "por_Latn"), ("nl", "nld_Latn"), ("pl", "pol_Latn"), ("cs", "ces_Latn"),
    ("sk", "slk_Latn"), ("sl", "slv_Latn"), ("hr", "hrv_Latn"), ("sr", "srp_Cyrl"), ("bg", "bul_Cyrl"), ("ro", "ron_Latn"),
    ("hu", "hun_Latn"), ("fi", "fin_Latn"), ("sv", "swe_Latn"), ("da", "dan_Latn"), ("nb", "nob_Latn"), ("no", "nob_Latn"),
    ("nn", "nno_Latn"), ("el", "ell_Grek"), ("et", "est_Latn"), ("lv", "lvs_Latn"), ("lt", "lit_Latn"), ("af", "afr_Latn"),
    ("sq", "als_Latn"), ("am", "amh_Ethi"), ("hy", "hye_Armn"), ("az", "azj_Latn"), ("eu", "eus_Latn"), ("be", "bel_Cyrl"),
    ("bs", "bos_Latn"), ("ca", "cat_Latn"), ("cy", "cym_Latn"), ("eo", "epo_Latn"), ("ga", "gle_Latn"), ("gl", "glg_Latn"),
    ("gu", "guj_Gujr"), ("is", "isl_Latn"), ("ka", "kat_Geor"), ("kk", "kaz_Cyrl"), ("km", "khm_Khmr"), ("kn", "kan_Knda"),
    ("ky", "kir_Cyrl"), ("lo", "lao_Laoo"), ("mk", "mkd_Cyrl"), ("ml", "mal_Mlym"), ("mn", "khk_Cyrl"), ("mr", "mar_Deva"),
    ("mt", "mlt_Latn"), ("my", "mya_Mymr"), ("ne", "npi_Deva"), ("pa", "pan_Guru"), ("si", "sin_Sinh"), ("so", "som_Latn"),
    ("sw", "swh_Latn"), ("ta", "tam_Taml"), ("te", "tel_Telu"), ("tl", "tgl_Latn"), ("ur", "urd_Arab"), ("uz", "uzn_Latn"),
    ("xh", "xho_Latn"), ("yi", "ydd_Hebr"), ("yo", "yor_Latn"), ("zu", "zul_Latn"),
];

/// whatlang (ISO 639-3) -> NLLB tags for automatic detection of non-CJK text.
const DETECTED: &[(&str, &str)] = &[
    ("eng", "eng_Latn"), ("rus", "rus_Cyrl"), ("cmn", "zho_Hans"), ("spa", "spa_Latn"), ("por", "por_Latn"), ("ita", "ita_Latn"),
    ("ben", "ben_Beng"), ("fra", "fra_Latn"), ("deu", "deu_Latn"), ("ukr", "ukr_Cyrl"), ("kat", "kat_Geor"), ("ara", "arb_Arab"),
    ("hin", "hin_Deva"), ("jpn", "jpn_Jpan"), ("heb", "heb_Hebr"), ("yid", "ydd_Hebr"), ("pol", "pol_Latn"), ("amh", "amh_Ethi"),
    ("kor", "kor_Hang"), ("nob", "nob_Latn"), ("dan", "dan_Latn"), ("swe", "swe_Latn"), ("fin", "fin_Latn"), ("tur", "tur_Latn"),
    ("nld", "nld_Latn"), ("hun", "hun_Latn"), ("ces", "ces_Latn"), ("ell", "ell_Grek"), ("bul", "bul_Cyrl"), ("bel", "bel_Cyrl"),
    ("mar", "mar_Deva"), ("kan", "kan_Knda"), ("ron", "ron_Latn"), ("slv", "slv_Latn"), ("hrv", "hrv_Latn"), ("srp", "srp_Cyrl"),
    ("mkd", "mkd_Cyrl"), ("lit", "lit_Latn"), ("lav", "lvs_Latn"), ("est", "est_Latn"), ("tam", "tam_Taml"), ("vie", "vie_Latn"),
    ("urd", "urd_Arab"), ("tha", "tha_Thai"), ("guj", "guj_Gujr"), ("uzb", "uzn_Latn"), ("pan", "pan_Guru"), ("aze", "azj_Latn"),
    ("ind", "ind_Latn"), ("tel", "tel_Telu"), ("pes", "pes_Arab"), ("mal", "mal_Mlym"), ("mya", "mya_Mymr"), ("nep", "npi_Deva"),
    ("sin", "sin_Sinh"), ("khm", "khm_Khmr"), ("zul", "zul_Latn"), ("afr", "afr_Latn"), ("slk", "slk_Latn"), ("cat", "cat_Latn"),
    ("tgl", "tgl_Latn"), ("hye", "hye_Armn"),
];

pub struct Engine {
    sp: SentencePieceProcessor,
    tags: HashSet<String>,
    translator: Translator,
    detector: Option<whatlang::Detector>,
    pub backend: &'static str,
}

/// Failures the host knows how to explain; everything else is `inference_failed`.
pub enum Failure {
    Language,
    TooLong,
    Other(String),
}

fn is_tag(token: &str) -> bool {
    let b = token.as_bytes();
    b.len() == 8 && b[..3].iter().all(u8::is_ascii_lowercase) && b[3] == b'_' && b[4].is_ascii_uppercase() && b[5..].iter().all(u8::is_ascii_lowercase)
}

impl Engine {
    pub fn new(root: &Path) -> Result<Self, String> {
        let sp = SentencePieceProcessor::open(root.join("sentencepiece.bpe.model")).map_err(|e| e.to_string())?;
        let vocabulary: Vec<String> = serde_json::from_slice(&std::fs::read(root.join("shared_vocabulary.json")).map_err(|e| e.to_string())?)
            .map_err(|e| e.to_string())?;
        let tags = vocabulary.into_iter().filter(|t| is_tag(t)).collect();
        let config = Config {
            device: Device::CPU,
            compute_type: ComputeType::INT8,
            num_threads_per_replica: INTRA_THREADS,
            max_queued_batches: 1,
            ..Config::default()
        };
        let translator = Translator::new(root, &config).map_err(|e| e.to_string())?;
        Ok(Engine { sp, tags, translator, detector: None, backend: "CTranslate2 CPU INT8" })
    }

    fn language(&mut self, language: &str, text: &str) -> Result<String, Failure> {
        if self.tags.contains(language) {
            return Ok(language.to_string());
        }
        let mut language = language.to_lowercase().replace('_', "-");
        if language == "auto" {
            let any = |ranges: &[(u32, u32)]| text.chars().any(|c| ranges.iter().any(|r| (r.0..=r.1).contains(&(c as u32))));
            if any(&[(0xac00, 0xd7a3), (0x1100, 0x11ff), (0x3130, 0x318f)]) {
                language = "ko".into();
            } else if any(&[(0x3040, 0x30ff)]) {
                language = "ja".into();
            } else if any(&[(0x4e00, 0x9fff)]) {
                language = "zh".into();
            } else if !text.chars().any(char::is_alphabetic) {
                language = "en".into();
            } else {
                let tags = &self.tags;
                let detector = self.detector.get_or_insert_with(|| {
                    let allowed = DETECTED.iter().filter(|(_, tag)| tags.contains(*tag)).filter_map(|(code, _)| whatlang::Lang::from_code(*code)).collect();
                    whatlang::Detector::with_allowlist(allowed)
                });
                let code = detector.detect_lang(text).map(|l| l.code()).unwrap_or("eng");
                let tag = DETECTED.iter().find(|(c, _)| *c == code).map(|(_, tag)| *tag).unwrap_or("eng_Latn");
                return if self.tags.contains(tag) { Ok(tag.to_string()) } else { Err(Failure::Language) };
            }
        }
        let tag = if ["zh-tw", "zh-hk", "zh-hant"].contains(&language.as_str()) {
            Some("zho_Hant")
        } else {
            let base = language.split('-').next().unwrap_or("");
            LANGUAGES.iter().find(|(code, _)| *code == base).map(|(_, tag)| *tag)
        };
        match tag {
            Some(tag) if self.tags.contains(tag) => Ok(tag.to_string()),
            _ => Err(Failure::Language),
        }
    }

    pub fn translate(&mut self, original: &str, source: &str, target: &str) -> Result<String, Failure> {
        let mut text = original.to_string();
        let hint = source.to_lowercase().replace('_', "-");
        if hint == "en" || hint == "eng-latn" || hint.starts_with("en-")
            || (hint == "auto" && !text.chars().any(|c| c.is_alphabetic() && c as u32 > 127))
        {
            // Expand before language detection/tokenization so terse English
            // game chat is not misclassified because of its abbreviations.
            text = expand_game_terms(&text);
        }
        let source = self.language(source, &text)?;
        let target = self.language(target, "")?;
        if source == target || !text.chars().any(char::is_alphabetic) {
            return Ok(original.to_string());
        }
        let pieces: Vec<String> = self.sp.encode(&text).map_err(|e| Failure::Other(e.to_string()))?.into_iter().map(|p| p.piece).collect();
        if pieces.len() > 256 {
            return Err(Failure::TooLong);
        }
        // NLLB's current tokenizer convention: language prefix, text, EOS.
        // CTranslate2 Transformers conversions do not insert source specials.
        let mut input = Vec::with_capacity(pieces.len() + 2);
        input.push(source);
        let count = pieces.len();
        input.extend(pieces);
        input.push("</s>".to_string());
        let options: TranslationOptions<String, String> = TranslationOptions {
            beam_size: BEAM_SIZE,
            max_decoding_length: MAX_DECODING_LENGTH,
            min_decoding_length: minimum_decoding_length(count),
            repetition_penalty: REPETITION_PENALTY,
            no_repeat_ngram_size: NO_REPEAT_NGRAM_SIZE,
            disable_unk: DISABLE_UNK,
            max_input_length: 0,
            return_end_token: true,
            ..Default::default()
        };
        let results = self.translator
            .translate_batch_with_target_prefix(&[input], &[vec![target]], &options, None)
            .map_err(|e| Failure::Other(e.to_string()))?;
        let tokens = results.into_iter().next().and_then(|r| r.hypotheses.into_iter().next()).unwrap_or_default();
        if tokens.last().map(String::as_str) != Some("</s>") {
            return Err(Failure::TooLong);
        }
        let kept: Vec<String> = tokens.into_iter().filter(|t| !self.tags.contains(t) && !["<s>", "</s>", "<pad>"].contains(&t.as_str())).collect();
        Ok(self.sp.decode_pieces(&kept).map_err(|e| Failure::Other(e.to_string()))?.trim().to_string())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn expands_terse_game_chat() {
        assert_eq!(expand_game_terms("ty for the dog fight, np"), "thank you for the aerial combat, no problem");
        assert_eq!(expand_game_terms("Strafing left"), "moving sideways left");
        assert_eq!(expand_game_terms("empty typo"), "empty typo");
    }

    #[test]
    fn minimum_length_follows_the_tuned_ratio() {
        assert_eq!(minimum_decoding_length(4), 1);
        assert_eq!(minimum_decoding_length(5), 6);
        assert_eq!(minimum_decoding_length(10), 10);
        assert_eq!(minimum_decoding_length(400), 149);
    }
}
