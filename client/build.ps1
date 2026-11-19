<#
.SYNOPSIS
    Builds CUSTOS for Windows: runs the tests, publishes the programs and makes the installer.

.DESCRIPTION
    Needs: .NET 10 SDK, Inno Setup 6.3 or newer. The .NET Desktop Runtime installer that goes inside setup.exe is
    downloaded once from Microsoft into installer\redist and reused after that (put the file there by hand on a
    machine without internet).

    7z.dll comes from the exact 7-Zip build named in installer\7-zip.json, never from the 7-Zip installed on this
    machine: its x64 installer and 7zr.exe are downloaded once from github.com/ip7z/7zip into
    installer\redist\7-zip\<version>, refused if their SHA-256 differs from the pinned one, and 7z.dll is unpacked
    from the installer with 7zr.exe. Without internet, put those two files there by hand. The tests use that 7z.dll
    too (CUSTOS_7Z_DLL, and the installer as CUSTOS_7Z_INSTALLER).

    Results:
        artifacts\publish\                 the program files, as installed to C:\Program Files\CUSTOS
        artifacts\installer\CUSTOS-Setup-<version>.exe
        artifacts\update\custos-<version>.zip
                                           the update package for the monitoring server (A.10 of docs/protocol.md):
                                           the program files and manifest.json with the SHA-256 of each
        artifacts\update-test\custos-<test version>-test.zip
                                           with -TestUpdateVersion: the same programs built once more with a higher
                                           version, to try an update from the server on a machine that was just
                                           installed from this very build (its own update package has the version
                                           the machine already runs, so the server has nothing to update)

    The legacy edition for Windows 7 SP1 and 8.1 (-Legacy, CustosEdition.cs): the same programs for .NET 6, 32-bit,
    with the 32-bit .NET 6 Desktop Runtime inside setup.exe and 7z.dll from the 32-bit 7-Zip of the same pin. Its
    results go to artifacts-legacy\ under their own names: CUSTOS-Legacy-Setup-<version>.exe and
    custos-legacy-<version>.zip, whose manifest.json says "edition": "legacy" (A.10). The tests are built for .NET 10
    and run on the standard edition only, so -Legacy skips them.

    Code signing (optional): set CUSTOS_SIGN_PFX (path to a .pfx) and CUSTOS_SIGN_PASSWORD. Without them the
    files are left unsigned. CUSTOS_SIGNTOOL may point to signtool.exe if it is not found automatically.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\build.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\build.ps1 -SkipTests -SkipInstaller

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\build.ps1 -Legacy
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',

    # Product version, for example 0.1.0. Default: VersionPrefix from Directory.Build.props.
    [string] $Version,

    [switch] $SkipTests,

    [switch] $SkipInstaller,

    # Download the newest .NET Desktop Runtime (10, or 6 with -Legacy) even if one is already in installer\redist.
    [switch] $RefreshRuntime,

    # Also build the programs with this version (higher than -Version, for example 0.3.15.1) into an update package
    # of its own: install the setup, upload this package to the server and update to it.
    [string] $TestUpdateVersion,

    # The legacy edition for Windows 7 SP1 and 8.1: .NET 6, 32-bit (see above).
    [switch] $Legacy
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$Root = $PSScriptRoot
# What differs between the editions: the runtime, the processor, the names of the results.
if ($Legacy) {
    $Artifacts = Join-Path $Root 'artifacts-legacy'
    $RuntimeMajor = '6'
    $Platform = 'x86'
    $PeMachine = 0x014C
    $PackagePrefix = 'custos-legacy-'
    $SetupPrefix = 'CUSTOS-Legacy-Setup-'
}
else {
    $Artifacts = Join-Path $Root 'artifacts'
    $RuntimeMajor = '10'
    $Platform = 'x64'
    $PeMachine = 0x8664
    $PackagePrefix = 'custos-'
    $SetupPrefix = 'CUSTOS-Setup-'
}
$PublishDir = Join-Path $Artifacts 'publish'
$InstallerOutDir = Join-Path $Artifacts 'installer'
$UpdateOutDir = Join-Path $Artifacts 'update'
$TestUpdateOutDir = Join-Path $Artifacts 'update-test'
$RedistDir = Join-Path (Join-Path $Root 'installer') 'redist'
$SevenZipPin = Join-Path (Join-Path $Root 'installer') '7-zip.json'
$Programs = @('Custos.Service', 'Custos.Gui', 'Custos.Updater')
$OwnFiles = @('Custos.exe', 'Custos.dll', 'CustosService.exe', 'CustosService.dll',
              'Custos.Updater.exe', 'Custos.Updater.dll', 'Custos.Core.dll')
