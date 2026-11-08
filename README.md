# 🚀 StarChat (스타챗)

> **스타시티즌(Star Citizen) 인게임 OCR 번역 및 스펙트럼(Spectrum) 채팅 보조 프로그램**  
> **In-Game OCR Translation & Spectrum Chat Assistant for Star Citizen**

---

## 📖 소개 | Overview

이 프로그램은 게임 **스타시티즌(Star Citizen)**에서 사용하는 번역 및 채팅 보조 프로그램입니다. OCR로 문자를 인식하여 번역문을 보기 편한 위치에 표시해 주며, 한국어(다국어) 채팅을 지원하는 공식 웹사이트(**SPECTRUM**)의 메시지를 인게임에서 쉽게 입력하고 주고받을 수 있는 기능도 지원합니다.

This program is a translation and chat assistant designed for **Star Citizen**. It uses OCR to recognize in-game text and overlays translations in a comfortable reading position, while also allowing you to easily send and receive multilingual (including Korean) messages from the official website (**Spectrum**) directly within the game.

---

## 🛠️ 사용 방법 | How to Use

### 1. 설치 및 실행 | Installation & Execution
* `StarChat-x.x.xx-win-x64.zip` 파일을 다운로드하여 압축을 풀고, 폴더 내의 `StarChat.exe` 파일을 실행합니다.
  * Download and extract `StarChat-x.x.xx-win-x64.zip`, then run `StarChat.exe` inside the folder.
* 설정 창에서 앱과 함께 실행시킬 프로그램(게임 런처 등)이나 언어를 선택할 수 있습니다.
  * In the Settings window, you can select your preferred language and external programs (such as the game launcher) to launch automatically with the app.
* **OCR 번역 기능만** 사용하면 로그인이 필요 없지만, **Spectrum 관련 기능** 사용에는 로그인이 필요합니다. Spectrum 기능들을 사용해 게임을 실행하지 않고도 자체적인 메신저 프로그램처럼 사용할 수 있으며, 친구 목록은 로그인 시 자동으로 불러옵니다.
  * No login is required if you only use the **OCR translation feature**, but logging in is required for **Spectrum features**. With Spectrum integration, the app can function as a standalone messenger even without running the game, and your friends list will be loaded automatically upon login.

<img width="1039" height="745" alt="메인 UI" src="https://github.com/user-attachments/assets/5536b912-1c7b-4d95-a8fa-4440b43d376e" />


---

### 2. 채팅창 및 OCR 영역 설정 | Chat Window & OCR Setup
* 앱과 게임을 실행한 후, 게임에서 **앱 채팅창 활성화 버튼**(기본값: `Shift + Enter`)을 누르면 내부가 투명한 창이 나타납니다. 이 채팅창을 드래그해서 아래 이미지처럼 투명한 영역을 게임 내 채팅창 영역에 맞도록 위치시키세요. 자동으로 채팅 내역을 읽고 번역을 띄워줍니다. 이 상태에서 채팅을 입력하면 자동으로 번역해 게임 채팅창으로 전송합니다.
  * After launching both the app and the game, press the **Chat Activation Hotkey** (Default: `Shift + Enter`) in-game to bring up a transparent overlay window. Drag and resize this window so the transparent area aligns with the in-game chat box as shown below. It will automatically read the chat and display translations. Typing a message in this state will automatically translate and send it to the in-game chat.

<img width="637" height="482" alt="2_채팅 영역 설정" src="https://github.com/user-attachments/assets/b90636d9-1f27-4ee3-b2ae-ffaee4cc43e8" />


* 이 창은 활성화 상태에서만 테두리가 보이고, 평상시에는 숨겨져 투명한 영역에 번역 문자만 띄워주므로 게임 UI를 해치지 않습니다.
  * The window frame is only visible while active. Normally, it stays hidden and only displays the translated text over the transparent area so it doesn't clutter your game UI.

<img width="575" height="336" alt="3_비활성화 사진" src="https://github.com/user-attachments/assets/af1f17d5-f8af-491f-ad88-325c7d8d8efb" />


* 이때 스펙트럼 채팅을 `On`으로 해뒀을 경우, 스펙트럼 채팅창도 별도의 투명한 채팅창으로 띄울 수 있습니다. 스펙트럼 채팅창은 OCR을 사용하지 않으니 번역이 필요한 영역이라면 **OCR 채팅창**을 위치시키세요. *(스펙트럼 채팅과 인게임 채팅은 채팅창 활성화 상태에서 `Tab` 키를 눌러 전환할 수 있습니다.)*
  * If Spectrum Chat is turned `On`, you can display a separate transparent window for Spectrum messages. Since the Spectrum window does not perform OCR, make sure to place the **OCR chat window** over areas that require screen translation. *(While the chat window is active, you can press `Tab` to cycle between In-Game Chat and Spectrum Chat.)*

