# SPDX-License-Identifier: GPL-3.0-only
# Copyright (C) 2026 StarChat contributors
"""NLLB-200 distilled-600M: CPU INT8, four threads, one translation at a time."""
import json
import math
from pathlib import Path
import re
import ctranslate2
import sentencepiece as spm

# Local translation tuning. Save this file and fully restart StarChat to reload
# the worker and clear completed/fuzzy translation caches. No rebuild is needed.
BEAM_SIZE = 2
REPETITION_PENALTY = 1.1
MAX_DECODING_LENGTH = 150
NO_REPEAT_NGRAM_SIZE = 3
DISABLE_UNK = True
MIN_LENGTH_INPUT_TOKENS = 5
MIN_OUTPUT_RATIO = 0.9

_GAME_TERMS = re.compile(r'\b(?:dog\s*fighting|dog\s*fight|strafing|strafe|ty|np)\b', re.IGNORECASE)
_EXPANSIONS = {'ty': 'thank you', 'np': 'no problem',
               'dogfighting': 'aerial combat', 'dogfight': 'aerial combat',
               'strafe': 'move sideways', 'strafing': 'moving sideways'}


def expand_game_terms(text):
    return _GAME_TERMS.sub(lambda m: _EXPANSIONS[re.sub(r'\s+', '', m[0].lower())], text)


def minimum_decoding_length(input_tokens):
    if input_tokens < MIN_LENGTH_INPUT_TOKENS:
        return 1  # Keep CT2's default for short messages.
    # CT2 counts the forced target-language prefix in its returned sequence.
    # Add that one token so it cannot satisfy part of the minimum text length.
    return min(MAX_DECODING_LENGTH - 1, math.ceil(input_tokens * MIN_OUTPUT_RATIO) + 1)

# ISO codes used by app settings / py3langid -> NLLB language+script tags.
LANGUAGES = dict(en='eng_Latn', ko='kor_Hang', ja='jpn_Jpan', zh='zho_Hans',
    de='deu_Latn', fr='fra_Latn', es='spa_Latn', ru='rus_Cyrl', uk='ukr_Cyrl',
    ar='arb_Arab', he='heb_Hebr', fa='pes_Arab', hi='hin_Deva', bn='ben_Beng',
    th='tha_Thai', vi='vie_Latn', id='ind_Latn', ms='zsm_Latn', tr='tur_Latn',
    it='ita_Latn', pt='por_Latn', nl='nld_Latn', pl='pol_Latn', cs='ces_Latn',
    sk='slk_Latn', sl='slv_Latn', hr='hrv_Latn', sr='srp_Cyrl', bg='bul_Cyrl',
    ro='ron_Latn', hu='hun_Latn', fi='fin_Latn', sv='swe_Latn', da='dan_Latn',
    nb='nob_Latn', no='nob_Latn', nn='nno_Latn', el='ell_Grek', et='est_Latn',
    lv='lvs_Latn', lt='lit_Latn', af='afr_Latn', sq='als_Latn', am='amh_Ethi',
    hy='hye_Armn', az='azj_Latn', eu='eus_Latn', be='bel_Cyrl', bs='bos_Latn',
    ca='cat_Latn', cy='cym_Latn', eo='epo_Latn', ga='gle_Latn', gl='glg_Latn',
    gu='guj_Gujr', is_='isl_Latn', ka='kat_Geor', kk='kaz_Cyrl', km='khm_Khmr',
    kn='kan_Knda', ky='kir_Cyrl', lo='lao_Laoo', mk='mkd_Cyrl',
    ml='mal_Mlym', mn='khk_Cyrl', mr='mar_Deva', mt='mlt_Latn', my='mya_Mymr',
    ne='npi_Deva', pa='pan_Guru', si='sin_Sinh', so='som_Latn', sw='swh_Latn',
    ta='tam_Taml', te='tel_Telu', tl='tgl_Latn', ur='urd_Arab', uz='uzn_Latn',
    xh='xho_Latn', yi='ydd_Hebr', yo='yor_Latn', zu='zul_Latn')
LANGUAGES['is'] = LANGUAGES.pop('is_')


class Engine:
    def __init__(self, root):
        self.root = Path(root)
        self.sp = spm.SentencePieceProcessor(model_file=str(self.root / 'sentencepiece.bpe.model'))
        vocabulary = json.loads((self.root / 'shared_vocabulary.json').read_text('utf-8'))
        self.tags = {token for token in vocabulary if re.fullmatch('[a-z]{3}_[A-Z][a-z]{3}', token)}
        self.translator = ctranslate2.Translator(str(self.root), device='cpu', compute_type='int8',
            inter_threads=1, intra_threads=4, max_queued_batches=1)
        self.backend = 'CTranslate2 CPU INT8'
        self.detector = None

    def language(self, language, text=''):
        if language in self.tags:
            return language
        language = language.lower().replace('_', '-')
        if language == 'auto':
            if re.search('[\uac00-\ud7a3\u1100-\u11ff\u3130-\u318f]', text):
                language = 'ko'
            elif re.search('[\u3040-\u30ff]', text):
                language = 'ja'
            elif re.search('[\u4e00-\u9fff]', text):
                language = 'zh'
            elif not any(c.isalpha() for c in text):
                language = 'en'
            else:
                if self.detector is None:
                    from py3langid.langid import LanguageIdentifier, MODEL_FILE
                    self.detector = LanguageIdentifier.from_pickled_model(MODEL_FILE, norm_probs=True)
                    self.detector.set_languages([code for code in self.detector.nb_classes if LANGUAGES.get(code) in self.tags])
                language, _ = self.detector.classify(text)
        tag = 'zho_Hant' if language in {'zh-tw', 'zh-hk', 'zh-hant'} else LANGUAGES.get(language.split('-')[0])
        if tag not in self.tags:
            raise ValueError('language')
        return tag

    def translate(self, text, source, target):
        original = text
        source_hint = source.lower().replace('_', '-')
        if (source_hint in {'en', 'eng-latn'} or source_hint.startswith('en-')
                or (source_hint == 'auto' and not any(c.isalpha() and ord(c) > 127 for c in text))):
            # Expand before language detection/tokenization so terse English
            # game chat is not misclassified because of its abbreviations.
            text = expand_game_terms(text)
        source = self.language(source, text)
        target = self.language(target)
        if source == target or not any(c.isalpha() for c in text):
            return original
        pieces = self.sp.encode(text, out_type=str)
        if len(pieces) > 256:
            raise ValueError('too_long')
        # NLLB's current tokenizer convention: language prefix, text, EOS.
        # CTranslate2 Transformers conversions do not insert source specials.
        results = self.translator.translate_batch([[source] + pieces + ['</s>']],
            target_prefix=[[target]], beam_size=BEAM_SIZE, max_decoding_length=MAX_DECODING_LENGTH,
            min_decoding_length=minimum_decoding_length(len(pieces)),
            repetition_penalty=REPETITION_PENALTY, no_repeat_ngram_size=NO_REPEAT_NGRAM_SIZE,
            disable_unk=DISABLE_UNK,
            max_input_length=0, return_end_token=True)
        tokens = results[0].hypotheses[0]
        if not tokens or tokens[-1] != '</s>':
            raise ValueError('too_long')
        tokens = [t for t in tokens if t not in self.tags and t not in {'<s>', '</s>', '<pad>'}]
        return self.sp.decode(tokens).strip()
