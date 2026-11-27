//! C ABI surface for the native core (RT10a).
//!
//! The crate is built as both `lib` and `cdylib`, so the Android shell can load
//! it without a Rust toolchain.  The contract is deliberately small:
//!
//! * the engine is created by [`tgws_engine_start`] and owned through an opaque
//!   [`EngineHandle`] pointer;
//! * configuration goes in as a JSON string and status/logs/link come out as
//!   JSON strings or plain strings owned by the caller;
//! * every string is released with [`tgws_string_free`];
//! * every entry point contains its body in `catch_unwind`, so a panic never
//!   crosses the C boundary.
//!
//! The exact wire format and ownership rules are documented in
//! `docs/rust-ffi.md`.

#![allow(clippy::not_unsafe_ptr_arg_deref)]

use std::collections::VecDeque;
use std::ffi::{CStr, CString, c_char, c_void};
use std::panic::{self, AssertUnwindSafe};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use serde_json::{Value, json};

use crate::relay::config::{LogLevel, RelayConfig, decode_secret};
use crate::relay::engine::{Engine, EngineStatsSnapshot, LogSeverity, LogSink};
use crate::settings::{IpAddress, build_ee, connect_address};

const VERSION: &str = concat!(env!("CARGO_PKG_VERSION"), "\0");

/// Success code returned by the `i32` entry points.
const ERR_OK: i32 = 0;
/// A required pointer argument was NULL.
const ERR_NULL: i32 = -1;
/// A lock inside the handle was poisoned.
const ERR_INTERNAL: i32 = -3;
/// A panic was caught at the ABI boundary.
const ERR_PANIC: i32 = -4;

/// How many event lines the in-memory ring keeps.
const MAX_LOG_LINES: usize = 512;

/// Severity numbers passed to the host log callback.
const LOG_DEBUG: i32 = 0;
const LOG_INFO: i32 = 1;
const LOG_WARN: i32 = 2;
const LOG_ERROR: i32 = 3;

/// Host callback invoked for every relay event line.
///
/// `level` is `0` (debug), `1` (info), `2` (warn) or `3` (error).  `msg` is a
/// NUL-terminated UTF-8 string valid only for the duration of the call.  `user`
/// is the pointer passed to [`tgws_engine_start`].  The callback runs on engine
/// threads and must be thread-safe; it must not call back into the same handle
/// synchronously.
pub type TgwsLogCallback =
    Option<unsafe extern "C" fn(level: i32, msg: *const c_char, user: *mut c_void)>;

/// Ring buffer plus host callback shared by the engine threads and the handle.
struct LogBuffer {
    lines: Mutex<VecDeque<String>>,
    callback: TgwsLogCallback,
    user: *mut c_void,
}

// SAFETY: `user` is treated as an opaque token and only ever handed back to the
// callback; the callback pointer is immutable.  The ring is protected by a
// `Mutex`, so concurrent engine threads can log safely.
unsafe impl Send for LogBuffer {}
// SAFETY: same reasoning as the `Send` impl.
unsafe impl Sync for LogBuffer {}

impl LogBuffer {
    fn new(callback: TgwsLogCallback, user: *mut c_void) -> Self {
        Self {
            lines: Mutex::new(VecDeque::with_capacity(MAX_LOG_LINES)),
            callback,
            user,
        }
    }
}

impl LogSink for LogBuffer {
    fn log(&self, severity: LogSeverity, message: &str) {
        if let Ok(mut lines) = self.lines.lock() {
            if lines.len() == MAX_LOG_LINES {
                lines.pop_front();
            }
            lines.push_back(format!("{}: {message}", severity_label(severity)));
        }

        let Some(callback) = self.callback else {
            return;
        };
        let text = match CString::new(message.replace('\0', " ")) {
            Ok(text) => text,
            Err(_) => return,
        };
        // SAFETY: the host supplied this function pointer and user token in
        // `tgws_engine_start` and promised they stay valid until the handle is
        // freed; `text` outlives the call.
        unsafe {
            callback(severity_code(severity), text.as_ptr(), self.user);
        }
    }
}

