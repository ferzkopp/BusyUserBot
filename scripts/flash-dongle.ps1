<#
.SYNOPSIS
    Interactive guided flash + smoke test for the LILYGO T-Dongle-S3.

.DESCRIPTION
    Combines steps 3-6 of docs/hardware-setup.md into a single interactive
    script:

      3. Detect the dongle's COM port via a before/after diff of
         `arduino-cli board list`.
      4. Compile and upload firmware/BusyUserBot. Tries the
         `esp32:esp32:lilygo_t_dongle_s3` FQBN first and falls back to
         the generic `esp32:esp32:esp32s3` board with explicit build
         properties if the lilygo variant isn't recognised.
        5. Run the Python test client (`tools/test_client.py`) to verify
         status / display / typing.

     Prerequisites (all installed by `scripts/dev-env-setup.ps1`):
        * arduino-cli on PATH or in a standard Windows install location
      * ESP32 Arduino core 3.x and the project's libraries
      * Python 3 + bleak (for the smoke test)
      * firmware/BusyUserBot/secrets.h with a real DEVICE_TOKEN
      * %APPDATA%\BusyUserBot\settings.json with the matching token

    Each step pauses for confirmation; you can abort at any prompt with
    Ctrl+C. Re-run any time -- the script does not change configuration,
    it only reads existing token/name values.

.PARAMETER Port
    Skip COM-port autodetection and use this port directly (e.g. `COM7`).
    Overrides any cached port from a previous run.

.PARAMETER NoCache
    Don't read or write the cached COM port. Forces full autodetection
    (the before/after `arduino-cli board list` diff) even if a previous
    run remembered a port.

.PARAMETER SkipPair
    Deprecated. Windows Bluetooth pairing is no longer required.

.PARAMETER SkipSmokeTest
    Don't run the Python test client at the end.

.PARAMETER RunTypingTest
    Also run a real HID typing test. Off by default because it types into
    whichever window is focused on the dongle's USB host.

.PARAMETER DeviceName
    BLE advertised name to scan for in the smoke test. Defaults to the
    `Dongle.Name` value in `%APPDATA%\BusyUserBot\settings.json`, or
    `BusyUserBot` if that file is missing.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts/flash-dongle.ps1

.EXAMPLE
    # Already know the COM port:
    powershell -ExecutionPolicy Bypass -File scripts/flash-dongle.ps1 -Port COM7

.EXAMPLE
    # Force full autodetection, ignoring any cached port:
    powershell -ExecutionPolicy Bypass -File scripts/flash-dongle.ps1 -NoCache
#>

