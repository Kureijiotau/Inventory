# Builds dist\Inventory.exe with the C# compiler that ships with Windows.
# Usage:  .\build.ps1            (development build, version 0.0.0)
#         .\build.ps1 1.0.12     (release build)
param([string]$Version = "0.0.0")

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$csc = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$dist = Join-Path $root "dist"
New-Item -ItemType Directory -Force $dist | Out-Null

$info = Join-Path $dist "BuildInfo.cs"
@"
using System.Reflection;
[assembly: AssemblyTitle("Inventory")]
[assembly: AssemblyProduct("Inventory")]
[assembly: AssemblyVersion("$Version.0")]
[assembly: AssemblyFileVersion("$Version.0")]
static class BuildInfo { public const string Version = "$Version"; }
"@ | Set-Content -Encoding UTF8 $info

& $csc /nologo /target:winexe /optimize+ /platform:anycpu `
    "/out:$dist\Inventory.exe" `
    "/win32icon:$root\host\Inventory.ico" `
    "/resource:$root\index.html,index.html" `
    "/resource:$root\logo.png,logo.png" `
    "/resource:$root\icon.png,icon.png" `
    /reference:System.Windows.Forms.dll `
    "$root\host\Program.cs" $info
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

Set-Content -Encoding ASCII -NoNewline (Join-Path $dist "version.txt") $Version
Write-Host "Built dist\Inventory.exe ($Version)"
