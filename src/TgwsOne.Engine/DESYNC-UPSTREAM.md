# Откуда приёмы обхода DPI

Слой обхода `TgwsOne.Engine/Desync/` реализует приёмы, впервые применённые в проекте
[ByeDPI](https://github.com/hufrea/byedpi) (hufrea, лицензия MIT), на Android и в .NET.
Реализация написана заново по описанию приёмов; код ByeDPI не копировался.

- Коммит-источник: `e268477`
- Раздел README ByeDPI, по которому переносилось: «Подробнее» о приёмах desync

## Что и где

| Приём | Как работает у ByeDPI | Где здесь |
|---|---|---|
| **disorder** | Первая часть отправляется с `IP_TTL=1` и гибнет на первом хопе; остальное уходит с системным TTL, сервер отвечает SACK, и ядро ретранслирует первую часть уже после второй — DPI видит куски в разлад | `DesyncModel` (`Disorder`), `DesyncPlanner` (сегмент `LowTtl`), `DesyncStream` (исполнение), `Interop/LinuxSocketTricks` (`SetTtl`) |
| **fake** | Фейковые байты попадают в сокет через `vmsplice(SPLICE_F_GIFT)` + `splice` с низким TTL, затем те же страницы переписываются оригиналом — ретрансмиссия несёт настоящие данные | `DesyncModel` (`Fake`), `DesyncStream` (ветка `FakeSplice`), `Interop/{PageAlignedBuffer,LinuxSocketTricks}`, `FakeClientHello` |
| **oob** | Байт в заданной позиции уходит как urgent data (`send` с `MSG_OOB`) | `DesyncModel` (`Oob`), `DesyncStream` (`Urgent`), `Interop/LinuxSocketTricks` (`TrySendUrgent`) |
| **tlsrec** | Одна TLS-запись перекраивается в несколько — DPI видит ClientHello по частям внутри валидных записей | `DesyncModel` (`TlsRec`), `DesyncPlanner` (перестройка байтов), `tls/record.rs` — то же в Rust-ядре (`native/tgws-core`) |

Планирование по разобранному ClientHello (позиции относительно SNI и SLD) — `ClientHelloLayout.cs`,
`DesyncPlanner.cs`; платформенные примитивы и их проверки — `Interop/`, тесты `tests/TgwsOne.Engine.Tests/Desync/`.

Оригинальный проект: <https://github.com/hufrea/byedpi>. Полный текст лицензии MIT — в
`DESYNC-UPSTREAM-LICENSE` рядом с этим файлом.
