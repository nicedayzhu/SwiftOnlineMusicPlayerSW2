param(
    [string]$ServerRoot = "F:\csgoserver_win\cs2",
    [string]$ClientRoot = "F:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive",
    [string]$OutputPath = ""
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $projectRoot ("build\test-diagnostics-{0}.txt" -f (Get-Date -Format "yyyyMMdd-HHmmss"))
}

$serverCsgo = Join-Path $ServerRoot "game\csgo"
$clientCsgo = Join-Path $ClientRoot "game\csgo"
$swiftlyRoot = Join-Path $serverCsgo "addons\swiftlys2"
$managedLogRoot = Join-Path $swiftlyRoot "logs\managed"
$consoleLogRoot = Join-Path $swiftlyRoot "logs\console"
$musicPlugin = Join-Path $swiftlyRoot "plugins\SwiftOnlineMusicPlayerSW2"
$audioPlugin = Join-Path $swiftlyRoot "plugins\Audio"
$musicConfig = Join-Path $swiftlyRoot "configs\plugins\SwiftOnlineMusicPlayerSW2\config.jsonc"
$audioConfig = Join-Path $swiftlyRoot "configs\plugins\Audio\config.jsonc"

$lines = [System.Collections.Generic.List[string]]::new()
function Add-Section {
    param([string]$Title, [string[]]$Content)
    $lines.Add("")
    $lines.Add("===== $Title =====")
    foreach ($line in $Content) { $lines.Add($line) }
}

$lines.Add("SwiftOnlineMusicPlayerSW2 diagnostics")
$lines.Add("generated: $((Get-Date).ToString('o'))")
$lines.Add("server root: $ServerRoot")
$lines.Add("client root: $ClientRoot")

$hashTargets = @(
    (Join-Path $serverCsgo "bin\win64\server.dll"),
    (Join-Path $musicPlugin "SwiftOnlineMusicPlayerSW2.dll"),
    (Join-Path $musicPlugin "AudioApi.dll"),
    (Join-Path $audioPlugin "Audio.dll"),
    (Join-Path $serverCsgo "overrides\swift_online_music_player.vpk"),
    (Join-Path $clientCsgo "overrides\swift_online_music_player.vpk")
)
$hashLines = foreach ($path in $hashTargets) {
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash
        "$hash  $path"
    } else {
        "MISSING  $path"
    }
}
Add-Section -Title "SHA-256" -Content $hashLines

foreach ($entry in @(
    @{ Name = "server gameinfo mount"; Path = (Join-Path $serverCsgo "gameinfo.gi") },
    @{ Name = "client gameinfo mount"; Path = (Join-Path $clientCsgo "gameinfo.gi") }
)) {
    $content = if (Test-Path -LiteralPath $entry.Path) {
        @(Get-Content -LiteralPath $entry.Path | Where-Object { $_ -match 'swift_online_music_player|swiftlys2' })
    } else {
        @("MISSING: $($entry.Path)")
    }
    Add-Section -Title $entry.Name -Content $content
}

foreach ($entry in @(
    @{ Name = "music config"; Path = $musicConfig },
    @{ Name = "audio config"; Path = $audioConfig }
)) {
    $content = if (Test-Path -LiteralPath $entry.Path) {
        @(Get-Content -LiteralPath $entry.Path)
    } else {
        @("NOT GENERATED YET: $($entry.Path)")
    }
    Add-Section -Title $entry.Name -Content $content
}

foreach ($entry in @(
    @{ Name = "latest managed log"; Root = $managedLogRoot },
    @{ Name = "latest console log"; Root = $consoleLogRoot }
)) {
    $latest = if (Test-Path -LiteralPath $entry.Root) {
        Get-ChildItem -LiteralPath $entry.Root -Filter "*.log" -File |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
    }
    $content = if ($null -ne $latest) {
        @("file: $($latest.FullName)") +
        @(Get-Content -LiteralPath $latest.FullName -Tail 500 |
            Where-Object { $_ -match 'SwiftOnlineMusicPlayer|\b(?:Audio|Exception|Error|Warning)\b|custom_hud_layout' })
    } else {
        @("NO LOG FOUND: $($entry.Root)")
    }
    Add-Section -Title $entry.Name -Content $content
}

New-Item -ItemType Directory -Force -Path (Split-Path -Parent $OutputPath) | Out-Null
[System.IO.File]::WriteAllLines($OutputPath, $lines, [System.Text.UTF8Encoding]::new($false))
Write-Host "Diagnostics written to: $OutputPath"
