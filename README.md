# QuotaGlass

.NET 10, WPF, C#으로 만든 Windows용 AI 코딩 도구 사용량 오버레이 프로토타입입니다.

실행 시 로컬 설치 상태를 감지하며, 설치된 에이전트의 사용량 카드만 표시합니다.
Codex, Claude Code, GitHub Copilot, Antigravity CLI가 시스템에서 발견되지
않으면 메인 창의 `QuotaGlass 전용 CLI` 영역에서 앱 전용 복사본을 설치할 수
있습니다. 전용 CLI는 `%LOCALAPPDATA%\QuotaGlass\tools` 아래에만 저장되며
시스템 `PATH`나 전역 npm 설치를 변경하지 않습니다. 다운로드 파일은 공식
릴리스 메타데이터의 SHA-256 또는 SHA-512로 검증한 뒤 활성화합니다. 시스템
CLI가 나중에 설치되면 시스템 CLI를 우선 사용합니다.

현재 Codex는 공식 로컬 app-server를 초기화한 뒤 `account/read`로 인증 토큰을
갱신하고, `account/rateLimits/read`를 통해 실제 rate-limit 데이터와 남은
리셋 티켓 개수, 가장 이른 티켓 기한을 읽습니다. Desktop/별도 Codex 인증
저장소가 있으면 해당 `CODEX_HOME`을 먼저 시도하고, 이후 CLI 저장소와 기존
환경 fallback을 순서대로 시도합니다. 일시적인 app-server 오류는 제한된
retry/backoff로 재시도합니다.
JetBrains AI는 IDE의 로컬 quota 상태에서 월간
AI Credits와 다음 refill 시각을 읽습니다. Claude Code는 `auth status`로 구독
로그인을 확인한 뒤, Claude Desktop의 OS 보호 OAuth 캐시, CLI의 OAuth 인증 파일
순서로 Claude Code의 OAuth 사용량 endpoint를 직접 읽습니다. 두 API 경로가
일시적으로 실패하면 status-line JSON 캐시와 headless `/usage` 화면을 순서대로
fallback으로 사용합니다. Pro/Max 계정의 공식 status-line JSON인
`rate_limits.five_hour`와 `rate_limits.seven_day`도 계속 지원하며, 기존 status line은 브리지 뒤에 그대로
연결됩니다. 브리지는 별도 PowerShell 콘솔을 만들지 않고 QuotaGlass 실행 파일의
숨김 명령 모드에서 동작합니다. Antigravity는 공식 `agy` CLI가 자체 인증으로 제공하는 로컬
`RetrieveUserQuotaSummary` RPC를 통해 Gemini 및 Claude/GPT 모델 그룹의
5시간·주간 quota를 읽습니다. GitHub Copilot은 설치된 공식 Copilot CLI를
headless JSON-RPC 서버로 실행하고, 기존 로그인을 재사용하는
`account.getQuota`를 통해 Chat, 코드 완성 또는 AI Credits의 월간 잔량을
읽습니다. Cursor는 Cursor Agent CLI의 로컬 로그인 상태를 재사용해 CLI의
`/usage` 화면과 같은 `GetCurrentPeriodUsage`를 호출하고, 현재 결제 주기의
포함 사용량 잔량을 읽습니다.

전용 CLI 설치 후 인증이 필요하면 같은 영역의 `로그인/설정` 버튼을 사용합니다.
Copilot은 열린 터미널에서 `/login`을 입력해야 합니다. Cursor는 Windows에서
기존 Cursor 로그인 상태를 재사용하며 전용 CLI 설치 대상에 포함하지 않습니다.
JetBrains AI는 독립 CLI가 없어 설치된 IDE 플러그인의 quota 캐시를 계속
사용합니다.

릴리즈로 설치한 `QuotaGlass.exe`는 시작 시와 이후 6시간마다 GitHub Releases를
확인합니다. 새 버전이 있으면 위젯 컨텍스트 메뉴에 업데이트 항목이 나타나며,
선택하면 exe와 SHA-256을 확인한 뒤 앱을 자동으로 교체하고 재시작합니다.

제한 구간은 서비스에 고정하지 않습니다. Codex 어댑터는 현재 계정이 반환한
primary/secondary window를 meter로 변환합니다. 따라서 개인 계정의
`5시간 + 주간`과 팀 계정의 `월간` 제한을 같은 모델로 처리합니다.

## 핵심 표시 규칙

- 막대: 남은 사용량 비율
- `◆` 기준선: 제한 초기화까지 남은 시간 비율
- 막대가 기준선보다 오른쪽: 현재 속도라면 안정권
- 막대가 기준선보다 5%p 이상 왼쪽: 예상보다 빠르게 소진 중

즉, 다음 식으로 공급자와 무관하게 속도를 판정합니다.

```text
pace delta = remaining usage ratio - remaining time ratio
```

## 실행

```powershell
dotnet run --project .\src\QuotaGlass\QuotaGlass.csproj
```

위젯은 기본적으로 작업표시줄 안에서 드래그해 이동할 수 있으며, 컨텍스트 메뉴의
`화면 전체에서 자유롭게 이동`을 켜면 작업표시줄 밖의 모든 모니터 화면으로
이동할 수 있습니다. 이 모드에서는 작업표시줄이 자동 숨김이어도 위젯을 표시하고
항상 위에 유지합니다. 같은 메뉴의 `그래프 세로로 배치`를 켜면 provider 그래프를
  위아래로 쌓아 표시할 수 있으며, `위젯 투명도`에서 0%, 25%, 50%, 75%를 선택하고
  `위젯 배경 표시`를 끌 수 있습니다. 다른 앱을 클릭하거나 `—` 버튼을 누르면 알림 없이 트레이로
숨고, 트레이 아이콘을 클릭하면 다시 열립니다. 메인 창의 각 에이전트 카드에서
`접기`를 누르면 해당 카드를 숨길 수 있고, 아래쪽 `접은 에이전트` 목록을 클릭하면
다시 펼쳐집니다. 이 상태는 앱을 재시작해도 유지됩니다. 트레이 아이콘의 툴팁과
`사용량` 하위 메뉴에서도 현재 수치와 경고 상태를 확인할 수 있습니다.

## 실제 수집기 추가

`IUsageProvider.FetchAsync`를 구현한 다음 `MainViewModel`의 provider 목록에
등록합니다. 수집기는 서비스 고유 응답을 다음 공통 구조로 변환하면 됩니다.

- 남은 값과 전체 한도
- 제한 구간 시작 시각
- 초기화 시각
- 단위(percent, credits, requests 등)
