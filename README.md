# StarChat 1.0.1

Star Citizen의 영어 채팅을 읽고 번역하는 Windows 도구입니다. 게임 OCR과 Spectrum 채팅을 별도의 오버레이로 표시합니다. RSI/Cloud Imperium Games의 공식 제품이 아닙니다.

## 1.0.1 변경

- **앞부분이 대문자로 입력되는 문제 수정**: 게임 채팅에 번역문을 넣을 때 Shift 누름·뗌 이벤트만 본문에서 떼어 앞뒤로 40ms씩 기다립니다. 게임이 Shift 상태를 프레임 단위로 읽어, 한 번에 넣으면 문장 첫 대문자의 Shift가 뒤 글자에도 적용되던 문제입니다. 글자 사이 지연은 없고 Shift가 필요 없는 문장은 이전처럼 한 번에 입력합니다. 실게임에서 사용자 확인을 마쳤습니다.
- **콘솔 키 문자 대체**: 게임은 채팅 입력 중에도 `` ` ``/`~` 키를 콘솔 열기로 가로챕니다. 게임 채팅으로 보내는 글에서는 `` ` ``를 `'`로, `~`를 `-`로 바꿔 입력합니다. Spectrum 전송과 화면 표시는 바꾸지 않습니다.
- **최초 실행 UI 언어**: 설정 파일이 없는 첫 실행에서 Windows 표시 언어가 한국어면 한국어, 그 밖에는 영어로 시작합니다. 기존 설정은 바꾸지 않습니다. 설정의 UI 언어 항목은 어느 언어에서든 `UI 언어 / UI language`, `한국어 (Korean)`, `English (영어)`로 표시합니다.
- `StarChat-1.0.1-win-x64.zip`을 폴더째 압축 해제한 뒤 안의 `StarChat.exe`를 실행하세요. 소스는 `StarChat-1.0.1-source.zip`으로 별도 제공합니다.

## 1.0.0 변경

- **Rust OCR 작업자**: 게임 창 캡처·검출·인식·위치 추적을 `ocr/starchat-ocr.exe`(소스 `rust/ocr-worker`)가 수행합니다. DirectML 세션의 입력 형상을 고정해 검출과 문자 인식이 기존 Python 작업자보다 몇 배 빠릅니다. exe가 없으면 기존 `ocr/worker.py`로 실행합니다.
- **HUD 흔들림 추종**: 검출 사이의 캡처 프레임마다 채팅 글자의 이동량을 구해 번역문 위치에 바로 반영합니다. 플레이어 움직임에 따라 채팅이 흔들릴 때 번역문이 함께 움직입니다.
- **채팅 색상 기반 전처리**: 게임의 채팅 색 네 가지(글로벌 `#FFFFFF`, 파티 `#00FF4E`, 귓속말 `#7E3EFF`, 채널 표시 `#8DCCFF`)와 다른 색의 화소를 어둡게 한 뒤 대비를 높입니다. 채팅 뒤에 색이 있는 밝은 조명·배경이 있을 때의 인식률을 높입니다. 흰색 조명처럼 채팅 색과 같은 배경은 구분할 수 없습니다.
- **기울기**: 번역 줄의 각도가 채팅 영역 안의 세로 위치를 따릅니다. 맨 위는 오른쪽이 0.5도 낮고, 중앙은 수평, 맨 아래는 오른쪽이 0.5도 높습니다(0.3.16의 "아래 네 줄" 규칙을 대체).
- **다시 번역 시 재인식**: 인게임 채팅을 우클릭해 **다시 번역**을 누르면 그 채팅의 줄을 먼저 다시 인식한 뒤 번역합니다. 2초 안에 다시 읽지 못하면 기존 원문으로 번역합니다.
- OCR 결과와 번역 시작이 100ms 타이머를 기다리지 않습니다.
- 로컬 번역(NLLB/CTranslate2)은 기존 Python 작업자를 그대로 사용합니다. Rust 번역 작업자(`rust/translate-worker`)는 측정 결과 더 느려 소스만 포함하며 기본 빌드·배포 대상이 아닙니다.
- Rust 작업자 빌드: rustup(stable, msvc)과 Visual Studio 2022 Build Tools(C++)를 설치하고 `rust/build.ps1`을 실행합니다. `ocr/setup.ps1`이 먼저 완료되어 있어야 ONNX Runtime/DirectML DLL이 복사됩니다.
- `StarChat-1.0.0-win-x64.zip`을 폴더째 압축 해제한 뒤 안의 `StarChat.exe`를 실행하세요. 소스는 `StarChat-1.0.0-source.zip`으로 별도 제공합니다.

