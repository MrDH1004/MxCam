# MxCam

광고 없는 가벼운 Windows 화면 녹화 프로그램. 용량은 작게, 화질은 좋게.

## 다운로드

[최신 릴리스](https://github.com/MrDH1004/MxCam/releases/latest)에서
- **설치판** `MxCam-Setup-x.y.z.exe` (권장) — 관리자 권한 없이 설치, 시작 메뉴/바탕화면 바로가기, 제거 지원
- **포터블** `MxCam.exe` — 설치 없이 바로 실행

한 번 설치하면 새 버전이 나올 때 **자동으로 업데이트**됩니다 (시작 시 확인 → 다운로드·SHA256 검증 → 재시작 시 적용).

### 필요한 것
- Windows 10 2004 이상 / Windows 11 (x64)
- [.NET 9 데스크톱 런타임](https://dotnet.microsoft.com/download/dotnet/9.0) — 없으면 첫 실행 때 설치 안내가 뜹니다
- ffmpeg: `winget install Gyan.FFmpeg` (또는 `ffmpeg.exe`를 `MxCam.exe` 옆에 두기)

## 기능

- **녹화**: 영역/전체 화면/창 선택, 시스템 소리 + 마이크, 일시정지(한 파일로 이어짐), 화면 캡처, GIF, 소리만 녹음
- **코덱**: MPEG-1/2/4, Xvid, DivX, MS MPEG-4, WMV, FLV, H.264/HEVC/AV1 (NVIDIA NVENC · Intel QSV · AMD AMF · x264/x265/SVT-AV1), VP9
  — AVI/MP4/MOV/MKV/M4V/TS/VOB/WMV/FLV/WEBM 컨테이너
- **자동 5단계 품질 + 코덱 추천**: 현재 화면을 실측해 "같은 화질에서 용량이 가장 작은" 인코더를 고르고 품질값을 맞춤
- **동영상 변환**: 파일을 고르면 원본을 분석해 형식을 추천하고, 6초 샘플로 예상 용량·화질을 보여준 뒤 변환
- **탐색기 우클릭 메뉴**: 비디오 파일 → `MxCam: 용량 최적화 변환` (Windows 11은 "추가 옵션 표시" 안)
- **자가 학습 최적화**: 영상 종류별로 시험 결과를 학습해, 목표 화질을 만족하는 가장 작은 코덱·품질값을 점점 더 빠르게 찾음
- **옵션**: FPS, 커서, 마우스 클릭 효과, 워터마크, 샘플레이트/채널, 파일 이름, 시간 제한(새 녹화/종료/시스템 종료), 단축키
- 단축키 기본값: 녹화 `F2`, 일시정지 `F3`, 캡처 `F11`
- 녹화 영역 테두리와 툴바는 영상에 찍히지 않음

벤치마크: [docs/benchmark.md](docs/benchmark.md)

## 빌드

```
dotnet publish src/MxCam/MxCam.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
```

`v*` 태그를 push하면 GitHub Actions가 빌드해서 릴리스(`MxCam.exe`, `MxCam.exe.sha256`)를 만들고, 설치된 앱이 자동 업데이트합니다.

### 자체 테스트
- `MxCam.exe --selftest <log>` : 녹화 엔진 (모든 코덱, 일시정지, 소리, GIF, 실측 추천)
- `MxCam.exe --uitest <log>` : 실제 창의 버튼·메뉴·단축키·옵션·변환·최적화 자동 검증 (사용자 설정은 건드리지 않음)
