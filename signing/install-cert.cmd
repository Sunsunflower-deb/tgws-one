@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "CERT=TgwsOne-CodeSigning.cer"

if not exist "%CERT%" (
    echo.
    echo Не найден "%CERT%" рядом с этим файлом.
    echo Положите install-cert.cmd и сертификат в одну папку.
    echo.
    pause
    exit /b 1
)

rem --- нужны права администратора ---
net session >nul 2>&1
if errorlevel 1 (
    echo Запрашиваю права администратора...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs" 2>nul
    if errorlevel 1 (
        echo.
        echo Не удалось поднять права. Запустите файл правой кнопкой -
        echo "Запуск от имени администратора".
        echo.
        pause
    )
    exit /b 0
)

echo.
echo Добавляю сертификат в "Доверенные корневые центры" и "Доверенные издатели"
echo для всех пользователей этого компьютера...
echo.

certutil -addstore -f "Root" "%CERT%" >nul
if errorlevel 1 goto :err

certutil -addstore -f "TrustedPublisher" "%CERT%" >nul
if errorlevel 1 goto :err

echo Готово. Теперь MSI запускается как от известного издателя.
echo.
echo Проверить подпись установщика:
echo   powershell -Command "Get-AuthenticodeSignature 'TgwsOne-1.0.5-selfsigned.msi' ^| Format-List Status"
echo.
echo Удалить сертификат потом:
echo   certutil -delstore "Root" 94A82AB20B72495E5AADFFBDD066483CF67694E8
echo   certutil -delstore "TrustedPublisher" 94A82AB20B72495E5AADFFBDD066483CF67694E8
echo.
pause
exit /b 0

:err
echo.
echo Не удалось добавить сертификат. Код ошибки: %errorlevel%
echo Убедитесь, что запущено от администратора.
echo.
pause
exit /b 1