[CmdletBinding()]
param(
    [string] $Port,
    [switch] $NoCache,
    [switch] $SkipPair,
    [switch] $SkipSmokeTest,
    [switch] $RunTypingTest,
    [string] $DeviceName
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ---------------------------------------------------------------------------
# Helpers (kept consistent with scripts/dev-env-setup.ps1)
# ---------------------------------------------------------------------------
function Write-Step    ([string]$msg) { Write-Host ""; Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok      ([string]$msg) { Write-Host "  OK $msg" -ForegroundColor Green }
function Write-Skip    ([string]$msg) { Write-Host "  -- $msg" -ForegroundColor DarkGray }
function Write-WarnMsg ([string]$msg) { Write-Host "  !! $msg" -ForegroundColor Yellow }
function Write-Info    ([string]$msg) { Write-Host "     $msg" -ForegroundColor DarkGray }

function Test-Command([string]$name) {
    $null -ne (Get-Command $name -ErrorAction SilentlyContinue)
}

function Resolve-ArduinoCli {
    $cmd = Get-Command 'arduino-cli' -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $candidates = @(
        (Join-Path $env:ProgramFiles 'Arduino CLI\arduino-cli.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Arduino CLI\arduino-cli.exe')
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }

    throw "arduino-cli not found. Run scripts/dev-env-setup.ps1 first."
}

function Read-UserConfirmation([string]$msg) {
    Write-Host ""
    Write-Host $msg -ForegroundColor Yellow
    Write-Host "    Press Enter to continue, or Ctrl+C to abort..." -ForegroundColor DarkGray
    [void](Read-Host)
}

function Get-BoardPorts {
    # Returns the set of serial port addresses currently visible to
    # arduino-cli. JSON output is more robust than parsing the table.
    $json = & $script:ArduinoCli board list --format json 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $json) { return @() }
    try {
        $obj = $json | ConvertFrom-Json
    } catch {
        return @()
    }
    # arduino-cli's JSON shape has changed across versions. Normalise.
    $list = $null
    if ($obj.PSObject.Properties['detected_ports']) { $list = $obj.detected_ports }
    elseif ($obj -is [System.Collections.IEnumerable])    { $list = $obj }

    $ports = @()
    foreach ($entry in $list) {
        $addr = $null
        if ($entry.PSObject.Properties['port'] -and $entry.port -and $entry.port.PSObject.Properties['address']) {
            $addr = $entry.port.address
        } elseif ($entry.PSObject.Properties['address']) {
            $addr = $entry.address
        }
        if ($addr) { $ports += [string]$addr }
    }
    return ,$ports
}

# ---------------------------------------------------------------------------
# Locate the repo and read the existing token / device name
# ---------------------------------------------------------------------------
$repoRoot     = Split-Path -Parent $PSScriptRoot
$sketchDir    = Join-Path $repoRoot 'firmware\BusyUserBot'
$secretsPath  = Join-Path $sketchDir 'secrets.h'
$settingsPath = Join-Path $env:APPDATA 'BusyUserBot\settings.json'
$testClient   = Join-Path $repoRoot 'tools\test_client.py'
$script:ArduinoCli = Resolve-ArduinoCli
$arduinoTemp = Join-Path $repoRoot '.arduino-tmp'
[void](New-Item -ItemType Directory -Force -Path $arduinoTemp)
$env:TEMP = $arduinoTemp
$env:TMP = $arduinoTemp

Write-Info "Using arduino-cli: $script:ArduinoCli"
Write-Info "Using Arduino temp dir: $arduinoTemp"
if (-not (Test-Path -LiteralPath $sketchDir)) {
    throw "Firmware sketch directory not found: $sketchDir"
}
if (-not (Test-Path -LiteralPath $secretsPath)) {
    throw "firmware/BusyUserBot/secrets.h not found. Run scripts/dev-env-setup.ps1 first to generate it."
}

# Resolve token + device name from settings.json (preferred) / secrets.h.
$token    = $null
$nameFromSettings = $null
if (Test-Path -LiteralPath $settingsPath) {
    try {
        $settings = Get-Content -Raw -LiteralPath $settingsPath | ConvertFrom-Json
        if ($settings -and $settings.Dongle) {
            if ($settings.Dongle.PSObject.Properties['Token']) { $token = [string]$settings.Dongle.Token }
            if ($settings.Dongle.PSObject.Properties['Name'])  { $nameFromSettings = [string]$settings.Dongle.Name }
        }
    } catch {
        Write-WarnMsg "Could not parse $settingsPath ($($_.Exception.Message)); falling back to secrets.h"
    }
}
if (-not $token) {
    $line = Select-String -LiteralPath $secretsPath -Pattern '^\s*#define\s+DEVICE_TOKEN\s+"([^"]*)"' -ErrorAction SilentlyContinue
    if ($line) { $token = $line.Matches[0].Groups[1].Value }
}
$nameFromSecrets = $null
$nameLine = Select-String -LiteralPath $secretsPath -Pattern '^\s*#define\s+DEVICE_NAME\s+"([^"]*)"' -ErrorAction SilentlyContinue
if ($nameLine) { $nameFromSecrets = $nameLine.Matches[0].Groups[1].Value }

if (-not $DeviceName) {
    if     ($nameFromSettings) { $DeviceName = $nameFromSettings }
    elseif ($nameFromSecrets)  { $DeviceName = $nameFromSecrets }
    else                       { $DeviceName = 'BusyUserBot' }
}

Write-Host ""
Write-Host "BusyUserBot dongle flash + smoke test" -ForegroundColor Cyan
Write-Host "    repo            : $repoRoot"
Write-Host "    sketch          : $sketchDir"
Write-Host "    secrets.h       : $secretsPath"
Write-Host "    settings.json   : $settingsPath"
Write-Host "    BLE name        : $DeviceName"
if ($token) {
    $masked = if ($token.Length -le 8) { '****' } else { $token.Substring(0,4) + '...' + $token.Substring($token.Length-4) }
    Write-Host "    DEVICE_TOKEN    : $masked  (length $($token.Length))"
} else {
    Write-WarnMsg "No DEVICE_TOKEN found. The smoke test step will be skipped."
}

# ---------------------------------------------------------------------------
# Step 3: Detect the COM port (before/after diff), with optional cache reuse
# ---------------------------------------------------------------------------
$cacheDir  = Join-Path $env:LOCALAPPDATA 'BusyUserBot'
$cachePath = Join-Path $cacheDir 'flash-dongle.json'

function Get-CachedPort {
    if (-not (Test-Path -LiteralPath $cachePath)) { return $null }
    try {
        $obj = Get-Content -Raw -LiteralPath $cachePath | ConvertFrom-Json
        if ($obj -and $obj.PSObject.Properties['Port'] -and $obj.Port) {
            return [string]$obj.Port
        }
    } catch { }
    return $null
}

function Save-CachedPort([string]$value) {
    try {
        if (-not (Test-Path -LiteralPath $cacheDir)) {
            New-Item -ItemType Directory -Path $cacheDir | Out-Null
        }
        ([pscustomobject]@{ Port = $value; SavedUtc = (Get-Date).ToUniversalTime().ToString('o') } |
            ConvertTo-Json) | Set-Content -LiteralPath $cachePath -Encoding UTF8
    } catch {
        Write-WarnMsg "Could not save cached port to $cachePath ($($_.Exception.Message))"
    }
}

$portSource = $null
if ($Port) {
    $portSource = '-Port argument'
} elseif (-not $NoCache) {
    $cached = Get-CachedPort
    if ($cached) {
        $Port = $cached
        $portSource = "cached ($cachePath)"
    }
}

if ($Port) {
    Write-Step "Step 3: COM port = $Port ($portSource)"
    Write-Info "Skipping autodetection. Pass -NoCache (or -Port <COMx>) to override."
    Write-Host ""
    Write-Host "Make sure the dongle is plugged into $Port now." -ForegroundColor Yellow
    Write-Host "    * If this is a re-flash on a fresh boot, no need to hold BOOT --" -ForegroundColor DarkGray
    Write-Host "      arduino-cli will reset the dongle into download mode for you." -ForegroundColor DarkGray
    Write-Host "    * If upload fails, re-run with -NoCache and hold BOOT while plugging in." -ForegroundColor DarkGray
    Read-UserConfirmation "Press Enter once the dongle is plugged in."

    # Sanity check: warn (don't block) if the cached / supplied port isn't visible.
    $visible = Get-BoardPorts
    if ($visible -notcontains $Port) {
        Write-WarnMsg "$Port is not currently in 'arduino-cli board list' output: $($visible -join ', ')"
        Write-WarnMsg "Continuing anyway -- the upload step will fail clearly if the port really is gone."
    }
} else {
    Write-Step "Step 3: Detect the dongle's COM port"
    Write-Info "We snapshot the serial ports now, then again after you plug the dongle in,"
    Write-Info "and identify it as the new entry. See docs/hardware-setup.md step 3 for details."

    Read-UserConfirmation "Make sure the dongle is UNPLUGGED right now."

    $before = Get-BoardPorts
    Write-Info ("Ports before: " + ($(if ($before.Count) { $before -join ', ' } else { '(none)' })))

    Write-Host ""
    Write-Host "Now plug the dongle in." -ForegroundColor Yellow
    Write-Host "    * If this is a fresh dongle or arduino-cli can't see the port, HOLD the BOOT" -ForegroundColor DarkGray
    Write-Host "      button while plugging in (download mode -- screen stays dark)." -ForegroundColor DarkGray
    Write-Host "    * Otherwise just plug it into a USB-A port directly on the PC." -ForegroundColor DarkGray
    Write-Host "    * Wait for any 'Setting up a device / Device is ready' Windows toast to finish." -ForegroundColor DarkGray
    Read-UserConfirmation "Press Enter once the dongle is plugged in and Windows is done with it."

    $after = Get-BoardPorts
    Write-Info ("Ports after : " + ($(if ($after.Count) { $after -join ', ' } else { '(none)' })))

    $new = @($after | Where-Object { $before -notcontains $_ })
    if ($new.Count -eq 0) {
        throw "No new COM port appeared. See the 'No new port appears' troubleshooting section in docs/hardware-setup.md."
    }
    if ($new.Count -gt 1) {
        Write-WarnMsg "More than one new port appeared: $($new -join ', '). Pick one and re-run with -Port <COMx>."
        throw "Ambiguous port detection."
    }
    $Port = $new[0]
    Write-Ok "Detected dongle on $Port"
}

# ---------------------------------------------------------------------------
# Step 4: Compile and upload
# ---------------------------------------------------------------------------
Write-Step "Step 4: Compile and upload firmware to $Port"

$primaryFqbn  = 'esp32:esp32:lilygo_t_dongle_s3'
# Fallback for ESP32 cores that don't ship the lilygo_t_dongle_s3 variant.
# Option IDs come from `arduino-cli board details --fqbn esp32:esp32:esp32s3`.
#   USBMode=default        -> USB-OTG (TinyUSB). REQUIRED so USBHIDKeyboard /
#                             USBHIDMouse enumerate as a HID device. Setting
#                             this to `hwcdc` (Hardware CDC and JTAG) makes
#                             the dongle appear as a 'USB JTAG/serial debug
#                             unit' instead and HID never comes up.
#   PSRAM=disabled         -> The T-Dongle-S3 has no PSRAM chip; selecting
#                             OPI PSRAM repurposes GPIOs 33-37 for an octal
#                             PSRAM that doesn't exist and the early-boot
#                             probe wedges before setup() ever runs.
#   PartitionScheme=app3M_fat9M_16MB -> "16M Flash (3MB APP/9.9MB FATFS)",
#                             matches the manual settings documented in
#                             docs/hardware-setup.md.
$fallbackFqbn = 'esp32:esp32:esp32s3:USBMode=default,CDCOnBoot=cdc,PSRAM=disabled,FlashSize=16M,PartitionScheme=app3M_fat9M_16MB'
$fqbn = $primaryFqbn

Write-Info "Compiling with FQBN: $fqbn"
& $script:ArduinoCli compile --fqbn $fqbn $sketchDir
if ($LASTEXITCODE -ne 0) {
    Write-WarnMsg "Compile failed with $primaryFqbn. Falling back to generic esp32s3 with explicit build properties."
    $fqbn = $fallbackFqbn
    Write-Info "Compiling with FQBN: $fqbn"
    & $script:ArduinoCli compile --fqbn $fqbn $sketchDir
    if ($LASTEXITCODE -ne 0) {
        throw "arduino-cli compile failed for both $primaryFqbn and the esp32s3 fallback."
    }
}
Write-Ok "Compile succeeded ($fqbn)"

Write-Info "Uploading to $Port ..."
& $script:ArduinoCli upload --fqbn $fqbn --port $Port $sketchDir
if ($LASTEXITCODE -ne 0) {
    throw "arduino-cli upload failed for $Port."
}
Write-Ok "Upload succeeded"

# Remember this port for future runs (skippable with -NoCache).
if (-not $NoCache) {
    Save-CachedPort $Port
    Write-Info "Cached $Port to $cachePath for future runs (use -NoCache to ignore)."
}

Write-Host ""
Write-Host "Now UNPLUG and REPLUG the dongle (no RST button)." -ForegroundColor Yellow
Write-Host "    * The on-board ST7735 display should show: BLE / advertising / $DeviceName" -ForegroundColor DarkGray
Read-UserConfirmation "Press Enter once the display shows the advertising message."

# ---------------------------------------------------------------------------
# Step 5: No Windows pairing required
# ---------------------------------------------------------------------------
Write-Step "Step 5: Windows pairing not required"
Write-Info "The controller and test client connect to the advertising BLE device by name."
Write-Info "If Windows shows '$DeviceName' stuck at Connecting, remove it from Bluetooth settings and do not pair it again."

# ---------------------------------------------------------------------------
# Step 6: Smoke test via tools/test_client.py
# ---------------------------------------------------------------------------
if ($SkipSmokeTest) {
    Write-Step "Step 6: Smoke test skipped (-SkipSmokeTest)"
    Write-Host ""
    Write-Host "Done." -ForegroundColor Green
    return
}

Write-Step "Step 6: Smoke test (status / display / type)"

if (-not (Test-Path -LiteralPath $testClient)) {
    Write-WarnMsg "Test client not found at $testClient -- skipping smoke test."
    return
}
if (-not $token) {
    Write-WarnMsg "No DEVICE_TOKEN available -- skipping smoke test."
    return
}

# Pick a python launcher.
$python = $null
foreach ($candidate in @('py', 'python', 'python3')) {
    if (Test-Command $candidate) { $python = $candidate; break }
}
if (-not $python) {
    Write-WarnMsg "Python not found on PATH -- skipping smoke test. Install Python 3 + bleak (see scripts/dev-env-setup.ps1)."
    return
}

function Invoke-TestClient {
    param([Parameter(Mandatory)] [string[]] $ExtraArgs, [Parameter(Mandatory)] [string] $Description)

    Write-Info "Running: $Description"
    $argsList = @($testClient, '--name', $DeviceName, '--token', $token) + $ExtraArgs
    & $python @argsList
    if ($LASTEXITCODE -ne 0) {
        Write-WarnMsg "$Description failed (exit $LASTEXITCODE). See output above."
        return $false
    }
    Write-Ok "$Description succeeded"
    return $true
}

$allSmokePassed = $true
if (-not (Invoke-TestClient -ExtraArgs @('status') -Description "status query")) { $allSmokePassed = $false }
if (-not (Invoke-TestClient -ExtraArgs @('display', 'ready') -Description "display 'ready'")) { $allSmokePassed = $false }

if ($RunTypingTest) {
    Write-Host ""
    Write-Host "Optional test: typing 'hello' as a real keystroke." -ForegroundColor Yellow
    Write-Host "    The keystrokes appear on whatever PC the dongle's USB-A plug is in." -ForegroundColor DarkGray
    Write-Host "    Focus a disposable text field on that PC before continuing." -ForegroundColor DarkGray
    Read-UserConfirmation "Press Enter when a disposable text field is focused on the host PC."

    if (-not (Invoke-TestClient -ExtraArgs @('type', 'hello') -Description "type 'hello'")) { $allSmokePassed = $false }
} else {
    Write-Skip "Skipping real HID typing test. Pass -RunTypingTest to enable it."
}

Write-Host ""
if ($allSmokePassed) {
    Write-Host "Done. The dongle is flashed and verified." -ForegroundColor Green
} else {
    Write-WarnMsg "Done flashing, but one or more smoke tests failed. See output above."
    exit 2
}