const fn severity_code(severity: LogSeverity) -> i32 {
    match severity {
        LogSeverity::Debug => LOG_DEBUG,
        LogSeverity::Info => LOG_INFO,
        LogSeverity::Warn => LOG_WARN,
    }
}

const fn severity_label(severity: LogSeverity) -> &'static str {
    match severity {
        LogSeverity::Debug => "debug",
        LogSeverity::Info => "info",
        LogSeverity::Warn => "warn",
    }
}

struct HandleState {
    engine: Option<Engine>,
    link: String,
    port: u16,
    last_stats: EngineStatsSnapshot,
}

/// Opaque owner of a running relay.
///
/// The pointer returned by [`tgws_engine_start`] must be released exactly once
/// with [`tgws_engine_free`].  [`tgws_engine_stop`] stops the engine but keeps
/// the handle valid for further status/log reads.
pub struct EngineHandle {
    state: Mutex<HandleState>,
    logs: Arc<LogBuffer>,
    started: Instant,
}

/// Returns a NUL-terminated static version string.
///
/// The pointer is valid for the lifetime of the library and must not be freed.
#[unsafe(no_mangle)]
pub extern "C" fn tgws_core_version() -> *const c_char {
    VERSION.as_ptr().cast()
}

/// Parses `config_json`, binds the relay and returns a new handle.
///
/// Returns NULL on a NULL/invalid configuration or a bind failure.  In that
/// case, when `log_cb` is not NULL, it is called once with level `3` and the
/// error text.
#[unsafe(no_mangle)]
pub extern "C" fn tgws_engine_start(
    config_json: *const c_char,
    log_cb: TgwsLogCallback,
    user: *mut c_void,
) -> *mut EngineHandle {
    match panic::catch_unwind(AssertUnwindSafe(|| start_impl(config_json, log_cb, user))) {
        Ok(Ok(handle)) => Box::into_raw(Box::new(handle)),
        Ok(Err(message)) => {
            notify(log_cb, user, LOG_ERROR, &message);
            std::ptr::null_mut()
        }
        Err(_) => {
            notify(log_cb, user, LOG_ERROR, "паника при запуске движка");
            std::ptr::null_mut()
        }
    }
}

/// Stops a running engine.  Calling it again, or on an already stopped handle,
/// is safe and returns [`ERR_OK`].  The handle itself stays valid until
/// [`tgws_engine_free`].
#[unsafe(no_mangle)]
pub extern "C" fn tgws_engine_stop(handle: *mut EngineHandle) -> i32 {
    guarded(ERR_PANIC, || {
        if handle.is_null() {
            return ERR_NULL;
        }
        // SAFETY: a non-NULL handle came from `tgws_engine_start` and is valid
        // until the caller invokes `tgws_engine_free`.
        let handle = unsafe { &*handle };

        let engine = match handle.state.lock() {
            Ok(mut state) => state.engine.take(),
            Err(_) => return ERR_INTERNAL,
        };
        if let Some(mut engine) = engine {
            engine.stop();
            let stats = engine.stats();
            if let Ok(mut state) = handle.state.lock() {
                state.last_stats = stats;
            }
        }
        ERR_OK
    })
}

/// Releases a handle and stops the engine if it is still running.
///
/// The pointer must not be used afterwards.  A NULL pointer is ignored.
#[unsafe(no_mangle)]
pub extern "C" fn tgws_engine_free(handle: *mut EngineHandle) -> i32 {
    guarded(ERR_PANIC, || {
        if handle.is_null() {
            return ERR_OK;
        }
        // SAFETY: the caller transfers ownership exactly once, as documented.
        unsafe {
            drop(Box::from_raw(handle));
        }
        ERR_OK
    })
}

