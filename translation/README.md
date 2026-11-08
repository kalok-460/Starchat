# Local translation: NLLB / CTranslate2 CPU

Base model: [facebook/nllb-200-distilled-600M](https://huggingface.co/facebook/nllb-200-distilled-600M), by Meta AI's NLLB team.
INT8 conversion: [osa911/nllb-200-distilled-600M-ct2-int8](https://huggingface.co/osa911/nllb-200-distilled-600M-ct2-int8), a community conversion, not an official Meta CT2 release.

`model.json` pins revision `46858753dbaf8eb5e21bb6f0037c3b90851e090a`, byte counts and SHA-256 hashes. Only model.bin, CT2 config, shared vocabulary, SentencePiece tokenizer and the model card download after consent (~601 MiB). No full-precision weights, ONNX conversion, remote Python code, PyTorch or Transformers installation is needed.

The model's license is **CC-BY-NC-4.0**, not GPL; see `licenses/NLLB-CC-BY-NC-4.0.txt`. Attribution: Meta AI NLLB Team (original weights), osa911 (CTranslate2 INT8 conversion), [NLLB paper](https://arxiv.org/abs/2207.04672). StarChat does not modify the downloaded weights. StarChat-authored code remains GPL-3.0-only. Weights are installed per user and excluded from source/runtime archives.

Engine parameters:
```
device="cpu", compute_type="int8"
inter_threads=1, intra_threads=4, max_queued_batches=1
beam_size=2, max_decoding_length=150
repetition_penalty=1.1, no_repeat_ngram_size=3, disable_unk=True
minimum text output = ceil(input text tokens * 0.9), when input text tokens >= 5
```

## 직접 옵션을 바꾸며 테스트하기

**현재 실행하는 StarChat.exe와 같은 폴더의 `translation/engine.py`**를 메모장이나 코드 편집기로 열어 맨 위의 `BEAM_SIZE = 2`, `REPETITION_PENALTY = 1.1`을 수정하세요. 이 배포본은 사용자가 조정한 현재 값을 보존합니다. 최소 출력 비율은 `MIN_OUTPUT_RATIO = 0.9`입니다. 값은 Python 숫자로 적습니다. Beam size는 1 이상의 정수입니다. 저장한 다음 **StarChat을 완전히 종료하고 다시 실행**하면 적용됩니다. 재빌드하거나 모델을 다시 설치할 필요는 없습니다. 재시작하면 이전 설정으로 계산된 메모리 번역 캐시와 80% 유사도 캐시도 비워집니다. 이전에 표시·저장된 번역문은 자동으로 다시 번역하지 않으므로 새 문장으로 테스트하세요.

한국어 번역에서는 모든 엔진에 다음 공통 규칙을 적용합니다. 대소문자는 구분하지 않고, 단어 경계를 확인하여 다른 단어나 아이디 속 문자열은 치환하지 않습니다. 다른 목적 언어에는 한국어 치환을 적용하지 않습니다.

- 단독 `lol` → `ㅋㅋ`, `lamo`/`rofl` → `ㅋㅋㅋㅋ`, `gg` → `수고하셨습니다`, `gf` → `좋은 승부였습니다`, `o7` → `충성(o7)`, `ty` → `감사합니다`, `np` → `천만에요`. 모델·API 요청 없이 반환합니다.
- 맨 앞/맨 뒤의 `lol`, `lamo`, `rofl`, `o7`은 분리하고 본문만 번역합니다. 결과 앞뒤에 대응하는 한글 표현을 같은 순서로 붙입니다. 연속된 표현과 `!`, `,` 같은 문장부호를 지원합니다. 문장 중간의 표현은 그대로 번역기에 전달합니다.
- 분리·복원은 본문 캐시 바깥에서 처리하므로 같은 본문 뒤의 서로 다른 표현이 캐시 때문에 섞이지 않습니다.
- 영어/자동 감지 입력에 적용하며, 한국어 등 다른 언어로 명시된 원문은 그대로 처리합니다.

로컬 엔진은 토큰화 전에 영어 `ty` → `thank you`, `np` → `no problem`, `dog fight`/`dog fighting`/`dogfight`/`dogfighting` → `aerial combat`, `strafe` → `move sideways`, `strafing` → `moving sideways`로 확장합니다. 원문 표시·기록은 변경하지 않습니다.

## Minimum length and decoding

Count SentencePiece text tokens after local preprocessing, excluding source language/EOS tokens. With 5 or more tokens, require at least 90% as many output text tokens (rounded up) in this release. CTranslate2 counts the forced target-language prefix in `min_decoding_length`, so the code adds one prefix token to the text minimum. Shorter input keeps the default minimum of 1. The existing 150-token maximum and 256-token input guard remain in place. `disable_unk=True` prevents unknown-token generation during decoding, rather than deleting unknown tokens from the completed text.

This length floor is not a semantic completeness check. NLLB can still omit a clause after satisfying the minimum; manual samples include this limitation. Higher beam size can also take longer. No automatic sentence splitting or additional decoding penalties are introduced.

CPU INT8 uses float32 for non-quantized operations (CT2 reports `int8_float32` on the tested CPU). Requests are serial and the host sets below-normal process priority. OpenMP/MKL pools are limited to four threads with passive waiting, OpenBLAS to one. The worker exits after 120 idle seconds and is killed on cancellation/app exit. Native process crashes are contained and retried at most once.

NLLB tokenization uses `[source_language] + SentencePiece tokens + [EOS]` and a forced target-language prefix. The target prefix and special tokens are removed before SentencePiece decoding. Auto language selection uses CJK script checks and bundled py3langid. Input exceeding 256 tokens is rejected. CT2 returns the end token so hitting the 150-token limit cannot silently send incomplete output.

CTranslate2 does not expose ONNX/Transformers' `use_cache=False`. Its decoder uses its own per-sentence cache; messages are independent and no prompt/history is appended across requests. Existing completed-translation and fuzzy duplicate caching is retained separately.

Installation path: `%LOCALAPPDATA%/StarChat/models/nllb-200-distilled-600m-ct2-int8`. A model-specific prompt ID ensures older M2M100 installation consent does not hide the new offer. Old model files/settings/sessions are left intact. Interrupted downloads reuse completed, hash-verified files. The marker is published only after every file is verified. Translation itself never downloads or sends chat to the network.

Windows requires the [Microsoft Visual C++ x64 runtime](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist). The app includes CTranslate2, SentencePiece and the Python runtime. The optional cuDNN loader from the CT2 wheel is excluded from the CPU package; inference is tested with no CUDA paths and no cuDNN module loaded. OCR's ONNX Runtime DirectML is an independent component.

Tests: `ocr/runtime/python.exe translation/test_local.py`. Use `STARCHAT_LOCAL_MODEL_ROOT` only to select an isolated test model directory; it does not redirect app settings or RSI sessions.

References: [CTranslate2 NLLB guide](https://opennmt.net/CTranslate2/guides/transformers.html#nllb), [Translator API](https://opennmt.net/CTranslate2/python/ctranslate2.Translator.html), [CPU performance guidance](https://opennmt.net/CTranslate2/performance.html).
