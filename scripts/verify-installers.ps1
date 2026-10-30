param([string]$ISCC = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe")
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
[xml]$project = Get-Content -LiteralPath (Join-Path $repo 'src/CodexMeter.csproj')
$version = [string]$project.Project.PropertyGroup.Version
$run = Join-Path $repo ('artifacts/installer-test-' + [Guid]::NewGuid().ToString('N'))
$destination = Join-Path $run '한글 공백 설치'
New-Item -ItemType Directory -Path $run -Force | Out-Null
$checks = [Collections.Generic.List[string]]::new()
$setupPaths = @{}
$payloads = @{}
$startup = Join-Path ([Environment]::GetFolderPath('Startup')) 'Codex Meter Packaging Test.lnk'
$menu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Codex Meter Packaging Test.lnk'
$registry = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CodexMeter.PackagingTest_is1'
if ((Test-Path -LiteralPath $startup) -or (Test-Path -LiteralPath $menu) -or (Test-Path -LiteralPath $registry)) { throw 'An older packaging test exists; inspect it before running another.' }
if (-not ('CodexMeter.ShortcutProbe' -as [type])) {
    $shortcutCode = Get-Content -LiteralPath (Join-Path $repo 'src/Common/WindowsShortcut.cs') -Raw
    $shortcutCode += "`npublic static class ShortcutProbe { public static string Target(string path) => WindowsShortcut.Read(path).Target; }"
    Add-Type -TypeDefinition $shortcutCode
}
function Check($value, [string]$message) { if (-not $value) { throw $message }; $checks.Add($message) }
function RunSetup([string]$file, [string[]]$arguments, [bool]$expectFailure = $false) {
    $p = Start-Process -FilePath $file -ArgumentList $arguments -WindowStyle Hidden -PassThru
    if (-not $p.WaitForExit(60000)) { $p.Kill(); throw 'Installer test timed out.' }
    $p.Refresh()
    if ($expectFailure) { Check ($p.ExitCode -ne 0) 'busy app aborts installation' }
    elseif ($p.ExitCode -ne 0) { throw "Installer exit code $($p.ExitCode). See logs in $run" }
}
function ShortcutTarget([string]$path) {
    return [CodexMeter.ShortcutProbe]::Target($path)
}
function StartFixture([bool]$ignore = $false) {
    $ready = Join-Path $run ([Guid]::NewGuid().ToString('N') + '.ready')
    $a = @('-NoProfile','-File',('"' + (Join-Path $PSScriptRoot 'installer-fixture.ps1') + '"'),'-ReadyFile',('"' + $ready + '"'))
    if ($ignore) { $a += '-IgnoreExit' }
    $p = Start-Process -FilePath (Get-Process -Id $PID).Path -ArgumentList $a -WindowStyle Hidden -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while (-not (Test-Path -LiteralPath $ready)) { if ([DateTime]::UtcNow -gt $deadline -or $p.HasExited) { throw 'Fixture did not initialize.' }; Start-Sleep -Milliseconds 100 }
    return $p
}
try {
    foreach ($edition in @('Classic','Expanded')) {
        $latest = Get-Content -LiteralPath (Join-Path $repo "artifacts/latest-$edition.json") -Raw | ConvertFrom-Json
        $payloads[$edition] = $latest.payload
        & $ISCC /Q /DTestMode "/DAppVersion=$version" "/DEdition=$edition" "/DPayloadDir=$($latest.payload)" "/DDistDir=$run" (Join-Path $repo 'installer/CodexMeter.iss')
        if ($LASTEXITCODE -ne 0) { throw 'Test installer compilation failed.' }
        $setupPaths[$edition] = Join-Path $run "CodexMeter-$version-$edition-Setup-x64.exe"
    }
    $common = @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/DIR="' + $destination + '"'))
    RunSetup $setupPaths.Classic ($common + @('/TASKS=autostart',('/LOG="' + (Join-Path $run 'install.log') + '"')))
    $exe = Join-Path $destination 'app/CodexMeter.exe'
    Check (Test-Path -LiteralPath $exe) 'install into a Korean path with spaces'
    Check ((ShortcutTarget $startup) -eq $exe) 'startup shortcut points to installed executable'
    Check ((ShortcutTarget $menu) -eq $exe) 'Start menu shortcut points to installed executable'
    Check (Test-Path -LiteralPath $registry) 'Windows installed-apps registration exists'
    $managed = Join-Path $destination 'app/CodexMeter.dll'
    Check ((Get-FileHash -LiteralPath $managed).Hash -eq (Get-FileHash -LiteralPath (Join-Path $payloads.Classic 'CodexMeter.dll')).Hash) 'installed Classic payload matches release'
    # Preserve files not owned by setup; never exercise the user's real settings.
    $sentinel = Join-Path $destination 'keep-user-file.txt'
    Set-Content -LiteralPath $sentinel -Value 'preserve me'
    $stubborn = StartFixture $true
    try { RunSetup $setupPaths.Expanded ($common + @('/TASKS=autostart',('/LOG="' + (Join-Path $run 'blocked.log') + '"'))) $true }
    finally { if (-not $stubborn.HasExited) { $stubborn.Kill(); $stubborn.WaitForExit() } }
    Check ((Get-FileHash -LiteralPath $managed).Hash -eq (Get-FileHash -LiteralPath (Join-Path $payloads.Classic 'CodexMeter.dll')).Hash) 'blocked update leaves original executable unchanged'
    $fixture = StartFixture
    RunSetup $setupPaths.Expanded ($common + @('/TASKS=autostart',('/LOG="' + (Join-Path $run 'upgrade.log') + '"')))
    Check ($fixture.WaitForExit(3000)) 'update signals a running application to exit gracefully'
    Check ((Get-FileHash -LiteralPath $managed).Hash -eq (Get-FileHash -LiteralPath (Join-Path $payloads.Expanded 'CodexMeter.dll')).Hash) 'same-version edition switch replaces Classic with Expanded'
    Check (Test-Path -LiteralPath $sentinel) 'upgrade preserves unrelated user files'
    # Reinstall without explicit task switches: preserve current autostart state.
    RunSetup $setupPaths.Expanded ($common + @(('/LOG="' + (Join-Path $run 'repair.log') + '"')))
    Check (Test-Path -LiteralPath $startup) 'reinstall preserves existing startup registration'
    RunSetup $setupPaths.Expanded ($common + @('/TASKS=',('/LOG="' + (Join-Path $run 'no-startup.log') + '"')))
    Check (-not (Test-Path -LiteralPath $startup)) 'explicit startup opt-out removes old shortcut'
    & (Join-Path $PSScriptRoot 'verify.ps1') -Executable $exe -ReportDirectory (Join-Path $run 'installed-verification')
    $fixture = StartFixture
    RunSetup (Join-Path $destination 'unins000.exe') @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="' + (Join-Path $run 'uninstall.log') + '"'))
    Check ($fixture.WaitForExit(3000)) 'uninstall signals a running application to exit gracefully'
    Check (-not (Test-Path -LiteralPath $exe)) 'uninstall removes managed executable'
    Check (-not (Test-Path -LiteralPath $registry)) 'uninstall removes installed-apps registration'
    Check (-not (Test-Path -LiteralPath $menu) -and -not (Test-Path -LiteralPath $startup)) 'uninstall removes its shortcuts'
    Check (Test-Path -LiteralPath $sentinel) 'uninstall preserves unrelated user files'
    @{passed=$true; checks=@($checks); scope='isolated test AppId, shortcuts, mutex and directory; real installation untouched'} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $run 'installer-verification.json') -Encoding utf8
    Write-Output "Installer verification passed ($($checks.Count) checks): $run"
} catch {
    @{passed=$false; checks=@($checks); error=$_.Exception.Message} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $run 'installer-verification.json') -Encoding utf8
    throw
}