# The protocol of the monitoring server the programs speak (MonitorClient.ProtocolVersion); goes into manifest.json.
$MinProtocol = 1

function Write-Step([string] $Text) {
    Write-Host ''
    Write-Host "==> $Text" -ForegroundColor Cyan
}

function Stop-Build([string] $Text) {
    Write-Host ''
    Write-Host "BUILD FAILED: $Text" -ForegroundColor Red
    exit 1
}

function Invoke-Native([string] $File, [string[]] $Arguments) {
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) {
        Stop-Build "'$File $($Arguments -join ' ')' exited with code $LASTEXITCODE."
    }
}

function Get-ProgramFiles64 {
    if ($env:ProgramW6432) { return $env:ProgramW6432 }
    return $env:ProgramFiles
}

function Find-Iscc {
    $command = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $candidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path (Get-ProgramFiles64) 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe')
    )
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }
    return $null
}

function Find-SignTool {
    if ($env:CUSTOS_SIGNTOOL) { return $env:CUSTOS_SIGNTOOL }
    $command = Get-Command 'signtool.exe' -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }
    $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    if (Test-Path $kits) {
        $found = Get-ChildItem -Path $kits -Filter 'signtool.exe' -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.Directory.Name -eq 'x64' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($found) { return $found.FullName }
    }
    return $null
}

function Invoke-Sign([string[]] $Files) {
    if (-not $env:CUSTOS_SIGN_PFX) {
        Write-Host 'Not signing: CUSTOS_SIGN_PFX is not set.'
        return
    }
    $signTool = Find-SignTool
    if (-not $signTool) { Stop-Build 'CUSTOS_SIGN_PFX is set but signtool.exe was not found. Install the Windows SDK or set CUSTOS_SIGNTOOL.' }
    $timestamp = 'http://timestamp.digicert.com'
    if ($env:CUSTOS_SIGN_TIMESTAMP) { $timestamp = $env:CUSTOS_SIGN_TIMESTAMP }
    $arguments = @('sign', '/fd', 'SHA256', '/td', 'SHA256', '/tr', $timestamp, '/f', $env:CUSTOS_SIGN_PFX)
    if ($env:CUSTOS_SIGN_PASSWORD) { $arguments += @('/p', $env:CUSTOS_SIGN_PASSWORD) }
    Invoke-Native $signTool ($arguments + $Files)
}

function Get-DesktopRuntime {
    if (-not (Test-Path $RedistDir)) { New-Item -ItemType Directory -Path $RedistDir | Out-Null }
    $existing = Get-ChildItem -Path $RedistDir -Filter "windowsdesktop-runtime-$RuntimeMajor.*-win-$Platform.exe" -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match "^windowsdesktop-runtime-$RuntimeMajor\.\d+\.\d+-win-$Platform\.exe$" } |
        Sort-Object { [version]($_.Name -replace '^windowsdesktop-runtime-(\d+\.\d+\.\d+)-win-.*$', '$1') } -Descending |
        Select-Object -First 1
    if ($existing -and -not $RefreshRuntime) {
        Write-Host "Using $($existing.Name) from installer\redist."
        return $existing.FullName
    }

    Write-Host "Looking up the newest .NET $RuntimeMajor Desktop Runtime ($Platform) at Microsoft..."
    try {
        $metadata = Invoke-RestMethod -UseBasicParsing -Uri "https://builds.dotnet.microsoft.com/dotnet/release-metadata/$RuntimeMajor.0/releases.json"
    }
    catch {
        if ($existing) {
            Write-Warning "Cannot reach Microsoft ($($_.Exception.Message)); using $($existing.Name)."
            return $existing.FullName
        }
        Stop-Build ("Cannot download the .NET Desktop Runtime: $($_.Exception.Message)`n" +
            "Download windowsdesktop-runtime-$RuntimeMajor.x.x-win-$Platform.exe from https://dotnet.microsoft.com/download/dotnet/$RuntimeMajor.0 " +
            "and put it into $RedistDir")
    }

    $release = $metadata.releases | Select-Object -First 1
    $file = $release.windowsdesktop.files | Where-Object { $_.name -eq "windowsdesktop-runtime-win-$Platform.exe" } | Select-Object -First 1
    if (-not $file) { Stop-Build "The Microsoft release list has no windowsdesktop-runtime-win-$Platform.exe." }
    $target = Join-Path $RedistDir "windowsdesktop-runtime-$($release.windowsdesktop.version)-win-$Platform.exe"
    if (-not (Test-Path $target)) {
        Write-Host "Downloading .NET Desktop Runtime $($release.windowsdesktop.version)..."
        $partial = "$target.part"
        Invoke-WebRequest -UseBasicParsing -Uri $file.url -OutFile $partial
        $hash = (Get-FileHash -Path $partial -Algorithm SHA512).Hash
        if ($hash -ne $file.hash.ToUpperInvariant()) {
            Remove-Item $partial -Force
            Stop-Build 'The downloaded .NET Desktop Runtime has a wrong SHA-512 checksum.'
        }
        Move-Item $partial $target -Force
    }
    return $target
}

