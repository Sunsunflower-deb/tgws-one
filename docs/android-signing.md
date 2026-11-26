# Подпись Android-приложения

Release-APK для распространения подписывается **собственным ключом владельца**. Отладочный ключ SDK
(`AndroidKeyStore=false`) годится только для проверки сборки: его нельзя использовать для обновлений,
опубликованных пользователям.

Версия отображаемого имени приложения берётся из `<Version>` в `Directory.Build.props`
(`ApplicationDisplayVersion` в `src/TgwsOne.Android/TgwsOne.Android.csproj`). Номер сборки
(`ApplicationVersion`) задаётся там же отдельно и при необходимости увеличивается перед публикацией.

## 1. Создать хранилище ключей вне репозитория

Ключ создаётся один раз и хранится **вне рабочего каталога проекта**, например в
`~/.android-keys/`. В репозиторий не попадают ни хранилище, ни пароли.

```sh
mkdir -p "$HOME/.android-keys"
chmod 700 "$HOME/.android-keys"

keytool -genkeypair \
  -keystore "$HOME/.android-keys/tgwsone-release.jks" \
  -storetype PKCS12 \
  -alias tgwsone \
  -keyalg RSA -keysize 4096 -validity 10000 \
  -dname "CN=TgwsOne, OU=Android, O=TgwsOne, C=RU"
```

`keytool` спрашивает пароль хранилища и пароль ключа. Можно оставить их одинаковыми, но лучше
задать разные и записать в менеджер паролей. Файл ключа после создания:

```sh
chmod 600 "$HOME/.android-keys/tgwsone-release.jks"
```

## 2. Переменные окружения

Свойства .NET Android для подписи (имена проверены по `Xamarin.Android.Common.targets` из
установленного workload):

| Свойство MSBuild | Что это |
|---|---|
| `AndroidKeyStore` | `true` — подписывать Release собственным ключом; `false` — отладочным ключом SDK |
| `AndroidSigningKeyStore` | путь к файлу `.jks`/`.keystore` |
| `AndroidSigningStorePass` | пароль хранилища |
| `AndroidSigningKeyAlias` | псевдоним ключа |
| `AndroidSigningKeyPass` | пароль ключа |

MSBuild читает эти имена и из переменных окружения. Пример для текущей оболочки:

```sh
export AndroidKeyStore=true
export AndroidSigningKeyStore="$HOME/.android-keys/tgwsone-release.jks"
export AndroidSigningStorePass='пароль-хранилища'
export AndroidSigningKeyAlias=tgwsone
export AndroidSigningKeyPass='пароль-ключа'
```

Имена `AndroidKeyStoreFile`, `AndroidKeyStorePassword` и `AndroidKeyAlias` из старых черновиков
**не используются** — современный .NET Android берёт свойства из таблицы выше. Пароли не
записывайте в git, `Makefile` или CI-файлы; для CI используйте секреты репозитория.

## 3. Собрать подписанный Release

Универсальный APK со всеми тремя ABI (`arm64-v8a`, `armeabi-v7a`, `x86_64`):

```sh
dotnet publish src/TgwsOne.Android -c Release -f net10.0-android \
  -o out/publish/android-release
```

Для отдельных ABI, как в CI:

```sh
dotnet publish src/TgwsOne.Android -c Release -f net10.0-android \
  -r android-arm64 -p:TargetFrameworks=net10.0-android \
  -p:RuntimeIdentifiers=android-arm64 \
  -o out/publish/android-release/arm64-v8a
```

Готовый файл: `out/publish/android-release/io.github.sunsunflowerdeb.tgwsone-Signed.apk`.

Проверить, что подпись действительно ваша:

```sh
$ANDROID_HOME/build-tools/<версия>/apksigner verify --print-certs \
  out/publish/android-release/io.github.sunsunflowerdeb.tgwsone-Signed.apk
```

В выводе должны быть отпечатки вашего ключа и строка `Verified using v2 scheme`.

Для локальной проверки без ключа владельца (как в CI) задайте `-p:AndroidKeyStore=false`:
Release-APK будет подписан отладочным ключом SDK и не предназначен для выдачи пользователям.

## 4. Обращение с ключом

- **Ключ нельзя восстановить.** Потеря хранилища или пароля означает, что обновить уже
  установленное приложение тем же `applicationId` невозможно: пользователям придётся удалять
  старую версию, а в Google Play опубликовать обновление под новым ключом нельзя.
- Сделайте зашифрованную резервную копию `.jks` и паролей в двух независимых местах.
- Не коммитьте `.jks`, `.keystore`, `*.p12` и пароли. Каталог `~/.android-keys/` не должен быть
  внутри репозитория и не должен попадать в резервные копии проекта.
- Не пересылайте ключ в чатах и не храните пароль в истории команд: используйте менеджер
  паролей или `read -s`.
- Один и тот же ключ используется для всех будущих версий приложения. Смена ключа — только
  вместе с новым `applicationId` или через процедуру смены ключа в магазине приложений.
