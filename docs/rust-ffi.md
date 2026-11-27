# C ABI Rust-ядра (RT10a)

Документ описывает контрольный FFI-слой `native/tgws-core/src/ffi/`, через
который Android-оболочка (C#-сервис) будет управлять движком в RT10b. Ядро
собирается как `lib` и `cdylib`; на Android это `libtgws_core.so`.

Философия слоя:

- снаружи обмениваемся JSON-строками и обычными C-строками, без пакованных
  структур;
- владение движком идёт через непрозрачный указатель `EngineHandle`;
- ни одна функция не паникует через C-границу: тело каждой точки входа
  обёрнуто в `catch_unwind`;
- нулевые указатели и битый JSON возвращают код ошибки или `NULL`, без
  `unwrap` и `expect` в рабочем коде.

## Таблица функций

Все функции объявлены как `#[no_mangle] pub extern "C"`. Соглашение вызова —
C (на стороне C#: `CallingConvention.Cdecl`).

| Функция | Возврат | Назначение |
|---|---|---|
| `tgws_core_version()` | `const char*` | Статическая строка версии. Живёт всё время работы библиотеки, освобождать нельзя. |
| `tgws_engine_start(config_json, log_cb, user)` | `EngineHandle*` | Разбирает JSON-конфиг, биндит порт, запускает движок. При ошибке — `NULL` (и один вызов `log_cb` с уровнем `3`). |
| `tgws_engine_stop(handle)` | `int32` | Останавливает движок. Идемпотентна: повторный вызов и вызов после остановки безопасны. Handle остаётся валидным. |
| `tgws_engine_free(handle)` | `int32` | Освобождает handle, при необходимости сначала останавливая движок. Вызывать ровно один раз. `NULL` игнорируется. |
| `tgws_engine_status(handle)` | `char*` JSON | Состояние движка. `NULL` при `NULL`-handle. Освобождать через `tgws_string_free`. |
| `tgws_engine_link(handle)` | `char*` | Ссылка `tg://proxy?...` с фактически занятым портом. `NULL` при `NULL`-handle. Освобождать через `tgws_string_free`. |
| `tgws_engine_logs(handle, last_n)` | `char*` JSON | JSON-массив последних строк журнала. `last_n <= 0` — весь кольцевой буфер. `NULL` при `NULL`-handle. Освобождать через `tgws_string_free`. |
| `tgws_string_free(s)` | `void` | Освобождает строку, полученную из `status`/`link`/`logs`. `NULL` игнорируется. |

### Коды ошибок `int32`

| Код | Значение |
|---|---|
| `0` | Успех. |
| `-1` | Обязательный указатель равен `NULL`. |
| `-3` | Внутренний мьютекс отравлен (падение потока). |
| `-4` | Паника поймана на границе ABI. |

`tgws_engine_start` кода не возвращает: при любой ошибке (в том числе панике)
она возвращает `NULL`. Если передан `log_cb`, причина один раз уходит в него с
уровнем `3` (error).

## Callback журнала

```c
typedef void (*tgws_log_callback)(int32_t level,
                                  const char *msg,
                                  void *user);
```

- `level`: `0` — debug, `1` — info, `2` — warn, `3` — error.
- `msg`: NUL-терминированная UTF-8 строка, валидна только на время вызова.
- `user`: указатель, переданный в `tgws_engine_start`; ядро его не
  разыменовывает.
- Callback вызывается на вызывающем потоке во время `start`/`stop` и на рабочих
  потоках движка для событий соединений, поэтому должен быть потокобезопасным.
  Не следует синхронно вызывать из него функции этого же handle: лучше
  складывать строки в свою очередь.
- Callback не должен паниковать/бросать исключения: паника внутри
  `extern "C"`-функции не определена. Сам FFI-слой дополнительно обёрнут в
  `catch_unwind`, но это страховка, а не контракт.

Параллельно с callback каждая строка попадает в кольцо на 512 записей; его
отдаёт `tgws_engine_logs`. Строки в кольце имеют префикс уровня, например
`info: listening on 127.0.0.1:1443 ...`.

## JSON-конфигурация

`config_json` — UTF-8 JSON-объект. Обязателен только `secret`; остальные поля
имеют значения по умолчанию из `RelayConfig`.

| Поле | Тип | По умолчанию | Смысл |
|---|---|---|---|
| `host` | string | `"127.0.0.1"` | Адрес прослушивания. |
| `port` | 0..65535 | `1443` | Порт; `0` — эфемерный (фактический вернётся в статусе и ссылке). |
| `secret` | string | — | 32 hex-символа, допускаются префиксы `dd`/`ee`. |
| `poolidle` / `pool_idle` | u64 | `180` | `[android] poolidle`, секунды. |
| `routes` / `dc_redirects` | object | `{}` | Маршруты `{"2":"149.154.167.220"}`. |
| `fake_tls_domain` / `faketls` | string | `""` | Домен маскировки; пусто — обычный `dd`. |
| `domains` | string[] | — | Удобный алиас: если `fake_tls_domain` пуст, берётся первый элемент. |
| `ws_domain_suffix` | string | `"web.telegram.org"` | Суффикс хостов `kws<dc>`. |
| `ws_path` / `ws_path_test` | string | из ядра | Пути WebSocket. |
| `ws_port` | 0..65535 | `443` | Порт WebSocket-хостов. |
| `ws_secure` | bool | `true` | Оборачивать ли WebSocket в TLS 1.3. |
| `pool_size` | i32 >= 0 | `4` | `[proxy] poolsize`; `0` отключает WebSocket-маршрут. |
| `tcp_fallback_port` | 0..65535 | `443` | Порт прямого TCP-fallback. |
| `tcp_fallback_host` | string | — | Тестовый шов: подменить адрес fallback. |
| `read_timeout_ms` | u64 | `10000` | Таймаут чтения/подключения. |
| `buffer_size` | u64 | `262144` | Подсказка размера буфера. |
| `force_test_dc` | bool | `false` | Использовать тестовые DC. |
| `proxy_protocol` | bool | `false` | Ожидать строку PROXY перед handshake. |
| `log_level` | string | `"normal"` | `silent`, `warnings`, `normal`, `verbose`. |

Пример:

```json
{
  "host": "127.0.0.1",
  "port": 0,
  "secret": "0123456789abcdef0123456789abcdef",
  "poolidle": 180,
  "routes": { "2": "149.154.167.220" },
  "faketls": "www.example.com",
  "log_level": "normal"
}
```

## JSON статуса

```json
{
  "running": true,
  "port": 1443,
  "uptime_seconds": 12,
  "connections": 1,
  "connections_closed": 0,
  "sessions": 1,
  "errors": 0,
  "bad_handshakes": 0,
  "ws_sessions": 1,
  "tcp_fallbacks": 0,
  "masked": 0,
  "bytes_up": 128,
  "bytes_down": 256
}
```

`running` становится `false` после `tgws_engine_stop`, но счётчики последнего
снимка сохраняются до `tgws_engine_free`.

## Владение и потоки

1. `tgws_engine_start` создаёт `EngineHandle`; владелец — вызывающая сторона.
2. `tgws_engine_stop` только останавливает движок и не освобождает handle.
3. `tgws_engine_free` освобождает handle ровно один раз; после него указатель
   использовать нельзя.
4. Строки из `tgws_engine_status`/`link`/`logs` освобождаются
   `tgws_string_free` ровно один раз. Строку из `tgws_core_version` освобождать
   нельзя.
5. Handle потокобезопасен (внутри `Mutex`), но вызовы лучше сериализовать:
   `stop` дожидается завершения сессий до трёх секунд.
6. `tgws_engine_free` можно вызывать без предварительного `stop` — движок
   остановится при уничтожении handle.

## Небезопасные блоки

Весь `unsafe` сосредоточен в `src/ffi/mod.rs`:

- разыменование входных указателей после проверки на `NULL` и по контракту
  владения;
- `CString::from_raw` в `tgws_string_free`;
- `Box::from_raw` в `tgws_engine_free`;
- `unsafe impl Send/Sync` для `LogBuffer` (внутри `user` — непрозрачный токен,
  кольцо защищено `Mutex`).

## Пример C#-обёртки (эскиз для RT10b)

Ниже — иллюстративный текст, а не готовый код RT10b. Имена библиотеки и
маршалинг строк нужно будет уточнить под Android (`libtgws_core`).

```csharp
internal static class NativeEngine
{
    private const string Library = "tgws_core";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void LogCallback(int level, IntPtr message, IntPtr user);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr tgws_engine_start(
        IntPtr configJson, LogCallback? logCallback, IntPtr user);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int tgws_engine_stop(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int tgws_engine_free(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr tgws_engine_status(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr tgws_engine_link(IntPtr handle);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr tgws_engine_logs(IntPtr handle, int lastN);

    [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void tgws_string_free(IntPtr text);
}
```

Порядок работы обёртки:

1. Сериализовать конфиг в UTF-8, положить во временный неуправляемый буфер с
   завершающим NUL и вызвать `tgws_engine_start`, сохранив делегат
   `LogCallback` в поле — иначе GC соберёт его и вызов из engine-потока упадёт.
2. Читать возвращённые строки через `Marshal.PtrToStringUTF8`, сразу вызывать
   `tgws_string_free`, не удерживая указатель.
3. При остановке: `tgws_engine_stop`, затем `tgws_engine_free`. Повторный
   `stop` безопасен, повторный `free` — уже нет.
4. Не вызывать `status`/`link`/`logs` из самого callback: складывать сообщения
   в очередь и разбирать их на своём потоке.

## Проверка

```sh
cd native
cargo build
cargo test
cargo clippy --all-targets -- -D warnings
cargo fmt --check
cargo ndk -t arm64-v8a -- build --release
```

Тесты FFI (`src/ffi/mod.rs`, модуль `tests`) покрывают жизненный цикл
`start → status → link → stop → restart`, повторный `stop`, `NULL`-указатели,
доставку строк в callback и кольцо, ошибку конфигурации и перехват паники на
границе ABI.