/// Returns a JSON object describing the engine, or NULL for a NULL handle.
///
/// Fields: `running`, `port`, `uptime_seconds`, `connections`,
/// `connections_closed`, `sessions`, `errors`, `bad_handshakes`, `ws_sessions`,
/// `tcp_fallbacks`, `masked`, `bytes_up`, `bytes_down`.
#[unsafe(no_mangle)]
pub extern "C" fn tgws_engine_status(handle: *mut EngineHandle) -> *mut c_char {
    guarded(std::ptr::null_mut(), || {
        if handle.is_null() {
            return std::ptr::null_mut();
        }
        // SAFETY: see `tgws_engine_stop`.
        let handle = unsafe { &*handle };
        let (running, port, stats) = match handle.state.lock() {
            Ok(state) => match &state.engine {
                Some(engine) => (true, state.port, engine.stats()),
                None => (false, state.port, state.last_stats),
            },
            Err(_) => return std::ptr::null_mut(),
        };
        let payload = json!({
            "running": running,
            "port": port,
            "uptime_seconds": handle.started.elapsed().as_secs(),
            "connections": stats.connections_opened,
            "connections_closed": stats.connections_closed,
            "sessions": stats.sessions,
            "errors": stats.errors,
            "bad_handshakes": stats.bad_handshakes,
            "ws_sessions": stats.ws_sessions,
            "tcp_fallbacks": stats.tcp_fallbacks,
            "masked": stats.masked,
            "bytes_up": stats.bytes_up,
            "bytes_down": stats.bytes_down,
        });
        to_c_string(payload.to_string())
    })
}

/// Returns the `tg://proxy?...` link for the handle, or NULL for a NULL handle.
///
/// The link uses the port the listener actually bound, so a configuration with
/// port `0` still produces a usable link.
#[unsafe(no_mangle)]
pub extern "C" fn tgws_engine_link(handle: *mut EngineHandle) -> *mut c_char {
    guarded(std::ptr::null_mut(), || {
        if handle.is_null() {
            return std::ptr::null_mut();
        }
        // SAFETY: see `tgws_engine_stop`.
        let handle = unsafe { &*handle };
        let link = match handle.state.lock() {
            Ok(state) => state.link.clone(),
            Err(_) => return std::ptr::null_mut(),
        };
        to_c_string(link)
    })
}

/// Returns a JSON array with the last `last_n` event lines, or NULL for a NULL
/// handle.
///
/// A non-positive `last_n` returns the whole ring.
#[unsafe(no_mangle)]
pub extern "C" fn tgws_engine_logs(handle: *mut EngineHandle, last_n: i32) -> *mut c_char {
    guarded(std::ptr::null_mut(), || {
        if handle.is_null() {
            return std::ptr::null_mut();
        }
        // SAFETY: see `tgws_engine_stop`.
        let handle = unsafe { &*handle };
        let lines: Vec<String> = match handle.logs.lines.lock() {
            Ok(lines) => {
                let wanted = if last_n <= 0 {
                    lines.len()
                } else {
                    usize::try_from(last_n).map_or(0, |n| n.min(lines.len()))
                };
                lines.iter().skip(lines.len() - wanted).cloned().collect()
            }
            Err(_) => return std::ptr::null_mut(),
        };
        to_c_string(json!(lines).to_string())
    })
}

/// Frees a string returned by this library.  A NULL pointer is ignored.
#[unsafe(no_mangle)]
pub extern "C" fn tgws_string_free(s: *mut c_char) {
    let _ = panic::catch_unwind(AssertUnwindSafe(|| {
        if s.is_null() {
            return;
        }
        // SAFETY: the pointer came from `CString::into_raw` in `to_c_string`.
        unsafe {
            drop(CString::from_raw(s));
        }
    }));
}

fn guarded<T>(fallback: T, body: impl FnOnce() -> T) -> T {
    match panic::catch_unwind(AssertUnwindSafe(body)) {
        Ok(value) => value,
        Err(_) => fallback,
    }
}

