param(
    [string]$ServerRoot = "F:\csgoserver_win\cs2",
    [string]$ClientRoot = "F:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive",
    [string]$AudioZip = "",
    [string]$ExpectedServerDllSha256 = "9E5749D77DCB68883477FEAE751A3F28068D119EC145EDCB0E4D48D15B538D36"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
if ([string]::IsNullOrWhiteSpace($AudioZip)) {
    $AudioZip = Join-Path $projectRoot "build\dependencies\Audio-v1.0.6.zip"
}

$publishDirectory = Join-Path $projectRoot "build\publish\SwiftOnlineMusicPlayerSW2"
$sourceVpk = Join-Path $projectRoot "dist\swift_online_music_player.vpk"
$serverCsgo = Join-Path $ServerRoot "game\csgo"
$clientCsgo = Join-Path $ClientRoot "game\csgo"
$serverDll = Join-Path $serverCsgo "bin\win64\server.dll"
$serverGameInfo = Join-Path $serverCsgo "gameinfo.gi"
$clientGameInfo = Join-Path $clientCsgo "gameinfo.gi"
$pluginsRoot = Join-Path $serverCsgo "addons\swiftlys2\plugins"
$targetPlugin = Join-Path $pluginsRoot "SwiftOnlineMusicPlayerSW2"
$targetAudio = Join-Path $pluginsRoot "Audio"
$serverVpk = Join-Path $serverCsgo "overrides\swift_online_music_player.vpk"
$clientVpk = Join-Path $clientCsgo "overrides\swift_online_music_player.vpk"
$mountLine = "Game`tcsgo/overrides/swift_online_music_player.vpk"
$stamp = Get-Date -Format "yyyyMMdd-HHmmss"
$backupRoot = Join-Path $projectRoot "build\install-backups\$stamp"
$stagingRoot = Join-Path $projectRoot "build\install-staging\$stamp"

function Assert-File {
    param([string]$Path, [string]$Description)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "$Description was not found: $Path"
    }
}

function Assert-Directory {
    param([string]$Path, [string]$Description)
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        throw "$Description was not found: $Path"
    }
}

function Test-IsChildPath {
    param([string]$Path, [string]$Parent)
    $absolutePath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $absoluteParent = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\', '/')
    return $absolutePath.StartsWith(
        $absoluteParent + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)
}

function Backup-ItemIfPresent {
    param([string]$Path, [string]$BackupName)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    New-Item -ItemType Directory -Force -Path $backupRoot | Out-Null
    Copy-Item -LiteralPath $Path -Destination (Join-Path $backupRoot $BackupName) -Recurse -Force
}

function Replace-PluginDirectory {
    param([string]$Source, [string]$Destination, [string]$BackupName)
    Assert-Directory -Path $Source -Description "Plugin source directory"
    if (-not (Test-IsChildPath -Path $Destination -Parent $pluginsRoot)) {
        throw "Refusing to replace a directory outside the SwiftlyS2 plugins root: $Destination"
    }
    Backup-ItemIfPresent -Path $Destination -BackupName $BackupName
    if (Test-Path -LiteralPath $Destination) {
        Remove-Item -LiteralPath $Destination -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Destination) | Out-Null
    Copy-Item -LiteralPath $Source -Destination $Destination -Recurse -Force
}

function Add-VpkMount {
    param([string]$GameInfoPath, [string]$BackupName)
    Assert-File -Path $GameInfoPath -Description "gameinfo.gi"
    $text = [System.IO.File]::ReadAllText($GameInfoPath)
    if ($text -match '(?m)^\s*Game\s+csgo/overrides/swift_online_music_player\.vpk\s*(?://.*)?$') {
        return
    }

    Backup-ItemIfPresent -Path $GameInfoPath -BackupName $BackupName
    $newLine = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $baseGamePattern = '(?m)^(\s*)Game\s+csgo\s*(?://.*)?$'
    $baseGameMatch = [regex]::Match($text, $baseGamePattern)
    if (-not $baseGameMatch.Success) {
        throw "Could not find the base 'Game csgo' SearchPath in: $GameInfoPath"
    }

    $indent = $baseGameMatch.Groups[1].Value
    $replacement = $indent + $mountLine + $newLine + $baseGameMatch.Value
    $updated = [regex]::Replace($text, $baseGamePattern, $replacement, 1)
    [System.IO.File]::WriteAllText($GameInfoPath, $updated, [System.Text.UTF8Encoding]::new($false))
}

