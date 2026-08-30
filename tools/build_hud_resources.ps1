param(
    [ValidateSet("Validate", "Compile", "Pack", "Build")]
    [string]$Action = "Build",
    [string]$Cs2Root = "F:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive",
    [string]$AddonName = "swift_online_music_player",
    [string]$VpkEditCli = "F:\cs2dev\SkinTools\VPKEdit-Windows-Standalone-msvc-Release\vpkeditcli.exe"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$projectRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$layoutPath = Join-Path $projectRoot "hud\layout\online_music_player_custom_hud.xml"
$stylePath = Join-Path $projectRoot "hud\styles\online_music_player_custom_hud.css"
$iconSourceDir = Join-Path $projectRoot "hud\icons"
$iconNames = @("close", "music_note", "next", "pause", "play", "previous", "volume_down", "volume_up", "heart")
$layoutIconNames = @("close", "music_note", "next", "pause", "play", "previous", "volume_up", "heart")
$pluginPath = Join-Path $projectRoot "src\SwiftOnlineMusicPlayerPlugin.cs"
$configPath = Join-Path $projectRoot "src\MusicPlayerConfig.cs"
$lyricsProviderPath = Join-Path $projectRoot "src\MusicLyricsProvider.cs"
$bridgePath = Join-Path $projectRoot "src\CustomHudNative.cs"
$gameDataPath = Join-Path $projectRoot "resources\gamedata\signatures.jsonc"
$distRoot = Join-Path $projectRoot "dist"
$outVpk = Join-Path $distRoot "$AddonName.vpk"

function Assert-FileExists {
    param([string]$Path, [string]$Message)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw $Message }
}

function Assert-DirectoryExists {
    param([string]$Path, [string]$Message)
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { throw $Message }
}

function Assert-PngIcon {
    param([string]$Path, [string]$IconName)
    Assert-FileExists -Path $Path -Message "Required PNG icon is missing: $IconName.png"
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $signature = @(137, 80, 78, 71, 13, 10, 26, 10)
    if ($bytes.Length -lt 33) { throw "PNG icon is truncated: $Path" }
    foreach ($index in 0..7) {
        if ($bytes[$index] -ne $signature[$index]) { throw "Icon is not a valid PNG: $Path" }
    }
    $width = ($bytes[16] -shl 24) -bor ($bytes[17] -shl 16) -bor ($bytes[18] -shl 8) -bor $bytes[19]
    $height = ($bytes[20] -shl 24) -bor ($bytes[21] -shl 16) -bor ($bytes[22] -shl 8) -bor $bytes[23]
    if ($width -ne 128 -or $height -ne 128 -or $bytes[24] -ne 8 -or $bytes[25] -ne 6) {
        throw "PNG icon must be 128x128, 8-bit RGBA: $Path"
    }
}

function Assert-SafeAddonName {
    param([string]$Name)
    if ([string]::IsNullOrWhiteSpace($Name) -or $Name -match '[\/:*?"<>|]') {
        throw "AddonName must be a simple directory name: $Name"
    }
}

