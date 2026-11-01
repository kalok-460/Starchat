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
repetition_penalty=1.2, no_repeat_ngram_size=3
```

CPU INT8 uses float32 for non-quantized operations (CT2 reports `int8_float32` on the tested CPU). Requests are serial and the host sets below-normal process priority. OpenMP/MKL pools are limited to four threads with passive waiting, OpenBLAS to one. The worker exits after 120 idle seconds and is killed on cancellation/app exit. Native process crashes are contained and retried at most once.

NLLB tokenization uses `[source_language] + SentencePiece tokens + [EOS]` and a forced target-language prefix. The target prefix and special tokens are removed before SentencePiece decoding. Auto language selection uses CJK script checks and bundled py3langid. Input exceeding 256 tokens is rejected. CT2 returns the end token so hitting the 150-token limit cannot silently send incomplete output.

CTranslate2 does not expose ONNX/Transformers' `use_cache=False`. Its decoder uses its own per-sentence cache; messages are independent and no prompt/history is appended across requests. Existing completed-translation and fuzzy duplicate caching is retained separately.

Installation path: `%LOCALAPPDATA%/StarChat/models/nllb-200-distilled-600m-ct2-int8`. A model-specific prompt ID ensures older M2M100 installation consent does not hide the new offer. Old model files/settings/sessions are left intact. Interrupted downloads reuse completed, hash-verified files. The marker is published only after every file is verified. Translation itself never downloads or sends chat to the network.

Windows requires the [Microsoft Visual C++ x64 runtime](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist). The app includes CTranslate2, SentencePiece and the Python runtime. The optional cuDNN loader from the CT2 wheel is excluded from the CPU package; inference is tested with no CUDA paths and no cuDNN module loaded. OCR's ONNX Runtime DirectML is an independent component.

Tests: `ocr/runtime/python.exe translation/test_local.py`. Use `STARCHAT_LOCAL_MODEL_ROOT` only to select an isolated test model directory; it does not redirect app settings or RSI sessions.

References: [CTranslate2 NLLB guide](https://opennmt.net/CTranslate2/guides/transformers.html#nllb), [Translator API](https://opennmt.net/CTranslate2/python/ctranslate2.Translator.html), [CPU performance guidance](https://opennmt.net/CTranslate2/performance.html).