---

## ✨ 기타 부가 기능 | Additional Features

### 1. 간편한 친구 추가 | Quick Friend Request
* 앱 채팅창 활성화 상태에서 유저 닉네임을 **우클릭**하여 바로 친구 추가 요청을 보낼 수 있습니다. *(스펙트럼 로그인 필요)*
* While the app chat window is active, **right-click** on a user's nickname to send a friend request directly. *(Requires Spectrum login)*

<img width="513" height="393" alt="4_친구 추가" src="https://github.com/user-attachments/assets/977fc79c-2a04-4d75-91fb-a95fa70d1618" />


### 2. 과거 채팅 확인 | Chat History & Logs
* 채팅 내역은 별도로 저장되므로 앱 화면 혹은 별도로 저장된 텍스트 파일(`.txt`)에서 다시 확인해 볼 수 있습니다. 기록 창에서도 마우스 우클릭으로 친구 추가 요청을 전송할 수 있습니다. 채팅 저장 위치는 설정창의 `OCR` 탭에서 확인 가능하며, 일정 기간 후 자동 삭제되도록 설정할 수도 있습니다.
* Chat history is saved separately and can be reviewed directly in the app or via saved text files (`.txt`). You can also right-click nicknames in the history view to send friend requests. The log save directory can be found under the `OCR` tab in Settings, where you can also configure an auto-deletion retention period.

<img width="1072" height="737" alt="5_ 채팅 기록" src="https://github.com/user-attachments/assets/51df17a0-e624-4be0-90b2-9d445fa7a550" />


### 3. 한국어(스펙트럼) 채팅 가능 | Native Korean (Multilingual) Chat via Spectrum
* 공식 웹사이트(Spectrum)의 메시지 기능은 한국어를 포함한 다국어 채팅을 지원합니다. 이를 연동하여 게임 내에서도 한국어(다국어) 채팅을 쉽게 주고받을 수 있으며, 이 기능은 게임이 켜져 있지 않을 때에도 단독으로 사용 가능합니다. 그룹 채팅방도 만들 수 있으나 상대방이 초대를 수락해야 합니다.
* The official website's (Spectrum) messaging system natively supports multilingual chat, including Korean. By integrating this, the app lets you easily send and receive Korean (or other native language) messages in-game, and it also works standalone when the game isn't running. Group chats are supported as well (recipients must accept the invitation).

<img width="479" height="270" alt="7_스팩트럼 인게임png" src="https://github.com/user-attachments/assets/bf5cac65-9201-483b-bd7f-1181581d09b7" />

<img width="1076" height="743" alt="6_스펙트럼 채팅" src="https://github.com/user-attachments/assets/93dc8b5a-7ea4-44fb-bda2-f434c1b64bd8" />


### 4. 사용자 정의 사전(용어집) 지원 | Custom Translation Glossary
* 번역 퀄리티 향상을 위해 게임 고유명사 사전 번역표를 작성해 두었습니다. 추가로 필요한 단어가 있다면 폴더 내의 `translation-glossary.ko-en.json` 파일을 메모장으로 열어 직접 단어를 추가하거나 삭제할 수 있습니다.
* A pre-configured glossary is included to improve translation accuracy for Star Citizen terms. If you want to add or remove custom terms, simply open `translation-glossary.ko-en.json` in a text editor (like Notepad) and edit it directly.

### 5. 다양한 번역 엔진 선택 가능 | Multiple Translation Engines Supported
* 현재 **로컬 번역기(NLLB-200), Google 번역, GPT, Gemini, DeepL, Papago, Ollama**의 총 7가지 엔진을 지원합니다.
  * Supports 7 translation engines: **Local Translator (NLLB-200), Google Translate, GPT, Gemini, DeepL, Papago, and Ollama**.
* 기본 번역기인 `NLLB-200`(선택 시 자동 설치)도 속도가 빠르고 의미를 파악하는 데 큰 지장이 없지만, 더 자연스러운 고품질 번역을 원한다면 별도의 API 키를 발급받거나(무료 API를 제공하는 곳도 많습니다), **Ollama**에 최신 딥러닝 기반 번역 모델(**HY-MT2** 등)을 설치해 사용해 보세요. 개인적으로 **HY-MT2**나 **DeepL**(무료 키 발급 가능)의 번역 품질이 가장 좋았습니다.
  * The default local engine, `NLLB-200` (auto-installed when selected), is fast and sufficient for understanding general chat. However, if you want higher quality translations, try issuing an API key (many providers offer free tiers) or installing a modern deep-learning translation model (such as **HY-MT2**) via **Ollama**. Personally, **HY-MT2** and **DeepL** (free API key available) delivered the best results.
