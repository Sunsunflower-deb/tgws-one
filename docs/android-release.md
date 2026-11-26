# Выпуск Android-релиза

Документ для владельца: как собрать, подписать и опубликовать Android-релиз TgwsOne. Агент готовит всё локально,
но не делает `git push` и не создаёт настоящий GitHub Release — это делает владелец.

## 1. Сверить версию

Версия одна на весь проект — `<Version>` в `Directory.Build.props`. Из неё получаются `versionName` APK и имя
тега. `versionCode` задаётся отдельно в `src/TgwsOne.Android/TgwsOne.Android.csproj` и должен увеличиваться
при каждом релизе.

```sh
grep -n '<Version>' Directory.Build.props
grep -n 'ApplicationVersion' src/TgwsOne.Android/TgwsOne.Android.csproj
# для 2.3.0: <Version>2.3.0</Version>, <ApplicationVersion>2</ApplicationVersion>
```

Тег обязан совпадать с версией: `v2.3.0`. Если тег и `<Version>` расходятся, CI Linux/Windows-джобы падают
на проверке версии, а Android-артефакт будет с другим `versionName`.

`Makefile` читает ту же строку:

```sh
make -n dist-linux | grep tgwsone-2.3.0 || true
```

## 2. Подписать релизным ключом

Ключ создаётся один раз и хранится вне репозитория. Полная процедура — в
[`docs/android-signing.md`](android-signing.md); кратко:

```sh
export AndroidKeyStore=true
export AndroidSigningKeyStore="$HOME/.android-keys/tgwsone-release.jks"
export AndroidSigningStorePass='пароль-хранилища'
export AndroidSigningKeyAlias=tgwsone
export AndroidSigningKeyPass='пароль-ключа'
```

Отладочный ключ SDK (`-p:AndroidKeyStore=false`) годится только для проверки сборки: обновления, подписанные
разными ключами, поверх друг друга не ставятся.

## 3. Собрать APK

CI собирает по одному APK на ABI, чтобы файлы оставались небольшими. Те же команды для локального релиза
(уберите `-p:AndroidKeyStore=false`, если ключ владельца уже задан переменными из шага 2):

```sh
set -euo pipefail
export PATH="$HOME/.dotnet:$PATH"
export ANDROID_SDK_ROOT="$HOME/android-sdk" ANDROID_HOME="$HOME/android-sdk"
export JAVA_HOME=$(ls -d "$HOME"/jdk/jdk-17*)
export PATH="$JAVA_HOME/bin:$HOME/android-sdk/build-tools/36.0.0:$PATH"

version=$(sed -n 's|.*<Version>\(.*\)</Version>.*|\1|p' Directory.Build.props)
rm -rf out/publish/android dist/android
mkdir -p dist/android
for rid in android-arm64 android-arm android-x64; do
  case "$rid" in
    android-arm64) abi=arm64-v8a ;;
    android-arm)   abi=armeabi-v7a ;;
    android-x64)   abi=x86_64 ;;
  esac
  dotnet publish src/TgwsOne.Android -c Release -f net10.0-android \
    -r "$rid" -p:TargetFrameworks=net10.0-android -p:RuntimeIdentifiers="$rid" \
    -p:AndroidKeyStore=false -o "out/publish/android/$rid"
  cp "out/publish/android/$rid/io.github.sunsunflowerdeb.tgwsone-Signed.apk" \
    "dist/android/tgwsone-$version-android-$abi.apk"
done
(cd dist/android && sha256sum *.apk > SHA256SUMS)
cat dist/android/SHA256SUMS
```

Для локальной проверки без ключа владельца оставьте `-p:AndroidKeyStore=false`: APK будет подписан отладочным
ключом SDK, но собран в Release. Универсальный APK со всеми тремя ABI (для ручной установки) собирается так:

```sh
dotnet publish src/TgwsOne.Android -c Release -f net10.0-android \
  -p:AndroidKeyStore=false -o out/publish/android-release
```

## 4. Проверить перед публикацией

```sh
apk=dist/android/tgwsone-2.3.0-android-arm64-v8a.apk

# versionName = 2.3.0, versionCode = 2
aapt dump badging "$apk" | grep -E "^package:|versionCode|versionName"

# подпись действительно ваша (без -p:AndroidKeyStore=false)
apksigner verify --print-certs "$apk"

# разрешения: ничего VPN-связанного
aapt dump permissions "$apk"

# контрольные суммы сходятся
(cd dist/android && sha256sum -c SHA256SUMS)
```

Установите APK на устройство (`adb install -r "$apk"`), подключите Telegram по ссылке из приложения и убедитесь,
что сообщения приходят и уходят. В сведениях о приложении (Настройки Android → Приложения → TgwsOne) версия
должна быть 2.3.0, номер сборки — 2.

## 5. Создать GitHub Release

1. Убедитесь, что тег ровно `v2.3.0`, а рабочее дерево соответствует собранному APK.
2. Отправьте тег (`git push origin v2.3.0`) — это запускает CI. Android-джоба соберёт три APK и `SHA256SUMS`
   как артефакт, но в релиз они автоматически не попадают: release-джоба публикует только Linux и Windows.
3. Создайте релиз и приложите Android-файлы вручную:

```sh
gh release create v2.3.0 \
  dist/android/tgwsone-2.3.0-android-arm64-v8a.apk \
  dist/android/tgwsone-2.3.0-android-armeabi-v7a.apk \
  dist/android/tgwsone-2.3.0-android-x86_64.apk \
  dist/android/SHA256SUMS \
  --title "TgwsOne v2.3.0" \
  --notes-file release-notes.md
```

Если релиз уже создан CI, добавьте файлы к нему: `gh release upload v2.3.0 dist/android/* --clobber`.

Описание релиза возьмите из раздела `## [2.3.0]` в `CHANGELOG.md`: CI подставляет этот раздел в notes для
Linux/Windows, для Android добавьте фразу про APK и требования (Android 8.0+, без root, VpnService нет).

## 6. Что проверить после публикации

- Страница релиза содержит тег `v2.3.0`, три APK и `SHA256SUMS`; описание совпадает с `CHANGELOG.md`.
- Скачанный APK проходит `sha256sum -c SHA256SUMS`.
- `aapt dump badging` показывает `versionName='2.3.0'` и `versionCode='2'`.
- `apksigner verify --print-certs` показывает отпечаток релизного ключа, а не отладочного.
- APK ставится поверх прошлой версии (если она была подписана тем же ключом), настройки сохраняются.
- Приложение запускается, служба поднимает движок, Telegram подключается по ссылке и передаёт сообщения.
- В `aapt dump permissions` нет разрешений, связанных с VPN.
