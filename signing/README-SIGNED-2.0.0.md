# TgwsOne 2.0.0 — сборка с самоподписанным сертификатом

Тот же установщик, что в [v2.0.0](https://github.com/Sunsunflower-deb/tgws-one/releases/tag/v2.0.0), но `TgwsOne-2.0.0.msi`
подписан **самоподписанным** сертификатом `CN=TgwsOne Self-Signed Code Signing, O=TgwsOne`.
Версия MSI не менялась — 2.0.0.0.

Зачем: в домене сертификат добавляется в «Доверенные корневые» и «Доверенные издатели» через GPO — и установка идёт
без предупреждения «Неизвестный издатель», в том числе тихо.

## Что в релизе

| Файл | Назначение |
|---|---|
| `TgwsOne-2.0.0-signed.msi` | Установщик 2.0.0, подписанный сертификатом |
| `TgwsOne-2.0.0-signed.msi.sha256` | Контрольная сумма SHA-256 |
| `TgwsOne-CodeSigning.cer` | Публичный сертификат (DER) — для GPO и `certutil` |
| `TgwsOne-CodeSigning.crt` | Он же в PEM |
| `install-cert.cmd` | Добавляет сертификат на отдельном ПК в оба хранилища (двойной клик, UAC) |

Приватный ключ не публикуется. Подпись содержит таймстамп, поэтому остаётся действительной и после 2036 года.

## Проверка подписи

До добавления сертификата в доверенные `Status` будет `UnknownError` — это нормально, корень ещё не доверенный.
После добавления:

```powershell
Get-AuthenticodeSignature .\TgwsOne-2.0.0-signed.msi | Format-List Status, SignerCertificate
# Status: Valid
```

## Отдельный компьютер (без домена)

Самый простой путь — `install-cert.cmd` рядом с `TgwsOne-CodeSigning.cer`: двойной клик, подтвердить UAC,
скрипт добавит сертификат в «Доверенные корневые центры» и «Доверенные издатели» для всех пользователей.

Вручную: двойной клик по `.cer` → «Установить сертификат…» → «Поместить все сертификаты в следующее хранилище»
(не «автоматически»!) → «Доверенные корневые центры сертификации» → готово; повторить для «Доверенных издателей».

Или из командной строки от администратора:

```cmd
certutil -addstore -f "Root" TgwsOne-CodeSigning.cer
certutil -addstore -f "TrustedPublisher" TgwsOne-CodeSigning.cer
```

PowerShell (от администратора):

```powershell
Import-Certificate -FilePath .\TgwsOne-CodeSigning.cer -CertStoreLocation Cert:\LocalMachine\Root
Import-Certificate -FilePath .\TgwsOne-CodeSigning.cer -CertStoreLocation Cert:\LocalMachine\TrustedPublisher
```

Проверка: `certmgr.msc` → «Доверенные корневые центры сертификации» → в списке `TgwsOne Self-Signed Code Signing`.

Удалить:

```cmd
certutil -delstore "Root" 94A82AB20B72495E5AADFFBDD066483CF67694E8
certutil -delstore "TrustedPublisher" 94A82AB20B72495E5AADFFBDD066483CF67694E8
```

## Через домен (GPO)

1. Положите `TgwsOne-CodeSigning.cer` на шару, доступную компьютерам.
2. `Computer Configuration → Policies → Windows Settings → Security Settings → Public Key Policies`:
   **Trusted Root Certification Authorities** → Import → `.cer`; **Trusted Publishers** → Import → тот же `.cer`.
3. `gpupdate /force` на тестовой машине.

Иногда удобнее startup-скрипт в GPO с двумя `certutil -addstore` — результат тот же.

## Установка

1. Добавьте сертификат (GPO, скрипт или вручную).
2. Запустите `TgwsOne-2.0.0-signed.msi` — обновит 1.x, поставит службу `TgwsOneZapret` и правило брандмауэра.
3. Обычный пользователь запускает ярлык и проходит мастер — без UAC.

Тихая установка: `msiexec /i TgwsOne-2.0.0-signed.msi /qn`.
Удаление: «Параметры → Приложения» или `msiexec /x TgwsOne-2.0.0-signed.msi /qn`.

## Что важно помнить

- Сертификат самоподписанный: доверяйте ему **только в своей организации**. Подписанное им — это «сделал владелец
  ключа», без подтверждения личности издателя.
- Подпись MSI не убирает детекты антивируса на драйвер WinDivert (часть zapret2) — при необходимости добавьте
  `C:\Program Files\TgwsOne` в исключения.
- Установщик 2.0.0 не выгружает драйвер WinDivert при удалении: он общий с другими программами обхода и остаётся
  в памяти до перезагрузки. Это поведение самого 2.0.0, не подписи.