fn notify(callback: TgwsLogCallback, user: *mut c_void, level: i32, message: &str) {
    let Some(callback) = callback else {
        return;
    };
    let Ok(text) = CString::new(message.replace('\0', " ")) else {
        return;
    };
    let _ = panic::catch_unwind(AssertUnwindSafe(|| {
        // SAFETY: the same callback contract as in `LogBuffer::log`; this call
        // only happens before a handle exists, with the host's own token.
        unsafe {
            callback(level, text.as_ptr(), user);
        }
    }));
}

fn start_impl(
    config_json: *const c_char,
    log_cb: TgwsLogCallback,
    user: *mut c_void,
) -> Result<EngineHandle, String> {
    if config_json.is_null() {
        return Err("config_json == NULL".to_string());
    }
    // SAFETY: the caller promised a NUL-terminated string readable for the
    // duration of this call.
    let text = unsafe { CStr::from_ptr(config_json) }
        .to_str()
        .map_err(|error| format!("config_json не UTF-8: {error}"))?;
    let config = parse_config(text)?;

    let logs = Arc::new(LogBuffer::new(log_cb, user));
    let sink: Arc<dyn LogSink> = logs.clone();
    let engine = Engine::start_with_sink(config, Some(sink))
        .map_err(|error| format!("не удалось запустить движок: {error}"))?;

    let port = engine.port();
    let link = build_link(engine.config(), port);
    Ok(EngineHandle {
        state: Mutex::new(HandleState {
            engine: Some(engine),
            link,
            port,
            last_stats: EngineStatsSnapshot::default(),
        }),
        logs,
        started: Instant::now(),
    })
}

fn parse_config(text: &str) -> Result<RelayConfig, String> {
    let value: Value = serde_json::from_str(text)
        .map_err(|error| format!("некорректный JSON конфигурации: {error}"))?;
    let object = value
        .as_object()
        .ok_or_else(|| "конфигурация должна быть JSON-объектом".to_string())?;

    let mut config = RelayConfig::default();

    if let Some(host) = string_field(object, "host")? {
        config.host = host;
    }
    if let Some(port) = u16_field(object, "port")? {
        config.port = port;
    }
    let secret = object
        .get("secret")
        .and_then(Value::as_str)
        .ok_or_else(|| "secret обязателен: строка из 32 hex-символов".to_string())?;
    config.secret = decode_secret(secret).map_err(|error| format!("secret: {error}"))?;

    if let Some(idle) = u64_field(object, "poolidle")?.or(u64_field(object, "pool_idle")?) {
        config.pool_idle_seconds = idle;
    }
    if let Some(domain) =
        string_field(object, "fake_tls_domain")?.or(string_field(object, "faketls")?)
    {
        config.fake_tls_domain = domain.trim().to_string();
    }
    if let Some(domains) = object.get("domains") {
        let list = domains
            .as_array()
            .ok_or_else(|| "domains должен быть массивом строк".to_string())?;
        if config.fake_tls_domain.is_empty() {
            if let Some(first) = list.first() {
                let domain = first
                    .as_str()
                    .ok_or_else(|| "domains должен быть массивом строк".to_string())?;
                config.fake_tls_domain = domain.trim().to_string();
            }
        }
    }

    let routes = object.get("routes").or_else(|| object.get("dc_redirects"));
    if let Some(routes) = routes {
        let routes = routes
            .as_object()
            .ok_or_else(|| "routes должен быть объектом вида {\"2\":\"1.2.3.4\"}".to_string())?;
        config.dc_redirects.clear();
        for (dc, ip) in routes {
            let dc_number: i32 = dc
                .parse()
                .map_err(|_| format!("routes: ключ {dc:?} не номер DC"))?;
            let ip = ip
                .as_str()
                .ok_or_else(|| format!("routes[{dc_number}]: адрес должен быть строкой"))?;
            config.dc_redirects.insert(dc_number, ip.to_string());
        }
    }

    if let Some(value) = string_field(object, "ws_domain_suffix")? {
        config.ws_domain_suffix = value;
    }
    if let Some(value) = string_field(object, "ws_path")? {
        config.ws_path = value;
    }
    if let Some(value) = string_field(object, "ws_path_test")? {
        config.ws_path_test = value;
    }
    if let Some(value) = u16_field(object, "ws_port")? {
        config.ws_port = value;
    }
    if let Some(value) = bool_field(object, "ws_secure")? {
        config.ws_secure = value;
    }
    if let Some(value) = i32_field(object, "pool_size")? {
        if value < 0 {
            return Err("pool_size не может быть отрицательным".to_string());
        }
        config.pool_size = value;
    }
    if let Some(value) = u16_field(object, "tcp_fallback_port")? {
        config.tcp_fallback_port = value;
    }
    if let Some(value) = string_field(object, "tcp_fallback_host")? {
        config.tcp_fallback_host_override = Some(value);
    }
    if let Some(value) = u64_field(object, "read_timeout_ms")? {
        config.read_timeout = Duration::from_millis(value);
    }
    if let Some(value) = u64_field(object, "buffer_size")? {
        config.buffer_size =
            usize::try_from(value).map_err(|_| "buffer_size слишком велик".to_string())?;
    }
    if let Some(value) = bool_field(object, "force_test_dc")? {
        config.force_test_dc = value;
    }
    if let Some(value) = bool_field(object, "proxy_protocol")? {
        config.proxy_protocol = value;
    }
    if let Some(value) = string_field(object, "log_level")? {
        config.log_level = parse_log_level(&value)?;
    }

    Ok(config)
}

