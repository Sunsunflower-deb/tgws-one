<#
.SYNOPSIS
    Builds the headless tg-ws-proxy engine for Windows: out\engine\win-x64\tgws-engine.exe

.DESCRIPTION
    The same steps as build-engine.sh: download the pinned upstream commit, verify its sha256, install the
    dependencies from the hashed requirements.lock into a fresh venv (uv) and bundle proxy/tg_ws_proxy.py with
    PyInstaller --onefile.
    Needs: Windows 10 1803+ (tar.exe), uv on PATH. Not run in SP1 (no Windows here); first run is in the VM (SP5).
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$here = $PSScriptRoot
$root = Split-Path -Parent $here

# engine.lock: plain KEY=value lines, '#' comments, optional double quotes around the value.
$lock = @{}
foreach ($line in Get-Content -LiteralPath (Join-Path $here 'engine.lock')) {
    if ($line -match '^\s*([A-Z0-9_]+)=(.*)$') { $lock[$Matches[1]] = $Matches[2].Trim().Trim('"') }
}

function Invoke-Native {
    # Runs a native command and fails the build on a non-zero exit code.
    param([string]$File, [string[]]$Arguments)
    & $File @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$File exited with code $LASTEXITCODE" }
}

$out = Join-Path $root 'out\engine\win-x64'
$work = Join-Path ([IO.Path]::GetTempPath()) ('tgws-engine-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null

try {
    $url = "https://github.com/$($lock.UPSTREAM_REPO)/archive/$($lock.UPSTREAM_COMMIT).tar.gz"
    Write-Host "==> downloading $($lock.UPSTREAM_REPO) $($lock.UPSTREAM_TAG) ($($lock.UPSTREAM_COMMIT))"
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12   # Windows PowerShell 5.1
    $tarball = Join-Path $work 'upstream.tar.gz'
    Invoke-WebRequest -Uri $url -OutFile $tarball -UseBasicParsing

    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $tarball).Hash.ToLowerInvariant()
    if (-not $lock.UPSTREAM_SHA256) {
        throw "UPSTREAM_SHA256 is empty in engine\engine.lock; the download hashes to $actual. Check it, record it there (trust on first use), then run again."
    }
    if ($actual -ne $lock.UPSTREAM_SHA256) {
        throw "sha256 mismatch for $url`n  expected $($lock.UPSTREAM_SHA256)`n  actual   $actual"
    }
    Write-Host '    sha256 ok'

    # The source is untrusted data: its own empty directory, nothing is run from inside it.
    $src = Join-Path $work 'src'
    New-Item -ItemType Directory -Path $src | Out-Null
    Invoke-Native 'tar' @('-xzf', $tarball, '-C', $src, '--strip-components=1')

    Write-Host "==> python $($lock.PYTHON) venv and pinned packages"
    $venv = Join-Path $work 'venv'
    Invoke-Native 'uv' @('venv', '--python', $lock.PYTHON, $venv)
    $py = Join-Path $venv 'Scripts\python.exe'
    # requirements.lock pins every package with hashes (the Windows-only ones sit under markers).
    # --require-hashes refuses anything that is missing from it, unpinned, or does not match its hash.
    Invoke-Native 'uv' @('pip', 'install', '--python', $py, '--require-hashes', '-r', (Join-Path $here 'requirements.lock'))

    Write-Host '==> bundling'
    New-Item -ItemType Directory -Path $out -Force | Out-Null
    # utils.logging_setup is imported lazily inside main(), so PyInstaller cannot see it. -I: isolated mode.
    Invoke-Native $py @(
        '-I', '-m', 'PyInstaller', '--noconfirm', '--clean', '--onefile', '--name', 'tgws-engine',
        '--paths', $src,
        '--hidden-import', 'utils.logging_setup',
        '--exclude-module', 'customtkinter', '--exclude-module', 'pystray', '--exclude-module', 'PIL', '--exclude-module', 'tkinter',
        '--distpath', $out, '--workpath', (Join-Path $work 'build'), '--specpath', $work,
        (Join-Path $here 'entry.py'))

    Write-Host '==> smoke test'
    $exe = Join-Path $out 'tgws-engine.exe'
    $help = (& $exe --help) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw "$exe --help exited with code $LASTEXITCODE" }
    # Every flag EngineCommand (src/TgwsOne.Core) puts on the command line must exist in this build.
    $flags = '--host', '--port', '--secret', '--dc-ip', '--no-cfproxy', '--no-h2', '--no-secure', '--cfproxy-domain',
             '--cfproxy-worker-domain', '--verbose', '--log-file', '--log-max-mb', '--log-backups', '--buf-kb', '--pool-size'
    foreach ($flag in $flags) {
        if (-not $help.Contains($flag)) { throw "engine --help does not list $flag: EngineCommand and this build disagree" }
    }

    $size = [math]::Round((Get-Item -LiteralPath $exe).Length / 1MB, 1)
    Write-Host "==> $exe ($size MB)"
}
finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