# Returns the file at $Url, from $Folder if it is already there with the right SHA-256, otherwise downloaded.
function Get-PinnedFile([string] $Url, [string] $Sha256, [string] $Folder) {
    $name = [IO.Path]::GetFileName(([Uri] $Url).AbsolutePath)
    $target = Join-Path $Folder $name
    $expected = $Sha256.ToUpperInvariant()
    if (Test-Path $target) {
        $hash = (Get-FileHash -Path $target -Algorithm SHA256).Hash
        if ($hash -eq $expected) {
            Write-Host "Using $name from $Folder."
            return $target
        }
        Write-Warning "$target has SHA-256 $hash instead of $expected; downloading it again."
        Remove-Item $target -Force
    }

    Write-Host "Downloading $Url ..."
    $partial = "$target.part"
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $Url -OutFile $partial
    }
    catch {
        Stop-Build "Cannot download $Url ($($_.Exception.Message)). Download it by hand and put it into $Folder"
    }
    $hash = (Get-FileHash -Path $partial -Algorithm SHA256).Hash
    if ($hash -ne $expected) {
        Remove-Item $partial -Force
        Stop-Build ("$name has SHA-256 $hash, but installer\7-zip.json expects $expected. The file was deleted and " +
            'not used. If 7-Zip really published a new file, check it and change installer\7-zip.json.')
    }
    Move-Item $partial $target -Force
    return $target
}

# Unpacks 7z.dll and the 7-Zip license from the pinned 7-Zip build (its 32-bit installer for -Legacy); returns the
# folder that holds them.
function Get-SevenZip {
    $pin = Get-Content -Raw -Path $SevenZipPin | ConvertFrom-Json
    $folder = Join-Path (Join-Path $RedistDir '7-zip') $pin.version
    if (-not (Test-Path $folder)) { New-Item -ItemType Directory -Path $folder | Out-Null }
    if ($Legacy) { $installer = Get-PinnedFile $pin.installer32Url $pin.installer32Sha256 $folder }
    else { $installer = Get-PinnedFile $pin.installerUrl $pin.installerSha256 $folder }
    $extractor = Get-PinnedFile $pin.extractorUrl $pin.extractorSha256 $folder
    # The tests of the 7-Zip update unpack this installer the way the service does.
    $env:CUSTOS_7Z_INSTALLER = $installer

    $out = Join-Path $folder "unpacked-$Platform"
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    Invoke-Native $extractor @('x', $installer, "-o$out", '-y', '-bso0', '-bsp0', '7z.dll', 'License.txt') | Out-Host
    foreach ($name in @('7z.dll', 'License.txt')) {
        if (-not (Test-Path (Join-Path $out $name))) { Stop-Build "$name is not in $installer." }
    }

    $dll = Join-Path $out '7z.dll'
    $bytes = [IO.File]::ReadAllBytes($dll)
    $peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
    $machine = [BitConverter]::ToUInt16($bytes, $peOffset + 4)
    if ($machine -ne $PeMachine) { Stop-Build "$dll is not a $Platform library." }
    Write-Host "7z.dll: 7-Zip $($pin.version), file version $((Get-Item $dll).VersionInfo.FileVersion)"
    return $out
}