fn string_field(
    object: &serde_json::Map<String, Value>,
    key: &str,
) -> Result<Option<String>, String> {
    match object.get(key) {
        None => Ok(None),
        Some(value) => value
            .as_str()
            .map(|text| Some(text.to_string()))
            .ok_or_else(|| format!("{key} должен быть строкой")),
    }
}

fn bool_field(object: &serde_json::Map<String, Value>, key: &str) -> Result<Option<bool>, String> {
    match object.get(key) {
        None => Ok(None),
        Some(value) => value
            .as_bool()
            .map(Some)
            .ok_or_else(|| format!("{key} должен быть true или false")),
    }
}

fn u64_field(object: &serde_json::Map<String, Value>, key: &str) -> Result<Option<u64>, String> {
    match object.get(key) {
        None => Ok(None),
        Some(value) => value
            .as_u64()
            .map(Some)
            .ok_or_else(|| format!("{key} должен быть неотрицательным целым")),
    }
}

fn u16_field(object: &serde_json::Map<String, Value>, key: &str) -> Result<Option<u16>, String> {
    let Some(value) = u64_field(object, key)? else {
        return Ok(None);
    };
    u16::try_from(value)
        .map(Some)
        .map_err(|_| format!("{key} {value} вне диапазона u16"))
}

fn i32_field(object: &serde_json::Map<String, Value>, key: &str) -> Result<Option<i32>, String> {
    let Some(value) = object.get(key) else {
        return Ok(None);
    };
    value
        .as_i64()
        .and_then(|number| i32::try_from(number).ok())
        .map(Some)
        .ok_or_else(|| format!("{key} должен быть целым числом i32"))
}

fn parse_log_level(text: &str) -> Result<LogLevel, String> {
    match text.trim().to_ascii_lowercase().as_str() {
        "silent" => Ok(LogLevel::Silent),
        "warnings" | "warn" => Ok(LogLevel::Warnings),
        "normal" | "info" => Ok(LogLevel::Normal),
        "verbose" | "debug" => Ok(LogLevel::Verbose),
        other => Err(format!("неизвестный log_level: {other}")),
    }
}

