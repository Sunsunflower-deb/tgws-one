# Подпись приложения

Подпись кода (Authenticode) убирает «Неизвестный издатель» в SmartScreen и повышает доверие
антивирусов к самому exe/msi. Драйвер WinDivert она не «обелит» — он подписан своим автором,
и детекты `RiskTool/WinDivert` от нашей подписи не зависят.

Подписывать нужно **два файла**: `TgwsOne.exe` (до упаковки в MSI) и сам `TgwsOne-x.y.z.msi`.

## Чем подписывать

| Вариант | Цена | Что даёт | Нюансы |
|---|---|---|---|
| **Самоподписанный** | 0 | Никакого предупреждения только там, где сертификат добавлен в «Доверенные корневые» (домен, GPO) | Для интернета бесполезен: SmartScreen всё равно предупредит |
| **OV-сертификат** (Certum, SSL.com, Sectigo) | ~$100–400/год | Постепенно набирается репутация в SmartScreen | С 2023 ключ обязан быть на HSM/токене, в CI — облачная подпись |
| **EV-сертификат** | ~$300–600/год | Доверие SmartScreen сразу | Тоже HSM/токен |
| **Azure Trusted Signing** | от ~$10/мес | Облачная подпись, удобно для CI | Нужна подписка Azure и проверка личности/организации |
| **SignPath Foundation** | бесплатно для OSS | Подпись в CI для открытых проектов | Нужно подать заявку, проект должен быть open source |

Для открытого проекта проще всего **SignPath Foundation** или дешёвый OSS-сертификат Certum;
для коммерческого — EV или Azure Trusted Signing.

## Локальная подпись (PFX)

```powershell
# 1. exe
signtool sign /fd sha256 /tr http://timestamp.digicert.com /td sha256 `
  /f cert.pfx /p "пароль" build\stage\TgwsOne.exe

# 2. msi (после сборки)
signtool sign /fd sha256 /tr http://timestamp.digicert.com /td sha256 `
  /f cert.pfx /p "пароль" dist\TgwsOne-1.0.5.msi
```

`signtool.exe` ставится с Windows SDK (компонент *Windows SDK Signing Tools*).

**Таймстамп обязателен**: без него подпись перестаёт действовать после истечения сертификата.

## Подпись сертификатом на токене/HSM

```powershell
signtool sign /fd sha256 /tr http://timestamp.digicert.com /td sha256 `
  /sha1 <отпечаток сертификата> dist\TgwsOne-1.0.5.msi
```

Сертификат должен быть виден в хранилище `Cert:\CurrentUser\My` (или `LocalMachine\My`).

## Как это встроено в сборку

`build.ps1` умеет подписывать сам:

```powershell
# PFX
.\build.ps1 -Engine _upstream\TgWsProxy_windows.exe -ZapretBin _zapret\bin -Version 1.0.5.0 `
  -Pfx cert.pfx -PfxPassword "пароль"

# сертификат из хранилища/токена
.\build.ps1 ... -Thumbprint 0123456789ABCDEF...

# или через переменные окружения
$env:TGWS_SIGN_PFX = "cert.pfx"; $env:TGWS_SIGN_PFX_PASSWORD = "пароль"; .\build.ps1 ...
```

Порядок: компиляция exe → подпись exe → staging → сборка MSI → подпись MSI.

## Подпись в CI

В workflow уже есть поддержка: если в настройках репозитория заданы секреты
`SIGN_PFX_BASE64` (файл PFX в base64) и `SIGN_PFX_PASSWORD`, сборка подписывает exe и MSI
автоматически. Без секретов шаг просто пропускается.

```powershell
# подготовить секрет
[Convert]::ToBase64String([IO.File]::ReadAllBytes("cert.pfx")) | Set-Clipboard
```

Секреты: Settings → Secrets and variables → Actions → New repository secret.
Для HSM/облачных схем (Azure Trusted Signing, SignPath) шаг подписи заменяется на их action.

## Проверка

```powershell
Get-AuthenticodeSignature dist\TgwsOne-1.0.5.msi | Format-List Status, SignerCertificate
signtool verify /pa /v dist\TgwsOne-1.0.5.msi
```

`Status = Valid` — подпись корректна и с таймстампом.

## Самоподписанный сертификат (только для внутренней сети)

```powershell
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=TgwsOne" `
  -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(3)
$cert.Thumbprint
.\build.ps1 ... -Thumbprint $cert.Thumbprint
```

Чтобы Windows не показывал предупреждение, сертификат нужно распространить в
`Trusted Root Certification Authorities` и `Trusted Publishers` (GPO или вручную).
Для публикации в интернете это не подходит: у пользователей сертификата нет.
