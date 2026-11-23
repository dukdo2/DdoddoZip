# Compression speed and size, DdoddoZip vs Bandizip, on a fixed mixed data set.
#   powershell -ExecutionPolicy Bypass -File tools\benchmark.ps1
#   powershell -ExecutionPolicy Bypass -File tools\benchmark.ps1 -Formats 7z -Levels normal -Runs 3
# Data: a copy of the .NET runtime and SDK folders (thousands of DLLs, text and config files),
# made once in %LOCALAPPDATA%\DdoddoZip.build\speed\data. Each case keeps the fastest of -Runs.
# Run it with nothing else busy on the PC: timings move 5-10% between runs.
param(
    [string]$Formats = 'zip,7z',
    [string]$Levels = 'fast,normal,max',
    [int]$Runs = 3,
    [string]$Ddz = "$env:LOCALAPPDATA\DdoddoZip.build\out\ddz.exe",
    [string]$Extract = 'yes'
)
$ErrorActionPreference = 'Stop'
$bz = "$env:ProgramFiles\Bandizip\bz.exe"
$hasBz = Test-Path $bz
$work = "$env:LOCALAPPDATA\DdoddoZip.build\speed"
$src = "$work\data"
if (-not (Test-Path $src)) {
    New-Item -ItemType Directory -Force $src | Out-Null
    Copy-Item "$env:ProgramFiles\dotnet\shared" "$src\shared" -Recurse
    $sdk = Get-ChildItem "$env:ProgramFiles\dotnet\sdk" -Directory | Sort-Object Name -Descending | Select-Object -First 1
    Copy-Item $sdk.FullName "$src\sdk" -Recurse
}
$s = Get-ChildItem $src -Recurse -File | Measure-Object Length -Sum
"Data: {0:N0} files, {1:N0} MB. DdoddoZip: {2}" -f $s.Count, ($s.Sum / 1MB), (Get-Item $Ddz).VersionInfo.ProductVersion
if (-not $hasBz) { "(Bandizip not installed: DdoddoZip only)" }
$bzLevel = @{ fast = 1; normal = 5; max = 9 }

function Best([scriptblock]$block) {
    $best = [double]::MaxValue
    for ($r = 0; $r -lt $Runs; $r++) {
        $sw = [Diagnostics.Stopwatch]::StartNew(); & $block | Out-Null; $sw.Stop()
        # A failed run ends early and would look like the fastest one: count it as an error instead.
        if ($LASTEXITCODE -ne 0) { throw "run failed (exit $LASTEXITCODE)" }
        $best = [Math]::Min($best, $sw.Elapsed.TotalSeconds)
    }
    $best
}

$rows = foreach ($fmt in $Formats -split ',') {
    foreach ($lv in $Levels -split ',') {
        $a = "$work\ddz.$fmt"; $b = "$work\bz.$fmt"
        $tA = Best { Remove-Item $a -ErrorAction SilentlyContinue; & $Ddz a $a $src --level $lv -y }
        $row = [ordered]@{ Format = $fmt; Level = $lv; 'DdoddoZip s' = [Math]::Round($tA, 1); 'DdoddoZip MB' = [Math]::Round((Get-Item $a).Length / 1MB, 1) }
        if ($hasBz) {
            $tB = Best { Remove-Item $b -ErrorAction SilentlyContinue; & $bz c -y -r "-l:$($bzLevel[$lv])" "-fmt:$fmt" $b $src }
            $row['Bandizip s'] = [Math]::Round($tB, 1); $row['Bandizip MB'] = [Math]::Round((Get-Item $b).Length / 1MB, 1)
        }
        if ($Extract -eq 'yes') {
            $out = "$work\x"
            $row['DdoddoZip extract s'] = [Math]::Round((Best { Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue; & $Ddz x $a -o $out --folder never }), 1)
            if ($hasBz) { $row['Bandizip extract s'] = [Math]::Round((Best { Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue; & $bz x -y "-o:$out" $b }), 1) }
            Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
        }
        Remove-Item $a, $b -ErrorAction SilentlyContinue
        [pscustomobject]$row
    }
}
$rows | Format-Table -AutoSize | Out-String -Width 200