Assert-Directory -Path $serverCsgo -Description "CS2 server game directory"
Assert-Directory -Path $clientCsgo -Description "CS2 client game directory"
Assert-Directory -Path $pluginsRoot -Description "SwiftlyS2 plugins directory"
Assert-Directory -Path $publishDirectory -Description "Published music plugin"
Assert-File -Path (Join-Path $publishDirectory "SwiftOnlineMusicPlayerSW2.dll") -Description "Published music plugin DLL"
Assert-File -Path (Join-Path $publishDirectory "resources\gamedata\signatures.jsonc") -Description "Published music plugin GameData"
Assert-File -Path $sourceVpk -Description "Music HUD VPK"
Assert-File -Path $AudioZip -Description "SwiftlyS2 Audio release archive"
Assert-File -Path $serverDll -Description "Server server.dll"
Assert-File -Path $serverGameInfo -Description "Server gameinfo.gi"
Assert-File -Path $clientGameInfo -Description "Client gameinfo.gi"

$actualServerHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $serverDll).Hash
if ($actualServerHash -ne $ExpectedServerDllSha256) {
    throw "server.dll hash mismatch. Expected $ExpectedServerDllSha256, found $actualServerHash. Revalidate GameData before installing."
}

$runningCs2 = @(Get-Process -Name "cs2" -ErrorAction SilentlyContinue)
if ($runningCs2.Count -gt 0) {
    throw "A cs2 process is running. Stop the client and dedicated server before installing."
}

New-Item -ItemType Directory -Force -Path $backupRoot, $stagingRoot | Out-Null
Expand-Archive -LiteralPath $AudioZip -DestinationPath $stagingRoot -Force
$audioSource = Join-Path $stagingRoot "Audio"
Assert-File -Path (Join-Path $audioSource "Audio.dll") -Description "Audio release DLL"
Assert-File -Path (Join-Path $audioSource "AudioApi.dll") -Description "Audio API DLL"
Assert-File -Path (Join-Path $audioSource "resources\natives\pcmdecoder.dll") -Description "Audio native decoder"

Replace-PluginDirectory -Source $audioSource -Destination $targetAudio -BackupName "Audio"
Replace-PluginDirectory -Source $publishDirectory -Destination $targetPlugin -BackupName "SwiftOnlineMusicPlayerSW2"

foreach ($target in @($serverVpk, $clientVpk)) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
}
Backup-ItemIfPresent -Path $serverVpk -BackupName "server-swift_online_music_player.vpk"
Backup-ItemIfPresent -Path $clientVpk -BackupName "client-swift_online_music_player.vpk"
Copy-Item -LiteralPath $sourceVpk -Destination $serverVpk -Force
Copy-Item -LiteralPath $sourceVpk -Destination $clientVpk -Force

Add-VpkMount -GameInfoPath $serverGameInfo -BackupName "server-gameinfo.gi"
Add-VpkMount -GameInfoPath $clientGameInfo -BackupName "client-gameinfo.gi"

$report = [ordered]@{
    installed_at = (Get-Date).ToString("o")
    server_root = [System.IO.Path]::GetFullPath($ServerRoot)
    client_root = [System.IO.Path]::GetFullPath($ClientRoot)
    server_dll_sha256 = $actualServerHash
    audio_zip_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $AudioZip).Hash
    plugin_dll_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $targetPlugin "SwiftOnlineMusicPlayerSW2.dll")).Hash
    server_vpk_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $serverVpk).Hash
    client_vpk_sha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $clientVpk).Hash
    backup_root = $backupRoot
}
$report | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backupRoot "install-report.json") -Encoding UTF8

Write-Host "Installed SwiftOnlineMusicPlayerSW2: $targetPlugin"
Write-Host "Installed SwiftlyS2 Audio v1.0.6: $targetAudio"
Write-Host "Mounted HUD VPK in server and client gameinfo.gi."
Write-Host "Backups and install report: $backupRoot"