# Publishes the three programs with $ProgramVersion into $Folder, with 7z.dll, the notices and the GPL text (LICENSE.txt),
# and signs them.
function Publish-Programs([string] $ProgramVersion, [string] $Folder, [string] $SevenZipFolder) {
    New-Item -ItemType Directory -Path $Folder -Force | Out-Null
    foreach ($program in $Programs) {
        $project = Join-Path $Root "src\$program\$program.csproj"
        $arguments = @('publish', $project, '-c', $Configuration, '-r', "win-$Platform", '--self-contained', 'false',
            "-p:Version=$ProgramVersion", '-o', $Folder)
        if ($Legacy) { $arguments += '-p:CustosLegacy=true' }
        Invoke-Native 'dotnet' $arguments
    }
    Copy-Item (Join-Path $SevenZipFolder '7z.dll') $Folder
    Copy-Item (Join-Path $SevenZipFolder 'License.txt') (Join-Path $Folder '7-Zip-License.txt')
    Copy-Item (Join-Path $Root 'THIRD-PARTY-NOTICES.txt') $Folder
    Copy-Item (Join-Path $Root '..\LICENSE') (Join-Path $Folder 'LICENSE.txt')
    Invoke-Sign ($OwnFiles | ForEach-Object { Join-Path $Folder $_ })
}

