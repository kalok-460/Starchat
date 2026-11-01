# OCR implementation

StarChat uses PP-OCRv6_small_det_onnx and PP-OCRv6_small_rec_onnx through ONNX Runtime DirectML or CPU. The recognition decoder admits ASCII English letters, digits and punctuation only. BGR color is preserved; CLAHE is applied to Lab luminance. No grayscale conversion or binarization is used.

`worker.py` captures only the selected game HWND with Windows Graphics Capture. It stores the newest cropped frame in memory. It never falls back to capturing the desktop. The OCR overlay is not part of the game window capture.

`ticks.py` owns the serial tick clock. Each available tick runs detection over the whole OCR region. New confirmed rows are recognized; existing text follows detection geometry. Ten idle ticks or ten partial recognition runs trigger full recognition for correction. Busy ticks are dropped instead of queued. `geometry.py` handles tracking. `Core.cs` groups rows into chat messages; the overlay clips fixed-size translated text to the chosen window.

`onnx_engine.py` owns separate detection and recognition sessions. The default device is auto: use DirectML on a supported GPU, otherwise CPU. Only selected crops execute the recognition session. The previous PaddleOCR pipeline's BGR normalization, resize and DB thresholds (0.3/0.6, unclip 1.5) are retained. English-only CTC masking preserves the original confidence without renormalization. CLAHE and tracking are unchanged.

DirectML uses sequential execution, no memory pattern, no memory arena and no CPU sync spinning. CPU helper work is limited to two inference threads and one OpenCV thread. No fixed percentage of VRAM is reserved. Dynamic shapes and inference still allocate working memory. A driver failure switches both model sessions to CPU once without clearing the row tracker. The application never starts an installer or checks CUDA/Paddle locations.

## Runtime and models

`runtime/` is the self-contained app-local Python environment with `onnxruntime-directml`, OpenCV, NumPy and capture dependencies. `models/onnx/det` and `models/onnx/rec` contain official ONNX files and original configuration/vocabulary. CPU and DirectML use the same models. The full Windows release works offline for OCR. No Paddle, CUDA or cuDNN libraries are included.

For developers, `setup.ps1` installs the pinned dependencies only in this folder, then runs `download_models.py`. The model manifest `models-onnx.json` records repository revisions and SHA-256 values; downloads are verified before replacement. Model initialization itself never downloads files. Vendor licenses are retained in `licenses/`, model folders and Python package metadata.

## Worker protocol

UTF-8 JSON Lines over stdin/stdout. Diagnostics go to stderr. Host configuration includes `type: config`, game `windowTitle`/`windowHwnd`, normalized `roi: {x,y,w,h}` and `intervalMs`. `refresh` requests a full check at the next available tick; `stop`/stdin EOF ends the worker.

`ready` reports model names, language and device. `status` reports setup/errors; `fatal: true` denotes startup failure. `lines` contains the current full display snapshot, not necessarily a full recognition run. Each row has source text, confidence, stable trackKey, position, size and layout coordinates. `recognizedRows`, `fullReview`, `idleTicks`, `partialRuns`, and `skippedTicks` describe the actual work performed.

## Local checks

```powershell
ocr/runtime/python.exe -m unittest discover -s ocr -p 'test_*.py'
ocr/runtime/python.exe ocr/worker.py --self-test
$env:STARCHAT_OCR_DEVICE='cpu'
ocr/runtime/python.exe ocr/worker.py --check
$env:STARCHAT_OCR_DEVICE='gpu' # DirectML verification; fails rather than silently using CPU
ocr/runtime/python.exe ocr/worker.py --check
$env:STARCHAT_OCR_DEVICE='auto'
ocr/runtime/python.exe ocr/check_ticks.py
```

The first checks are synthetic. `--check` and `check_ticks.py` load real models on synthetic pixels, not the game. Timing and recognition quality vary by hardware and game display conditions. `integration_check.ps1`/`scroll_check.ps1` create owned Windows capture fixtures. Their output stays in `test-output/` and is not distributed.
