param(
    [ValidateSet('Classic', 'Expanded', 'Both')][string]$Edition = 'Both',
    [string]$DotNet = 'dotnet',
    [string]$ISCC = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    [switch]$SkipInstaller
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
# Keep downloaded build dependencies separate from the user's global cache.
$env:NUGET_PACKAGES = Join-Path $repo '.nuget/packages'
$env:DOTNET_CLI_HOME = Join-Path $repo '.tools/cli-home'
[xml]$project = Get-Content -LiteralPath (Join-Path $repo 'src/CodexMeter.csproj')
$version = [string]$project.Project.PropertyGroup.Version
$runtime = [string]$project.Project.PropertyGroup.RuntimeFrameworkVersion
$dist = Join-Path $repo 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$editions = if ($Edition -eq 'Both') { @('Classic', 'Expanded') } else { @($Edition) }
Push-Location $repo
try {
    foreach ($item in $editions) {
        # Fresh directory on every build avoids publishing stale runtime files.
        $run = Join-Path $repo ('artifacts/' + $item + '-' + [Guid]::NewGuid().ToString('N'))
        $payload = Join-Path $run 'app'
        & $DotNet publish 'src/CodexMeter.csproj' -c Release -r win-x64 --self-contained true "-p:MeterEdition=$item" -p:UseSharedCompilation=false -o $payload --nologo
        if ($LASTEXITCODE -ne 0) { throw "Publish failed: $item" }
        & (Join-Path $PSScriptRoot 'verify.ps1') -Executable (Join-Path $payload 'CodexMeter.exe') -ReportDirectory (Join-Path $run 'verification')
        $licenses = Join-Path $payload 'licenses'
        New-Item -ItemType Directory -Path $licenses -Force | Out-Null
        $core = Join-Path $env:NUGET_PACKAGES "microsoft.netcore.app.runtime.win-x64/$runtime"
        $desktop = Join-Path $env:NUGET_PACKAGES "microsoft.windowsdesktop.app.runtime.win-x64/$runtime"
        Copy-Item -LiteralPath (Join-Path $core 'LICENSE.TXT') -Destination (Join-Path $licenses 'dotnet-runtime-license.txt')
        Copy-Item -LiteralPath (Join-Path $core 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $licenses 'dotnet-third-party-notices.txt')
        Copy-Item -LiteralPath (Join-Path $desktop 'LICENSE') -Destination (Join-Path $licenses 'windowsdesktop-license.txt')
        foreach ($doc in @('LICENSE','README.md','README.en.md','PRIVACY.md','THIRD-PARTY-NOTICES.md','SECURITY.md','CHANGELOG.md')) {
            Copy-Item -LiteralPath (Join-Path $repo $doc) -Destination $payload
        }
        Copy-Item -LiteralPath (Join-Path $repo 'docs') -Destination (Join-Path $payload 'docs') -Recurse
        # Record hashes for every shipped file; no user settings or diagnostics enter the payload.
        $files = @(Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName | ForEach-Object {
            @{ path = [IO.Path]::GetRelativePath($payload, $_.FullName).Replace('\','/'); size = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
        })
        @{ version=$version; edition=$item; runtime=$runtime; architecture='win-x64'; files=$files } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $payload 'manifest.json') -Encoding utf8
        $zip = Join-Path $dist "CodexMeter-$version-$item-Portable-x64.zip"
        if (Test-Path -LiteralPath $zip) { throw "Release artifact already exists: $zip. Choose a new version or archive it first." }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::CreateFromDirectory($payload, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
        if (-not $SkipInstaller) {
            if (-not (Test-Path -LiteralPath $ISCC)) { throw 'Inno Setup 6 compiler not found. Pass -ISCC or use -SkipInstaller.' }
            $setup = Join-Path $dist "CodexMeter-$version-$item-Setup-x64.exe"
            if (Test-Path -LiteralPath $setup) { throw "Release artifact already exists: $setup" }
            & $ISCC /Qp "/DAppVersion=$version" "/DEdition=$item" "/DPayloadDir=$payload" "/DDistDir=$dist" (Join-Path $repo 'installer/CodexMeter.iss')
            if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed: $item" }
        }
        @{ edition=$item; payload=$payload; verification=(Join-Path $run 'verification') } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $repo "artifacts/latest-$item.json") -Encoding utf8
    }
    Get-ChildItem -LiteralPath $dist -File | Where-Object Extension -in '.exe','.zip' | Sort-Object Name | ForEach-Object {
        (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Name
    } | Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii
    Write-Output "Release files: $dist"
} finally { Pop-Location }
