# Setup script to find and copy BepInEx and Unity interop DLLs for FIH Custom Map Editor.
# Works across different Steam library locations and game versions.
#
# Usage:
#   .\setup-libs.ps1                       # auto-detect the installed game
#   .\setup-libs.ps1 -GamePath "X:\...\Flipping is Hard"   # build against a specific install
#
# NOTE: the copied interop DLLs are only build-time references. Each player's BepInEx must
# have REGENERATED its interop for their game version; a stale interop can crash at runtime.
param(
    [string]$GamePath
)

Write-Host "Setting up FIH Custom Map Editor build environment..." -ForegroundColor Cyan
Write-Host ""

# Create lib folder
if (-not (Test-Path "lib")) {
    New-Item -ItemType Directory -Path "lib" | Out-Null
}

# Find game installation. The product folder is "Flipping is Hard Demo" on the demo and
# "Flipping is Hard" once the demo tag is dropped — search both.
$gamePath = $null
if ($GamePath) {
    if (Test-Path "$GamePath\BepInEx") { $gamePath = $GamePath }
    else { Write-Host "ERROR: no BepInEx under -GamePath '$GamePath'" -ForegroundColor Red; exit 1 }
}
else {
    $drives = @("C","D","E","F","G","H","I","J","K")
    $names  = @("Flipping is Hard Demo", "Flipping is Hard Playtest", "Flipping is Hard")
    $roots  = @("SteamLibrary\steamapps\common",
                "Program Files\Steam\steamapps\common",
                "Program Files (x86)\Steam\steamapps\common")
    $candidates = @()
    foreach ($d in $drives) { foreach ($r in $roots) { foreach ($n in $names) {
        $candidates += "$($d):\$r\$n"
    } } }

    foreach ($path in $candidates) {
        if (Test-Path "$path\BepInEx\interop") { $gamePath = $path; break }
    }
}

if (-not $gamePath) {
    Write-Host "ERROR: Could not find game installation automatically." -ForegroundColor Red
    Write-Host ""
    Write-Host "Please make sure:" -ForegroundColor Yellow
    Write-Host "  1. BepInEx IL2CPP is installed in your game folder"
    Write-Host "  2. The game is in a standard Steam library location"
    Write-Host "  3. You have run the game at least once so BepInEx generates the interop DLLs"
    Write-Host ""
    Write-Host "If your game is elsewhere, run with -GamePath."
    exit 1
}

Write-Host "Found game at: $gamePath" -ForegroundColor Green
Write-Host ""

Write-Host "Copying Game and Unity Interop DLLs..." -ForegroundColor Cyan

$dllsToCopy = @(
    "Assembly-CSharp.dll",
    "EHS.Core.Components.dll",
    "FishNet.Runtime.dll",
    "Il2Cppmscorlib.dll",
    "Il2CppSystem.dll",
    "Unity.InputSystem.dll",
    "UnityEngine.AssetBundleModule.dll",
    "UnityEngine.AudioModule.dll",
    "UnityEngine.CoreModule.dll",
    "UnityEngine.IMGUIModule.dll",
    "UnityEngine.InputLegacyModule.dll",
    "UnityEngine.PhysicsModule.dll",
    "UnityEngine.TextRenderingModule.dll",
    "UnityEngine.UIModule.dll"
)

$interopPath = "$gamePath\BepInEx\interop"
if (-not (Test-Path $interopPath)) {
    Write-Host "ERROR: BepInEx\interop folder not found." -ForegroundColor Red
    Write-Host "You must launch the game at least once after installing BepInEx so it can generate the interop assemblies."
    exit 1
}

$success = $true
foreach ($dll in $dllsToCopy) {
    $sourcePath = "$interopPath\$dll"
    if (Test-Path $sourcePath) {
        Copy-Item $sourcePath "lib\" -Force
        Write-Host "  OK: $dll copied" -ForegroundColor Green
    } else {
        Write-Host "  ERROR: $dll not found in interop folder" -ForegroundColor Red
        $success = $false
    }
}

Write-Host ""
if ($success) {
    Write-Host "Setup complete! You can now build the project." -ForegroundColor Green
} else {
    Write-Host "Setup finished with errors. Some DLLs are missing." -ForegroundColor Yellow
}
