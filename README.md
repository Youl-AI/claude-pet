# claude-pet

Claude Code 활동에 반응하는 Windows 데스크톱 펫. 작업표시줄 위를 돌아다니며
지금 Claude가 뭘 하고 있는지 색과 동작으로 보여주고, 누적 사용량만큼 레벨이 오릅니다.

![펫과 레벨 표기](docs/images/pet-level.png)

## 하는 일

- **상태 반응** — 열려 있는 모든 Claude Code 세션을 지켜보다가 상태를 몸 색으로 보여줍니다.

  | 색 | 상태 |
  |---|---|
  | 산호주황 | 대기 중, 또는 당신 차례 (물음표가 뜹니다) |
  | 파랑 | 읽는 중 (Read/Grep/검색) |
  | 노랑 | 쓰는 중 (Edit/Write) |
  | 초록 | 도구 실행 중 |
  | 보라 | 도구 오류 |
  | 빨강 + 💢 | 권한 승인 대기로 막힘 |
  | 검정 (누움) | 오래 방치됨 |
  | 청회색 (누움, Zzz) | 토큰 한도 도달 — 리셋되면 스스로 깨어납니다 |

- **레벨** — 트랜스크립트에서 누적 사용량을 계산해 `Lv` 숫자로 표시합니다.
  금액은 어디에도 표시하지 않습니다. 레벨이 오르는 순간 펫 주변에 링이 한 번 반짝입니다.
  최대 레벨 9999.

- **방해하지 않음** — 창은 클릭을 통과시키고, 포커스를 뺏지 않고, 전체화면 앱이
  앞에 있으면 숨습니다. 모든 세션이 끝나면 스스로 종료합니다.

## 요구사항

- Windows 10/11
- [.NET Desktop Runtime 10 이상](https://dotnet.microsoft.com/download/dotnet) (이후 메이저 버전도 동작)
- Claude Code
- 이 플러그인은 Windows 전용이며 macOS/Linux 에서는 훅이 동작하지 않습니다 (세션 진행은 막지 않습니다).

## 설치

Claude Code 안에서:

```
/plugin marketplace add Youl-AI/claude-pet
/plugin install claude-pet@claude-pet
```

설치 후 새 세션을 시작하면 펫이 나타납니다.

## 업데이트

```
/plugin marketplace update claude-pet
/plugin install claude-pet@claude-pet
```

최신 Claude Code에서는 `/plugin install` 한 줄로 마켓플레이스 새로고침까지 됩니다.
설치 결과에 `Run /reload-plugins to activate`가 보이면 `/reload-plugins`를 실행하세요 —
재시작 없이 현재 세션에서 새 버전이 적용됩니다.

> 서드파티 마켓플레이스는 자동 업데이트가 기본으로 꺼져 있습니다.
> `/plugin` → **Marketplaces** 탭에서 auto-update를 켤 수 있습니다.

## 동작 방식

훅(`SessionStart` / `Notification` / `SessionEnd`)이 세션 기록을 남기고 펫 프로세스를
하나만 띄웁니다. 펫은 세션의 트랜스크립트(JSONL)를 읽어 상태를 판단하고, 30초에 한 번
백그라운드에서 사용량을 다시 계산합니다. 파일별 (크기, mtime)을 기억해 바뀐 파일만
다시 읽으므로 트랜스크립트가 커져도 부담이 없습니다.

소스는 `src/`(WPF 앱)와 `plugin/`(훅 + 배포 바이너리)에 있고, 스프라이트는
`tools/spritegen/`의 스크립트로 생성합니다.
