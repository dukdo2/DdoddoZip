# Builds everything into %LOCALAPPDATA%\DdoddoZip.build\out (app + 7z.dll + right-click DLL).
#   powershell -ExecutionPolicy Bypass -File build.ps1            # Release
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Test      # also run tests
#   powershell -ExecutionPolicy Bypass -File build.ps1 -Installer # also make the setup .exe
# (Messages are in English: Windows PowerShell 5.1 reads BOM-less scripts as ANSI.)
param([switch]$Test, [switch]$Installer)
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
$cl = "cl /nologo /Brepro /O2 /MT /EHsc /std:c++17 /utf-8 /W4 /wd4324 /permissive- /DUNICODE /D_UNICODE /LD " +
      "`"$src\ShellCommands.cpp`" /Fo`"$obj\\`" /link /DEF:`"$src\exports.def`" " +
      "/Brepro /OUT:`"$obj\DdoddoZip.ShellExt.dll`" /IMPLIB:`"$obj\DdoddoZip.ShellExt.lib`" shlwapi.lib ole32.lib shell32.lib runtimeobject.lib"
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

# 3. Installer: self-contained publish (no .NET runtime needed on the user's PC) + Inno Setup
if ($Installer) {
    $dist = Join-Path $env:LOCALAPPDATA 'DdoddoZip.build\dist'
    $setupOut = Join-Path $env:LOCALAPPDATA 'DdoddoZip.build\installer'
    if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
    foreach ($proj in 'DdoddoZip.App\DdoddoZip.App.csproj', 'DdoddoZip.Cli\DdoddoZip.Cli.csproj') {
        dotnet publish "$root\src\$proj" -c Release -r win-x64 --self-contained true -o $dist -nologo -v q
        if ($LASTEXITCODE -ne 0) { throw "Self-contained publish failed: $proj" }
    }
    Copy-Item "$obj\DdoddoZip.ShellExt.dll" $dist -Force

    $iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
            Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $iscc) { throw 'Inno Setup 6 not found (winget install JRSoftware.InnoSetup).' }
    $version = (Get-Item "$dist\DdoddoZip.exe").VersionInfo.FileVersion -replace '\.0$', ''
    # The right-click DLL is built byte-for-byte the same (/Brepro) while its source is unchanged;
    # setup replaces it only when this hash differs, so most updates never touch Explorer.
    $shellHash = (Get-FileHash "$dist\DdoddoZip.ShellExt.dll" -Algorithm SHA256).Hash.ToLowerInvariant()
    & $iscc /Q "/DAppVersion=$version" "/DSourceDir=$dist" "/DOutputDir=$setupOut" "/DShellExtHash=$shellHash" "$root\installer\DdoddoZip.iss"
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }
    "Installer: $setupOut\DdoddoZip-$version-setup.exe"
}
