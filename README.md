# StarChat 0.3.13

Star Citizen의 영어 채팅을 읽고 번역하는 Windows 도구입니다. 게임 OCR과 Spectrum 채팅을 별도의 오버레이로 표시합니다. RSI/Cloud Imperium Games의 공식 제품이 아닙니다.

## 실행

1. `StarChat-0.3.13-win-x64.zip` 전체를 쓰기 가능한 폴더에 압축 해제합니다. exe만 따로 옮기지 마세요.
2. Windows 10 (2004 이상) 또는 Windows 11 x64, .NET Framework 4.7.2 이상, Microsoft Edge WebView2 Runtime이 필요합니다. [WebView2 공식 설치](https://developer.microsoft.com/microsoft-edge/webview2/), [.NET 공식 설치](https://dotnet.microsoft.com/download/dotnet-framework).
3. `StarChat.exe` 또는 `Start-StarChat.cmd`를 실행합니다. DirectML GPU/CPU 런타임과 PP-OCRv6 small ONNX 검출·인식 모델이 포함되어 있습니다.
4. 설정 → 일반에서 UI 언어를 한국어/English로 선택할 수 있습니다. UI 언어는 재실행 시 적용되며 채팅 번역 언어와 별도입니다. 함께 실행할 프로그램은 최대 3개입니다.
5. OCR 영역을 게임 채팅에 맞추고 앱 채팅 열기 키와 게임 채팅 열기 키를 설정하세요. 입력 모드에서 이동·크기 조절·우클릭을 사용할 수 있습니다.

## 로컬 번역 / 기본값

**설정 → 일반 → 번역 엔진 → 로컬 (NLLB · CPU)**에서 NLLB-200 distilled-600M을 사용합니다. 첫 실행과 로컬 엔진 선택 시 모델이 없으면 **설치 / 나중에**를 묻습니다. 이전 M2M100 버전에서 업그레이드한 경우 새 모델을 한 번 안내합니다. 기존 모델 파일·API 키·로그인·저장 설정은 자동 삭제하거나 초기화하지 않습니다. 설치를 거절하면 온라인 번역으로 자동 전환하지 않습니다.

[Meta의 원본 모델](https://huggingface.co/facebook/nllb-200-distilled-600M)을 변환한 [CTranslate2 INT8 파일](https://huggingface.co/osa911/nllb-200-distilled-600M-ct2-int8)과 토크나이저만 받습니다(약 601MiB, 여유 공간 약 1GB 권장). 변환본은 Meta 공식 배포본이 아닌 커뮤니티 변환본입니다. 고정된 리비전과 SHA-256으로 파일을 검증하며 PC에서 별도로 변환할 필요가 없습니다. 설치 위치는 `%LOCALAPPDATA%\StarChat\models\nllb-200-distilled-600m-ct2-int8`입니다. 설치 중단 후 재시도할 때 검증된 다운로드 파일을 재사용합니다.

로컬 번역은 **CPU 전용**입니다. `compute_type=int8`, `inter_threads=1`, `intra_threads=4`, `beam_size=2`, `max_decoding_length=150`, `repetition_penalty=1.2`, `no_repeat_ngram_size=3`으로 실행합니다. 프로세스 우선순위를 낮추고 유휴 스핀 대기를 끕니다. 요청은 한 번에 하나씩 처리하며 2분 동안 사용하지 않으면 모델 메모리를 해제합니다. CTranslate2는 `use_cache=False` 옵션을 제공하지 않아 문장 내부의 디코딩 캐시를 사용합니다. 문장 간 대화 문맥이나 KV 상태는 이어 붙이지 않습니다. 완성된 번역문 재사용과 영문 80% 유사도 중복 방지는 유지됩니다. 입력 256토큰 초과 또는 150토큰 생성 한도로 문장이 끝나지 않으면 잘린 결과를 보내지 않고 오류를 알립니다.

**모델 라이선스는 CC-BY-NC-4.0(비상업용)**이며 앱 소스의 GPL-3.0과 별개입니다. 모델 출처와 조건은 `translation/README.md`, `licenses/NLLB-CC-BY-NC-4.0.txt`를 확인하세요. 모델 가중치는 소스/실행 ZIP에 포함하지 않습니다.

Windows에서 CTranslate2는 Microsoft Visual C++ x64 런타임이 필요합니다. 없는 PC는 [Microsoft 공식 다운로드 안내](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist)를 참고하세요. Python·CTranslate2·SentencePiece는 앱에 포함되어 별도 설치할 필요가 없습니다. 로컬 번역용 ONNX, PyTorch, Transformers, CUDA, cuDNN은 사용하지 않습니다.

새 사용자 기본값은 **로컬 번역 / Shift+Enter / OCR 인식 주기 200ms(최소 50ms) / 번역 세로 위치 -4px**입니다. 기존 저장 설정은 유지됩니다. 게임 채팅 열기 키는 일반 탭에 있습니다. OCR은 투명 영역만 읽고 제목·입력칸은 제외합니다. OCR 번역문은 각 줄의 왼쪽을 기준으로 오른쪽 끝이 **0.5도 올라가도록** 표시합니다. 행 위치·고정 글자 크기·창 영역 밖 잘림은 유지됩니다.

Google은 번역 라이브러리 없이 .NET HTTP 클라이언트로 직접 요청하며 Chrome 형식의 헤더를 사용합니다. 서버의 사용량 제한을 해제하는 기능은 아니며 재요청 중단 처리를 유지합니다.

## GPU 사용

PP-OCRv6 small의 공식 ONNX 검출·인식 모델을 ONNX Runtime DirectML로 실행합니다. 배포본에 ONNX Runtime과 DirectML 구성 요소, CPU 실행 경로가 모두 포함되어 있습니다. CUDA, cuDNN, PaddlePaddle 또는 별도 GPU 설치 스크립트가 필요하지 않습니다. 앱 시작 시 GPU 설치 안내창을 표시하지 않습니다.

DirectX 12를 지원하는 NVIDIA·AMD Radeon·Intel GPU에서 가속을 시도합니다. 여러 GPU가 있으면 고성능 GPU를 우선 선택합니다. 초기화 실패나 실행 중 GPU 오류가 발생하면 동일한 모델로 CPU OCR에 전환합니다. 실제 지원 여부와 속도는 GPU·드라이버에 따라 달라집니다. DirectML 메모리 아레나와 CPU 스핀 대기를 끄며, 전체 VRAM의 일정 비율을 미리 확보하지 않습니다. 모델과 추론에 필요한 GPU 메모리는 사용합니다.

출처: [DirectML 실행 공급자](https://onnxruntime.ai/docs/execution-providers/DirectML-ExecutionProvider.html), [검출 ONNX 모델](https://huggingface.co/PaddlePaddle/PP-OCRv6_small_det_onnx), [인식 ONNX 모델](https://huggingface.co/PaddlePaddle/PP-OCRv6_small_rec_onnx).

## 기능과 데이터

- 로컬 NLLB(기본), Google, GPT, Gemini, DeepL, Papago, Ollama 번역. 각 서비스의 계정·키·사용 요금 및 제한은 별도입니다. Google 키 없는 웹 번역은 요청 제한을 받을 수 있습니다.
- OCR은 영어를 인식합니다. 단일 틱의 위치 검출, 새 행 부분 인식, 10회마다 전체 재확인 구조입니다. 색상을 유지한 CLAHE 전처리를 사용합니다.
- OCR/Spectrum 별도 영역, 방별 번역과 Tab 전환, 알림, 친구·그룹·1:1 대화 기능. Spectrum의 메시지 위치와 표시 영역은 입력창 활성화 여부와 관계없이 동일합니다.
- RSI 로그인은 앱 내부의 공식 웹 페이지에서 합니다. 설정과 WebView 세션은 `%LOCALAPPDATA%\StarChat`에 저장됩니다. API 키는 Windows 사용자 계정으로 암호화됩니다.
- OCR 기록만 설정한 폴더에 저장합니다. Spectrum 대화는 앱을 종료하면 메모리에서 사라지며 서버에 보관됩니다. 로그아웃은 로그인 세션을 지우고 OCR 기록은 남깁니다.
- 온라인 번역에는 인식한 채팅 내용이 선택한 서비스에 전송됩니다. 로컬 NLLB은 이 PC에서 실행하며 채팅을 외부로 보내지 않습니다. Ollama를 선택하면 해당 로컬 서버를 사용합니다.
- 이 배포본에는 제작자의 설정·API 키·로그인 세션·채팅 기록·녹화 파일이 포함되지 않습니다.

## 소스 빌드 / 검사

소스 ZIP을 압축 해제하고 PowerShell에서 실행하세요. Windows x64와 .NET Framework의 C# 컴파일러를 사용합니다.

```powershell
.\build.ps1
.\run-core-tests.ps1
.\run-translation-tests.ps1
.\run-ocr-regressions.ps1
.\run-ui-tests.ps1
node --test test-bridge.cjs
```

UI 검사는 자체 테스트 창에만 키를 전달합니다. 실계정 API나 게임 전송을 수행하지 않습니다. WebView2 SDK 파일은 `deps/`에 포함됩니다. OCR 런타임을 새로 준비하려면 `ocr/setup.ps1`을 실행하세요(인터넷 필요). Python 검사: `ocr/runtime/python.exe -m unittest discover -s ocr -p 'test_*.py'`. 소스만 빌드한 앱에는 OCR 모델/런타임 설치가 필요합니다.

배포 파일 생성: `package.ps1 -Version 0.3.13 -DestinationRoot <새로운 절대경로>` (`build.ps1`, `ocr/setup.ps1` 완료 후). 패키징은 허용 목록만 사용하며 기존 목적지 폴더를 덮어쓰지 않습니다. OCR small 모델 두 개만 포함하며, 로컬 번역 모델은 사용자가 동의한 뒤 설치합니다.

## 라이선스

StarChat의 자체 소스코드는 GNU GPL **버전 3** (`GPL-3.0-only`)으로 제공됩니다. 전체 조건은 `LICENSE`를 확인하세요. 보증 없이 제공됩니다. 바이너리를 배포할 때 같은 버전의 대응 소스코드도 함께 제공하세요. 서드파티 구성 요소는 `THIRD_PARTY_NOTICES.txt`와 각 원본 라이선스를 따릅니다. Star Citizen/Spectrum 명칭·로고는 각 권리자에게 속하며 GPL 코드 라이선스가 해당 브랜드 권리를 부여하지 않습니다.

---

## English quick start

New profiles default to **Local (NLLB · CPU)**, **Shift+Enter**, **200 ms OCR interval** (minimum 50 ms), and **-4 px translation offset**. Existing preferences are preserved. On first launch (including an upgrade from M2M100), accept or postpone installing NLLB-200 distilled-600M INT8 (~601 MiB). Settings offers installation again when Local is selected and the model is missing. No automatic online fallback occurs.

Local translation uses CTranslate2 on **CPU only**, with `intra_threads=4`, `inter_threads=1`, `beam_size=2`, `max_decoding_length=150`, `repetition_penalty=1.2`, and `no_repeat_ngram_size=3`. The worker has below-normal process priority, passive thread waiting, and a 120-second idle timeout. CTranslate2 manages its own per-sentence decoder cache; the former ONNX `use_cache=False` option does not apply. Input over 256 tokens and output that hits the 150-token limit without EOS return an error instead of incomplete chat.

The community CT2 conversion is pinned and SHA-256 verified, stored at `%LOCALAPPDATA%\StarChat\models\nllb-200-distilled-600m-ct2-int8`. Model weights are not bundled. **NLLB uses CC-BY-NC-4.0 (non-commercial), separately from StarChat's GPL code.** CTranslate2 requires the Microsoft Visual C++ x64 runtime on Windows; see Microsoft's download link above if missing. Translation requires no GPU/CUDA/cuDNN. OCR continues using DirectML. OCR translations rotate by -0.5 degrees around each row's left origin, raising the right endpoint while staying clipped to the transparent viewport.

StarChat is an independent Windows chat companion for Star Citizen. Extract the entire Windows ZIP into a writable folder and run `StarChat.exe`. Windows 10 (2004+)/11 x64, .NET Framework 4.7.2+ and the Microsoft Edge WebView2 Runtime are required. The DirectML GPU/CPU runtime and PP-OCRv6 small ONNX models are included.

Choose **Settings → General → UI language → English**, save, then restart. The UI language is separate from the chat translation language. Select up to three companion executables in General settings. Set the OCR region and both chat activation keys before playing.

ONNX Runtime DirectML and both PP-OCRv6 small ONNX models are bundled. No PaddlePaddle, CUDA, cuDNN or GPU installer is required. DirectX 12-capable NVIDIA, AMD Radeon and Intel GPUs are supported through DirectML, with a high-performance GPU preference and automatic CPU fallback if GPU initialization or execution fails. There is no GPU dependency installation prompt; the optional local translation model has its own consent prompt. GPU memory is allocated for inference without pre-reserving a percentage of total VRAM. Actual performance and compatibility depend on hardware and drivers.

Both overlays keep the same transparent viewport when input controls are hidden. OCR captures only that viewport, excluding the header and compose area.

Sign in using the official RSI page inside the app. Credentials/settings are stored locally in `%LOCALAPPDATA%\StarChat`, not beside the executable. Only OCR history is persisted; logging out keeps that history. Online translation sends chat text to the selected provider; Local NLLB translates entirely on this PC; Ollama uses your local server. No developer credentials or personal history are shipped.

Build with `build.ps1`; run the tests listed above. Install OCR dependencies with `ocr/setup.ps1`. The source is GPL-3.0-only; distribute the matching source alongside binaries. See `LICENSE` and `THIRD_PARTY_NOTICES.txt` for complete terms and vendor notices. This is not an official RSI/CIG product.
