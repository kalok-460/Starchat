# 🚀 StarChat (스타챗)

> **스타시티즌(Star Citizen) 인게임 OCR 번역 및 스펙트럼(Spectrum) 채팅 보조 프로그램**  
> **In-Game OCR Translation & Spectrum Chat Assistant for Star Citizen**

---

## 📖 소개 | Overview

이 프로그램은 게임 **스타시티즌(Star Citizen)**에서 사용하는 번역 및 채팅 보조 프로그램입니다. OCR로 문자를 인식하여 번역문을 보기 편한 위치에 표시해 주며, 공식 웹사이트(SPECTRUM)의 메시지를 인게임에서 쉽게 입력하고 주고받을 수 있는 기능도 지원합니다.

This program is a translation and chat assistant designed for **Star Citizen**. It uses OCR to recognize in-game text and overlays translations for comfortable reading, while also allowing you to easily send and receive messages from the official website (**Spectrum**) directly within the game.

---

## 🛠️ 사용 방법 | How to Use

### 1. 설치 및 실행 | Installation & Execution
* `StarChat-0.3.11-win-x64.zip` 파일을 다운로드하여 압축을 풀고, 폴더 내의 `StarChat.exe` 파일을 실행합니다.
  * Download and extract `StarChat-0.3.11-win-x64.zip`, then run `StarChat.exe` inside the folder.
* **Spectrum** 기능을 사용하려면 로그인이 필요합니다. 로그인 시 친구 목록을 자동으로 불러옵니다.
  * Logging in is required to use **Spectrum** features. Once logged in, your friends list will be loaded automatically.
* **OCR 번역 기능만 사용할 경우** 스펙트럼 로그인은 필요하지 않습니다. 설정 창에서 원하는 번역 엔진과 언어 등을 설정할 수 있습니다.
  * **If you only use the OCR translation feature**, Spectrum login is not required. You can configure the translation engine and languages in the Settings window.

### 2. 채팅창 및 OCR 영역 설정 | Chat Window & OCR Setup
* 앱 채팅창 활성화 단축키는 옵션에서 설정 가능합니다.
  * The hotkey to activate the app's chat window can be customized in the Options.
* 게임 내에서 설정창 활성화 버튼을 누른 뒤, 나타나는 투명한 창을 **인게임 채팅 영역**에 맞춰 크기와 위치를 조절하세요. 이후 자동으로 번역이 시작됩니다.
  * Press the settings activation key in-game, then resize and align the transparent overlay window over the **in-game chat area**. Translation will start automatically.
* **스펙트럼 채팅창**과 **인게임 OCR 채팅창**은 별개로 동작하며, OCR 채팅창 영역에서만 문자 인식(OCR)을 진행합니다. 앱에서 각 채팅창의 `On/Off` 여부와 번역 사용 여부를 개별 설정할 수 있습니다.
  * The **Spectrum chat window** and the **In-game OCR chat window** operate separately; OCR is only performed on the OCR chat window. You can toggle each chat window `On/Off` and enable/disable translation individually in the app.

### 3. 발신 번역 설정 | Outgoing Translation Mode
* 기본 설정은 `Enter` 키 혹은 채팅 활성화 버튼을 누르면 입력한 내용이 즉시 번역되어 채팅창에 입력됩니다. 번역 결과를 먼저 확인한 후 전송하고 싶다면 설정 창에서 방식을 변경할 수 있습니다.
  * By default, pressing `Enter` or the activation key immediately translates and inputs your message into the chat. If you prefer to review the translation before sending, you can change this behavior in the Settings window.

---

## ✨ 주요 기능 | Key Features

1. **인게임 채팅 수신/발신 실시간 번역 | Real-time Incoming/Outgoing Chat Translation**
   * 번역문은 원문 줄 사이의 빈 공간에 자연스럽게 배치됩니다. 텍스트 좌표, 색상 등은 설정에서 자유롭게 변경 가능합니다.
   * Translations are neatly placed in the empty space between original text lines. Text coordinates, colors, and other styles can be customized in the settings.

2. **간편한 친구 추가 | Quick Friend Request**
   * 채팅창에서 유저를 우클릭하여 바로 친구를 추가할 수 있습니다. *(스펙트럼 로그인 필요)*
   * Right-click a user in the chat window to send a friend request directly. *(Requires Spectrum login)*

3. **인게임 스펙트럼(Spectrum) 연동 | Seamless In-Game Spectrum Messaging**
   * 인게임 채팅창을 사용하듯 편하게 스펙트럼 메시지를 수신 및 발신할 수 있습니다. 스펙트럼 메시지는 다국어를 지원하므로 **한국어 채팅**이 가능합니다.
   * Send and receive Spectrum messages comfortably just like using an in-game chat window. Since Spectrum supports multiple languages, you can freely chat in **Korean** (or your native language).

4. **단독 메신저 모드 지원 | Standalone Messenger Mode**
   * 게임을 켜지 않았을 때에도 스펙트럼 메시지 기반 기능은 독립적인 앱으로 자체 사용이 가능합니다.
   * Spectrum messaging features can be used independently on your desktop even when the game is not running.

5. **그룹 채팅 지원 | Group Chat Support**
   * 그룹 채팅 기능은 상대방이 채팅 초대를 수락해야 활성화됩니다.
   * For group chats, the recipient must accept the chat invitation to join.

6. **채팅 로그 자동 저장 및 관리 | Chat Log Saving & Auto-Cleanup**
   * OCR 채팅 기록은 텍스트 파일(`.txt`)로 별도 저장되며, 저장 경로 및 자동 삭제 보관 기간을 옵션 창에서 직접 설정할 수 있습니다.
   * OCR chat logs are saved separately as text files (`.txt`). You can customize the save directory and the auto-deletion retention period in the Options window.

7. **원클릭 자동 로그인 및 보안 | One-Click Login & Session Management**
   * 최초 1회 로그인 시, 다음 실행부터는 로그인 버튼 클릭 한 번으로 간편하게 접속할 수 있습니다. 로그아웃 버튼을 누르면 저장된 모든 로그인 관련 정보가 완전히 삭제됩니다.
   * Once you log in for the first time, subsequent launches allow one-click login. Clicking Logout will completely delete all saved login credentials and session data.
