# Storage Audit - Portable File Activity Logger

외부 저장소(USB/외장하드/네트워크 드라이브) 내부에서 직접 실행되어, 해당 저장소의 파일 활동을 실시간 감사 로그로 기록하는 휴대형 보안 감사 도구.

## 핵심 특징

- **설치 불필요**: self-contained 단일 실행 파일, .NET 런타임 포함
- **저장소 중심 감시**: 프로그램이 위치한 저장소를 자동 감지하여 감시
- **실시간 로그**: FileSystemWatcher 기반 파일 이벤트 실시간 감지 및 정규화
- **자기 이벤트 제외**: `.storageaudit/` 폴더 내부 변경은 자동 필터링/태깅
- **이벤트 정규화**: 중복 이벤트 병합, 이동/복사 패턴 인식, 반입/반출 방향 추정
- **경고 시스템**: 대량 삭제, 반출 의심, 대량 이동 자동 감지
- **오프라인 동작**: 인터넷 연결 불필요, 완전 로컬 단독 실행
- **감사 내보내기**: CSV, JSON, HTML 리포트

## 빠른 시작

### Windows에서 실행 (권장)

```powershell
# 1. 빌드 (개발 PC에서)
dotnet publish src/StorageAudit/StorageAudit.csproj -c Release -r win-x64 --self-contained -o publish/

# 2. publish/ 폴더 전체를 USB/외장하드에 복사
# 3. 저장소 내부에서 StorageAudit.exe 실행
# 4. 브라우저에서 http://localhost:19840 접속
```

### Linux에서 실행

```bash
dotnet publish src/StorageAudit/StorageAudit.csproj -c Release -r linux-x64 --self-contained -o publish/
# publish/ 폴더를 저장소에 복사 후 ./StorageAudit 실행
```

### 개발 모드 실행

```bash
dotnet run --project src/StorageAudit/
```

## 프로젝트 구조

```
StorageManagement/
├── src/StorageAudit/
│   ├── Models/
│   │   ├── AuditConfig.cs          # 설정 모델
│   │   └── FileEvent.cs            # 이벤트/쿼리/통계 모델
│   ├── Services/
│   │   ├── AuditEngine.cs          # 핵심 오케스트레이터
│   │   ├── SqliteLogRepository.cs  # 비동기 큐 + SQLite WAL DB
│   │   ├── SelfEventFilter.cs      # 자기 이벤트 필터링
│   │   ├── AlertDetector.cs        # 경고 규칙 엔진
│   │   ├── ExportService.cs        # CSV/JSON/HTML 내보내기
│   │   └── StorageRootDetector.cs  # 저장소 루트 자동 감지
│   ├── Watchers/
│   │   ├── StorageWatcher.cs       # FileSystemWatcher 래퍼
│   │   └── EventNormalizer.cs      # 이벤트 정규화/중복 제거
│   ├── wwwroot/                    # 웹 대시보드 정적 파일
│   │   ├── index.html
│   │   ├── css/style.css
│   │   └── js/app.js
│   └── Program.cs                  # 진입점 + REST API
├── tests/StorageAudit.Tests/       # xUnit 테스트
└── README.md
```

## 배포 시 저장소 내부 폴더 구조

```
E:\  (USB 드라이브 예시)
├── StorageAudit.exe                # 실행 파일
├── wwwroot/                        # 웹 UI 리소스
├── .storageaudit/                  # 시스템 폴더 (자동 생성)
│   ├── config.json                 # 설정 파일
│   ├── logs/
│   │   └── audit.db                # SQLite 감사 로그 DB
│   └── exports/                    # 내보내기 결과물
└── [사용자 파일들...]               # 감시 대상
```

## 저장소 루트 자동 감지 전략

1. `config.json`에 `WatchRoot`가 지정되어 있으면 해당 경로 사용
2. 실행 파일이 이동식/네트워크 드라이브에 있으면 드라이브 루트 사용
3. 아니면 실행 파일의 상위 디렉토리를 저장소 루트로 사용
4. 최종 폴백: 실행 파일 디렉토리 자체

## 자기 이벤트 제외 방식

- `.storageaudit/` 폴더 하위의 모든 파일 변경은 `IsSelfGenerated = true`로 태깅
- 기본적으로 대시보드에서 자기 이벤트는 숨김 (토글로 표시 가능)
- `audit.db`, `audit.db-wal`, `audit.db-shm`, `config.json` 파일명 패턴 매칭

## 로그 저장 위치

- **SQLite DB**: `.storageaudit/logs/audit.db` (WAL 모드)
- **내보내기**: `.storageaudit/exports/` 폴더
- **설정**: `.storageaudit/config.json`

## 감시 이벤트 종류

| 이벤트 | 감지 방법 | 신뢰도 |
|--------|----------|--------|
| 파일 생성 | FileSystemWatcher.Created | Confirmed |
| 파일 수정 | FileSystemWatcher.Changed + 중복 억제 | Confirmed |
| 파일 삭제 | FileSystemWatcher.Deleted | Confirmed |
| 이름 변경 | FileSystemWatcher.Renamed (같은 디렉토리) | Confirmed |
| 내부 이동 | Renamed (다른 디렉토리) 또는 Delete+Create 패턴 | High |
| 외부 반입 | 새 파일 생성 + 경로 분석 | Medium |
| 외부 반출 | 삭제 이벤트 + 경로 분석 | Medium~Low |

