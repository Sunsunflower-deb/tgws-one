# Сертификат подписи

`TgwsOne-CodeSigning.cer` (DER) и `TgwsOne-CodeSigning.crt` (PEM) — публичная часть
самоподписанного сертификата, которым подписан релиз
[selfsigned-1.0.5](../../releases/tag/selfsigned-1.0.5).

- Subject: `CN=TgwsOne Self-Signed Code Signing, O=TgwsOne`
- EKU: Code Signing, RSA 3072, SHA-256
- Действует до 2036 года, подпись с таймстампом DigiCert

## Зачем

Чтобы в домене добавить сертификат в «Доверенные корневые» и «Доверенные издатели» через GPO —
тогда MSI и exe запускаются без предупреждения «Неизвестный издатель».

```cmd
certutil -addstore -f "Root" TgwsOne-CodeSigning.cer
certutil -addstore -f "TrustedPublisher" TgwsOne-CodeSigning.cer
```

Подробно (включая GPO) — в `README-SIGNED.md` релиза.

## Приватный ключ

`TgwsOne-CodeSigning.pfx` и пароль **в репозиторий не попадают** (см. `.gitignore`). Они хранятся
только у владельца сборки. Без них подписать следующий релиз тем же сертификатом нельзя — придётся
выпускать новый сертификат и заново добавлять его в доверенные.

## Подписать релиз этим сертификатом

```powershell
.\build.ps1 -AppDir out\publish\win-x64 -Engine out\engine\win-x64\tgws-engine.exe -Version 2.0.0.0 `
  -Out dist\TgwsOne-2.0.0-selfsigned.msi `
  -Pfx signing\TgwsOne-CodeSigning.pfx -PfxPassword "<пароль>"
```

Проверка:

```powershell
Get-AuthenticodeSignature dist\TgwsOne-2.0.0-selfsigned.msi | Format-List Status, SignerCertificate
```