## 0.3.17 변경

- 게임 입력 본문의 키 누름·놓음 이벤트를 배열 하나로 만들고 `SendInput` 한 번으로 보냅니다. 영문·숫자·기호는 게임 창의 키보드 배열에 맞춘 스캔 코드를 사용하며 대문자·기호의 Shift와 CapsLock을 고려합니다. 글자 사이 대기와 Ctrl+V/클립보드 사용을 제거했습니다.
- 한글·`ā` 등 직접 대응하는 키가 없거나 Ctrl/Alt 조합이 필요한 문자는 같은 배열의 Unicode 이벤트로 보존합니다. 이 문자의 실제 표시·수신 여부는 게임 입력 지원에 달려 있습니다.
- 채팅 열기와 마지막 전송 Enter는 별도 입력입니다. 창이 열릴 시간 300ms, 본문 전달 후 250ms의 고정 대기는 유지합니다. **전송 전에 번역 결과 확인**을 켜면 마지막 Enter를 생략합니다. 일부 이벤트만 전달된 경우 본문을 재시도하지 않고 앱 초안을 보존합니다.
- 대응표를 무기류·방어구·함선·지역·인물/세력별로 묶었습니다. `아르고 몰` → `Argo MOLE`, `엔빌` → `Anvil` 등 사용자가 수정한 내용을 보존했습니다. JSON 순서와 무관하게 긴 이름부터 치환합니다.
- 실제 Star Citizen에서 일괄 입력이 정상 작동함을 사용자가 확인했습니다. `StarChat-0.3.17-win-x64.zip`을 폴더째 압축 해제한 뒤 안의 `StarChat.exe`를 실행하세요. 소스는 `StarChat-0.3.17-source.zip`으로 별도 제공합니다.

## 0.3.16 변경

- 모든 번역기의 한국어→영어 입력에 `translation-glossary.ko-en.json`을 적용합니다. 제공된 엑셀에서 174개 명칭·231개 한글 별칭을 옮겼습니다. 긴 이름 우선 정규식, 조사 및 단어 경계를 적용합니다. 수정·추가 방법은 `TranslationGlossary.md`에 있습니다.
- **전송 전에 번역 결과 확인**을 켜면 게임 채팅 입력칸에 번역문을 붙여넣고 마지막 전송 Enter는 생략합니다. 앱 투명창은 비활성화되고 게임 입력칸에서 직접 확인·수정한 뒤 Enter로 보냅니다. 옵션을 끄면 기존처럼 즉시 전송합니다.
- 화면 안에 표시되는 OCR 번역문의 가장 아래 네 줄만 오른쪽이 올라가는 0.5도 기울기를 적용합니다. 위쪽 줄은 수평입니다. 메시지 수가 아니라 표시되는 번역 줄 수를 기준으로 셉니다.
- 본창 및 입력 모드의 OCR/Spectrum 투명창에서 메시지를 우클릭하면 **다시 번역**을 선택할 수 있습니다. 정확 일치·유사도 캐시를 건너뛰고 선택한 메시지만 새로 요청합니다. 진행 중 중복 요청을 막고 실패하면 기존 번역을 유지합니다.
- 전송의 Ctrl+V 방식은 유지합니다. 이번 변경은 붙여넣기를 받지 못하는 게임 자체의 상태를 복구하지는 않습니다.

## 0.3.15 변경

이번 개발본은 프로젝트 폴더의 `StarChat.exe`로 실행합니다. 새 배포 ZIP은 생성하지 않았습니다.

- 설정의 **설치 모델**에서 모델을 선택할 때 발생하던 `ContextMenuStrip` 삭제 개체 예외를 수정했습니다. 선택 이벤트가 끝나기 전에 메뉴를 삭제하던 코드를 제거하고 설정창이 메뉴를 소유하도록 바꿨습니다.
- Ollama에서 `hy-mt2:1.8b` 같은 짧은 모델 이름이 404로 실패하면, 해당 로컬 서버의 설치 목록에서 같은 모델·태그를 가진 전체 이름을 찾습니다. 후보가 하나일 때만 한 번 재시도하며 `maternion/hy-mt2:1.8b` 실설치 모델로 한↔영 번역을 확인했습니다. 모델을 자동 다운로드하거나 다른 크기·태그로 바꾸지 않습니다. 설정의 **설치 모델** 버튼으로 전체 이름을 선택해도 됩니다.
- 게임 시작 시 메신저 본창을 자동으로 숨기지 않습니다. 게임 종료 때도 본창을 강제로 활성화하지 않습니다.
- Spectrum 웹 업데이트로 변경된 내부 저장소 이름에 대응합니다. 현재 공식 페이지의 계정·소켓 구조를 확인해 연결하며, 읽음 처리는 서버 확인 후 반영합니다.
- 검색한 방에 들어가면 첫 일치 채팅으로 이동하고 일치 메시지를 노란색으로 강조합니다. 검색창 옆 이전/다음 화살표로 현재 방의 일치 메시지를 하나씩 이동합니다. Enter/Shift+Enter도 사용할 수 있습니다. 저장된 OCR 기록도 검색 결과로 열어 볼 수 있습니다. Spectrum은 현재 앱에 불러온 메시지에서 검색합니다.
- 본창의 OCR 채팅에서도 아이디를 우클릭해 ID 복사·친구 추가 메뉴를 열 수 있습니다. 친구 추가는 RSI 로그인 후 사용할 수 있습니다.

