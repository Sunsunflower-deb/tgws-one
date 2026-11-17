# TgwsOne 1.0.5 — сборка с самоподписанным сертификатом

Тот же код, что в v1.0.5, но `TgwsOne.exe` и MSI подписаны **самоподписанным** сертификатом
`CN=TgwsOne Self-Signed Code Signing, O=TgwsOne`. Версия MSI — **1.0.5.1** (ставится поверх 1.0.5).

Зачем: в домене можно добавить сертификат в «Доверенные корневые» и «Доверенные издатели» через GPO —
и тогда пользователи не увидят ни «Неизвестный издатель» от SmartScreen, ни предупреждения UAC.

## Что в релизе

| Файл | Назначение |
|---|---|
| `TgwsOne-1.0.5-selfsigned.msi` | Установщик, подписанный сертификатом |
| `TgwsOne-1.0.5-selfsigned.msi.sha256` | Контрольная сумма SHA-256 |
| `TgwsOne-CodeSigning.cer` | Публичный сертификат (DER) — для GPO и `certutil` |
| `TgwsOne-CodeSigning.crt` | Тот же сертификат в PEM (base64) |

Приватный ключ **не публикуется**. Следующие релизы будут подписаны этим же сертификатом, поэтому
достаточно один раз добавить его в доверенные.

## Проверка подписи

До добавления сертификата в доверенные:

```powershell
Get-AuthenticodeSignature .\TgwsOne-1.0.5-selfsigned.msi | Format-List Status, SignerCertificate
# Status: UnknownError — это нормально: корень сертификата ещё не доверенный
```

После добавления:

```powershell
Get-AuthenticodeSignature .\TgwsOne-1.0.5-selfsigned.msi | Format-List Status, SignerCertificate
# Status: Valid
```

## Отдельный компьютер (без домена)

Сертификат нужно положить в **два** хранилища: «Доверенные корневые центры сертификации»
(чтобы подпись проверялась) и «Доверенные издатели» (чтобы не было предупреждений издателя).

### Вариант 1: двойной клик по `install-cert.cmd`

Скрипт лежит в этом же релизе. Положите его рядом с `TgwsOne-CodeSigning.cer`, запустите —
он попросит UAC и добавит сертификат сразу в оба хранилища для всех пользователей ПК.

### Вариант 2: мастер установки сертификата

1. Двойной клик по `TgwsOne-CodeSigning.cer` → кнопка **«Установить сертификат…»**.
2. **«Далее»** → выберите **«Поместить все сертификаты в следующее хранилище»**
   (не «автоматически»!) → **«Обзор…»** → **«Доверенные корневые центры сертификации»**
   → ОК → «Далее» → «Готово».
3. На предупреждение системы безопасности — **«Да»**.
4. Повторите шаги 1–3, но на шаге 2 выберите **«Доверенные издатели»**.

Для всех учётных записей ПК мастер должен запускаться с правами администратора
и хранилище выбирается в разделе «Локальный компьютер».

### Вариант 3: командная строка (от администратора)

```cmd
certutil -addstore -f "Root" TgwsOne-CodeSigning.cer
certutil -addstore -f "TrustedPublisher" TgwsOne-CodeSigning.cer
```

`certutil` без ключа `-user` пишет в хранилище компьютера; с `-user` — только текущего пользователя.

### Вариант 4: PowerShell (от администратора)

```powershell
Import-Certificate -FilePath .\TgwsOne-CodeSigning.cer -CertStoreLocation Cert:\LocalMachine\Root
Import-Certificate -FilePath .\TgwsOne-CodeSigning.cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher
```

### Проверка

```powershell
Get-AuthenticodeSignature .\TgwsOne-1.0.5-selfsigned.msi | Format-List Status, SignerCertificate
# Status: Valid
```

Или посмотреть хранилище: `certmgr.msc` → «Доверенные корневые центры сертификации» →
в списке должен быть `TgwsOne Self-Signed Code Signing`.

### Удалить сертификат

```cmd
certutil -delstore "Root" 94A82AB20B72495E5AADFFBDD066483CF67694E8
certutil -delstore "TrustedPublisher" 94A82AB20B72495E5AADFFBDD066483CF67694E8
```

## Добавить через домен (GPO)

1. Положите `TgwsOne-CodeSigning.cer` на сетевую шару, доступную компьютерам.
2. В `Group Policy Management` создайте/откройте политику, привязанную к OU с компьютерами.
3. `Computer Configuration → Policies → Windows Settings → Security Settings → Public Key Policies`:
   - **Trusted Root Certification Authorities** → правый клик → **Import** → выберите `.cer`;
   - **Trusted Publishers** → правый клик → **Import** → тот же `.cer`.
4. `gpupdate /force` на тестовой машине (или дождитесь обновления политики).

Альтернатива без правки GPO: startup-скрипт в GPO с теми же двумя командами `certutil`.

Для Intune/SCCM — платформенный скрипт (PowerShell) с `certutil -addstore`, запуск от SYSTEM.

## После установки сертификата

- MSI запускается без предупреждения «Неизвестный издатель», в том числе в тихом режиме:
  `msiexec /i TgwsOne-1.0.5-selfsigned.msi /qn`
- UAC-запрос при установке остаётся (это установка в `Program Files`), но без «неизвестного издателя».
- SmartScreen для интернет-загрузок всё равно может предупредить — репутация в Microsoft строится
  только для сертификатов, выданных публичным CA.

## Важно

- Сертификат **самоподписанный**: он подтверждает только то, что файл подписан его владельцем, а не
  личность издателя. Доверяйте ему **только в своей организации**.
- Не добавляйте сертификат в доверенные на чужих компьютерах «на всякий случай».
- Приватный ключ хранится у автора сборки; при его утрате потребуется новый сертификат и повторное
  добавление в доверенные.
- Подпись не убирает детекты антивирусов на `WinDivert64.sys` — это отдельная история, см. README
  репозитория.

## Установка

1. Добавьте сертификат (GPO или `certutil`).
2. Запустите `TgwsOne-1.0.5-selfsigned.msi`, подтвердите UAC один раз.
3. Приложение запускается ярлыком; обход DPI ставится задачей `TgwsOne-Zapret` (от SYSTEM).

Удаление — «Параметры → Приложения» или `msiexec /x TgwsOne-1.0.5-selfsigned.msi /qn`.
