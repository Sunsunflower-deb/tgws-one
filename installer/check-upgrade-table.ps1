# Проверка готового MSI: в таблице Upgrade нет языкового фильтра.
#   powershell -File installer\check-upgrade-table.ps1 <путь к .msi>      код 0 — всё в порядке, 1 — нет
#
# Зачем. <MajorUpgrade> переносит в таблицу Upgrade язык пакета. У 2.0 это русский (1049), а 1.x собран на английском (1033), и
# FindRelatedProducts не находил старую версию: RemoveExistingProducts ничего не делал, и после «обновления» стояли оба продукта
# (SP5, проверка в Windows-ВМ). Теперь строки Upgrade заданы явно и без языка; эта проверка не даёт вернуть фильтр незаметно.
param([Parameter(Mandatory)][string]$Msi)
$ErrorActionPreference = 'Stop'

$installer = New-Object -ComObject WindowsInstaller.Installer
# [string]: из командлета приходит строка в обёртке PSObject, и COM отвечает «Type mismatch»
$db = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @([string](Convert-Path $Msi), 0))
# колонки: UpgradeCode, VersionMin, VersionMax, Language, Attributes, Remove, ActionProperty (по номерам: так надёжнее, чем по именам)
$view = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, @('SELECT * FROM Upgrade'))
$view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null

$rows = 0
$bad = @()
while ($true)
{
    $rec = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
    if (-not $rec) { break }
    $rows++
    $n = $rec.GetType().InvokeMember('FieldCount', 'GetProperty', $null, $rec, $null)
    $f = @(1..$n | ForEach-Object { $rec.GetType().InvokeMember('StringData', 'GetProperty', $null, $rec, $_) })
    $line = "{0}  min={1} max={2} language='{3}' -> {4}" -f $f[0], $f[1], $f[2], $f[3], $f[6]
    Write-Host "  Upgrade: $line"
    if ($f[3]) { $bad += $line }
}
$view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null) | Out-Null

if ($rows -eq 0) { Write-Error "в таблице Upgrade нет строк: $Msi не заменит 1.x"; exit 1 }
if ($bad.Count -gt 0)
{
    Write-Error ("в таблице Upgrade задан язык, и MSI не найдёт 1.x (английский, 1033):`n  " + ($bad -join "`n  "))
    exit 1
}
Write-Host "Upgrade: $rows строк, языкового фильтра нет"
