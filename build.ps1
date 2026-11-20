<#
    Сборка MSI-установщика TgwsOne 2.x из уже собранных частей: приложения, движка прокси и обхода.

    Пример:
      .\build.ps1 -AppDir out\publish\win-x64 -Engine out\engine\win-x64\tgws-engine.exe -Version 2.0.0.0

    Требуется:
      * WiX 5 и два его расширения:
          dotnet tool install --global wix --version 5.0.2
          wix extension add -g WixToolset.Util.wixext/5.0.2 WixToolset.Firewall.wixext/5.0.2
      * -AppDir — папка с TgwsOne.exe, zapret2\ и lists\ (на Linux: make build-win; на Windows: dotnet publish -r win-x64
        --self-contained -p:PublishSingleFile=true, затем zapret2\fetch.ps1 и копирование zapret2\ и lists\ рядом);
      * -Engine — tgws-engine.exe, его собирает engine\build-engine.ps1 (PyInstaller, только на Windows).

    Пути без диска и без \\ отсчитываются от папки этого скрипта.
#>
[CmdletBinding()]
param(
    [string]$AppDir = "out\publish\win-x64",
    [string]$Engine = "out\engine\win-x64\tgws-engine.exe",
    [string]$Version = "2.0.0.0",
    [string]$Out,
    # Где собирать дерево для MSI. Должно быть на локальном диске: с сетевой папки (например, общей папки виртуальной
    # машины) WiX читает сотни файлов в разы медленнее.
    [string]$StageDir,

    # Подпись (необязательно): PFX-файл или отпечаток сертификата в хранилище/на токене.
    [string]$Pfx = $env:TGWS_SIGN_PFX,
    [string]$PfxPassword = $env:TGWS_SIGN_PFX_PASSWORD,
    [string]$Thumbprint = $env:TGWS_SIGN_THUMBPRINT,
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

function Resolve-Input([string]$path)
{
    if ([System.IO.Path]::IsPathRooted($path)) { return $path }
    return Join-Path $root $path
}

if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw "версия должна быть из четырёх чисел (2.0.0.0): $Version" }
$AppDir = Resolve-Input $AppDir
$Engine = Resolve-Input $Engine
if (-not $StageDir) { $StageDir = Join-Path $root "build\stage" }
$StageDir = Resolve-Input $StageDir

$verShort = (($Version -split '\.') | Select-Object -First 3) -join '.'
if (-not $Out) { $Out = Join-Path $root ("dist\TgwsOne-" + $verShort + ".msi") }
$Out = Resolve-Input $Out

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
foreach ($rel in @("TgwsOne.exe", "zapret2\winws2.exe", "zapret2\WinDivert64.sys"))
{
    if (-not (Test-Path (Join-Path $AppDir $rel))) { throw "в $AppDir нет $rel (приложение и обход собирает make build-win)" }
}
if (-not (Get-ChildItem (Join-Path $AppDir "lists") -Filter *.txt -ErrorAction SilentlyContinue))
{
    throw "в $AppDir\lists нет списков *.txt"
}
if (-not (Test-Path $Engine)) { throw "не найден движок прокси: $Engine (его собирает engine\build-engine.ps1)" }
foreach ($rel in @("installer\TgwsOne.wxs", "src\app.ico", "README.md", "LICENSE"))
{
    if (-not (Test-Path (Join-Path $root $rel))) { throw "не найден $rel рядом со скриптом" }
}

# ---------- staging ----------
# Папка для дерева стирается целиком, а путь к ней задаёт вызывающий: не позволяем указать на корень диска, на папку
# скрипта, на папку приложения или на любую папку над ними (-StageDir C:\ и опечатки такого рода).
$stageFull = [System.IO.Path]::GetFullPath($StageDir).TrimEnd('\') + '\'
foreach ($protected in @($root, $AppDir, (Split-Path -Parent $Engine)))
{
    $p = [System.IO.Path]::GetFullPath($protected).TrimEnd('\') + '\'
    if ($p.StartsWith($stageFull, [System.StringComparison]::OrdinalIgnoreCase))
    {
        throw "-StageDir $StageDir стёрла бы ${protected} — укажите отдельную папку для дерева сборки"
    }
}
Write-Host "staging -> $StageDir"
Remove-Item $StageDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $StageDir | Out-Null

# Всё из папки приложения, кроме отладочных символов (один libSkiaSharp.pdb — 80 МБ).
& robocopy $AppDir $StageDir /E /XF *.pdb /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy: не удалось скопировать $AppDir ($LASTEXITCODE)" }

Copy-Item $Engine (Join-Path $StageDir "tgws-engine.exe") -Force
Copy-Item (Join-Path $root "README.md") $StageDir -Force
Copy-Item (Join-Path $root "LICENSE") $StageDir -Force

Sign-File (Join-Path $StageDir "TgwsOne.exe")
Sign-File (Join-Path $StageDir "tgws-engine.exe")

# ---------- MSI ----------
$env:PATH = "$env:USERPROFILE\.dotnet\tools;$env:PATH"
$env:DOTNET_ROLL_FORWARD = "LatestMajor"
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Out) | Out-Null

# wix.exe — приложение-обёртка (apphost) .NET. На Windows без полной поддержки CET (нелатаная Windows 10, как образ сборочной
# виртуальной машины, build 19045.2965) apphost .NET 9+/10 падает при старте: «Your Windows doesn't fully support CET»,
# код 0x80131506; сам dotnet.exe при этом запускается нормально. Тогда wix запускается как `dotnet wix.dll` из папки
# инструмента, с теми же аргументами. На обновлённой Windows (в том числе на раннерах GitHub Actions) срабатывает обычный путь.
function Invoke-Wix([string[]]$WixArguments)
{
    if (-not (Get-Command wix -ErrorAction SilentlyContinue))
    {
        throw "wix не найден: dotnet tool install --global wix --version 5.0.2"
    }
    & wix @WixArguments
    if ($LASTEXITCODE -ne -2146233082) { return }   # 0x80131506

    $dll = Get-ChildItem (Join-Path $env:USERPROFILE ".dotnet\tools\.store\wix") -Recurse -Filter wix.dll -ErrorAction SilentlyContinue |
           Select-Object -First 1
    if (-not $dll) { throw "wix.exe не запускается (CET), а wix.dll для запуска через dotnet не найден" }
    Write-Host "wix.exe не стартует (Windows без полной поддержки CET): запускаю как dotnet wix.dll"
    & dotnet $dll.FullName @WixArguments
}

Write-Host "wix build -> $Out"
Invoke-Wix @(
    "build", "-arch", "x64",
    "-ext", "WixToolset.Util.wixext", "-ext", "WixToolset.Firewall.wixext",
    "-d", "Version=$Version",
    "-d", "StageDir=$StageDir",
    "-d", "IconFile=$(Join-Path $root 'src\app.ico')",
    "-pdbtype", "none",
    "-o", $Out,
    (Join-Path $root "installer\TgwsOne.wxs"))
if ($LASTEXITCODE -ne 0) { throw "wix build failed ($LASTEXITCODE)" }

Sign-File $Out

$size = [math]::Round((Get-Item $Out).Length / 1MB, 2)
$hash = (Get-FileHash $Out -Algorithm SHA256).Hash.ToLower()
Write-Host "OK: $Out ($size MB)"
Write-Host "sha256: $hash"
