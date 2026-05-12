<#
.SYNOPSIS
    Sets up a development environment for the Busy User Bot project.

.DESCRIPTION
    Idempotent bootstrap script. Safe to re-run; each step checks for prior
    installation and skips work that has already been done.

    Installs / configures:
      * winget          (verified present)
      * .NET 8 SDK      (host architecture)
      * Python 3        (for tools/test_client.py)
      * Python package: bleak
      * VS Code + extensions:
          - ms-dotnettools.csdevkit
          - ms-vscode.cpptools
          - vscode-arduino-community.vscode-arduino-community
      * arduino-cli
      * Arduino ESP32 board package (3.x)
      * Arduino libraries: NimBLE-Arduino, ArduinoJson,
        Adafruit GFX Library, Adafruit ST7735 and ST7789 Library
      * LM Studio       (opt-in only, pass -InstallLMStudio)

    Does NOT touch:
      * %APPDATA%\BusyUserBot\settings.json (other than mirroring the
        DEVICE_TOKEN to keep it in sync with firmware/secrets.h)

.PARAMETER InstallLMStudio
    Also install LM Studio (a multi-GB download). Off by default; pass this
    flag if you want a local AI backend instead of Azure OpenAI / another
    OpenAI-compatible endpoint. When set, the script will also try to
    download the multimodal model named by -LMStudioModel via the bundled
    `lms` CLI.

.PARAMETER LMStudioModel
    Model identifier to download with `lms get` after LM Studio is installed.
    Only used when -InstallLMStudio is also set. Defaults to Qwen 3.5-9B
    (latest multimodal model with excellent visual understanding and reasoning).
    Pass an empty string to skip the download (e.g. -LMStudioModel '').