# Makes the update package (A.10): every file of $Folder and manifest.json {version, edition (legacy only),
# min_protocol, files[{path, sha256}]}. Paths use "/", as zip wants. $NameSuffix only marks the file
# (custos-<version><suffix>.zip, custos-legacy-... for -Legacy): the server reads the version and the edition from
# manifest.json. Returns the path of the zip.
function New-UpdatePackage([string] $PackageVersion, [string] $Folder, [string] $OutDir, [string] $NameSuffix = '') {
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
    $base = (Resolve-Path $Folder).Path.TrimEnd('\') + '\'
    $files = @(Get-ChildItem -Path $Folder -File -Recurse | Sort-Object FullName)
    $list = @()
    foreach ($file in $files) {
        $list += [ordered]@{
            path   = $file.FullName.Substring($base.Length).Replace('\', '/')
            sha256 = (Get-FileHash -Path $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    $manifest = [ordered]@{ version = $PackageVersion }
    if ($Legacy) { $manifest.edition = 'legacy' }
    $manifest.min_protocol = $MinProtocol
    $manifest.files = $list
    $json = ConvertTo-Json -InputObject $manifest -Depth 5

    $zipPath = Join-Path $OutDir "$PackagePrefix$PackageVersion$NameSuffix.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    $stream = [IO.File]::Open($zipPath, [IO.FileMode]::CreateNew)
    $zip = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $entry = $zip.CreateEntry('manifest.json', [IO.Compression.CompressionLevel]::Optimal)
        $writer = New-Object IO.StreamWriter($entry.Open(), (New-Object Text.UTF8Encoding($false)))
        try { $writer.Write($json) } finally { $writer.Dispose() }
        for ($i = 0; $i -lt $files.Count; $i++) {
            [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $files[$i].FullName, $list[$i].path,
                [IO.Compression.CompressionLevel]::Optimal) | Out-Null
        }
    }
    finally {
        $zip.Dispose()
        $stream.Dispose()
    }
    $hash = (Get-FileHash -Path $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host "Update package: $zipPath ($($files.Count) files, SHA-256 $hash)"
    return $zipPath
}

# ---------------------------------------------------------------------------------------------------------------

[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

Write-Step 'Checking the tools'
$dotnet = Get-Command 'dotnet' -ErrorAction SilentlyContinue
if (-not $dotnet) { Stop-Build '.NET SDK not found. Install the .NET 10 SDK x64: https://dotnet.microsoft.com/download/dotnet/10.0' }
$sdks = & dotnet --list-sdks
if (-not ($sdks | Where-Object { $_ -match '^10\.' })) { Stop-Build ".NET 10 SDK not found (installed: $($sdks -join '; ')). Install it from https://dotnet.microsoft.com/download/dotnet/10.0" }
Write-Host "dotnet: $(($sdks | Where-Object { $_ -match '^10\.' } | Select-Object -Last 1))"

$iscc = $null
if (-not $SkipInstaller) {
    $iscc = Find-Iscc
    if (-not $iscc) { Stop-Build 'Inno Setup 6 not found. Install it from https://jrsoftware.org/isdl.php (or run with -SkipInstaller).' }
    Write-Host "Inno Setup: $iscc"
}

if (-not $Version) {
    [xml] $props = Get-Content (Join-Path $Root 'Directory.Build.props')
    $Version = $props.SelectSingleNode('/Project/PropertyGroup/VersionPrefix').InnerText
}
# The same rule as the server and the service (A.10).
$versionPattern = '^\d{1,5}(\.\d{1,5}){1,3}([-+][0-9A-Za-z.-]{1,20})?$'
if ($Version -notmatch $versionPattern) { Stop-Build "Version '$Version' is not like 0.3.0 or 0.3.15." }
if ($Version -match '^(\d+\.\d+\.\d+)') { $fileVersion = $Matches[1] }
else { Stop-Build "Version '$Version' must start with three numbers, like 0.1.0." }
Write-Host "Version: $Version"
if ($Legacy) {
    Write-Host 'Edition: legacy (Windows 7 SP1 and 8.1, .NET 6, 32-bit)'
    if (-not $SkipTests) {
        Write-Host 'The tests are built for .NET 10 and run on the standard edition: skipped.'
        $SkipTests = $true
    }
}
if ($TestUpdateVersion) {
    if ($TestUpdateVersion -notmatch $versionPattern) { Stop-Build "Test update version '$TestUpdateVersion' is not like 0.3.15.1." }
    Write-Host "Test update version: $TestUpdateVersion"
}

Write-Step 'Getting 7z.dll'
$sevenZip = Get-SevenZip
$env:CUSTOS_7Z_DLL = Join-Path $sevenZip '7z.dll'

Write-Step 'Cleaning artifacts'
if (Test-Path $Artifacts) { Remove-Item $Artifacts -Recurse -Force }
New-Item -ItemType Directory -Path $PublishDir | Out-Null

$solution = Join-Path $Root 'Custos.sln'
if (-not $SkipTests) {
    Write-Step 'Running the tests'
    Invoke-Native 'dotnet' @('test', $solution, '-c', $Configuration, "-p:Version=$Version")
}

Write-Step 'Publishing the programs'
Publish-Programs $Version $PublishDir $sevenZip

Write-Step 'Making the update package'
$package = New-UpdatePackage $Version $PublishDir $UpdateOutDir
$testPackage = $null
if ($TestUpdateVersion) {
    Write-Step "Building the test update $TestUpdateVersion"
    $testPublish = Join-Path $Artifacts 'publish-test'
    Publish-Programs $TestUpdateVersion $testPublish $sevenZip
    $testPackage = New-UpdatePackage $TestUpdateVersion $testPublish $TestUpdateOutDir '-test'
}

if (-not $SkipInstaller) {
    Write-Step 'Getting the .NET Desktop Runtime for the installer'
    $runtime = Get-DesktopRuntime

    Write-Step 'Building the installer'
    $script = Join-Path $Root 'installer\custos.iss'
    $arguments = @("/DAppVersion=$Version", "/DFileVersion=$fileVersion", "/DPublishDir=$PublishDir",
        "/DOutputDir=$InstallerOutDir", "/DRuntimeInstaller=$runtime")
    if ($Legacy) { $arguments += '/DLegacy' }
    Invoke-Native $iscc ($arguments + $script)
    $setup = Join-Path $InstallerOutDir "$SetupPrefix$Version.exe"
    Invoke-Sign @($setup)
}

Write-Step 'Done'
Write-Host "Program files:  $PublishDir"
Write-Host "Update package: $package"
if ($testPackage) { Write-Host "Test update:    $testPackage" }
if (-not $SkipInstaller) { Write-Host "Installer:      $setup" -ForegroundColor Green }
