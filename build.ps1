# Builds everything into %LOCALAPPDATA%\DdoddoZip.build\out (app + 7z.dll + right-click DLL).
#   powershell -ExecutionPolicy Bypass -File build.ps1            # Release
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Test      # also run tests
# (Messages are in English: Windows PowerShell 5.1 reads BOM-less scripts as ANSI.)
param([switch]$Test)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $env:LOCALAPPDATA 'DdoddoZip.build\out'
$obj = Join-Path $env:LOCALAPPDATA 'DdoddoZip.build\shellext'
New-Item -ItemType Directory -Force $out, $obj | Out-Null

# 1. C++ right-click DLL (static CRT, so no Visual C++ runtime is needed on user PCs)
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio C++ tools not found (Desktop development with C++).' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$src = Join-Path $root 'src\DdoddoZip.ShellExt'
$cl = "cl /nologo /O2 /MT /EHsc /std:c++17 /utf-8 /W4 /wd4324 /permissive- /DUNICODE /D_UNICODE /LD " +
      "`"$src\QuickZipCommand.cpp`" /Fo`"$obj\\`" /link /DEF:`"$src\exports.def`" " +
      "/OUT:`"$obj\DdoddoZip.ShellExt.dll`" /IMPLIB:`"$obj\DdoddoZip.ShellExt.lib`" shlwapi.lib ole32.lib shell32.lib runtimeobject.lib"
Push-Location $obj   # keep any compiler side files out of the caller's folder
try { cmd /c "`"$vcvars`" >nul 2>nul && $cl" } finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { throw 'C++ build failed' }

# 2. .NET app (ReadyToRun for fast start; needs the .NET 10 Desktop Runtime)
dotnet publish "$root\src\DdoddoZip.App\DdoddoZip.App.csproj" -c Release -r win-x64 --self-contained false -o $out -nologo -v q
if ($LASTEXITCODE -ne 0) { throw '.NET app build failed' }
dotnet publish "$root\src\DdoddoZip.Cli\DdoddoZip.Cli.csproj" -c Release -r win-x64 --self-contained false -o $out -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'CLI build failed' }

Copy-Item "$obj\DdoddoZip.ShellExt.dll" $out -Force

if ($Test) {
    dotnet test "$root\tests\DdoddoZip.Tests" -nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
}
"Build done: $out"
