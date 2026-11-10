# Builds OptiWin with the C# compiler that ships with Windows: no SDK to install.
# Output: dist\OptiWin.exe (single file; .NET Framework 4.x is already on Windows 10/11).
# -OutDir builds elsewhere, for when dist\OptiWin.exe is open and therefore locked.
param([string]$OutDir)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$fx   = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path $fx)) { $fx = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319' }
$csc  = Join-Path $fx 'csc.exe'
if (-not (Test-Path $csc)) { throw "csc.exe not found: .NET Framework 4.x is required." }

$dist = if ($OutDir) { $OutDir } else { Join-Path $root 'dist' }
New-Item -ItemType Directory -Force -Path $dist | Out-Null

$refs = @(
    "$fx\WPF\PresentationFramework.dll", "$fx\WPF\PresentationCore.dll", "$fx\WPF\WindowsBase.dll",
    "$fx\System.Xaml.dll", "$fx\System.Management.dll", "$fx\System.Web.Extensions.dll",
    "$fx\Microsoft.CSharp.dll", "$fx\System.Core.dll", "$fx\System.Xml.dll", "$fx\System.dll"
) | ForEach-Object { "/r:`"$_`"" }

# XAML files are embedded in the exe and loaded on demand.
$res = Get-ChildItem (Join-Path $root 'ui') -Filter *.xaml | ForEach-Object { "/resource:`"$($_.FullName)`",OptiWin.ui.$($_.Name)" }
# The rescue kit is embedded too, and written onto the USB key on demand.
$res += Get-ChildItem (Join-Path $root 'assets') -File | Where-Object { $_.Extension -in '.cmd', '.txt' } | ForEach-Object { "/resource:`"$($_.FullName)`",OptiWin.assets.$($_.Name)" }
$src = Get-ChildItem (Join-Path $root 'src') -Recurse -Filter *.cs | ForEach-Object { "`"$($_.FullName)`"" }

$rsp = @(
    '/nologo', '/nostdlib+', '/target:winexe', '/platform:anycpu', '/optimize+', '/warn:3', '/nowarn:1701,1702,162,168,219,414,649',
    '/codepage:65001', '/utf8output', "/out:`"$dist\OptiWin.exe`"", "/win32manifest:`"$root\app.manifest`"",
    "/r:`"$fx\mscorlib.dll`""
) + $refs + $res + $src

$icon = Join-Path $root 'assets\OptiWin.ico'
if (Test-Path $icon) { $rsp += "/win32icon:`"$icon`"" }

$rspFile = Join-Path $env:TEMP "optiwin-build-$PID.rsp"
[IO.File]::WriteAllLines($rspFile, [string[]]$rsp, (New-Object Text.UTF8Encoding($false)))
try {
    & $csc /noconfig "@$rspFile"
    if ($LASTEXITCODE -ne 0) { throw "Compilation failed (code $LASTEXITCODE)." }
} finally { Remove-Item $rspFile -ErrorAction SilentlyContinue }

$f = Get-Item "$dist\OptiWin.exe"
Write-Host ("OK : {0} ({1:N0} Ko)" -f $f.FullName, ($f.Length / 1KB))
