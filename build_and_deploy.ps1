param(
    [Parameter(Mandatory = $true)]
    [string]$ServerRoot
)

$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile = Join-Path $projectRoot "SwiftOnlineMusicPlayerSW2.csproj"
$publishDirectory = Join-Path $projectRoot "build\publish\SwiftOnlineMusicPlayerSW2"
$targetDirectory = Join-Path $ServerRoot "game\csgo\addons\swiftlys2\plugins\SwiftOnlineMusicPlayerSW2"
$gameDataRelativePath = "resources\gamedata\signatures.jsonc"

if (-not (Test-Path -LiteralPath (Join-Path $ServerRoot "game\csgo"))) {
    throw "ServerRoot does not look like a CS2 server root: $ServerRoot"
}

& dotnet publish $projectFile -c Release
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $gameDataRelativePath) -PathType Leaf)) {
    throw "Published plugin is missing GameData: $gameDataRelativePath"
}

New-Item -ItemType Directory -Force -Path $targetDirectory | Out-Null
Copy-Item -Path (Join-Path $publishDirectory "*") -Destination $targetDirectory -Recurse -Force

Write-Host "SwiftOnlineMusicPlayerSW2 deployed to: $targetDirectory"
Write-Host "The Audio dependency and client HUD VPK are intentionally not installed by this script."
