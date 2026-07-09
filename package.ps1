# Builds the plugin in Release and produces an importable InfoPanel plugin zip.
#
#   InfoPanel.LianLiLedRing.zip
#     InfoPanel.LianLiLedRing\
#       InfoPanel.LianLiLedRing.dll
#       LedRingColorPicker.exe, MahApps.Metro.dll, ...
#       PluginInfo.ini
#
# Install the result via InfoPanel -> Plugins -> "Add Plugin from ZIP",
# or by extracting it into %ProgramData%\InfoPanel\plugins\.

$ErrorActionPreference = "Stop"

$name   = "InfoPanel.LianLiLedRing"
$proj   = Join-Path $name "$name.csproj"
$outDir = Join-Path $name "bin\Release\net8.0-windows"
$stage  = Join-Path "dist" $name
$zip    = Join-Path "dist" "$name.zip"

Write-Host "Building $proj (Release)..."
dotnet build $proj -c Release

if (-not (Test-Path (Join-Path $outDir "$name.dll"))) {
    throw "Build output not found at $outDir. Did the build succeed?"
}

Write-Host "Staging plugin folder..."
Remove-Item -Recurse -Force "dist" -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# Copy everything the plugin needs, minus debug symbols.
Copy-Item (Join-Path $outDir "*") $stage -Recurse -Force -Exclude *.pdb

Write-Host "Compressing to $zip..."
Compress-Archive -Path $stage -DestinationPath $zip -Force

Write-Host ""
Write-Host "Done: $zip"
Write-Host "Install via InfoPanel -> Plugins -> Add Plugin from ZIP."
