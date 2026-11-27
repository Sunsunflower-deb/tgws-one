# native — ядро TgwsOne на Rust

Решение владельца от 2026-11-26 (вне рамок исходного плана, раздел 12):
ядро переписывается на Rust **со своим TCP/TLS стеком**. Это нужно, чтобы
позже без смены фундамента добавить QUIC/HTTP-3, ECH и отпечатки в духе uTLS
для обхода DPI. Основной C#-план продолжается параллельно; этот каталог от него
не зависит.

Весь код написан с нуля по RFC 8446/5246/6066/8701/7685. Чужие реализации не
копировались. Лицензия — MIT, как у репозитория.

## Карта модулей

| Путь | Назначение |
|---|---|
| `tgws-core/src/tls/parse.rs` | Разбор первого полёта: ClientHello без аллокаций, смещения SNI и расширений, `DesyncPosition`. |
| `tgws-core/src/tls/client_hello.rs` | Сборка правдоподобного TLS 1.3 ClientHello с отпечатком Chrome 120, `build_padded`, собственный RNG. |
| `tgws-core/src/tls/record.rs` | Каркас TLS-записей: типы контента, `encode_record`, перефрагментация `split_record`. Без шифрования. |
| `tgws-core/src/tcp/mod.rs` | Свой блокирующий TCP поверх `libc`: IPv4/IPv6, `TCP_NODELAY`, TTL/hop limit, `MSG_OOB`. |
| `tgws-core/src/ffi/mod.rs` | C ABI движка (RT10a): `EngineHandle`, JSON-конфиг/статус/логи, callback журнала, `tgws_core_version`. Документ — `docs/rust-ffi.md`. |

## Что уже есть

- Синтаксический разбор ClientHello строго в первой записи (правило E2.1) и
  разрешение позиций для будущего desync.
- Сборка ClientHello с порядком расширений «по мотивам Chrome 120», GREASE,
  key share, ALPN `h2,http/1.1`, padding до точной длины (517/1024/4096).
- Детерминированные RNG (`XorShift64Star`, `SplitMix64`) для воспроизводимых
  тестов. Системный источник энтропии появится позже.
- Перефрагментация записи `0x16` без изменения plaintext.
- TCP-обёртка с TTL и OOB, тесты на loopback.

## Что дальше

1. Рекордный слой TLS 1.3: HKDF, AEAD, защита/расшифровка записей.
2. Полноценный TLS-клиент: key exchange, проверка сертификатов, ALPN.
3. Desync-примитивы на `vmsplice`/`splice` (своя реализация, не копия ByeDPI).
4. QUIC/HTTP-3, ECH, uTLS-подобные профили.
5. C ABI для создания egress-стрима со стратегией; C#-биндинг — отдельным шагом.

## Проверка

```sh
cd native
cargo build
cargo test
cargo clippy --all-targets -- -D warnings
cargo fmt --check
```