fn build_link(config: &RelayConfig, port: u16) -> String {
    let secret = hex_lower(&config.secret);
    let host = match IpAddress::parse(&config.host) {
        Some(address) => connect_address(&address).to_string(),
        None => config.host.clone(),
    };
    build_ee(&host, i32::from(port), &secret, &config.fake_tls_domain)
}

fn hex_lower(bytes: &[u8]) -> String {
    const DIGITS: &[u8; 16] = b"0123456789abcdef";
    let mut out = String::with_capacity(bytes.len() * 2);
    for &byte in bytes {
        out.push(char::from(DIGITS[usize::from(byte >> 4)]));
        out.push(char::from(DIGITS[usize::from(byte & 0x0f)]));
    }
    out
}

fn to_c_string(text: String) -> *mut c_char {
    match CString::new(text.replace('\0', " ")) {
        Ok(value) => value.into_raw(),
        Err(_) => std::ptr::null_mut(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const SECRET: &str = "0123456789abcdef0123456789abcdef";

    #[derive(Default)]
    struct Capture {
        lines: Mutex<Vec<String>>,
        levels: Mutex<Vec<i32>>,
    }

    unsafe extern "C" fn capture_cb(level: i32, msg: *const c_char, user: *mut c_void) {
        if msg.is_null() || user.is_null() {
            return;
        }
        // SAFETY: the test passes a live `Box<Capture>` and only reclaims it
        // after the handle (and all engine threads) are gone.
        let text = unsafe { CStr::from_ptr(msg) }
            .to_string_lossy()
            .into_owned();
        // SAFETY: same lifetime guarantee as above.
        let capture = unsafe { &*user.cast::<Capture>() };
        if let Ok(mut levels) = capture.levels.lock() {
            levels.push(level);
        }
        if let Ok(mut lines) = capture.lines.lock() {
            lines.push(text);
        }
    }

    fn config_json(port: u16, secret: &str) -> CString {
        CString::new(format!(
            "{{\"host\":\"127.0.0.1\",\"port\":{port},\"secret\":\"{secret}\",\
             \"poolidle\":0,\"routes\":{{\"2\":\"127.0.0.1\"}},\
             \"faketls\":\"www.example.com\",\"log_level\":\"normal\"}}"
        ))
        .expect("test JSON has no NUL")
    }

    fn take_string(pointer: *mut c_char) -> String {
        assert!(!pointer.is_null(), "expected a string");
        // SAFETY: the library returned an owned NUL-terminated string.
        let text = unsafe { CStr::from_ptr(pointer) }
            .to_str()
            .expect("library strings are UTF-8")
            .to_string();
        tgws_string_free(pointer);
        text
    }

    fn status_of(handle: *mut EngineHandle) -> Value {
        let text = take_string(tgws_engine_status(handle));
        serde_json::from_str(&text).expect("status JSON")
    }

    #[test]
    fn start_status_link_stop_and_restart() {
        let config = config_json(0, SECRET);
        let handle = tgws_engine_start(config.as_ptr(), None, std::ptr::null_mut());
        assert!(!handle.is_null(), "engine failed to start");

        let status = status_of(handle);
        assert_eq!(status["running"], true);
        let port = status["port"].as_u64().expect("port");
        assert!(port > 0, "ephemeral port must be resolved");

        let link = take_string(tgws_engine_link(handle));
        assert!(link.starts_with("tg://proxy?server=127.0.0.1&port="));
        assert!(link.contains(&format!("&port={port}&secret=ee{SECRET}")));
        assert!(link.contains("7777772e6578616d706c652e636f6d"));

        assert_eq!(tgws_engine_stop(handle), ERR_OK);
        assert_eq!(tgws_engine_stop(handle), ERR_OK, "second stop is safe");
        let stopped = status_of(handle);
        assert_eq!(stopped["running"], false);
        assert_eq!(stopped["port"].as_u64(), Some(port));

        let restart = config_json(u16::try_from(port).expect("port fits"), SECRET);
        let again = tgws_engine_start(restart.as_ptr(), None, std::ptr::null_mut());
        assert!(!again.is_null(), "restart on the same port failed");
        assert_eq!(status_of(again)["running"], true);
        assert_eq!(tgws_engine_stop(again), ERR_OK);

        assert_eq!(tgws_engine_free(again), ERR_OK);
        assert_eq!(tgws_engine_free(handle), ERR_OK);
    }

    #[test]
    fn null_pointers_are_rejected() {
        assert!(tgws_engine_start(std::ptr::null(), None, std::ptr::null_mut()).is_null());
        assert_eq!(tgws_engine_stop(std::ptr::null_mut()), ERR_NULL);
        assert_eq!(tgws_engine_free(std::ptr::null_mut()), ERR_OK);
        assert!(tgws_engine_status(std::ptr::null_mut()).is_null());
        assert!(tgws_engine_link(std::ptr::null_mut()).is_null());
        assert!(tgws_engine_logs(std::ptr::null_mut(), 5).is_null());
        tgws_string_free(std::ptr::null_mut());
    }

    #[test]
    fn logs_reach_the_callback_and_the_ring() {
        let user = Box::into_raw(Box::new(Capture::default()));
        let config = config_json(0, SECRET);
        let handle = tgws_engine_start(config.as_ptr(), Some(capture_cb), user.cast());
        assert!(!handle.is_null());
        assert_eq!(tgws_engine_stop(handle), ERR_OK);

        {
            // SAFETY: the handle is still alive and no engine thread is running.
            let capture = unsafe { &*user };
            let lines = capture.lines.lock().expect("lines lock");
            assert!(
                lines.iter().any(|line| line.contains("listening")),
                "callback missed the startup line: {lines:?}"
            );
            let levels = capture.levels.lock().expect("levels lock");
            assert!(levels.contains(&LOG_INFO));
        }

        let logs = take_string(tgws_engine_logs(handle, 16));
        let value: Value = serde_json::from_str(&logs).expect("logs JSON");
        let lines = value.as_array().expect("logs array");
        assert!(!lines.is_empty());
        assert!(
            lines
                .iter()
                .any(|line| line.as_str().unwrap_or_default().contains("listening"))
        );

        let last = take_string(tgws_engine_logs(handle, 1));
        let value: Value = serde_json::from_str(&last).expect("logs JSON");
        assert_eq!(value.as_array().expect("logs array").len(), 1);

        assert_eq!(tgws_engine_free(handle), ERR_OK);
        // SAFETY: the handle was freed, so no engine thread can call the
        // callback any more.
        drop(unsafe { Box::from_raw(user) });
    }

    #[test]
    fn invalid_config_returns_null_and_reports_through_the_callback() {
        let user = Box::into_raw(Box::new(Capture::default()));
        let bad = CString::new("{ not json").expect("no NUL");
        let handle = tgws_engine_start(bad.as_ptr(), Some(capture_cb), user.cast());
        assert!(handle.is_null());

        // SAFETY: no engine was created, so only this thread touches `user`.
        let capture = unsafe { &*user };
        let levels = capture.levels.lock().expect("levels lock");
        assert!(
            levels.contains(&LOG_ERROR),
            "config error did not reach the callback: {levels:?}"
        );
        drop(unsafe { Box::from_raw(user) });
    }

    #[test]
    fn a_panic_is_contained_by_the_abi_guard() {
        let pointer: *mut c_char = guarded(std::ptr::null_mut(), || panic!("boom"));
        assert!(pointer.is_null());
        assert_eq!(guarded(ERR_INTERNAL, || 42), 42);
        assert_eq!(
            guarded(ERR_INTERNAL, || -> i32 { panic!("again") }),
            ERR_INTERNAL
        );
    }
}
