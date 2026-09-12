<#
.SYNOPSIS
  Builds Code Three and, optionally, drops it into GTA V.

.DESCRIPTION
  Uses the self-contained Roslyn compiler rather than `dotnet build`. The machine SDK is a
  partial install -- every `dotnet` command dies with "hostpolicy.dll not found" and
  `dotnet --list-runtimes` hides the cause. Nothing here needs MSBuild anyway: one library,
  no NuGet, no project file.

  The toolchain is NOT in this repo. It is ~174 MB of compiler and reference assemblies and
  there is already a copy on this machine under the hoodrich project, so this looks there
  rather than carrying a third one. Point -Tools somewhere else, or drop a tools\ folder in
  beside this script, and it will use that instead.

.EXAMPLE
  .\build.ps1
  .\build.ps1 -Deploy
  .\build.ps1 -Deploy -HotSwap
  .\build.ps1 -Package
#>
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',

    [switch]$Deploy,

    # Builds the release zip in release\, with the tree a player unpacks.
    [switch]$Package,

    [ValidateSet('Legacy', 'Enhanced', 'Both')]
    [string]$Target = 'Both',

    # Deploy while the game is running, for a hot reload.
    #
    # Normally refused, and the refusal is right for asset mods -- but this is one dll and one
    # ini, and ScriptHookVDotNet SHADOW-COPIES the assembly before running it. The file in
    # scripts\ is therefore not locked, and replacing it then pressing Insert reloads the mod
    # in place without leaving the game.
    #
    # Safe here specifically because the Aborted handler hands everything back before the
    # reload: the ambulance dispatch service goes back on, and the van, the crew, the trolley
    # and any body welded to it are all released. A mod that leaked any of those on unload
    # would not be safe to hot swap, and this one is written to be tested on exactly that.
    [switch]$HotSwap,

    [string]$GtaDir = 'C:\Program Files (x86)\Steam\steamapps\common\Grand Theft Auto V',
    [string]$EnhancedDir = 'C:\Program Files (x86)\Steam\steamapps\common\Grand Theft Auto V Enhanced',

    # Where the compiler lives. Its own tools\ first, then the one next door.
    [string]$Tools = ''
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# --- the toolchain ----------------------------------------------------------
if (-not $Tools) {
    $candidates = @(
        (Join-Path $root 'tools'),
        (Join-Path (Split-Path $root -Parent) 'hoodrich\tools')
    )

    foreach ($c in $candidates) {
        if (Test-Path (Join-Path $c 'roslyn\tasks\net472\csc.exe')) { $Tools = $c; break }
    }
}

if (-not $Tools) {
    throw "No compiler found. Looked in .\tools\ and ..\hoodrich\tools\. Pass -Tools <path>."
}

$csc    = Join-Path $Tools 'roslyn\tasks\net472\csc.exe'
$refDir = Join-Path $Tools 'refasm\build\.NETFramework\v4.8'

if (-not (Test-Path $csc))    { throw "Compiler missing: $csc" }
if (-not (Test-Path $refDir)) { throw "net48 reference assemblies missing: $refDir" }

$srcDir = Join-Path $root 'src\CodeThree'
$outDir = Join-Path $root 'build'
$outDll = Join-Path $outDir 'CodeThree.dll'

# Either install serves as the reference source -- both ship the identical
# ScriptHookVDotNet3.dll, so a pure SHVDN script is one build that runs on both.
$shvdn = $null
foreach ($d in @($EnhancedDir, $GtaDir)) {
    $p = Join-Path $d 'ScriptHookVDotNet3.dll'
    if (Test-Path $p) { $shvdn = $p; break }
}
if (-not $shvdn) { throw "ScriptHookVDotNet3.dll not found in either install." }

