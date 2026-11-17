# 또또집 (DdoddoZip)

광고 없는 가벼운 윈도우 압축 프로그램. 한글 파일명이 깨지지 않고, 위험한 압축 파일로부터 PC를 지킵니다.

> v1.0.0. 개발 과정과 설계 문서는 [docs](docs/) 폴더에 있습니다.

## 설치

`DdoddoZip-1.0.0-setup.exe` 를 실행합니다. 관리자 권한이 필요하고, .NET 런타임이 들어 있어 따로 설치할 것은 없습니다.
설치하면 탐색기 우클릭 메뉴("더 많은 옵션 표시")와 "연결 프로그램"에 또또집이 추가됩니다.

## 지원 형식

| 형식 | 압축 | 풀기 |
|---|---|---|
| ZIP, 7Z, TAR, TAR.GZ, TAR.XZ, TAR.BZ2 | O | O |
| RAR, ISO, CAB 등 | | O |

## 빌드

필요: Windows 10/11 x64, .NET 10 SDK

```
dotnet build
dotnet test
```

빌드 결과물은 `%LOCALAPPDATA%\DdoddoZip.build` 에 생깁니다.

설치 파일까지 만들려면 (Visual Studio C++ 도구, Inno Setup 6 필요):

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1 -Test -Installer
```

결과: `%LOCALAPPDATA%\DdoddoZip.build\installer\DdoddoZip-<버전>-setup.exe`

## 구조

| 폴더 | 내용 |
|---|---|
| `src/DdoddoZip.Core` | 압축 엔진 연결과 핵심 기능 |
| `src/DdoddoZip.Cli` | 명령줄 도구 `ddz` |
| `tests/DdoddoZip.Tests` | 자동 테스트 |
| `third_party/7zip` | 7-Zip 엔진 |
| `installer` | 설치 파일 스크립트 (Inno Setup) |
| `docs` | 기획서, 설계서 |

## 라이선스

- 또또집 코드: MIT ([LICENSE.txt](LICENSE.txt))
- 압축 엔진: [7-Zip](https://7-zip.org) 26.06, Copyright (C) Igor Pavlov. GNU LGPL 2.1 + unRAR 조건.
  `third_party/7zip/7z.dll` 은 공식 배포본을 수정하지 않고 사용하며, 라이선스 전문은 `third_party/7zip/License.txt` 에 있습니다.
  unRAR 조건에 따라 또또집은 RAR 압축 기능을 만들지 않습니다.
