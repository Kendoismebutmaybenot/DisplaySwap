<#
  Builds DisplaySwap and its Playnite extension with the C# compiler that ships with Windows
  (.NET Framework 4.x). No SDK or Visual Studio needed.

  Output (dist\):
    DisplaySwap-v<version>.zip             the app (unzip anywhere and run DisplaySwap.exe)
    DisplaySwap-Playnite-v<version>.pext   the Playnite extension (double-click to install)

  Options:
    -PlayniteDir <folder>     Playnite install folder (needed to build the extension; auto-detected if omitted)
    -Install <folder>         also copy DisplaySwap.exe there (close DisplaySwap first)
    -InstallPlaynite          also install the extension into Playnite (close Playnite first)
#>
param([string]$PlayniteDir, [string]$Install, [switch]$InstallPlaynite)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$src = "$root\src"
$ext = "$root\playnite-extension"
$obj = "$root\obj"
$dist = "$root\dist"
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$csc = "$fw\csc.exe"
if (-not (Test-Path $csc)) { throw "C# compiler not found at $csc (.NET Framework 4.x is part of Windows 10/11)." }

$version = (Select-String -Path "$src\App.cs" -Pattern 'public const string Version = "([^"]+)"').Matches[0].Groups[1].Value
Remove-Item $obj, $dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force "$obj\app", "$obj\ext", $dist | Out-Null

# ---------------- app ----------------
$wpf = "/r:$fw\WPF\PresentationFramework.dll", "/r:$fw\WPF\PresentationCore.dll", "/r:$fw\WPF\WindowsBase.dll", "/r:$fw\System.Xaml.dll"
$refs = $wpf + "/r:System.Windows.Forms.dll", "/r:System.Drawing.dll", "/r:System.Runtime.Serialization.dll", "/r:System.Core.dll"
$files = "App.cs", "Engine.cs", "DisplayCore.cs", "Audio.cs", "LgTv.cs", "TvDrivers.cs" | ForEach-Object { "$src\$_" }
$res = "/resource:$src\MainWindow.xaml,MainWindow.xaml", "/resource:$src\lgtv-pairing.json,lgtv-pairing.json"
function Compile-App($out, $extra) {
  & $csc /nologo /target:winexe /platform:x64 /optimize+ "/out:$out" $res $refs $extra $files
  if ($LASTEXITCODE -ne 0) { throw "Building DisplaySwap failed" }
}
# pass 1 draws the icon, pass 2 embeds it
Compile-App "$obj\iconpass.exe" @()
& "$obj\iconpass.exe" --make-icon "$obj\DisplaySwap.ico" | Out-Null
Compile-App "$obj\app\DisplaySwap.exe" @("/win32icon:$obj\DisplaySwap.ico")
Copy-Item "$root\LICENSE", "$root\THIRD-PARTY-NOTICES.md" "$obj\app"
Compress-Archive -Path "$obj\app\*" -DestinationPath "$dist\DisplaySwap-v$version.zip"
Write-Host "Built dist\DisplaySwap-v$version.zip"

# ---------------- Playnite extension ----------------
if (-not $PlayniteDir) {
  $running = Get-Process Playnite.DesktopApp, Playnite.FullscreenApp -ErrorAction SilentlyContinue | Select-Object -First 1
  $candidates = @()
  if ($running) { $candidates += Split-Path $running.Path }
  $candidates += "$env:LOCALAPPDATA\Playnite"
  $PlayniteDir = $candidates | Where-Object { Test-Path "$_\Playnite.SDK.dll" } | Select-Object -First 1
}
if (-not $PlayniteDir -or -not (Test-Path "$PlayniteDir\Playnite.SDK.dll")) {
  Write-Warning "Playnite not found, so the extension was not built. Use -PlayniteDir <Playnite folder>."
} else {
  & $csc /nologo /target:library /optimize+ "/out:$obj\ext\DisplaySwapPlaynite.dll" "/r:$PlayniteDir\Playnite.SDK.dll" $wpf /r:System.Core.dll "$ext\DisplaySwapPlugin.cs"
  if ($LASTEXITCODE -ne 0) { throw "Building the Playnite extension failed" }
  # extension icon: the 64x64 PNG inside the app icon
  $b = [IO.File]::ReadAllBytes("$obj\DisplaySwap.ico"); $n = [BitConverter]::ToUInt16($b, 4)
  for ($i = 0; $i -lt $n; $i++) { $e = 6 + 16 * $i
    if ($b[$e] -eq 64) { $len = [BitConverter]::ToInt32($b, $e + 8); $off = [BitConverter]::ToInt32($b, $e + 12)
      [IO.File]::WriteAllBytes("$obj\ext\icon.png", $b[$off..($off + $len - 1)]) } }
  # check mark shown next to the chosen profile in the menu
  Add-Type -AssemblyName System.Drawing
  $bmp = New-Object System.Drawing.Bitmap 32, 32; $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(0x5B, 0x8C, 0xFF)), 4
  $pen.StartCap = 'Round'; $pen.EndCap = 'Round'; $pen.LineJoin = 'Round'
  $g.DrawLines($pen, [System.Drawing.PointF[]]@((New-Object System.Drawing.PointF 6, 17), (New-Object System.Drawing.PointF 13, 24), (New-Object System.Drawing.PointF 26, 9)))
  $bmp.Save("$obj\ext\check.png", [System.Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose()
  Copy-Item "$ext\extension.yaml" "$obj\ext"
  # a .pext is a zip of the extension folder; Playnite installs it on double-click
  Compress-Archive -Path "$obj\ext\*" -DestinationPath "$obj\ext.zip"
  Move-Item "$obj\ext.zip" "$dist\DisplaySwap-Playnite-v$version.pext"
  Write-Host "Built dist\DisplaySwap-Playnite-v$version.pext"

  if ($InstallPlaynite) {
    if (Get-Process Playnite.DesktopApp, Playnite.FullscreenApp -ErrorAction SilentlyContinue) { throw "Close Playnite first, then run this again." }
    # same folder Playnite uses when it installs the .pext (named after the extension Id)
    $extId = (Select-String -Path "$ext\extension.yaml" -Pattern '^Id:\s*(\S+)').Matches[0].Groups[1].Value
    $dest = "$PlayniteDir\Extensions\$extId"
    New-Item -ItemType Directory -Force $dest | Out-Null
    Copy-Item "$obj\ext\*" $dest -Force
    Write-Host "Installed the extension to $dest"
  }
}

if ($Install) {
  if (Get-Process DisplaySwap -ErrorAction SilentlyContinue) { throw "Close DisplaySwap first, then run this again." }
  New-Item -ItemType Directory -Force $Install | Out-Null
  Copy-Item "$obj\app\DisplaySwap.exe" $Install -Force
  Write-Host "Installed DisplaySwap.exe to $Install"
}