function Test-PathIsChild {
    param([string]$Path, [string]$Parent)
    $resolvedPath = [System.IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $resolvedParent = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\', '/')
    return $resolvedPath.StartsWith(
        $resolvedParent + [System.IO.Path]::DirectorySeparatorChar,
        [System.StringComparison]::OrdinalIgnoreCase)
}

function Remove-ChildDirectory {
    param([string]$Path, [string]$Parent)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    if (-not (Test-PathIsChild -Path $Path -Parent $Parent)) {
        throw "Refusing to remove path outside expected parent: $Path"
    }
    Remove-Item -LiteralPath $Path -Recurse -Force
}

function Write-TextNoBom {
    param([string]$Path, [string]$Value)
    [System.IO.File]::WriteAllText($Path, $Value, [System.Text.UTF8Encoding]::new($false))
}

function Write-IconVtex {
    param([string]$Path, [string]$IconName)
    $descriptor = @"
<!-- dmx encoding keyvalues2_noids 1 format vtex 1 -->
"CDmeVtex"
{
    "m_inputTextureArray" "element_array"
    [
        "CDmeInputTexture"
        {
            "m_name" "string" "InputTexture0"
            "m_fileName" "string" "panorama/images/custom_game/music_player/$IconName.png"
            "m_colorSpace" "string" "srgb"
            "m_typeString" "string" "2D"
            "m_imageProcessorArray" "element_array"
            [
                "CDmeImageProcessor"
                {
                    "m_algorithm" "string" "None"
                    "m_stringArg" "string" ""
                    "m_vFloat4Arg" "vector4" "0 0 0 0"
                }
            ]
        }
    ]
    "m_outputTypeString" "string" "2D"
    "m_outputFormat" "string" "BGRA8888"
    "m_outputClearColor" "vector4" "0 0 0 0"
    "m_nOutputMinDimension" "int" "0"
    "m_nOutputMaxDimension" "int" "64"
    "m_textureOutputChannelArray" "element_array"
    [
        "CDmeTextureOutputChannel"
        {
            "m_inputTextureArray" "string_array" [ "InputTexture0" ]
            "m_srcChannels" "string" "rgba"
            "m_dstChannels" "string" "rgba"
            "m_mipAlgorithm" "CDmeImageProcessor"
            {
                "m_algorithm" "string" "Box"
                "m_stringArg" "string" ""
                "m_vFloat4Arg" "vector4" "0 0 0 0"
            }
            "m_outputColorSpace" "string" "srgb"
        }
    ]
    "m_vClamp" "vector3" "0 0 0"
    "m_bNoLod" "bool" "1"
}
"@
    Write-TextNoBom -Path $Path -Value $descriptor
}

function Test-HudSources {
    foreach ($path in @($layoutPath, $stylePath, $pluginPath, $configPath, $lyricsProviderPath, $bridgePath, $gameDataPath)) {
        Assert-FileExists -Path $path -Message "Required source is missing: $path"
    }
    foreach ($iconName in $iconNames) {
        Assert-PngIcon -Path (Join-Path $iconSourceDir "$iconName.png") -IconName $iconName
    }

    [xml]$layout = Get-Content -Raw -LiteralPath $layoutPath
    $allowedAttributes = @{
        "root" = @()
        "styles" = @()
        "include" = @("src")
        "Panel" = @("id", "class", "hittest")
        "Label" = @("id", "class", "hittest", "text")
        "Image" = @("id", "class", "hittest", "src", "texturewidth", "textureheight")
        "Button" = @("id", "class")
    }

    foreach ($node in $layout.SelectNodes("//*")) {
        if (-not $allowedAttributes.ContainsKey($node.Name)) {
            throw "Custom HUD layout contains disallowed node: $($node.Name)"
        }
        foreach ($attribute in $node.Attributes) {
            if ($allowedAttributes[$node.Name] -notcontains $attribute.Name) {
                throw "Custom HUD layout contains disallowed attribute '$($attribute.Name)' on <$($node.Name)>"
            }
        }
    }

    $stylesheet = $layout.SelectSingleNode("/root/styles/include")
    if (-not $stylesheet -or
        $stylesheet.GetAttribute("src") -ne "s2r://panorama/styles/custom_game/online_music_player_custom_hud.vcss_c") {
        throw "Custom HUD layout must include the compiled online music player stylesheet."
    }
    if ($layout.SelectSingleNode("/root/scripts")) {
        throw "CCSCustomHudLayout does not permit a scripts node."
    }
    if (-not $layout.SelectSingleNode("//Panel[@id='music_dialog']")) {
        throw "Custom HUD layout is missing #music_dialog."
    }
    if (-not $layout.SelectSingleNode("//Panel[@id='music_lyrics']")) {
        throw "Custom HUD layout is missing #music_lyrics."
    }
    foreach ($xpath in @(
        "//Panel[contains(concat(' ', normalize-space(@class), ' '), ' PlayerSurface ')]/Panel[contains(concat(' ', normalize-space(@class), ' '), ' MusicHeader ')]",
        "//Panel[contains(concat(' ', normalize-space(@class), ' '), ' PlayerSurface ')]/Panel[contains(concat(' ', normalize-space(@class), ' '), ' ControlDeck ')]",
        "//Panel[contains(concat(' ', normalize-space(@class), ' '), ' PlayerSurface ')]/Panel[contains(concat(' ', normalize-space(@class), ' '), ' TimelineRow ')]"
    )) {
        if (-not $layout.SelectSingleNode($xpath)) {
            throw "Custom HUD layout no longer matches the reference card hierarchy: $xpath"
        }
    }
    if ($layout.SelectSingleNode("//Image[contains(@src, '.vtex_c')]")) {
        throw "Image.src must use the logical .vtex path, not the packed .vtex_c filename."
    }
    foreach ($iconName in $layoutIconNames) {
        $resource = "s2r://panorama/images/custom_game/music_player/$iconName.vtex"
        if (-not $layout.SelectSingleNode("//Image[@src='$resource']")) {
            throw "Custom HUD layout is missing logical VTEX image reference: $resource"
        }
    }

    $buttonIds = @($layout.SelectNodes("//Button") | ForEach-Object { $_.GetAttribute("id") })
    $expectedButtons = @(
        "music_player_prev",
        "music_player_play_pause",
        "music_player_next",
        "music_player_volume_down",
        "music_player_volume_up",
        "music_player_favorite",
        "music_player_search_toggle",
        "music_player_results_prev",
        "music_player_results_next",
        "music_player_result_1",
        "music_player_result_2",
        "music_player_result_3",
        "music_player_result_4",
        "music_player_result_5",
        "music_player_return_to_aim",
        "music_player_close"
    )
    foreach ($buttonId in $expectedButtons) {
        if ($buttonIds -notcontains $buttonId) { throw "Missing Button id: $buttonId" }
    }
    if ($buttonIds.Count -ne $expectedButtons.Count) {
        throw "Layout contains an unexpected Button id."
    }

    $style = Get-Content -Raw -LiteralPath $stylePath
    $referenceStyleContracts = [ordered]@{
        ".MusicPlayerPresenter" = @("width: 250px;", "height: 170px;")
        ".PlayerSurface" = @("width: 250px;", "height: 170px;", "background-color: #191414;", "border-radius: 10px;")
        ".AlbumArt" = @("width: 40px;", "height: 40px;", "background-color: #ffffff;", "border-radius: 5px;")
        ".TrackTitle" = @("font-size: 20px;", "color: #ffffff;")
        ".ControlButton" = @("width: 24px;", "height: 24px;", "background-color: #00000000;")
        ".TimeBadge" = @("background-color: #00000060;", "border-radius: 8px;")
        ".ProgressTrack" = @("height: 6px;", "background-color: #5e5e5e;", "border-radius: 3px;")
        ".ProgressFill" = @("background-color: #1db954;", "border-radius: 3px;")
        ".InteractionModeButton" = @("height: 22px;", "background-color: #00000000;", "cursor: pointer;")
        ".InteractionHint" = @("font-size: 12px;", "font-weight: bold;", "text-shadow: 0px 1px 2px 1.5 #000000;")
        ".SearchDrawer" = @("height: 250px;")
        ".SearchResultTitle" = @("font-size: 12px;")
        ".SearchResultMeta" = @("font-size: 10px;", "color: #ffffffaa;")
    }
    foreach ($selector in $referenceStyleContracts.Keys) {
        $selectorMatch = [regex]::Match(
            $style,
            [regex]::Escape($selector) + '\s*\{(?<body>.*?)\}',
            [System.Text.RegularExpressions.RegexOptions]::Singleline)
        if (-not $selectorMatch.Success) {
            throw "Stylesheet is missing reference selector: $selector"
        }
        foreach ($declaration in $referenceStyleContracts[$selector]) {
            if (-not $selectorMatch.Groups["body"].Value.Contains($declaration)) {
                throw "Reference selector $selector is missing declaration: $declaration"
            }
        }
    }
    foreach ($step in 0..20) {
        if ($style -notmatch [regex]::Escape(".Progress$step .ProgressFill")) {
            throw "Stylesheet is missing Progress$step fill coverage."
        }
    }
    foreach ($step in 0..5) {
        if ($style -notmatch [regex]::Escape(".Volume$step")) {
            throw "Stylesheet is missing Volume$step coverage."
        }
        if ($style -notmatch [regex]::Escape(".SearchItems$step")) {
            throw "Stylesheet is missing SearchItems$step coverage."
        }
        if ($style -notmatch [regex]::Escape(".SearchSelection$step")) {
            throw "Stylesheet is missing SearchSelection$step coverage."
        }
    }
    foreach ($requiredStyle in @(
        ".SpectrumBar",
        ".MusicHudPlaying .PauseIcon",
        ".MusicHudSearchOpen .SearchDrawer",
        ".MusicHudInteractive .InteractionHint",
        ".MusicHudInteractive .InteractionModeButton:hover",
        ".MusicHudInteractive .InteractionModeButton:active",
        ".MusicHudFavorite .HeartIcon")) {
        if ($style -notmatch [regex]::Escape($requiredStyle)) {
            throw "Stylesheet is missing the native spectrum/icon contract: $requiredStyle"
        }
    }
    foreach ($bar in 1..5) {
        foreach ($requiredStyle in @(
            ".MusicHudPlaying .SpectrumBar$bar",
            "animation-name: spectrum-$bar;",
            "@keyframes 'spectrum-$bar'")) {
            if ($style -notmatch [regex]::Escape($requiredStyle)) {
                throw "Stylesheet is missing playing spectrum contract: $requiredStyle"
            }
        }
    }

    $plugin = Get-Content -Raw -LiteralPath $pluginPath
    foreach ($buttonId in $expectedButtons) {
        if ($plugin -notmatch [regex]::Escape('case "' + $buttonId + '"')) {
            throw "Plugin click allowlist is missing: $buttonId"
        }
    }
    foreach ($dialogVariable in @(
        "track-title", "artist-name", "source-name", "current-time", "duration", "play-action",
        "volume-text", "status", "search-hint-kicker", "search-hint-text", "results-label",
        "results-hint", "results-chevron", "search-heading", "search-query", "search-page",
        "search-empty-title", "search-empty-hint", "search-drawer-hint", "interaction-hint")) {
        if ($plugin -notmatch [regex]::Escape('"' + $dialogVariable + '"')) {
            throw "Plugin does not render dialog variable: $dialogVariable"
        }
    }
    if ($layout.OuterXml -notmatch [regex]::Escape("{s:interaction-hint}")) {
        throw "Layout is missing the interaction hint dialog variable."
    }
    foreach ($resultSuffix in @("index", "title", "meta")) {
        $template = 'search-result-{variableIndex}-' + $resultSuffix
        if ($plugin -notmatch [regex]::Escape($template)) {
            throw "Plugin does not render result dialog-variable template: $template"
        }
        foreach ($row in 1..5) {
            $variable = "{s:search-result-$row-$resultSuffix}"
            if ($layout.OuterXml -notmatch [regex]::Escape($variable)) {
                throw "Layout is missing result dialog variable: $variable"
            }
        }
    }
    if ($plugin -notmatch [regex]::Escape("PlaybackState.Browsing")) {
        throw "Plugin is missing the non-autoplay browsing state."
    }
    foreach ($searchContract in @(
        "_config.AutoPlayFirstSearchResult",
        "BeginLoadSearchTrack(session, 0);",
        "LooksLikeVariant(result.Title, result.Artist)")) {
        if ($plugin -notmatch [regex]::Escape($searchContract)) {
            throw "Plugin is missing search result behavior: $searchContract"
        }
    }
    foreach ($inputContract in @(
        "Core.GameHooks.Controller.ProcessUsercmds.Pre += OnProcessUsercmdsPre;",
        "SetHudInteraction(playerSlot, enabled: false);",
        "_mouse2CapturePendingSlots.Add(playerSlot);",
        "enableInteractionAfterRelease",
        "buttons.Buttonstate1 &= ~mouse2Mask;",
        "command.Attack2StartHistoryIndex = -1;")) {
        if ($plugin -notmatch [regex]::Escape($inputContract)) {
            throw "Plugin is missing two-stage mouse interaction behavior: $inputContract"
        }
    }
    $configSource = Get-Content -Raw -LiteralPath $configPath
    if ($configSource -notmatch [regex]::Escape("public bool AutoPlayFirstSearchResult { get; set; } = true;")) {
        throw "MusicPlayerConfig must enable AutoPlayFirstSearchResult by default."
    }
    foreach ($apiUse in @("DecodeFromUrlAsync", ".Play(", ".Pause(", ".Resume(", ".SetVolume(")) {
        if ($plugin -notmatch [regex]::Escape($apiUse)) {
            throw "Plugin is missing expected Audio API use: $apiUse"
        }
    }

    $gameData = Get-Content -Raw -LiteralPath $gameDataPath
    foreach ($key in @(
        "SetDialogVariableStringForPlayer",
        "SetHasClassForPlayer",
        "SetInputCaptureEnabled",
        "CustomHudClickedReceiver")) {
        if ($gameData -notmatch [regex]::Escape("SwiftOnlineMusicPlayerSW2::$key")) {
            throw "GameData is missing signature key: $key"
        }
    }

    Write-Host "Online music player source validation passed."
    Write-Host "Verified buttons: $($buttonIds -join ', ')"
}

function Get-AddonPaths {
    Assert-SafeAddonName -Name $AddonName
    $contentAddonsRoot = Join-Path $Cs2Root "content\csgo_addons"
    $gameAddonsRoot = Join-Path $Cs2Root "game\csgo_addons"
    return [pscustomobject]@{
        ContentAddonsRoot = $contentAddonsRoot
        GameAddonsRoot = $gameAddonsRoot
        ContentAddon = Join-Path $contentAddonsRoot $AddonName
        GameAddon = Join-Path $gameAddonsRoot $AddonName
        GameDir = Join-Path $Cs2Root "game\csgo"
        ResourceCompiler = Join-Path $Cs2Root "game\bin\win64\resourcecompiler.exe"
    }
}

function Compile-HudResources {
    Test-HudSources
    $paths = Get-AddonPaths
    Assert-FileExists -Path $paths.ResourceCompiler -Message "resourcecompiler.exe not found: $($paths.ResourceCompiler)"

    New-Item -ItemType Directory -Force -Path $paths.ContentAddonsRoot, $paths.GameAddonsRoot | Out-Null
    Remove-ChildDirectory -Path $paths.ContentAddon -Parent $paths.ContentAddonsRoot
    Remove-ChildDirectory -Path $paths.GameAddon -Parent $paths.GameAddonsRoot

    $contentLayoutDir = Join-Path $paths.ContentAddon "panorama\layout\custom_game"
    $contentStyleDir = Join-Path $paths.ContentAddon "panorama\styles\custom_game"
    $contentImageDir = Join-Path $paths.ContentAddon "panorama\images\custom_game\music_player"
    $gameLayoutDir = Join-Path $paths.GameAddon "panorama\layout\custom_game"
    $gameStyleDir = Join-Path $paths.GameAddon "panorama\styles\custom_game"
    $gameImageDir = Join-Path $paths.GameAddon "panorama\images\custom_game\music_player"
    New-Item -ItemType Directory -Force -Path $contentLayoutDir, $contentStyleDir, $contentImageDir, $gameLayoutDir, $gameStyleDir, $gameImageDir | Out-Null

    Set-Content -LiteralPath (Join-Path $paths.ContentAddon "panorama\preprocessor_config.txt") -Encoding ASCII -Value @'
"PanzipCfg"
{
    "BlockDefs"
    {
    }
}
'@
    Write-TextNoBom -Path (Join-Path $contentLayoutDir "online_music_player_custom_hud.vxml") -Value (Get-Content -Raw -LiteralPath $layoutPath)
    Write-TextNoBom -Path (Join-Path $contentStyleDir "online_music_player_custom_hud.vcss") -Value (Get-Content -Raw -LiteralPath $stylePath)
    foreach ($iconName in $iconNames) {
        $sourcePngPath = Join-Path $iconSourceDir "$iconName.png"
        $pngPath = Join-Path $contentImageDir "$iconName.png"
        Copy-Item -LiteralPath $sourcePngPath -Destination $pngPath -Force
        Write-IconVtex -Path (Join-Path $contentImageDir "$iconName.vtex") -IconName $iconName
    }
    Set-Content -LiteralPath (Join-Path $paths.ContentAddon "addoninfo.txt") -Encoding ASCII -Value @'
<!-- kv3 encoding:text:version{e21c7f3c-8a33-41c5-9977-a76d3a32aa0d} format:generic:version{7412167c-06e9-4698-aff2-e63eb59037e7} -->
{
    IsPlayable = false
    Panorama =
    {
        AllowCustomGameUI = true
        AddonLayoutPath = "panorama/layout/custom_game/"
    }
}
'@

    foreach ($iconName in $iconNames) {
        $pngPath = Join-Path $contentImageDir "$iconName.png"
        $vtexPath = Join-Path $contentImageDir "$iconName.vtex"
        Assert-FileExists -Path $pngPath -Message "Staged PNG icon is missing: $pngPath"
        Assert-FileExists -Path $vtexPath -Message "Generated VTEX descriptor is missing: $vtexPath"
        & $paths.ResourceCompiler -game $paths.GameDir -i $vtexPath -f -nop4 -v
        if ($LASTEXITCODE -ne 0) { throw "resourcecompiler failed for $iconName.vtex with exit code $LASTEXITCODE" }
    }

    & $paths.ResourceCompiler -game $paths.GameDir `
        -i (Join-Path $contentStyleDir "online_music_player_custom_hud.vcss") `
        -i (Join-Path $contentLayoutDir "online_music_player_custom_hud.vxml") `
        -f -nop4 -v
    if ($LASTEXITCODE -ne 0) { throw "resourcecompiler failed with exit code $LASTEXITCODE" }

    foreach ($output in @(
        (Join-Path $gameLayoutDir "online_music_player_custom_hud.vxml_c"),
        (Join-Path $gameStyleDir "online_music_player_custom_hud.vcss_c"))) {
        Assert-FileExists -Path $output -Message "Expected compiled resource not found: $output"
    }
    foreach ($iconName in $iconNames) {
        Assert-FileExists -Path (Join-Path $gameImageDir "$iconName.vtex_c") -Message "Expected compiled VTEX not found: $iconName.vtex_c"
    }

    $strippedDir = Join-Path $paths.GameAddon "panorama_stripped"
    Remove-ChildDirectory -Path $strippedDir -Parent $paths.GameAddon
    Copy-Item -LiteralPath (Join-Path $paths.ContentAddon "addoninfo.txt") -Destination (Join-Path $paths.GameAddon "addoninfo.txt") -Force
    Write-Host "Compiled HUD resources: $($paths.GameAddon)"
}

function Pack-HudVpk {
    $paths = Get-AddonPaths
    Assert-DirectoryExists -Path $paths.GameAddon -Message "Compiled addon not found: $($paths.GameAddon). Run Compile first."
    Assert-FileExists -Path $VpkEditCli -Message "VPKEdit CLI not found: $VpkEditCli"
    foreach ($relativePath in @(
        "addoninfo.txt",
        "panorama\layout\custom_game\online_music_player_custom_hud.vxml_c",
        "panorama\styles\custom_game\online_music_player_custom_hud.vcss_c",
        "panorama\images\custom_game\music_player\close.vtex_c",
        "panorama\images\custom_game\music_player\music_note.vtex_c",
        "panorama\images\custom_game\music_player\next.vtex_c",
        "panorama\images\custom_game\music_player\pause.vtex_c",
        "panorama\images\custom_game\music_player\play.vtex_c",
        "panorama\images\custom_game\music_player\previous.vtex_c",
        "panorama\images\custom_game\music_player\volume_down.vtex_c",
        "panorama\images\custom_game\music_player\volume_up.vtex_c",
        "panorama\images\custom_game\music_player\heart.vtex_c")) {
        Assert-FileExists -Path (Join-Path $paths.GameAddon $relativePath) -Message "Compiled addon is missing: $relativePath"
    }

    New-Item -ItemType Directory -Force -Path $distRoot | Out-Null
    & $VpkEditCli --output $outVpk --type vpk --version 2 --single-file $paths.GameAddon
    if ($LASTEXITCODE -ne 0) { throw "vpkeditcli failed with exit code $LASTEXITCODE" }
    Assert-FileExists -Path $outVpk -Message "Expected VPK was not created: $outVpk"

    $tree = (& $VpkEditCli --file-tree $outVpk | Out-String)
    foreach ($fileName in @("addoninfo.txt", "online_music_player_custom_hud.vxml_c", "online_music_player_custom_hud.vcss_c", "music_note.vtex_c", "play.vtex_c", "pause.vtex_c", "previous.vtex_c", "next.vtex_c", "close.vtex_c", "volume_down.vtex_c", "volume_up.vtex_c", "heart.vtex_c")) {
        if ($tree -notmatch [regex]::Escape($fileName)) { throw "Packed VPK is missing: $fileName" }
    }
    Write-Host "Packed HUD VPK: $outVpk"
}

switch ($Action) {
    "Validate" { Test-HudSources }
    "Compile" { Compile-HudResources }
    "Pack" { Pack-HudVpk }
    "Build" { Compile-HudResources; Pack-HudVpk }
}