## 경고 규칙

- **대량 삭제**: 60초 내 10개 이상 삭제 → Critical
- **대량 이동**: 60초 내 20개 이상 이동 → Warning
- **외부 반출**: 파일이 저장소 밖으로 이동 → Warning
- **대량 반출**: 60초 내 5개 이상 반출 → Critical
- 임계치는 `config.json`에서 조정 가능

## 성능 최적화

- **비동기 쓰기 큐**: BlockingCollection + 배치 INSERT (500ms 주기, 최대 1000건/배치)
- **SQLite WAL 모드**: 동시 읽기/쓰기 가능, 충돌 안전
- **이벤트 중복 제거**: 2초 윈도우 내 동일 이벤트 병합
- **FileSystemWatcher 버퍼**: 64KB 내부 버퍼로 이벤트 손실 최소화
- **인덱스**: timestamp, action_type, alert_level, full_path 컬럼 인덱싱
- **로그 보관 정책**: 90일(기본) 이후 자동 삭제, 1시간마다 점검

## 설정 파일 예시 (config.json)

```json
{
  "WatchRoot": "E:\\",
  "IgnorePatterns": [
    ".storageaudit", ".git", "node_modules",
    "$RECYCLE.BIN", "System Volume Information",
    "*.tmp", "~$*", "Thumbs.db", "desktop.ini"
  ],
  "EventBatchIntervalMs": 500,
  "EventDeduplicationWindowMs": 2000,
  "LogRetentionDays": 90,
  "MaxDbSizeMb": 500,
  "WebPort": 19840,
  "BulkDeleteThreshold": 10,
  "BulkMoveThreshold": 20,
  "RapidEventWindowSeconds": 60,
  "SuspiciousExportThreshold": 5
}
```

## API 엔드포인트

| 메서드 | 경로 | 설명 |
|--------|------|------|
| GET | `/api/status` | 엔진 상태 |
| GET | `/api/stats` | 이벤트 통계 |
| GET | `/api/events` | 이벤트 목록 (페이징/필터/정렬) |
| POST | `/api/export/{csv\|json\|html}` | 내보내기 생성 |
| GET | `/api/export/download/{fileName}` | 내보내기 파일 다운로드 |
| POST | `/api/config/watchroot` | 감시 루트 변경 |
| POST | `/api/config/ignorepatterns` | 무시 패턴 변경 |

## 테스트

```bash
dotnet test
```

## 제한사항 및 주의점

### 감사 관점 제한사항

1. **파일 읽기/열람 추적 불가**: FileSystemWatcher는 읽기 이벤트를 감지하지 않음. 읽기 추적은 ETW(Event Tracing for Windows) 또는 미니필터 드라이버가 필요하며, 이는 관리자 권한/설치가 필요
2. **외부 반출 추정의 한계**: 저장소에서 삭제된 파일이 실제로 외부로 복사된 것인지, 단순 삭제인지는 FileSystemWatcher로 구분 불가. 방향은 경로 기반 추정이며 신뢰도가 제한적
3. **프로세스 추적 제한**: FileSystemWatcher는 어떤 프로세스가 파일을 변경했는지 정보를 제공하지 않음. 현재 사용자 계정만 기록
4. **이벤트 손실 가능**: 대량의 파일 작업 시 FileSystemWatcher의 내부 버퍼가 초과되면 이벤트가 손실될 수 있음 (64KB 버퍼로 완화)
5. **시간 정확도**: 이벤트 타임스탬프는 감지 시점이며, 실제 파일 변경 시점과 미세한 차이가 있을 수 있음

### 저장소 내부 실행의 장단점

**장점:**
- 별도 설치 없이 저장소에 복사만으로 배포
- 로그가 저장소와 함께 이동하여 감사 추적 연속성 유지
- 저장소별 독립적인 감사 기록

**단점:**
- 저장소 갑작스런 분리 시 DB 쓰기 중 데이터 손실 가능 (WAL 모드로 완화)
- 저장소 용량을 로그가 점유 (보관 정책으로 관리)
- 프로그램 자체의 파일 변경이 감시 대상과 같은 볼륨에 발생 (자기 이벤트 필터로 처리)

### 향후 개선 포인트

- ETW 기반 프로세스 정보 수집 (관리자 권한 필요)
- USN Journal 기반 오프라인 기간 변경 추적
- 클립보드 복사 감지
- 파일 해시 기반 무결성 검증
- 다중 저장소 동시 감시
- 네트워크 공유 드라이브 전용 최적화

## 보안 고려사항

- 웹 대시보드는 `localhost`만 바인딩하여 외부 네트워크 접근 차단
- SQL 쿼리는 파라미터화된 쿼리만 사용 (SQL Injection 방지)
- 파일 다운로드 경로 순회 공격 방지 (경로 검증)
- export format은 화이트리스트 검증
- 모든 HTML 출력에 XSS 이스케이프 적용

## 라이선스

MIT
