# 개인정보와 로컬 저장 / Privacy

CodexMeter는 로컬 `codex.exe app-server`를 표준 입출력으로 실행하여 사용량 한도를 읽습니다. Codex 프로세스는 계정 인증과 OpenAI 서비스 통신을 담당합니다. 이 앱을 사용하려면 해당 PC의 Codex 로그인과 네트워크 연결이 필요합니다.

CodexMeter에는 분석 수집, 광고, 개발자 서버로의 전송, 원격 오류 보고 또는 자동 업데이트 기능이 없습니다. 앱은 Codex 인증 파일·비밀번호·토큰을 직접 읽거나 배포물에 포함하지 않습니다. 단, 실행되는 Codex 자체의 데이터 처리에는 Codex의 정책과 설정이 적용됩니다.

`%LOCALAPPDATA%\CodexMeter`에 저장하는 항목:

- `settings.json`: 창 위치, 표시 방식, 선택한 CLI 경로 등 사용자 설정.
- `notifications-v1.json`: 리셋 주기, 알림 단계, 잔여량 등 중복 알림 방지 정보. 계정 식별자가 응답에 있는 경우 해시로 구분하며 원본은 저장하지 않습니다. 계정 식별자가 없는 응답의 계정 전환 판별에는 한계가 있습니다.
- `last-error.txt`: 예기치 않은 UI 오류의 시각과 예외 종류. 원본 서버 응답과 인증 정보는 기록하지 않습니다.

앱을 제거해도 이 폴더는 남습니다. 완전히 초기화하려면 앱을 종료한 후 해당 폴더를 삭제할 수 있습니다. 포터블도 동일한 사용자별 폴더를 사용합니다. 자동 실행을 선택하면 현재 사용자의 시작프로그램 폴더에 바로가기를 만듭니다.

개발용 진단·테스트 명령의 보고서에는 로컬 경로가 포함될 수 있습니다. 공개 제보 전에 개인 정보가 없는지 확인하세요. 로그인 파일이나 원본 계정 응답을 공유할 필요는 없습니다.

CodexMeter communicates with a local Codex app-server, which handles authentication and network access to OpenAI. CodexMeter has no developer-operated backend, analytics, ads, remote crash reporting, or automatic updater. Preferences and notification deduplication state stay in the Windows user profile, including in portable mode. Uninstall preserves those files. Do not post credentials, authentication files, or raw account responses in issues.