.PARAMETER SkipPython
    Skip Python + bleak install (skip if you don't plan to use test_client.py).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts/dev-env-setup.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts/dev-env-setup.ps1 -InstallLMStudio

.EXAMPLE
    # Install LM Studio but skip the (large) model download.
    powershell -ExecutionPolicy Bypass -File scripts/dev-env-setup.ps1 -InstallLMStudio -LMStudioModel ''
#>

[CmdletBinding()]
param(
    [switch] $InstallLMStudio,
    [string] $LMStudioModel = 'lmstudio-community/Qwen3.5-9B-GGUF',
    [switch] $SkipPython
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# winget emits UTF-8 box-drawing characters and progress spinners. The default
# Windows console code page (437/850/1252) renders these as mojibake like
# "ΓûêΓûêΓûê...". Switch the console (input + output) to UTF-8 for the duration
# of this script. Restored on exit.
$prevOutEnc = [Console]::OutputEncoding
$prevInEnc  = [Console]::InputEncoding
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }
try { [Console]::InputEncoding  = [System.Text.Encoding]::UTF8 } catch { }
$OutputEncoding = [System.Text.Encoding]::UTF8

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
function Write-Step    ([string]$msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok      ([string]$msg) { Write-Host "  OK $msg" -ForegroundColor Green }
function Write-Skip    ([string]$msg) { Write-Host "  -- $msg" -ForegroundColor DarkGray }
function Write-WarnMsg ([string]$msg) { Write-Host "  !! $msg" -ForegroundColor Yellow }

function Test-Command([string]$name) {
    $null -ne (Get-Command $name -ErrorAction SilentlyContinue)
}

function Refresh-Path {
    # Pick up PATH changes made by installers in the current session.
    $machine = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    $user    = [Environment]::GetEnvironmentVariable('Path', 'User')
    $env:Path = "$machine;$user"
}

function Install-WingetPackage {
    param(
        [Parameter(Mandatory)] [string] $Id,
        [Parameter(Mandatory)] [string] $FriendlyName,
        [string] $TestCommand = $null
    )

    if ($TestCommand -and (Test-Command $TestCommand)) {
        Write-Skip "$FriendlyName already installed ($TestCommand on PATH)"
        return
    }

    # winget list returns nonzero when the package is not installed; capture and
    # check the output instead of relying on the exit code.
    $listed = & winget list --id $Id --exact --accept-source-agreements 2>&1 | Out-String
    if ($listed -match [regex]::Escape($Id)) {
        Write-Skip "$FriendlyName already installed (winget reports $Id)"
        Refresh-Path
        return
    }

    Write-Step "Installing $FriendlyName via winget ($Id)"
    # Do NOT pipe winget's output. winget uses carriage returns to redraw its
    # progress bar and spinner in place; piping turns each frame into a new
    # line ("- \ | / -" repeated dozens of times). Letting it write directly
    # to the console preserves the in-place animation.
    & winget install --id $Id --exact --silent `
        --accept-source-agreements --accept-package-agreements `
        --disable-interactivity
    if ($LASTEXITCODE -ne 0) {
        throw "winget install failed for $Id (exit $LASTEXITCODE)"
    }
    Refresh-Path
    Write-Ok "$FriendlyName installed"
}

function Install-VSCodeExtension {
    param(
        # One or more candidate marketplace IDs. The first that succeeds wins.
        # Useful when an extension has been republished under a new id.
        [Parameter(Mandatory)] [string[]] $Ids,
        # If true, a failure to install any candidate is a warning instead of
        # an error. Use for nice-to-have extensions.
        [switch] $Optional
    )

    $installed = & code --list-extensions 2>$null
    foreach ($id in $Ids) {
        if ($installed -contains $id) {
            Write-Skip "VS Code extension $id already installed"
            return
        }
    }

    foreach ($id in $Ids) {
        Write-Step "Installing VS Code extension $id"
        $output = & code --install-extension $id --force 2>&1
        $output | Out-Host
        if ($LASTEXITCODE -eq 0 -and ($output -notmatch 'not found in the (Marketplace|gallery)')) {
            Write-Ok "Extension $id installed"
            return
        }
        Write-WarnMsg "Could not install $id (exit $LASTEXITCODE)"
    }

    $msg = "None of the candidate extension IDs installed: $($Ids -join ', ')"
    if ($Optional) { Write-WarnMsg $msg } else { throw $msg }
}

# ---------------------------------------------------------------------------
# 0. Pre-flight
# ---------------------------------------------------------------------------
Write-Step "Pre-flight checks"

if ($PSVersionTable.PSVersion.Major -lt 5) {
    throw "Requires PowerShell 5.1 or newer."
}
if (-not (Test-Command winget)) {
    throw "winget not found. Install 'App Installer' from the Microsoft Store, then re-run."
}
Write-Ok "winget present"

# ---------------------------------------------------------------------------
# 1. .NET 8 SDK
# ---------------------------------------------------------------------------
Write-Step ".NET 8 SDK"
$haveNet8 = $false
if (Test-Command dotnet) {
    $sdks = & dotnet --list-sdks 2>$null
    if ($sdks -match '^8\.') { $haveNet8 = $true }
}
if ($haveNet8) {
    Write-Skip ".NET 8 SDK already installed"
} else {
    Install-WingetPackage -Id 'Microsoft.DotNet.SDK.8' -FriendlyName '.NET 8 SDK'
}

# ---------------------------------------------------------------------------
# 2. VS Code + extensions
# ---------------------------------------------------------------------------
Write-Step "VS Code"
Install-WingetPackage -Id 'Microsoft.VisualStudioCode' -FriendlyName 'Visual Studio Code' -TestCommand 'code'

if (-not (Test-Command code)) {
    Write-WarnMsg "'code' is not on PATH yet. Open a new terminal (or sign out/in) and re-run, then VS Code extensions will install."
} else {
    Install-VSCodeExtension -Ids 'ms-dotnettools.csdevkit'
    Install-VSCodeExtension -Ids 'ms-vscode.cpptools'
    # Arduino integration is optional; the C/C++ extension alone is enough
    # for IntelliSense via .vscode/c_cpp_properties.json. Try a couple of
    # known marketplace IDs in order; warn if none are available.
    Install-VSCodeExtension -Optional -Ids @(
        'vscode-arduino.vscode-arduino-community',
        'moozzyk.ArduinoTools',
        'vsciot-vscode.vscode-arduino'
    )
}

# ---------------------------------------------------------------------------
# 3. Python + bleak (for tools/test_client.py)
# ---------------------------------------------------------------------------
if ($SkipPython) {
    Write-Skip "Python install skipped (-SkipPython)"
} else {
    Write-Step "Python 3"
    Install-WingetPackage -Id 'Python.Python.3.12' -FriendlyName 'Python 3.12' -TestCommand 'python'

    if (Test-Command python) {
        Write-Step "Python package: bleak"
        # `pip show <pkg>` exits non-zero and prints a warning to stderr if the
        # package isn't installed. With $ErrorActionPreference='Stop' even a
        # redirected stderr write surfaces as a NativeCommandError, so probe
        # via cmd which keeps everything inside its own process and only
        # exposes the exit code to PowerShell.
        $null = & cmd /c 'python -m pip show bleak >nul 2>&1'
        $haveBleak = ($LASTEXITCODE -eq 0)
        $global:LASTEXITCODE = 0
        if ($haveBleak) {
            Write-Skip "bleak already installed"
        } else {
            & python -m pip install --upgrade --user bleak | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "pip install bleak failed" }
            Write-Ok "bleak installed"
        }
    } else {
        Write-WarnMsg "python not on PATH yet; skipping bleak. Re-run after restarting the terminal."
    }
}

# ---------------------------------------------------------------------------
# 4. arduino-cli + ESP32 core + libraries
# ---------------------------------------------------------------------------
Write-Step "arduino-cli"
Install-WingetPackage -Id 'ArduinoSA.CLI' -FriendlyName 'arduino-cli' -TestCommand 'arduino-cli'

if (-not (Test-Command arduino-cli)) {
    Write-WarnMsg "arduino-cli not on PATH yet. Open a new terminal and re-run to install board package and libraries."
    return
}

Write-Step "arduino-cli config: ESP32 board manager URL"
$espUrl = 'https://raw.githubusercontent.com/espressif/arduino-esp32/gh-pages/package_esp32_index.json'
$cfg = & arduino-cli config dump 2>$null | Out-String
if ($cfg -match [regex]::Escape($espUrl)) {
    Write-Skip "ESP32 board URL already configured"
} else {
    # Initialise config file if missing, then add the URL.
    if (-not ($cfg.Trim())) { & arduino-cli config init | Out-Null }
    & arduino-cli config add board_manager.additional_urls $espUrl | Out-Null
    Write-Ok "ESP32 board URL added"
}

Write-Step "arduino-cli core index update"
& arduino-cli core update-index | Out-Host
if ($LASTEXITCODE -ne 0) { throw "arduino-cli core update-index failed" }

Write-Step "ESP32 board package (esp32:esp32)"
$installedCores = & arduino-cli core list 2>$null | Out-String
if ($installedCores -match '^esp32:esp32\s') {
    Write-Skip "esp32:esp32 core already installed"
} else {
    & arduino-cli core install esp32:esp32 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Failed to install esp32:esp32 core" }
    Write-Ok "esp32:esp32 core installed"
}

# Arduino libraries
$libs = @(
    @{ Name = 'NimBLE-Arduino';                       Match = 'NimBLE-Arduino' },
    @{ Name = 'ArduinoJson';                          Match = '^ArduinoJson\s' },
    @{ Name = 'Adafruit GFX Library';                 Match = 'Adafruit GFX Library' },
    @{ Name = 'Adafruit ST7735 and ST7789 Library';   Match = 'Adafruit ST7735' }
)
$installedLibs = & arduino-cli lib list 2>$null | Out-String
foreach ($lib in $libs) {
    Write-Step "Arduino library: $($lib.Name)"
    if ($installedLibs -match $lib.Match) {
        Write-Skip "$($lib.Name) already installed"
        continue
    }
    & arduino-cli lib install $lib.Name | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Failed to install Arduino library $($lib.Name)" }
    Write-Ok "$($lib.Name) installed"
}

# ---------------------------------------------------------------------------
# 5. LM Studio (optional)
# ---------------------------------------------------------------------------
if (-not $InstallLMStudio) {
    Write-Skip "LM Studio install skipped (pass -InstallLMStudio to opt in)"
} else {
    Write-Step "LM Studio"
    Install-WingetPackage -Id 'ElementLabs.LMStudio' -FriendlyName 'LM Studio'

    # Locate the bundled `lms` CLI. winget puts LM Studio under
    # %LOCALAPPDATA%\Programs\LM Studio\, and the CLI ships at
    # %USERPROFILE%\.lmstudio\bin\lms.exe after first launch / bootstrap.
    Refresh-Path
    $lmsExe = $null
    if (Test-Command lms) {
        $lmsExe = 'lms'
    } else {
        $candidate = Join-Path $env:USERPROFILE '.lmstudio\bin\lms.exe'
        if (Test-Path $candidate) { $lmsExe = $candidate }
    }

    if (-not $lmsExe) {
        Write-WarnMsg "`lms` CLI not found yet. Launch LM Studio once to finish setup, then re-run this script (or run 'lms get $LMStudioModel' manually) to download the model."
    } elseif ([string]::IsNullOrWhiteSpace($LMStudioModel)) {
        Write-Skip "LM Studio model download skipped (-LMStudioModel '')"
    } else {
        Write-Step "LM Studio model: $LMStudioModel"
        # `lms ls` writes status messages ("Waking up LM Studio service...")
        # to stderr. Under $ErrorActionPreference='Stop' that surfaces as a
        # NativeCommandError, so route through cmd.exe (which swallows stderr
        # in-process) and capture stdout via a temp file.
        #
        # The first invocation of *any* `lms` subcommand on a cold machine
        # boots the LM Studio backend service, which can sit silent for
        # 30-60s. Warn the user and cap the wait so a wedged backend doesn't
        # freeze the whole installer.
        Write-Host "    Checking installed models (lms may take ~30-60s to wake the LM Studio service the first time)..." -ForegroundColor DarkGray
        $lsOut = New-TemporaryFile
        $lsErr = New-TemporaryFile
        $existing = $null
        try {
            $proc = Start-Process -FilePath $lmsExe -ArgumentList @('ls') `
                -NoNewWindow -PassThru `
                -RedirectStandardOutput $lsOut.FullName `
                -RedirectStandardError  $lsErr.FullName
            if (-not $proc.WaitForExit(90000)) {
                try { $proc.Kill() } catch { }
                Write-WarnMsg "lms ls timed out after 90s (LM Studio backend not responding). Skipping model check."
            } else {
                $existing = Get-Content -Raw -LiteralPath $lsOut.FullName -ErrorAction SilentlyContinue
            }
            $global:LASTEXITCODE = 0
        } finally {
            Remove-Item -LiteralPath $lsOut.FullName -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $lsErr.FullName -ErrorAction SilentlyContinue
        }
        if ($existing -and ($existing -match [regex]::Escape($LMStudioModel))) {
            Write-Skip "Model $LMStudioModel already downloaded"
        } else {
            # Don't try to drive `lms get` from inside the script. In practice
            # it produces no visible progress when launched as a child of
            # PowerShell, ignores Ctrl+C, and can stall for minutes while the
            # LM Studio backend warms up - leaving the user with a frozen
            # terminal and no way out short of killing the window. Just print
            # the exact command and let the user run it interactively.
            Write-WarnMsg "Model $LMStudioModel is not downloaded yet."
            Write-Host ""
            Write-Host "    Run this in a separate terminal to download it (multi-GB):" -ForegroundColor Yellow
            Write-Host ""
            Write-Host "        `"$lmsExe`" get $LMStudioModel --yes" -ForegroundColor White
            Write-Host ""
            Write-Host "    Or download it from the LM Studio UI (Discover > search '$LMStudioModel')." -ForegroundColor DarkGray
        }
    }
}

# ---------------------------------------------------------------------------
# 6. Device token (firmware secrets.h + controller settings.json)
# ---------------------------------------------------------------------------
Write-Step "Device token (firmware <-> controller shared secret)"

$repoRoot       = Split-Path -Parent $PSScriptRoot
$secretsPath    = Join-Path $repoRoot 'firmware\BusyUserBot\secrets.h'
$secretsExample = Join-Path $repoRoot 'firmware\BusyUserBot\secrets_example.h'
$settingsExample = Join-Path $repoRoot 'controller\src\BusyUserBot\appsettings.example.json'
$settingsPath   = Join-Path $env:APPDATA 'BusyUserBot\settings.json'

# Default placeholder values shipped in the example files. Treat these as
# "unset" so we replace them rather than considering the side configured.
$placeholderFirmware  = 'change-me-to-a-long-random-string'
$placeholderSettings  = 'change-me-to-match-firmware-secrets'

function Get-FirmwareToken([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $line = Select-String -LiteralPath $path -Pattern '^\s*#define\s+DEVICE_TOKEN\s+"([^"]*)"' -ErrorAction SilentlyContinue
    if (-not $line) { return $null }
    return $line.Matches[0].Groups[1].Value
}

function Set-FirmwareToken([string]$path, [string]$token) {
    $content = Get-Content -Raw -LiteralPath $path
    $new = [regex]::Replace(
        $content,
        '(^\s*#define\s+DEVICE_TOKEN\s+")[^"]*(")',
        { param($m) $m.Groups[1].Value + $token + $m.Groups[2].Value },
        [System.Text.RegularExpressions.RegexOptions]::Multiline)
    Set-Content -LiteralPath $path -Value $new -NoNewline:$false -Encoding UTF8
}

function Get-SettingsToken([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try {
        $obj = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json
        if ($obj -and $obj.Dongle -and $obj.Dongle.PSObject.Properties['Token']) {
            return [string]$obj.Dongle.Token
        }
    } catch { }
    return $null
}

function Set-SettingsToken([string]$path, [string]$token) {
    $obj = $null
    if (Test-Path -LiteralPath $path) {
        try { $obj = Get-Content -Raw -LiteralPath $path | ConvertFrom-Json } catch { $obj = $null }
    }
    if (-not $obj -and (Test-Path -LiteralPath $settingsExample)) {
        $obj = Get-Content -Raw -LiteralPath $settingsExample | ConvertFrom-Json
    }
    if (-not $obj) {
        # Minimal fallback if the example file is missing for some reason.
        $obj = [pscustomobject]@{ Dongle = [pscustomobject]@{ Name = 'BusyUserBot'; DeviceId = ''; Token = '' } }
    }
    if (-not $obj.Dongle) {
        $obj | Add-Member -NotePropertyName Dongle -NotePropertyValue ([pscustomobject]@{ Name='BusyUserBot'; DeviceId=''; Token='' })
    }
    if ($obj.Dongle.PSObject.Properties['Token']) {
        $obj.Dongle.Token = $token
    } else {
        $obj.Dongle | Add-Member -NotePropertyName Token -NotePropertyValue $token
    }
    $dir = Split-Path -Parent $path
    if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    ($obj | ConvertTo-Json -Depth 10) | Set-Content -LiteralPath $path -Encoding UTF8
}

function New-DeviceToken {
    # 64 hex chars (~256 bits of entropy); safe in C string literals and JSON.
    return ([Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N'))
}

# Bootstrap secrets.h from the example file if missing.
if (-not (Test-Path -LiteralPath $secretsPath)) {
    if (Test-Path -LiteralPath $secretsExample) {
        Copy-Item -LiteralPath $secretsExample -Destination $secretsPath
        Write-Ok "Created firmware\BusyUserBot\secrets.h from secrets_example.h"
    } else {
        Write-WarnMsg "secrets_example.h not found at $secretsExample; cannot create secrets.h."
    }
}

$firmwareToken = Get-FirmwareToken $secretsPath
$settingsToken = Get-SettingsToken $settingsPath

# A token "set" means: present, non-empty, and not the example placeholder.
function Test-TokenSet([string]$tok, [string[]]$placeholders) {
    if ([string]::IsNullOrWhiteSpace($tok)) { return $false }
    foreach ($p in $placeholders) { if ($tok -eq $p) { return $false } }
    return $true
}

$firmwareSet = Test-TokenSet $firmwareToken @($placeholderFirmware)
$settingsSet = Test-TokenSet $settingsToken @($placeholderSettings)

if ($firmwareSet -and $settingsSet -and ($firmwareToken -eq $settingsToken)) {
    Write-Skip "DEVICE_TOKEN already set and matches in both secrets.h and settings.json"
}
elseif ($firmwareSet -and $settingsSet -and ($firmwareToken -ne $settingsToken)) {
    Write-WarnMsg "DEVICE_TOKEN differs between firmware secrets.h and controller settings.json. Leaving both untouched; reconcile manually."
    Write-Host "    secrets.h     : $secretsPath" -ForegroundColor DarkGray
    Write-Host "    settings.json : $settingsPath" -ForegroundColor DarkGray
}
else {
    # Reuse whichever side already has a real token; otherwise generate one.
    if     ($firmwareSet) { $token = $firmwareToken; $source = 'firmware secrets.h' }
    elseif ($settingsSet) { $token = $settingsToken; $source = 'controller settings.json' }
    else                  { $token = New-DeviceToken;  $source = 'newly generated' }

    if (Test-Path -LiteralPath $secretsPath) {
        Set-FirmwareToken $secretsPath $token
        Write-Ok "Wrote DEVICE_TOKEN to firmware\BusyUserBot\secrets.h"
    }
    Set-SettingsToken $settingsPath $token
    Write-Ok "Wrote Dongle.Token to $settingsPath"
    Write-Host "    Token source: $source" -ForegroundColor DarkGray
    Write-Host "    Reflash the firmware so the dongle picks up the new token." -ForegroundColor DarkGray
}

# ---------------------------------------------------------------------------
# Done
# ---------------------------------------------------------------------------
Write-Host ""
Write-Host "Setup complete." -ForegroundColor Green
Write-Host ""
Write-Host "Next steps:"
Write-Host "  1. Build the controller:  cd controller; dotnet build"
Write-Host "  2. Flash the dongle (see docs/hardware-setup.md)."
Write-Host "  3. Pair the dongle once via Windows Settings > Bluetooth > Add device."
Write-Host ""

# Restore prior console encoding.
try { [Console]::OutputEncoding = $prevOutEnc } catch { }
try { [Console]::InputEncoding  = $prevInEnc  } catch { }
