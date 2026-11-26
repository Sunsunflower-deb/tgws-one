# Откуда этот движок

`TgwsOne.Engine` — построчный перенос на C# логики движка прокси из проекта
[Flowseal/tg-ws-proxy](https://github.com/Flowseal/tg-ws-proxy), лицензия MIT.

- Версия-источник: **v1.11.4**
- Коммит-источник: `2085861957070eeea07990a522b29f42e5230210`
- Файлы-источники: `proxy/tg_ws_proxy.py`, `proxy/bridge.py`, `proxy/raw_websocket.py`,
  `proxy/pool.py`, `proxy/cf_h2.py`, `proxy/h2_transport.py`, `proxy/fake_tls.py`,
  `proxy/config.py`, `proxy/balancer.py`, `proxy/utils.py`, `proxy/stats.py`.

Перенос сохраняет формат аргументов командной строки, INI-настройки приложения, схему
obfuscated2-рукопожатия, адреса дата-центров, список CF-доменов и порядок запасных путей
(WS-пул → CF Worker → CF proxy / HTTP/2 для медиа → прямой TCP).

Отличия намеренные:

- движок работает внутри процесса приложения (и как отдельный режим `--engine` того же
  исполняемого файла), а не как PyInstaller-сборка Python;
- HTTP/2 для медиа использует `System.Net.Http` вместо `httpx`/`h2`; сохранены лимиты,
  коды ошибок и правило «один MTProto-пакет — один запрос», но упрощена эвристика replay;
- Fake TLS (`ee`-секрет) перенесён из `proxy/fake_tls.py` целиком: проверка HMAC ClientHello и окно метки
  времени, сборка ServerHello + ChangeCipherSpec + ApplicationData с HMAC в поле random, зеркалирование
  гибридной группы x25519mlkem768, обмен внутри ApplicationData-записей. Не перенесён только замер размера
  первой записи маскировочного сайта (`probe_appdata_len`): размер берётся из диапазона 64–160 байт, как в
  upstream при неудачном замере.

Включение маскировки — настройка приложения (`faketls` в `[proxy]`, ключ `--fake-tls-domain` у движка): без
неё движок ведёт себя как раньше (`dd`-секрет), с ней ссылка становится `ee`-секретом с доменом в hex.

Оригинальный код: https://github.com/Flowseal/tg-ws-proxy. Текст лицензии MIT, под которой он
распространяется, — в `UPSTREAM-LICENSE` рядом с этим файлом.