NLLB `translation/engine.py`의 사용자가 조정한 값과 기존 사용자 설정은 이번 수정에서 변경하지 않았습니다.

## 실행

1. 현재 개발본은 프로젝트 폴더의 `StarChat.exe`를 실행합니다. 향후 배포 ZIP을 이용할 때는 전체를 쓰기 가능한 폴더에 압축 해제합니다. exe만 따로 옮기지 마세요.
2. Windows 10 (2004 이상) 또는 Windows 11 x64, .NET Framework 4.7.2 이상, Microsoft Edge WebView2 Runtime이 필요합니다. [WebView2 공식 설치](https://developer.microsoft.com/microsoft-edge/webview2/), [.NET 공식 설치](https://dotnet.microsoft.com/download/dotnet-framework).
3. `StarChat.exe` 또는 `Start-StarChat.cmd`를 실행합니다. DirectML GPU/CPU 런타임과 PP-OCRv6 small ONNX 검출·인식 모델이 포함되어 있습니다.
4. 설정 → 일반에서 UI 언어를 한국어/English로 선택할 수 있습니다. UI 언어는 재실행 시 적용되며 채팅 번역 언어와 별도입니다. 함께 실행할 프로그램은 최대 3개입니다.
5. OCR 영역을 게임 채팅에 맞추고 앱 채팅 열기 키와 게임 채팅 열기 키를 설정하세요. 입력 모드에서 이동·크기 조절·우클릭을 사용할 수 있습니다.

## 로컬 번역 / 기본값

**설정 → 일반 → 번역 엔진 → 로컬 (NLLB · CPU)**에서 NLLB-200 distilled-600M을 사용합니다. 첫 실행과 로컬 엔진 선택 시 모델이 없으면 **설치 / 나중에**를 묻습니다. 이전 M2M100 버전에서 업그레이드한 경우 새 모델을 한 번 안내합니다. 기존 모델 파일·API 키·로그인·저장 설정은 자동 삭제하거나 초기화하지 않습니다. 설치를 거절하면 온라인 번역으로 자동 전환하지 않습니다.

[Meta의 원본 모델](https://huggingface.co/facebook/nllb-200-distilled-600M)을 변환한 [CTranslate2 INT8 파일](https://huggingface.co/osa911/nllb-200-distilled-600M-ct2-int8)과 토크나이저만 받습니다(약 601MiB, 여유 공간 약 1GB 권장). 변환본은 Meta 공식 배포본이 아닌 커뮤니티 변환본입니다. 고정된 리비전과 SHA-256으로 파일을 검증하며 PC에서 별도로 변환할 필요가 없습니다. 설치 위치는 `%LOCALAPPDATA%\StarChat\models\nllb-200-distilled-600m-ct2-int8`입니다. 설치 중단 후 재시도할 때 검증된 다운로드 파일을 재사용합니다.

로컬 번역은 **CPU 전용**입니다. 이 배포본은 현재 사용 중인 `translation/engine.py`의 설정인 `compute_type=int8`, `inter_threads=1`, `intra_threads=4`, `beam_size=2`, `max_decoding_length=150`, `repetition_penalty=1.1`, `no_repeat_ngram_size=3`으로 실행합니다. 프로세스 우선순위를 낮추고 유휴 스핀 대기를 끕니다. 요청은 한 번에 하나씩 처리하며 2분 동안 사용하지 않으면 모델 메모리를 해제합니다. CTranslate2는 `use_cache=False` 옵션을 제공하지 않아 문장 내부의 디코딩 캐시를 사용합니다. 문장 간 대화 문맥이나 KV 상태는 이어 붙이지 않습니다. 완성된 번역문 재사용과 영문 80% 유사도 중복 방지는 유지됩니다. 입력 256토큰 초과 또는 150토큰 생성 한도로 문장이 끝나지 않으면 잘린 결과를 보내지 않고 오류를 알립니다.

로컬 번역은 `disable_unk=True`로 `<unk>` 생성을 차단합니다. 전처리 후 입력 본문이 5토큰 이상이면 최소 출력 본문 길이를 입력의 90%(올림)로 설정합니다. 특수 토큰은 본문 길이에 포함하지 않습니다. 최소 길이를 만족하더라도 모델이 일부 내용을 누락할 수 있으므로 완전한 의미 보존을 보장하는 옵션은 아닙니다.

한국어로 번역할 때 모든 엔진에 단독 게임 약어 즉시 치환과 문장 앞뒤의 `lol`/`lamo`/`rofl`/`o7` 분리·복원을 적용합니다. 로컬 번역에는 영어 약어·게임 용어 확장도 적용합니다. 규칙 목록과 직접 테스트하는 방법은 `translation/README.md`에 있습니다. **현재 실행하는 앱 폴더의 `translation/engine.py` 맨 위 `BEAM_SIZE`, `REPETITION_PENALTY`를 수정하고 앱을 완전히 재시작**하면 새 옵션이 적용됩니다. 재빌드·모델 재설치는 필요하지 않습니다.

**모델 라이선스는 CC-BY-NC-4.0(비상업용)**이며 앱 소스의 GPL-3.0과 별개입니다. 모델 출처와 조건은 `translation/README.md`, `licenses/NLLB-CC-BY-NC-4.0.txt`를 확인하세요. 모델 가중치는 소스/실행 ZIP에 포함하지 않습니다.

Windows에서 CTranslate2는 Microsoft Visual C++ x64 런타임이 필요합니다. 없는 PC는 [Microsoft 공식 다운로드 안내](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist)를 참고하세요. Python·CTranslate2·SentencePiece는 앱에 포함되어 별도 설치할 필요가 없습니다. 로컬 번역용 ONNX, PyTorch, Transformers, CUDA, cuDNN은 사용하지 않습니다.

새 사용자 기본값은 **로컬 번역 / Shift+Enter / OCR 인식 주기 200ms(최소 50ms) / 번역 세로 위치 -4px**입니다. 기존 저장 설정은 유지됩니다. 게임 채팅 열기 키는 일반 탭에 있습니다. OCR은 투명 영역만 읽고 제목·입력칸은 제외합니다. OCR 번역문은 가장 아래 표시되는 네 줄만 왼쪽을 기준으로 오른쪽 끝이 **0.5도 올라가도록** 표시하며 나머지는 수평입니다. 행 위치·고정 글자 크기·창 영역 밖 잘림은 유지됩니다.

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

배포 파일 생성: `package.ps1 -Version 1.0.1 -DestinationRoot <새로운 절대경로>` (`build.ps1`, `ocr/setup.ps1` 완료 후). 패키징은 허용 목록만 사용하며 기존 목적지 폴더를 덮어쓰지 않습니다. OCR small 모델 두 개만 포함하며, 로컬 번역 모델은 사용자가 동의한 뒤 설치합니다. 대응표 JSON과 관리 안내도 포함됩니다.

## 라이선스

StarChat의 자체 소스코드는 GNU GPL **버전 3** (`GPL-3.0-only`)으로 제공됩니다. 전체 조건은 `LICENSE`를 확인하세요. 보증 없이 제공됩니다. 바이너리를 배포할 때 같은 버전의 대응 소스코드도 함께 제공하세요. 서드파티 구성 요소는 `THIRD_PARTY_NOTICES.txt`와 각 원본 라이선스를 따릅니다. Star Citizen/Spectrum 명칭·로고는 각 권리자에게 속하며 GPL 코드 라이선스가 해당 브랜드 권리를 부여하지 않습니다.

---

## English quick start

Version 1.0.1 sends Shift presses apart from the text (the game samples Shift per frame, which upper-cased the first letters), replaces the console-key characters ` and ~ with ' and - in game chat, and starts in Korean or English according to the Windows display language on first run. Version 1.0.0 moved OCR to a Rust worker (`ocr/starchat-ocr.exe`, source in `rust/ocr-worker`) with fixed-shape DirectML sessions, follows HUD sway between detections, dims pixels that are not one of the four in-game chat colours before contrast enhancement, tilts translated rows by their vertical position (+0.5° at the top to -0.5° at the bottom) and re-reads a chat line before a manual re-translation. Build the worker with `rust/build.ps1` (rustup stable-msvc and Visual Studio 2022 C++ Build Tools). The glossary stays grouped by category by category while preserving user edits. Game text is sent in a single SendInput array of scan-code press/release events, with no per-character delay or clipboard access. Characters without a plain keyboard mapping use Unicode events in that same array. Opening chat and optionally submitting with Enter remain separate steps. Review mode fills the game input without the final Enter. Input passed an owned Windows test fixture and was confirmed working in Star Citizen by the user. Extract the entire StarChat-0.3.17-win-x64.zip archive and run StarChat.exe inside; matching source is distributed separately. Only the bottom four visible OCR translation rows tilt. Right-click a message to request a new translation. Spectrum search covers currently loaded messages, and read synchronization still requires server confirmation.

New profiles default to **Local (NLLB · CPU)**, **Shift+Enter**, **200 ms OCR interval** (minimum 50 ms), and **-4 px translation offset**. Existing preferences are preserved. On first launch (including an upgrade from M2M100), accept or postpone installing NLLB-200 distilled-600M INT8 (~601 MiB). Settings offers installation again when Local is selected and the model is missing. No automatic online fallback occurs.

Local translation uses CTranslate2 on **CPU only**, with `intra_threads=4`, `inter_threads=1`, `beam_size=2`, `max_decoding_length=150`, `repetition_penalty=1.1`, and `no_repeat_ngram_size=3`. The worker has below-normal process priority, passive thread waiting, and a 120-second idle timeout. CTranslate2 manages its own per-sentence decoder cache; the former ONNX `use_cache=False` option does not apply. Input over 256 tokens and output that hits the 150-token limit without EOS return an error instead of incomplete chat.

Local decoding uses `disable_unk=True`. For inputs with at least 5 text tokens, the output text minimum is 90% of the input text token count, rounded up, excluding language/EOS tokens. This does not guarantee semantic completeness. Edit `BEAM_SIZE` and `REPETITION_PENALTY` near the top of `translation/engine.py` beside the executable you run, then fully restart the app to reload the worker and clear translation caches; no rebuild or model download is required. All engines share Korean-target chat idiom handling, while English game-term expansion applies only to the local engine. See `translation/README.md` for details.

The community CT2 conversion is pinned and SHA-256 verified, stored at `%LOCALAPPDATA%\StarChat\models\nllb-200-distilled-600m-ct2-int8`. Model weights are not bundled. **NLLB uses CC-BY-NC-4.0 (non-commercial), separately from StarChat's GPL code.** CTranslate2 requires the Microsoft Visual C++ x64 runtime on Windows; see Microsoft's download link above if missing. Translation requires no GPU/CUDA/cuDNN. OCR continues using DirectML. Only the bottom four visible OCR translation rows rotate by -0.5 degrees around their left origin; higher rows remain horizontal. All text stays clipped to the transparent viewport.

StarChat is an independent Windows chat companion for Star Citizen. Extract the entire Windows ZIP into a writable folder and run `StarChat.exe`. Windows 10 (2004+)/11 x64, .NET Framework 4.7.2+ and the Microsoft Edge WebView2 Runtime are required. The DirectML GPU/CPU runtime and PP-OCRv6 small ONNX models are included.

Choose **Settings → General → UI language → English**, save, then restart. The UI language is separate from the chat translation language. Select up to three companion executables in General settings. Set the OCR region and both chat activation keys before playing.

ONNX Runtime DirectML and both PP-OCRv6 small ONNX models are bundled. No PaddlePaddle, CUDA, cuDNN or GPU installer is required. DirectX 12-capable NVIDIA, AMD Radeon and Intel GPUs are supported through DirectML, with a high-performance GPU preference and automatic CPU fallback if GPU initialization or execution fails. There is no GPU dependency installation prompt; the optional local translation model has its own consent prompt. GPU memory is allocated for inference without pre-reserving a percentage of total VRAM. Actual performance and compatibility depend on hardware and drivers.

Both overlays keep the same transparent viewport when input controls are hidden. OCR captures only that viewport, excluding the header and compose area.

Sign in using the official RSI page inside the app. Credentials/settings are stored locally in `%LOCALAPPDATA%\StarChat`, not beside the executable. Only OCR history is persisted; logging out keeps that history. Online translation sends chat text to the selected provider; Local NLLB translates entirely on this PC; Ollama uses your local server. No developer credentials or personal history are shipped.

Build with `build.ps1`; run the tests listed above. Install OCR dependencies with `ocr/setup.ps1`. The source is GPL-3.0-only; distribute the matching source alongside binaries. See `LICENSE` and `THIRD_PARTY_NOTICES.txt` for complete terms and vendor notices. This is not an official RSI/CIG product.
