# QuotaGlass

.NET 10, WPF, C#으로 만든 Windows용 AI 코딩 도구 사용량 오버레이 프로토타입입니다.

실행 시 로컬 설치 상태를 감지하며, 설치되지 않은 에이전트는 표시하지 않습니다.
현재 Codex는 공식 로컬 app-server의 `account/rateLimits/read`를 통해 실제
rate-limit 데이터를 읽습니다. JetBrains AI는 IDE의 로컬 quota 상태에서 월간
AI Credits와 다음 refill 시각을 읽습니다. Claude Code는 `auth status`로 구독
로그인을 확인하고 Team 계정은 headless `/usage` 화면에서 5시간·주간 사용량을
읽습니다. Pro/Max 계정은 공식 status-line JSON의 `rate_limits.five_hour`와
`rate_limits.seven_day`도 지원합니다. 기존 status line은 브리지 뒤에 그대로
연결됩니다. Antigravity는 공식 `agy` CLI가 자체 인증으로 제공하는 로컬
`RetrieveUserQuotaSummary` RPC를 통해 Gemini 및 Claude/GPT 모델 그룹의
5시간·주간 quota를 읽습니다.

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

창은 현재 마우스가 있는 모니터의 작업 영역 오른쪽 아래에 자동 고정되며 직접
옮길 수 없습니다. 다른 앱을 클릭하거나 `—` 버튼을 누르면 알림 없이 트레이로
숨고, 트레이 아이콘을 클릭하면 다시 열립니다. 트레이 아이콘의 툴팁과 `사용량`
하위 메뉴에서도 현재 수치와 경고 상태를 확인할 수 있습니다.

## 실제 수집기 추가

`IUsageProvider.FetchAsync`를 구현한 다음 `MainViewModel`의 provider 목록에
등록합니다. 수집기는 서비스 고유 응답을 다음 공통 구조로 변환하면 됩니다.

- 남은 값과 전체 한도
- 제한 구간 시작 시각
- 초기화 시각
- 단위(percent, credits, requests 등)
