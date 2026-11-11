<#
.SYNOPSIS
    Builds CUSTOS for Windows: runs the tests, publishes the programs and makes the installer.

.DESCRIPTION
    Needs: .NET 10 SDK, Inno Setup 6.3 or newer. Optional: 7-Zip x64 (7z.dll is shipped from stage 2 on).
    The .NET Desktop Runtime installer that goes inside setup.exe is downloaded once from Microsoft into
    installer\redist and reused after that (put the file there by hand on a machine without internet).

    Results:
        artifacts\publish\                 the program files, as installed to C:\Program Files\CUSTOS
        artifacts\installer\CUSTOS-Setup-<version>.exe

    Code signing (optional): set CUSTOS_SIGN_PFX (path to a .pfx) and CUSTOS_SIGN_PASSWORD. Without them the
    files are left unsigned. CUSTOS_SIGNTOOL may point to signtool.exe if it is not found automatically.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\build.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\build.ps1 -SkipTests -SkipInstaller
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',

    # Product version, for example 0.1.0. Default: VersionPrefix from Directory.Build.props.
    [string] $Version,

    [switch] $SkipTests,

    [switch] $SkipInstaller,

    # Download the newest .NET 10 Desktop Runtime even if one is already in installer\redist.
    [switch] $RefreshRuntime
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$Root = $PSScriptRoot
$Artifacts = Join-Path $Root 'artifacts'
$PublishDir = Join-Path $Artifacts 'publish'
$InstallerOutDir = Join-Path $Artifacts 'installer'
$RedistDir = Join-Path (Join-Path $Root 'installer') 'redist'
$Programs = @('Custos.Service', 'Custos.Gui', 'Custos.Updater')
$OwnFiles = @('Custos.exe', 'Custos.dll', 'CustosService.exe', 'CustosService.dll',
              'Custos.Updater.exe', 'Custos.Updater.dll', 'Custos.Core.dll')

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
    $existing = Get-ChildItem -Path $RedistDir -Filter 'windowsdesktop-runtime-10.*-win-x64.exe' -ErrorAction SilentlyContinue |
        Sort-Object { [version]($_.Name -replace '^windowsdesktop-runtime-(10\.\d+\.\d+)-win-x64\.exe$', '$1') } -Descending |
        Select-Object -First 1
    if ($existing -and -not $RefreshRuntime) {
        Write-Host "Using $($existing.Name) from installer\redist."
        return $existing.FullName
    }

    Write-Host 'Looking up the newest .NET 10 Desktop Runtime at Microsoft...'
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    try {
        $metadata = Invoke-RestMethod -UseBasicParsing -Uri 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
    }
    catch {
        if ($existing) {
            Write-Warning "Cannot reach Microsoft ($($_.Exception.Message)); using $($existing.Name)."
            return $existing.FullName
        }
        Stop-Build ("Cannot download the .NET Desktop Runtime: $($_.Exception.Message)`n" +
            "Download windowsdesktop-runtime-10.x.x-win-x64.exe from https://dotnet.microsoft.com/download/dotnet/10.0 " +
            "and put it into $RedistDir")
    }

    $release = $metadata.releases | Select-Object -First 1
    $file = $release.windowsdesktop.files | Where-Object { $_.name -eq 'windowsdesktop-runtime-win-x64.exe' } | Select-Object -First 1
    if (-not $file) { Stop-Build 'The Microsoft release list has no windowsdesktop-runtime-win-x64.exe.' }
    $target = Join-Path $RedistDir "windowsdesktop-runtime-$($release.windowsdesktop.version)-win-x64.exe"
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

# ---------------------------------------------------------------------------------------------------------------

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

$sevenZipDll = Join-Path (Get-ProgramFiles64) '7-Zip\7z.dll'
if (Test-Path $sevenZipDll) {
    Write-Host "7-Zip: $sevenZipDll"
}
else {
    Write-Warning '7-Zip x64 not found; 7z.dll is not included. That is fine now, it is needed from stage 2 on (https://www.7-zip.org).'
    $sevenZipDll = $null
}

if (-not $Version) {
    [xml] $props = Get-Content (Join-Path $Root 'Directory.Build.props')
    $Version = $props.SelectSingleNode('/Project/PropertyGroup/VersionPrefix').InnerText
}
if ($Version -match '^(\d+\.\d+\.\d+)') { $fileVersion = $Matches[1] }
else { Stop-Build "Version '$Version' must start with three numbers, like 0.1.0." }
Write-Host "Version: $Version"

Write-Step 'Cleaning artifacts'
if (Test-Path $Artifacts) { Remove-Item $Artifacts -Recurse -Force }
New-Item -ItemType Directory -Path $PublishDir | Out-Null

$solution = Join-Path $Root 'Custos.sln'
if (-not $SkipTests) {
    Write-Step 'Running the tests'
    Invoke-Native 'dotnet' @('test', $solution, '-c', $Configuration, "-p:Version=$Version")
}

Write-Step 'Publishing the programs'
foreach ($program in $Programs) {
    $project = Join-Path $Root "src\$program\$program.csproj"
    Invoke-Native 'dotnet' @('publish', $project, '-c', $Configuration, '-r', 'win-x64', '--self-contained', 'false',
        "-p:Version=$Version", '-o', $PublishDir)
}
if ($sevenZipDll) { Copy-Item $sevenZipDll $PublishDir }
Invoke-Sign ($OwnFiles | ForEach-Object { Join-Path $PublishDir $_ })

if (-not $SkipInstaller) {
    Write-Step 'Getting the .NET Desktop Runtime for the installer'
    $runtime = Get-DesktopRuntime

    Write-Step 'Building the installer'
    $script = Join-Path $Root 'installer\custos.iss'
    Invoke-Native $iscc @("/DAppVersion=$Version", "/DFileVersion=$fileVersion", "/DPublishDir=$PublishDir",
        "/DOutputDir=$InstallerOutDir", "/DRuntimeInstaller=$runtime", $script)
    $setup = Join-Path $InstallerOutDir "CUSTOS-Setup-$Version.exe"
    Invoke-Sign @($setup)
}

Write-Step 'Done'
Write-Host "Program files: $PublishDir"
if (-not $SkipInstaller) { Write-Host "Installer:     $setup" -ForegroundColor Green }
