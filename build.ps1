<#
    Сборка TgwsOne.exe и установщика MSI.

    Пример:
      .\build.ps1 -Engine _upstream\TgWsProxy_windows.exe -ZapretBin _zapret\bin -Version 1.0.3.0

    Требуется:
      * csc.exe из .NET Framework 4.x (входит в Windows);
      * WiX 5: dotnet tool install --global wix --version 5.*;
      * бинарники upstream (движок прокси и zapret) — в репозитории их нет.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Engine,
    [Parameter(Mandatory = $true)][string]$ZapretBin,
    [string]$Version = "1.0.3.0",
    [string]$Out,

    # Подпись (необязательно): PFX-файл или отпечаток сертификата в хранилище/на токене.
    [string]$Pfx = $env:TGWS_SIGN_PFX,
    [string]$PfxPassword = $env:TGWS_SIGN_PFX_PASSWORD,
    [string]$Thumbprint = $env:TGWS_SIGN_THUMBPRINT,
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$build = Join-Path $root "build"
$stage = Join-Path $build "stage"

$verShort = (($Version -split '\.') | Select-Object -First 3) -join '.'
if (-not $Out) { $Out = Join-Path $root ("dist\TgwsOne-" + $verShort + ".msi") }

# ---------- подпись ----------
function Find-SignTool
{
    $roots = @(
        (Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"),
        (Join-Path $env:ProgramFiles "Windows Kits\10\bin")
    )
    foreach ($root in $roots)
    {
        if (-not (Test-Path $root)) { continue }
        $exe = Get-ChildItem $root -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
               Where-Object { $_.FullName -match '\\x64\\' } |
               Sort-Object FullName -Descending | Select-Object -First 1
        if ($exe) { return $exe.FullName }
    }
    $cmd = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    return $null
}

function Sign-File([string]$path)
{
    if (-not $Pfx -and -not $Thumbprint)
    {
        Write-Host ("подпись: пропущено (нет -Pfx/-Thumbprint) — {0}" -f (Split-Path $path -Leaf))
        return
    }

    $signtool = Find-SignTool
    if (-not $signtool) { throw "signtool.exe не найден: установите Windows SDK (Signing Tools)" }

    $arguments = @("sign", "/fd", "sha256", "/tr", $TimestampUrl, "/td", "sha256")
    if ($Pfx)
    {
        $arguments += @("/f", $Pfx)
        if ($PfxPassword) { $arguments += @("/p", $PfxPassword) }
    }
    else
    {
        $arguments += @("/sha1", $Thumbprint)
    }
    $arguments += @($path)

    & $signtool @arguments
    if ($LASTEXITCODE -ne 0) { throw ("signtool: не удалось подписать " + $path) }
    Write-Host ("подписано: {0}" -f (Split-Path $path -Leaf))
}

# ---------- проверки ----------
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR "Microsoft.NET\Framework\v4.0.30319\csc.exe" }
if (-not (Test-Path $csc)) { throw "csc.exe (.NET Framework 4.x) не найден" }
if (-not (Test-Path $Engine)) { throw "не найден движок прокси: $Engine" }
if (-not (Test-Path (Join-Path $ZapretBin "winws.exe"))) { throw "в $ZapretBin нет winws.exe" }

# ---------- staging ----------
Write-Host "staging -> $stage"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stage, (Join-Path $stage "lists"), (Join-Path $stage "zapret\bin") | Out-Null

$exe = Join-Path $stage "TgwsOne.exe"
Write-Host "compile TgwsOne.exe"
& $csc /nologo /target:winexe /platform:anycpu /utf8output /win32icon:"$root\src\app.ico" `
    /out:$exe `
    /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll `
    (Join-Path $root "src\TgwsOne.cs")
if ($LASTEXITCODE -ne 0) { throw "ошибка компиляции ($LASTEXITCODE)" }

Sign-File $exe

Copy-Item $Engine (Join-Path $stage "TgWsProxy_windows.exe") -Force
Copy-Item (Join-Path $root "src\app.ico") $stage -Force
Copy-Item (Join-Path $root "README.md") $stage -Force
Copy-Item (Join-Path $root "lists\*.txt") (Join-Path $stage "lists") -Force
Copy-Item (Join-Path $ZapretBin "*") (Join-Path $stage "zapret\bin") -Force

# ---------- генерация компонентов ----------
function San([string]$s)
{
    $r = [regex]::Replace($s, '[^A-Za-z0-9]', '_')
    if ($r -match '^\d') { $r = '_' + $r }
    return $r
}

$dirs = @(
    @{ Path = $stage;                    Id = "INSTALLDIR" },
    @{ Path = (Join-Path $stage "lists"); Id = "DIR_LISTS" },
    @{ Path = (Join-Path $stage "zapret\bin"); Id = "DIR_ZAPRET_BIN" }
)

$sb = New-Object System.Text.StringBuilder
foreach ($d in $dirs)
{
    foreach ($f in (Get-ChildItem $d.Path -File | Sort-Object Name))
    {
        $rel = $f.FullName.Substring($stage.Length + 1)
        $id = San ($d.Id.ToLower() + "_" + $f.Name)
        [void]$sb.AppendLine('      <Component Id="cmp_' + $id + '" Directory="' + $d.Id + '" Bitness="always64" Guid="*">')
        [void]$sb.AppendLine('        <File Id="fil_' + $id + '" Source="$(var.StageDir)\' + $rel + '" KeyPath="yes" />')
        [void]$sb.AppendLine('      </Component>')
    }
}

$template = Get-Content (Join-Path $root "installer\TgwsOne.template.wxs") -Raw -Encoding UTF8
$wxs = $template.Replace('<!--COMPONENTS-->', $sb.ToString().TrimEnd())
$generated = Join-Path $build "TgwsOne.generated.wxs"
[System.IO.File]::WriteAllText($generated, $wxs, (New-Object System.Text.UTF8Encoding($false)))

# ---------- MSI ----------
$env:PATH = "$env:USERPROFILE\.dotnet\tools;$env:PATH"
$env:DOTNET_ROLL_FORWARD = "LatestMajor"
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Out) | Out-Null

Write-Host "wix build -> $Out"
& wix build -arch x64 `
    -d Version=$Version `
    -d StageDir="$stage" `
    -o $Out `
    $generated
if ($LASTEXITCODE -ne 0) { throw "wix build failed ($LASTEXITCODE)" }

Sign-File $Out

$size = [math]::Round((Get-Item $Out).Length / 1MB, 2)
Write-Host "OK: $Out ($size MB)"
