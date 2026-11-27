# Архитектура android-порта

> Документ для сопровождающих (задача E7.3 плана `ANDROID-PORT-PLAN.md`): состав проектов, слой обхода и его правила, где что проверяется. Пользовательская документация — [android.md](android.md).

## Состав проектов

| Проект | TargetFramework | Ссылается на | Содержимое |
|---|---|---|---|
| `TgwsOne.Shared` | `net10.0;net10.0-android` | — | Переносимое: `IniFile`, `ProxySettings`, `PortProbe`, `TelegramLink`, `AppLog`, каталоги профиля |
| `TgwsOne.Engine` | `net10.0;net10.0-android` | Shared | Движок MTProto-over-WebSocket: `Transport/`, `Relay/` (`ProxyEngine`), `Crypto/`, `MtProto/`, `SelfCheck/` и слой обхода `Desync/` |
| `TgwsOne.Core` | `net10.0` | Shared, Engine | Только десктоп: запуск zapret2 (`Zapret/`), служба, `EngineCommand`/`EngineConfigFactory`, `InProcessEngineHost` |
| `TgwsOne` | `net10.0-windows…` | Core | Десктоп-приложение |
| `TgwsOne.Mobile` | `net10.0;net10.0-android` | Shared, Engine | Логика мобильного приложения без Android-API (хост, хранение); представления добавят E4.x/E5.2 |
| `TgwsOne.Android` | `net10.0-android` | Mobile, Engine, Shared | Тонкий клей: `MainActivity`, `MainApplication`, `ProxyService` (foreground, `specialUse`), `EngineRuntime`, манифест |
| `native/tgws-core` | Rust 2024, зависимости `libc`, `serde_json` | — | Ядро на Rust со своим TCP/TLS стеком (ниже) |

Принцип деления: вся логика живёт в сборках, тестируемых на десктопе (`TgwsOne.Mobile.Tests`, headless, как `TgwsOne.App.Tests`); `TgwsOne.Android` — только клей: один экземпляр движка на процесс (`EngineRuntime`), foreground service владеет его жизненным циклом. В `TgwsOne.slnx` Android-проект не входит, APK собирается отдельно (`make android`). zapret2 — десктопная зависимость и в APK не попадает: обход на Android делает собственный слой движка (`Desync/`).

### native/tgws-core (Rust-ядро)

Решение владельца вне рамок C#-плана: фундамент со своим TCP/TLS стеком под будущие QUIC/HTTP-3, ECH и отпечатки. В обходе C#-движка пока не участвует, C#-биндинга нет.

- `tls/parse.rs` — строгий разбор первого полёта: ClientHello без аллокаций, смещения SNI, `DesyncPosition`.
- `tls/client_hello.rs` — сборка правдоподобного TLS 1.3 ClientHello (отпечаток Chrome 120, GREASE, padding до точной длины), детерминированные RNG.
- `tls/record.rs` — каркас TLS-записей: `encode_record`, перефрагментация `split_record`. Без шифрования.
- `tcp/mod.rs` — блокирующий TCP поверх libc: IPv4/IPv6, `TCP_NODELAY`, TTL/hop limit, `MSG_OOB`.
- `strategy/`, `net/` — чистые юниты (без сокетов и файловых записей), перенесённые из `TgwsOne.Core.Zapret` через rust-anything: record эталона C#, diff вывода Rust, gate против расхождений.
- `ffi/` — C ABI движка (RT10a): `tgws_engine_start/stop/free`, JSON-статус/логи, ссылка `tg://`, callback журнала; крейт собирается `lib`+`cdylib`, контракт — `docs/rust-ffi.md`. C#-биндинг появится в RT10b.

## Слой обхода (`TgwsOne.Engine/Desync`)

Контракт (`DesyncModel.cs`): шаги-записи `Split`, `Disorder`, `Fake`, `Oob`, `TlsRec`, `Pause`; позиции `Absolute`, `SniStart`/`SniEnd`/`SniMid`/`SldMid`/`FromEnd` — дельты к разобранному ClientHello, планировщик сводит их к абсолютным смещениям; `DesyncStrategy(Id, Steps)`; `IDesyncController.Resolve/Report` (null = без обхода); `EgressFailure` — классификация исхода попытки.

Правила слоя (план, раздел 3.3):

1. Обход применяется только к первому ClientHello исходящего соединения и только если первые байты выглядят как запись рукопожатия (`0x16 0x03`) и hello разбирается строго. Всё остальное проходит без изменений.
2. Ошибка шага пишется в журнал с ограничением частоты (раз в 5 с), после чего байты уходят обычной записью. Соединение не рвать.
3. В поток не должно попасть ни одного лишнего байта: порция либо отправлена, либо нет.
4. Контроллер не задан или стратегия пуста — поведение ровно прежнее. Инвариант десктопа.
5. Стратегия меняется на лету, без перезапуска движка.
6. Позиции считаются по разобранному ClientHello; неразрешённый шаг (нет SNI, позиция вне буфера, разрез не по возрастанию) пропускается с явной причиной (`DesyncSkippedStep`), а не молча.

