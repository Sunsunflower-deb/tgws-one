# Windows counterpart of fetch.sh: fetches the pinned zapret2 release, verifies its sha256 and unpacks only
# what TgwsOne ships into out\zapret2\linux-x64 and out\zapret2\win-x64. Not exercised until SP5.
# -ZipPath <file> uses a local copy of the archive instead of downloading it; the hash is checked either way.
param([string]$ZipPath = '')
$ErrorActionPreference = 'Stop'

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$lock = @{}
foreach ($line in Get-Content (Join-Path $here 'zapret2.lock')) {
    if ($line -match '^\s*([A-Z0-9_]+)=(.*)$') { $lock[$Matches[1]] = $Matches[2].Trim() }
}

$work = Join-Path ([IO.Path]::GetTempPath()) ('tgws-z2-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    $zip = $ZipPath
    if (-not $zip) {
        $zip = Join-Path $work 'zapret2.zip'
        Write-Host "загрузка $($lock.Z2_URL)"
        Invoke-WebRequest -Uri $lock.Z2_URL -OutFile $zip
    }
    $actual = (Get-FileHash -Algorithm SHA256 -Path $zip).Hash.ToLowerInvariant()
    if ($actual -ne $lock.Z2_SHA256) {
        throw "контрольная сумма архива не совпадает: ожидалась $($lock.Z2_SHA256), получена $actual"
    }

    Expand-Archive -Path $zip -DestinationPath (Join-Path $work 'x')
    $top = Join-Path $work ('x\zapret2-' + $lock.Z2_VERSION)

    $out = Join-Path $root 'out\zapret2'
    foreach ($rid in 'linux-x64', 'win-x64') {
        $dir = Join-Path $out $rid
        if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
        New-Item -ItemType Directory -Path "$dir\lua", "$dir\files\fake" -Force | Out-Null
        foreach ($f in 'zapret-lib.lua', 'zapret-antidpi.lua', 'zapret-auto.lua') {
            Copy-Item "$top\lua\$f" "$dir\lua\"
        }
        foreach ($f in 'tls_clienthello_www_google_com.bin', 'tls_clienthello_vk_com.bin') {
            Copy-Item "$top\files\fake\$f" "$dir\files\fake\"
        }
    }
    Copy-Item "$top\binaries\linux-x86_64\nfqws2" "$out\linux-x64\"
    foreach ($f in 'winws2.exe', 'WinDivert.dll', 'WinDivert64.sys', 'cygwin1.dll') {
        Copy-Item "$top\binaries\windows-x86_64\$f" "$out\win-x64\"
    }

    Write-Host "zapret2 $($lock.Z2_VERSION) готов: $out"
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
