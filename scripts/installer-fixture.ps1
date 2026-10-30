param([Parameter(Mandatory)][string]$ReadyFile, [switch]$IgnoreExit)
$ErrorActionPreference = 'Stop'
$mutex = [Threading.Mutex]::new($false, 'Local\CodexMeter.PackagingTest')
$event = [Threading.EventWaitHandle]::new($false, [Threading.EventResetMode]::AutoReset, 'Local\CodexMeter.PackagingTest.Exit')
try {
    Set-Content -LiteralPath $ReadyFile -Value 'ready'
    if ($IgnoreExit) { Start-Sleep -Seconds 120 } else { [void]$event.WaitOne(120000) }
} finally { $event.Dispose(); $mutex.Dispose() }