Платформенные примитивы (`ISocketTricks`): TTL/hop limit для IPv4/IPv6, fake через `vmsplice(SPLICE_F_GIFT)`+`splice`, срочный байт `MSG_OOB`, счётчик ретрансмиссий `getsockopt(TCP_INFO)` — только диагностика, не дата-путь. Буферы под vmsplice — `posix_memalign` (`PageAlignedBuffer`: в проекте нет `AllowUnsafeBlocks`). Реализации: `LinuxSocketTricks` для Linux и Android (одно ядро, bionic; вызовы в `SYSCALLS.TXT` и вне seccomp-блэклиста), `WindowsSocketTricks`, `UnsupportedSocketTricks`. Всё best-effort: Try-методы возвращают false вместо исключений.

Поток: `EgressSocket.ConnectAsync` (настройка сокета как у `RawWebSocket` — без Nagle, те же буферы; системный TTL читается один раз, чтобы шаг мог вернуть его после низкого) → `DesyncStream` → первый полёт разбирает `ClientHelloLayout`, `DesyncPlanner` превращает стратегию в `DesyncPlan` (сначала все разрезы `TlsRec` — они меняют раскладку байтов, потом остальные шаги; от платформенных `DesyncCapabilities` зависит, какие шаги вообще рассматриваются) → сегменты уходят в режимах `Normal`/`LowTtl`/`FakeSplice`/`Urgent`. Всё после первого полёта поток пропускает прозрачно. Отправитель `FakeSplice` — пока заглушка (сегмент уходит обычной записью, по правилу 2): путь через vmsplice/splice добавит E2.5.

Состояние: слой автономен, в транспорт ещё не встроен — подключение к `RawWebSocket`/`WebSocketPool` и маршруты (`ws`, `cf-worker`, …), классификация исходов и контроллер стратегий впереди (E2.7, E2.8, E3.x). Что именно переносилось из ByeDPI — `src/TgwsOne.Engine/DESYNC-UPSTREAM.md`.

## Где что проверяется

- **Сборка .NET**: `dotnet build TgwsOne.slnx` — `TreatWarningsAsErrors` включён, ноль предупреждений. `Shared`/`Engine`/`Mobile` собираются и под `net10.0-android`, поэтому slnx требует workload android. APK — отдельно: `make android` (`dotnet publish src/TgwsOne.Android -c Debug -f net10.0-android`); разрешения проверяются `aapt dump permissions`: `INTERNET`, `FOREGROUND_SERVICE`, `FOREGROUND_SERVICE_SPECIAL_USE`, `POST_NOTIFICATIONS` и обычное install-time `ACCESS_NETWORK_STATE` (нужно `ConnectivityManager.RegisterDefaultNetworkCallback` из E5.4). Ничего VPN-связанного.
- **Тесты**: `make test` — `Core.Tests`, `Engine.Tests`, `App.Tests`, `Mobile.Tests` в песочнице XDG (одноразовые каталоги профиля, `tools/leak-check.sh`, без DISPLAY и шины). Тесты слоя обхода — `tests/TgwsOne.Engine.Tests/Desync/`: разбор ClientHello, планировщик, поток и примитивы на loopback — локальный слушатель перехватывает сегменты, рукопожатие идёт через настоящий `SslStream`.
- **Root-тесты в netns**: `tests/root/netns-check.sh` — репетиция десктопного nfqws2+nftables в одноразных namespace, сеть хоста не трогается. Для слоя обхода план E2.6 предусматривает `netns-desync.sh`, `tests/DesyncProbe` и `pcap-check.py` (TTL, порядок сегментов, флаг URG в pcap) — в ветку ещё не добавлены.
- **Rust-ядро**: `cargo build && cargo test`, `cargo clippy --all-targets -- -D warnings`, `cargo fmt --check` (см. `native/README.md`).
- **CI** (`.github/workflows/build.yml`): сейчас jobs `linux` (`make build test`, `make test-engine`, `make dist-linux`), `windows` и `release`; job `android` (план E1.4) в ветку ещё не добавлен.

## Известные ограничения

- Работа ведётся без устройства: спайки S0.x не выполнялись, приёмка в сети оператора (E6) впереди. Android-часть проверена сборкой и десктопными тестами, не «в поле».
- Флейки этой машины: `PortProbe.IsBusy` (гонка за занятый порт) и `TgwsOne.App.Tests` при параллельном xunit.
- Слой обхода не встроен в транспорт (E2.7/E2.8), стратегии, «Авто» и наборы по оператору — E3; Rust-ядро в обходе пока не задействовано.
