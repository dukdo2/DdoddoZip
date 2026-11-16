# 또또집 (DdoddoZip)

광고 없는 가벼운 윈도우 압축 프로그램. 한글 파일명이 깨지지 않고, 위험한 압축 파일로부터 PC를 지킵니다.

> 개발 중 (v0.1, 코어 완료). 진행 상황은 [docs](docs/) 폴더를 보세요.

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

## 구조

| 폴더 | 내용 |
|---|---|
| `src/DdoddoZip.Core` | 압축 엔진 연결과 핵심 기능 |
| `src/DdoddoZip.Cli` | 명령줄 도구 `ddz` |
| `tests/DdoddoZip.Tests` | 자동 테스트 |
| `third_party/7zip` | 7-Zip 엔진 |
| `docs` | 기획서, 설계서 |

## 라이선스

- 또또집 코드: MIT (출시 전 LICENSE 파일 추가 예정)
- 압축 엔진: [7-Zip](https://7-zip.org) 26.06, Copyright (C) Igor Pavlov. GNU LGPL 2.1 + unRAR 조건.
  `third_party/7zip/7z.dll` 은 공식 배포본을 수정하지 않고 사용하며, 라이선스 전문은 `third_party/7zip/License.txt` 에 있습니다.
  unRAR 조건에 따라 또또집은 RAR 압축 기능을 만들지 않습니다.
