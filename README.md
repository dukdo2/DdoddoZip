<p align="center">
  <img src="src/DdoddoZip.App/Assets/app-source.png" width="112" alt="또또집 아이콘">
</p>

<h1 align="center">또또집 (DdoddoZip)</h1>

<p align="center">광고 없는 가벼운 윈도우 압축 프로그램<br>한글 파일명이 깨지지 않고, 위험한 압축 파일로부터 PC를 지킵니다.</p>

<p align="center">
  <a href="../../releases/latest"><b>설치 파일 내려받기</b></a> ·
  Windows 10/11 64비트 · 무료 · MIT 라이선스
</p>

---

## 특징

- **광고·번들 설치 없음.** 설치 파일 하나, 끝.
- **한글 파일명 자동 처리.** 옛 윈도우(CP949)나 맥에서 만든 ZIP도 이름이 깨지지 않게 자동으로 맞춥니다. 필요하면 직접 바꿀 수도 있습니다.
- **탐색기 우클릭으로 바로.** "○○.zip으로 압축", "여기에 풀기", "○○\ 에 풀기" 등 6가지 메뉴 ("더 많은 옵션 표시"에 나옵니다).
- **안전한 풀기.** 압축 파일 밖으로 파일을 쓰려는 공격(Zip Slip), 윈도우 예약 이름, 260자 넘는 긴 경로를 처리합니다.
- **암호.** ZIP·7Z를 AES-256으로 암호화하고, 7Z는 파일 이름까지 숨길 수 있습니다.
- **무결성 검사.** 풀지 않고 손상 여부만 확인합니다.
- **빠름.** 작은 파일이 많은 압축 파일도 여러 파일을 동시에 써서 빠르게 풉니다.

## 지원 형식

| 형식 | 압축 | 풀기 |
|---|:---:|:---:|
| ZIP, 7Z, TAR, TAR.GZ, TAR.XZ, TAR.BZ2 | ✓ | ✓ |
| RAR, ISO, CAB, WIM, LZH, ARJ 등 | | ✓ |

## 설치

[Releases](../../releases/latest)에서 `DdoddoZip-<버전>-setup.exe`를 받아 실행합니다.

- 관리자 권한이 필요합니다 (우클릭 메뉴를 모든 사용자에게 등록하기 위해).
- .NET 런타임이 들어 있어 따로 설치할 것이 없습니다.
- 코드 서명이 없어서 처음 실행할 때 "Windows의 PC 보호" 창이 뜰 수 있습니다. **추가 정보 → 실행**을 누르세요.
- 압축 파일을 더블클릭으로 또또집에서 열려면: 압축 파일 우클릭 → 연결 프로그램 → 다른 앱 선택 → 또또집 → 항상.

## 명령줄 도구

설치 폴더의 `ddz.exe`로 같은 기능을 쓸 수 있습니다.

```
ddz l 사진.zip                    목록 보기
ddz x 사진.zip -o D:\풀위치        풀기
ddz t 사진.zip                    검사
ddz a 새파일.7z 폴더 -p 암호       압축하기
```

## 빌드

필요: Windows 10/11 x64, .NET 10 SDK

```
dotnet build
dotnet test
```

빌드 결과물은 `%LOCALAPPDATA%\DdoddoZip.build`에 생깁니다.

우클릭 메뉴 DLL과 설치 파일까지 만들려면 (Visual Studio C++ 도구, Inno Setup 6 필요):

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1 -Test -Installer
```

## 구조

| 폴더 | 내용 |
|---|---|
| `src/DdoddoZip.Core` | 압축 엔진(7z.dll) 연결과 핵심 기능 |
| `src/DdoddoZip.App` | 화면 (WPF) |
| `src/DdoddoZip.ShellExt` | 탐색기 우클릭 메뉴 (C++) |
| `src/DdoddoZip.Cli` | 명령줄 도구 `ddz` |
| `tests/DdoddoZip.Tests` | 자동 테스트 |
| `installer` | 설치 파일 스크립트 (Inno Setup) |
| `docs` | 기획서부터 검수까지 단계별 개발 문서 |

## 라이선스

- 또또집 코드: MIT ([LICENSE.txt](LICENSE.txt))
- 압축 엔진: [7-Zip](https://7-zip.org) 26.06, Copyright (C) Igor Pavlov. GNU LGPL 2.1 + unRAR 조건.
  `third_party/7zip/7z.dll`은 공식 배포본을 수정하지 않고 사용하며, 라이선스 전문은 `third_party/7zip/License.txt`에 있습니다.
  unRAR 조건에 따라 또또집은 RAR 압축 기능을 만들지 않습니다.
