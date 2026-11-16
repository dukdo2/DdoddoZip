# Developer install: copies the build to %LOCALAPPDATA%\Programs\DdoddoZip and registers the
# right-click item for all users (HKLM). Registration shows one admin (UAC) prompt: Explorer
# on the dev PC ignores per-user (HKCU) context-menu entries.
#   powershell -ExecutionPolicy Bypass -File tools\dev-install.ps1
#   powershell -ExecutionPolicy Bypass -File tools\dev-install.ps1 -SkipRegister   # copy files only
#   powershell -ExecutionPolicy Bypass -File tools\dev-install.ps1 -Uninstall
param([switch]$Uninstall, [switch]$SkipRegister)
$ErrorActionPreference = 'Stop'
$src = Join-Path $env:LOCALAPPDATA 'DdoddoZip.build\out'
$dst = Join-Path $env:LOCALAPPDATA 'Programs\DdoddoZip'

if ($Uninstall) {
    if (Test-Path "$dst\DdoddoZip.exe") {
        Start-Process "$dst\DdoddoZip.exe" '--unregister-shell --user' -Wait
        Start-Process "$dst\DdoddoZip.exe" '--unregister-shell' -Verb RunAs -Wait
    }
    "Unregistered. Files stay in $dst until Explorer releases the DLL (sign out or restart Explorer, then delete the folder)."
    return
}

if (-not (Test-Path "$src\DdoddoZip.exe")) { throw "Build first: $src" }
New-Item -ItemType Directory -Force $dst | Out-Null

# Explorer keeps the right-click DLL loaded. A loaded DLL cannot be overwritten but can be
# renamed, so move it aside and copy the new one in. (Explorer keeps using the old copy
# until it restarts.)
$dll = Join-Path $dst 'DdoddoZip.ShellExt.dll'
if (Test-Path $dll) {
    Get-ChildItem $dst -Filter 'DdoddoZip.ShellExt.dll.old*' | ForEach-Object { try { Remove-Item $_.FullName -Force } catch {} }
    try { Remove-Item $dll -Force } catch { Rename-Item $dll ("DdoddoZip.ShellExt.dll.old" + [DateTime]::Now.Ticks) }
}
Copy-Item "$src\*" $dst -Recurse -Force

if ($SkipRegister) { "Copied to $dst (registration skipped)."; return }
Start-Process "$dst\DdoddoZip.exe" '--unregister-shell --user' -Wait   # drop any old per-user entry
Start-Process "$dst\DdoddoZip.exe" '--register-shell' -Verb RunAs -Wait
"Installed to $dst and registered the right-click item for all users."
