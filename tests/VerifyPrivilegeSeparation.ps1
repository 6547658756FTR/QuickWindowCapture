$ErrorActionPreference = "Stop"

$helperSource = Get-Content -LiteralPath $args[0] -Raw
$programSource = Get-Content -LiteralPath $args[1] -Raw
$bridgeSource = Get-Content -LiteralPath $args[2] -Raw

$forbiddenHelperReferences = @(
    "ScreenshotService",
    "CaptureWorker",
    "CaptureSound",
    "DiagnosticLog",
    "AppSettings.Load",
    "AppSettings.Save",
    "SaveFolder"
)

foreach ($forbiddenReference in $forbiddenHelperReferences) {
    if ($helperSource.Contains($forbiddenReference)) {
        throw "관리자 입력 도우미에 금지된 기능 참조가 있습니다: $forbiddenReference"
    }
}

if ($programSource.Contains("TryRestartAsAdministrator")) {
    throw "일반 앱 전체를 관리자 권한으로 다시 시작하는 코드가 남아 있습니다."
}

foreach ($requiredReference in @("PipeSecurity", "GetNamedPipeClientProcessId")) {
    if (-not $bridgeSource.Contains($requiredReference)) {
        throw "보안 입력 채널 검증 코드가 없습니다: $requiredReference"
    }
}

if (-not $helperSource.Contains("GetNamedPipeServerProcessId")) {
    throw "입력 도우미가 부모 프로세스 신원을 확인하지 않습니다."
}

Write-Output "최소 권한 구조 분리 정적 검증 테스트 통과"
