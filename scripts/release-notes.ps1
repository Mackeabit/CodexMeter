param(
    [Parameter(Mandatory)][string]$Version,
    [Parameter(Mandatory)][string]$OutDirectory,
    [string]$RunUrl = '',
    [string]$Repository = 'Mackeabit/CodexMeter'
)
# Writes release-notes.md and release-title.txt for v$Version.
# docs/release-notes/v<version>.md wins when present ("# Title" on its first line, {{RUN_URL}} placeholder).
# Otherwise the notes are built from the CHANGELOG section plus a standard download table.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$tag = "v$Version"
$download = "https://github.com/$Repository/releases/download/$tag"
New-Item -ItemType Directory -Path $OutDirectory -Force | Out-Null

$custom = Join-Path $repo "docs/release-notes/$tag.md"
if (Test-Path -LiteralPath $custom) {
    $lines = Get-Content -LiteralPath $custom -Encoding utf8
    $title = "CodexMeter $tag"
    if ($lines.Count -gt 0 -and $lines[0].StartsWith('# ')) { $title = $lines[0].Substring(2).Trim(); $lines = $lines | Select-Object -Skip 1 }
    $body = ($lines -join "`n").Trim().Replace('{{RUN_URL}}', $RunUrl).Replace('{{VERSION}}', $Version)
} else {
    $changelog = Get-Content -LiteralPath (Join-Path $repo 'CHANGELOG.md') -Raw -Encoding utf8
    $match = [regex]::Match($changelog, "(?ms)^## $([regex]::Escape($Version))\b[^\n]*\n(.*?)(?=^## |\z)")
    if (-not $match.Success) { throw "CHANGELOG.md has no section for $Version." }
    $previous = git -C $repo tag --list 'v*' --sort=-v:refname | Where-Object { $_ -ne $tag } | Select-Object -First 1
    $title = "CodexMeter $tag"
    $rows = @(
        @('Dual', 'Codex·Claude 이중 링'),
        @('Classic', '기존 작은 막대·숫자와 리셋 날짜'),
        @('Expanded', '한도가 하나면 크게, 두 개면 세로 정렬')
    ) | ForEach-Object { "| **$($_[0])** — $($_[1]) | [$($_[0]) 설치]($download/CodexMeter-$Version-$($_[0])-Setup-x64.exe) | [$($_[0]) 포터블]($download/CodexMeter-$Version-$($_[0])-Portable-x64.zip) |" }
    $verification = if ($RunUrl) { "- [Windows CI 빌드 및 검증]($RunUrl)에서 생성하고 설치·업데이트·제거 검사를 통과한 파일을 배포합니다." } else { '- Windows CI 빌드 및 검증을 통과한 파일을 배포합니다.' }
    $compare = if ($previous) { "`n**전체 변경 내역:** https://github.com/$Repository/compare/$previous...$tag" } else { '' }
    $body = @"
## 다운로드

| 디자인 | 설치형 EXE | 포터블 ZIP |
| --- | --- | --- |
$($rows -join "`n")

여섯 파일 모두 Windows x64용이며 **.NET 10 실행환경을 포함**합니다. 원하는 디자인 하나를 선택하세요. 포터블은 ZIP 전체를 풀고 ``CodexMeter.exe``를 실행하면 됩니다.

## 변경 내용

$($match.Groups[1].Value.Trim())

## 업데이트

새 설치 EXE를 실행하면 기존 앱에 정상 종료를 요청한 뒤 업데이트합니다. 사용자 설정, 중복 알림 방지 기록, 자동 실행 등록을 유지합니다. 세 디자인은 같은 설치 위치를 사용하므로 다른 디자인을 설치하면 교체됩니다.

## 검증

$verification

EXE는 아직 코드 서명이 없습니다. Windows SmartScreen이 경고할 수 있습니다. [SHA256SUMS.txt]($download/SHA256SUMS.txt)로 받은 파일의 무결성을 확인할 수 있으며, 해시는 코드 서명을 대신하지 않습니다.

OpenAI·Anthropic과 무관한 커뮤니티 프로젝트입니다.
$compare
"@
}
Set-Content -LiteralPath (Join-Path $OutDirectory 'release-notes.md') -Value $body -Encoding utf8
Set-Content -LiteralPath (Join-Path $OutDirectory 'release-title.txt') -Value $title -Encoding utf8
Write-Output "Release notes for ${tag}: $title"
