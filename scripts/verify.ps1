param([Parameter(Mandatory)][string]$Executable, [Parameter(Mandatory)][string]$ReportDirectory)
$ErrorActionPreference = 'Stop'
$reports = @{'--verify'='verification.json'; '--verify-portability'='portability.json'; '--verify-alerts'='alerts.json'}
foreach ($flag in @('--verify', '--verify-portability', '--verify-alerts')) {
    $folder = Join-Path $ReportDirectory $flag.TrimStart('-')
    $process = Start-Process -FilePath $Executable -ArgumentList @($flag, ('"' + $folder + '"')) -WindowStyle Hidden -PassThru
    if (-not $process.WaitForExit(60000)) { $process.Kill(); throw "Verification timed out: $flag" }
    $process.Refresh()
    if ($process.ExitCode -ne 0) { throw "Verification failed: $flag ($folder)" }
    # Test names are explicit; a missing report is a failure, never a successful empty run.
    $report = Get-Content -LiteralPath (Join-Path $folder $reports[$flag]) -Raw | ConvertFrom-Json
    if (-not $report.passed) { throw "Failed report: $folder" }
    Write-Output "$flag : $($report.checks.Count) checks passed"
}