# WHICH ScriptHookVDotNet, said out loud, every build.
#
# The compiler stamps the reference assembly's EXACT version into the output, so a mod built
# against 3.9 is a mod that ASKS for 3.9 -- and a player on 3.7 gets a load failure with no
# log, because the thing that would have written the log is the thing that did not load.
$shvdnVer = [System.Reflection.AssemblyName]::GetAssemblyName($shvdn).Version
Write-Host "ScriptHookVDotNet reference: $shvdnVer  (players need this or newer)" -ForegroundColor DarkCyan

New-Item -ItemType Directory -Force $outDir | Out-Null

# --- references -------------------------------------------------------------
# Same rule as hoodrich, five0patrol and overspray: the BCL and SHVDN, nothing else. A mod
# with no external dependencies cannot lose a version fight with another mod in scripts\ --
# one folder is one assembly resolution namespace.
$refNames = @(
    'mscorlib.dll'
    'System.dll'
    'System.Core.dll'
    'System.Drawing.dll'
    'System.Windows.Forms.dll'
    'System.Numerics.dll'
)

$refs = @()
foreach ($n in $refNames) {
    $p = Join-Path $refDir $n
    if (-not (Test-Path $p)) { throw "Reference assembly missing: $p" }
    $refs += "/reference:`"$p`""
}
$refs += "/reference:`"$shvdn`""

# --- sources ----------------------------------------------------------------
$sources = Get-ChildItem $srcDir -Recurse -Filter *.cs |
    Where-Object { $_.FullName -notmatch '\\(obj|bin)\\' } |
    ForEach-Object { $_.FullName }

if (-not $sources) { throw "No .cs sources found under $srcDir" }

# --- compiler options -------------------------------------------------------
$opts = @(
    '/target:library'
    '/platform:x64'
    '/langversion:9.0'
    '/nologo'
    '/warnaserror-'
    '/warn:4'
    '/nostdlib+'
    '/utf8output'
    "/out:`"$outDll`""
)

if ($Configuration -eq 'Debug') {
    $opts += '/debug:portable', '/define:DEBUG;TRACE', '/optimize-'
} else {
    $opts += '/debug-', '/optimize+'
}

$rsp = Join-Path $outDir 'build.rsp'
($opts + $refs + ($sources | ForEach-Object { "`"$_`"" })) | Set-Content -Path $rsp -Encoding UTF8

Write-Host "Compiling $($sources.Count) source files -> $outDll ($Configuration)" -ForegroundColor Cyan
$sw = [Diagnostics.Stopwatch]::StartNew()
& $csc "@$rsp"
$exit = $LASTEXITCODE
$sw.Stop()

if ($exit -ne 0) { throw "Compilation failed (csc exit $exit)." }
Write-Host ("OK  {0:N0} bytes in {1:N1}s" -f (Get-Item $outDll).Length, $sw.Elapsed.TotalSeconds) -ForegroundColor Green

# --- deploy -----------------------------------------------------------------
function Deploy-To([string]$dir, [string]$label) {
    if (-not (Test-Path $dir)) { Write-Host "  skip   $label (not installed)" -ForegroundColor DarkGray; return }

    # A FOLDER IS NOT AN INSTALL. The Legacy tree on this machine still has a scripts\ folder
    # full of mods but no GTA5.exe -- the game was migrated to Enhanced and the leftovers stayed.
    # Deploying into it silently puts the dll somewhere nothing will ever load it from, and the
    # symptom is a mod that "does not work" with a clean build and no log.
    $exe = @('GTA5.exe', 'GTA5_Enhanced.exe') | Where-Object { Test-Path (Join-Path $dir $_) }
    if (-not $exe) {
        Write-Host "  skip   $label (folder exists but has no game exe)" -ForegroundColor DarkYellow
        return
    }

    $scripts = Join-Path $dir 'scripts'
    New-Item -ItemType Directory -Force $scripts | Out-Null

    Copy-Item $outDll (Join-Path $scripts 'CodeThree.dll') -Force

    $iniSrc = Join-Path $root 'CodeThree.ini'
    $iniDst = Join-Path $scripts 'CodeThree.ini'

    if (Test-Path $iniSrc) {
        if (Test-Path $iniDst) {
            # NEVER OVERWRITTEN, BUT KEPT UP TO DATE.
            #
            # It is the one file a player hand-edits, so their values are sacred and nothing
            # here changes one. But a deploy that only ever says "keep" leaves every option
            # added since they installed invisible, running on built-in defaults that cannot be
            # seen or changed from the file. So the keys that are MISSING are added, with the
            # comment block that explains each one, at the end of the section they belong to.
            $srcLines = Get-Content $iniSrc
            $dstLines = [System.Collections.Generic.List[string]](Get-Content $iniDst)

            $have = @{}
            foreach ($l in $dstLines) {
                if ($l -match '^\s*([A-Za-z_]\w*)\s*=') { $have[$Matches[1]] = $true }
            }

            $add = @{}
            $sec = ''
            $block = New-Object System.Collections.Generic.List[string]

            foreach ($l in $srcLines) {
                if ($l -match '^\s*\[(.+?)\]\s*$') { $sec = $Matches[1]; $block.Clear(); continue }

                if ($l -match '^\s*([A-Za-z_]\w*)\s*=') {
                    $key = $Matches[1]
                    if (-not $have.ContainsKey($key)) {
                        if (-not $add.ContainsKey($sec)) {
                            $add[$sec] = New-Object System.Collections.Generic.List[string]
                        }
                        $add[$sec].Add('')
                        foreach ($b in $block) { $add[$sec].Add($b) }
                        $add[$sec].Add($l)
                    }
                    $block.Clear()
                    continue
                }

                if ($l -match '^\s*$') { $block.Clear() } else { $block.Add($l) }
            }

            if ($add.Count -eq 0) {
                Write-Host "  keep   CodeThree.ini" -ForegroundColor DarkGray
            } else {
                $added = 0

                foreach ($sec in $add.Keys) {
                    $start = -1
                    for ($i = 0; $i -lt $dstLines.Count; $i++) {
                        if ($dstLines[$i] -match "^\s*\[$([regex]::Escape($sec))\]\s*$") { $start = $i; break }
                    }

                    if ($start -lt 0) {
                        $dstLines.Add('')
                        $dstLines.Add("[$sec]")
                        foreach ($line in $add[$sec]) { $dstLines.Add($line) }
                    } else {
                        $end = $dstLines.Count
                        for ($i = $start + 1; $i -lt $dstLines.Count; $i++) {
                            if ($dstLines[$i] -match '^\s*\[.+?\]\s*$') { $end = $i; break }
                        }
                        $dstLines.InsertRange($end, [string[]]$add[$sec])
                    }

                    foreach ($line in $add[$sec]) {
                        if ($line -match '^\s*([A-Za-z_]\w*)\s*=') { $added++ }
                    }
                }

                # CRLF, because every other line in the file has one and a mixed file is a file
                # somebody's editor will rewrite wholesale the next time they open it.
                [System.IO.File]::WriteAllText($iniDst, ($dstLines -join "`r`n") + "`r`n")

                Write-Host "  ini    $added new option(s) added, your settings untouched" -ForegroundColor Green
            }
        } else {
            Copy-Item $iniSrc $iniDst
            Write-Host "  new    CodeThree.ini" -ForegroundColor Green
        }
    }

    # --- art -----------------------------------------------------------------
    #
    # The set's seal, which UI.Splash draws beside the name on the load row. Overwritten when
    # it differs -- nobody hand-edits one, and a stale mark is a bug that looks like a
    # rendering fault.
    $artSrc = Join-Path $root 'data\icons'
    $artDst = Join-Path $scripts 'CodeThree\icons'

    if (Test-Path $artSrc) {
        New-Item -ItemType Directory -Force $artDst | Out-Null

        $a = 0
        foreach ($f in Get-ChildItem $artSrc -Filter *.png) {
            $to = Join-Path $artDst $f.Name

            if ((Test-Path $to) -and (Get-Item $to).Length -eq $f.Length -and
                (Get-FileHash $to).Hash -eq (Get-FileHash $f.FullName).Hash) { continue }

            Copy-Item $f.FullName $to -Force
            $a++
        }

        if ($a -gt 0) { Write-Host "  art    $a icon(s)" -ForegroundColor Green }
        else          { Write-Host "  art    up to date" -ForegroundColor DarkGray }
    }

    Write-Host "  ok     $label" -ForegroundColor Green
}

if ($Deploy) {
    $running = Get-Process GTA5, GTA5_Enhanced -ErrorAction SilentlyContinue

    if ($running -and -not $HotSwap) {
        throw "GTA V is running - close it before deploying, or pass -HotSwap and press Insert."
    }

    if ($running -and $HotSwap) {
        Write-Host "GTA V is running; hot swapping. Press Insert in game to reload scripts." -ForegroundColor Yellow
        Write-Host "  note   new ini options only apply once they exist in the installed ini" -ForegroundColor DarkGray
    }

    if ($Target -in 'Legacy', 'Both')   { Deploy-To $GtaDir      'Legacy' }
    if ($Target -in 'Enhanced', 'Both') { Deploy-To $EnhancedDir 'Enhanced' }

    Write-Host "Deploy complete." -ForegroundColor Green
}

# --- package ----------------------------------------------------------------
#
# THE ZIP IS THE PRODUCT, and its shape is the whole install. Somebody who has never seen this
# repo has one job -- drag "scripts" into the GTA folder -- and every way that goes wrong is a
# folder in the wrong place. So this builds the tree explicitly and then CHECKS it, because a
# packaging script that quietly ships four files instead of five is a support thread.
if ($Package) {
    $ver = (Select-String -Path (Join-Path $root 'src\CodeThree\Core\Log.cs') `
                          -Pattern 'Version = "([^"]+)"').Matches[0].Groups[1].Value

    $stage = Join-Path $root 'build\pkg'
    $zip = Join-Path $root ("release\CodeThree-" + $ver + ".zip")

    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force (Join-Path $stage 'scripts\CodeThree\icons') | Out-Null
    New-Item -ItemType Directory -Force (Join-Path $root 'release') | Out-Null

    Copy-Item $outDll                          (Join-Path $stage 'scripts\CodeThree.dll')
    Copy-Item (Join-Path $root 'CodeThree.ini') (Join-Path $stage 'scripts\CodeThree.ini')
    Copy-Item (Join-Path $root 'README.md')    (Join-Path $stage 'README.txt')

    foreach ($p in Get-ChildItem (Join-Path $root 'data\icons') -Filter *.png) {
        Copy-Item $p.FullName (Join-Path $stage 'scripts\CodeThree\icons')
    }

    # Every file the mod actually reads, by the path it reads it from. Missing any one of
    # these is a different broken install, and all of them are silent.
    [string[]]$must = @(
        'README.txt',
        'scripts\CodeThree.dll',
        'scripts\CodeThree.ini',
        'scripts\CodeThree\icons\seal-face.png',
        'scripts\CodeThree\icons\seal-ring.png'
    )

    $missing = @()
    foreach ($m in $must) { if (-not (Test-Path (Join-Path $stage $m))) { $missing += $m } }
    if ($missing) { throw "Package is missing: $($missing -join ', ')" }

    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -CompressionLevel Optimal

    Write-Host ""
    Write-Host ("Packaged  {0}" -f (Split-Path $zip -Leaf)) -ForegroundColor Green
    foreach ($m in $must) {
        $f = Get-Item (Join-Path $stage $m)
        Write-Host ("  {0,-42} {1,9:N0} bytes" -f $m, $f.Length) -ForegroundColor DarkGray
    }
    Write-Host ("  {0,-42} {1,9:N0} bytes" -f '(zip)', (Get-Item $zip).Length)
}
