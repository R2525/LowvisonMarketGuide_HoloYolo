# LowVision Guide — 실행 가이드

저시력자를 위한 MR 공간 앵커 기반 상품 탐색·집기 보조 서비스.

```
server/   백엔드 (Python) — main.py
unity/    HoloLens 2 클라이언트 (Unity)
app/      스마트폰 앱 (Flutter) — 원래 별도 리포(voice_app)였던 것을 여기로 통합
```

## 1. 필요한 키

| 키 | 어디서 발급 | 어디서 쓰는지 |
|---|---|---|
| `OPENAI_API_KEY` | OpenAI 콘솔 | `server/main.py`(Whisper STT) **+ `app/lib/main.dart`**(Whisper 직접 호출) — 용도별로 키 2개 써도 되고 하나로 통일해도 됨 |
| `NAVER_OCR_URL` | NAVER Cloud → CLOVA OCR (Custom Template) | `server/main.py` |
| `NAVER_SECRET_KEY` | 위와 동일 | `server/main.py` |

- 서버 키는 `.env` 파일(→ `server/.env.example` 참고)로 관리, 코드에 직접 쓰지 않음
- 앱 키는 실행할 때 `--dart-define=OPENAI_API_KEY=...`로 주입, 코드에 직접 쓰지 않음
- **예전에 코드에 박혀 있던 키들은 재발급 전이면 그대로 쓰면 안 됩니다** — 새로 발급한 키를 위 방식으로 넣어서 쓰세요.

## 2. IP 연결 구조

세 기기(서버 PC / 스마트폰 / HoloLens 2)가 전부 **같은 Wi-Fi·LAN**에 있어야 합니다.
연결은 방향이 있습니다 — 아래 표의 "누가 → 누구에게" 방향으로 접속을 시도합니다.

| 누가 → 누구에게 | 프로토콜:포트 | 설정 위치 |
|---|---|---|
| 스마트폰 앱 → 서버 | HTTP `:8008`, WebSocket `:5000` | `app/lib/main.dart`의 `_backendBase` |
| HoloLens → 서버 | HTTP `:8008` (프레임+좌표 전송), WebSocket `:5000` (명령 수신) | `unity/Assets/Application/Scripts/Services/MainController_Base64.cs` / `ServerController.cs`의 `dataServerBase`, `commandWsUrl` |
| HoloLens → 스마트폰 앱 | TCP `:6000` (손 방향 안내·집기 이벤트 전송) | `unity/Assets/Application/Scripts/Services/MainAppFlow.cs`의 `flutterServerIp` / `flutterServerPort` |

**즉 서버 PC의 IP 하나, 스마트폰의 IP 하나 — 이 둘을 알아야 합니다.**

1. 서버 PC에서 `ipconfig`(Windows) / `ifconfig`(Mac)로 자신의 로컬 IP 확인
2. 그 IP를 `app/lib/main.dart`의 `_backendBase`와 `unity`의 `dataServerBase`/`commandWsUrl`에 넣기
3. 스마트폰(앱 켠 상태)에서 자신의 로컬 IP 확인
4. 그 IP를 `unity`의 `MainAppFlow` → `flutterServerIp`에 넣기

지금 코드 기본값(`192.168.0.110`, `192.168.0.100`)은 예전 개발 환경 값이라 지금 네트워크에서는 안 맞을 가능성이 높습니다.

## 3. 사용 방법

### 서버 (`server/`)
```bash
cd server
pip install aiohttp opencv-python numpy openai packaging pyaudio requests websockets
cp .env.example .env   # OPENAI_API_KEY, NAVER_OCR_URL, NAVER_SECRET_KEY 채우기
export $(cat .env | xargs) && python main.py
```
- 기본 동작(프레임을 HoloLens가 HTTP로 밀어주는 방식, `USE_PUSHED_FRAMES=True`)은 이 실행만으로 충분합니다.
- ⚠️ `main.py` 706행 근처에 `ffmpeg.exe` 경로가 개발자 PC 기준(`C:/Users/14288/...`)으로 **하드코딩**되어 있습니다. 다른 PC에서 돌리면 이 경로부터 본인 환경에 맞게 고쳐야 합니다(모니터링용 RTSP 재송출 기능이라, 당장 안 고쳐도 핵심 인식 파이프라인 자체는 동작합니다).

### 스마트폰 앱 (`app/`)
```bash
cd app
flutter pub get
flutter run --dart-define=OPENAI_API_KEY=sk-...
```
실행 전 `lib/main.dart`의 `_backendBase`를 서버 PC IP로 바꿔둘 것.

### HoloLens 2 (`unity/`)
1. Unity Editor로 `unity/` 폴더 열기
2. `MainController_Base64`/`ServerController`에 서버 PC IP, `MainAppFlow`에 스마트폰 IP 입력
3. UWP(ARM64)로 빌드 → HoloLens 2에 배포·실행

**실행 순서: 서버 → 앱 → HoloLens** (서버가 먼저 떠 있어야 앱·HoloLens의 연결 시도가 성공합니다)
