# HD_ACS
HD현대중공업 LNG 화물창 용접검사로봇 관제 시스템

## Herdr 개발 환경 실행

Herdr 세션 안에서 아래 명령을 실행하면 `HD_ACS` workspace에 Infra, App,
Simulator, UI, Codex 탭이 생성되고 각 프로세스가 자동으로 시작됩니다.

```bash
herdr --session hd-acs
./tools/herdr-dev.sh
```

UI는 macOS/Linux/Windows 공용 Avalonia 프로젝트인 `HD.Acs.UI.Desktop`을
실행합니다. 프로세스를 종료하려면 해당 탭에서 `Ctrl+C`를 누르세요.
