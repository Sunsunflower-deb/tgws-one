//! The accept loop, event log and counters of the sync relay.
//!
//! Port of the listener half of `ProxyEngine`: a `TcpListener` backlog, one
//! `std::thread` per client, `EngineStats`-like counters and an event line for
//! every connection.  `Engine::start` binds immediately so a taken port fails
//! at the call site; `Engine::stop` stops accepting, wakes the accept loop by
//! dropping the listener and waits briefly for live sessions.

use std::io;
use std::net::{SocketAddr, TcpListener, TcpStream};
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::sync::{Arc, Mutex};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use super::client_handshake::{self, HandshakeError};
use super::config::{LogLevel, RelayConfig};
use super::pool::{Connector, PoolKey, SystemTimeProvider, WsPool};
use super::routes::{self, WsConnection};
use super::session;

const ACCEPT_RETRY: Duration = Duration::from_millis(5);

/// Atomic counters shaped like the C# `EngineStats`.
#[derive(Debug, Default)]
pub struct EngineStats {
    connections_opened: AtomicU64,
    connections_closed: AtomicU64,
    sessions: AtomicU64,
    bad_handshakes: AtomicU64,
    ws_sessions: AtomicU64,
    tcp_fallbacks: AtomicU64,
    errors: AtomicU64,
    masked: AtomicU64,
    bytes_up: AtomicU64,
    bytes_down: AtomicU64,
}

impl EngineStats {
    /// A consistent-enough read for logs and tests.
    #[must_use]
    pub fn snapshot(&self) -> EngineStatsSnapshot {
        EngineStatsSnapshot {
            connections_opened: self.connections_opened.load(Ordering::Relaxed),
            connections_closed: self.connections_closed.load(Ordering::Relaxed),
            sessions: self.sessions.load(Ordering::Relaxed),
            bad_handshakes: self.bad_handshakes.load(Ordering::Relaxed),
            ws_sessions: self.ws_sessions.load(Ordering::Relaxed),
            tcp_fallbacks: self.tcp_fallbacks.load(Ordering::Relaxed),
            errors: self.errors.load(Ordering::Relaxed),
            masked: self.masked.load(Ordering::Relaxed),
            bytes_up: self.bytes_up.load(Ordering::Relaxed),
            bytes_down: self.bytes_down.load(Ordering::Relaxed),
        }
    }

    pub(crate) fn connection_opened(&self) {
        self.connections_opened.fetch_add(1, Ordering::Relaxed);
    }

    pub(crate) fn connection_closed(&self) {
        self.connections_closed.fetch_add(1, Ordering::Relaxed);
    }

    pub(crate) fn session(&self) {
        self.sessions.fetch_add(1, Ordering::Relaxed);
    }

    pub(crate) fn bad_handshake(&self) {
        self.bad_handshakes.fetch_add(1, Ordering::Relaxed);
    }

    pub(crate) fn ws_session(&self) {
        self.ws_sessions.fetch_add(1, Ordering::Relaxed);
    }

    pub(crate) fn tcp_fallback(&self) {
        self.tcp_fallbacks.fetch_add(1, Ordering::Relaxed);
    }

    pub(crate) fn error(&self) {
        self.errors.fetch_add(1, Ordering::Relaxed);
    }

    pub(crate) fn masked(&self) {
        self.masked.fetch_add(1, Ordering::Relaxed);
    }

    pub(crate) fn up(&self, bytes: usize) {
        self.bytes_up.fetch_add(bytes as u64, Ordering::Relaxed);
    }

    pub(crate) fn down(&self, bytes: usize) {
        self.bytes_down.fetch_add(bytes as u64, Ordering::Relaxed);
    }
}

/// Immutable snapshot of [`EngineStats`].
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub struct EngineStatsSnapshot {
    /// Accepted TCP connections.
    pub connections_opened: u64,
    /// Connections whose handler returned.
    pub connections_closed: u64,
    /// Accepted handshakes.
    pub sessions: u64,
    /// Handshakes refused for a wrong secret/tag.
    pub bad_handshakes: u64,
    /// Sessions carried over a WebSocket.
    pub ws_sessions: u64,
    /// Sessions carried by direct TCP.
    pub tcp_fallbacks: u64,
    /// Transport/protocol errors.
    pub errors: u64,
    /// Fake-TLS handshakes answered.
    pub masked: u64,
    /// Client-to-Telegram bytes.
    pub bytes_up: u64,
    /// Telegram-to-client bytes.
    pub bytes_down: u64,
}

/// Severity of a relay event line.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LogSeverity {
    /// Protocol or lifecycle detail.
    Debug,
    /// Connection and lifecycle news.
    Info,
    /// Something recoverable went wrong.
    Warn,
}

/// A destination for relay event lines in addition to stderr.
///
/// The FFI layer implements this to fan lines out into a ring buffer and the
/// host's callback.  Implementations must be thread-safe because engine worker
/// threads log concurrently.
pub trait LogSink: Send + Sync {
    /// Receives one already-formatted event line.
    fn log(&self, severity: LogSeverity, message: &str);
}

/// Event log with the C# engine's line-per-event shape.
pub struct EngineLog {
    level: LogLevel,
    sink: Option<Arc<dyn LogSink>>,
}

impl EngineLog {
    fn with_sink(level: LogLevel, sink: Option<Arc<dyn LogSink>>) -> Self {
        Self { level, sink }
    }

    fn emit(&self, severity: LogSeverity, message: &str) {
        if let Some(sink) = &self.sink {
            sink.log(severity, message);
        }
    }

    pub(crate) fn info(&self, message: &str) {
        if matches!(self.level, LogLevel::Normal | LogLevel::Verbose) {
            eprintln!("[relay] {message}");
            self.emit(LogSeverity::Info, message);
        }
    }

    pub(crate) fn warn(&self, message: &str) {
        if !matches!(self.level, LogLevel::Silent) {
            eprintln!("[relay] warn: {message}");
            self.emit(LogSeverity::Warn, message);
        }
    }

    pub(crate) fn debug(&self, message: &str) {
        if matches!(self.level, LogLevel::Verbose) {
            eprintln!("[relay] debug: {message}");
            self.emit(LogSeverity::Debug, message);
        }
    }
}

struct Inner {
    config: RelayConfig,
    stats: EngineStats,
    log: EngineLog,
    pool: Option<Arc<WsPool<WsConnection>>>,
    stopping: AtomicBool,
    active: AtomicUsize,
}

/// A running relay.
pub struct Engine {
    inner: Arc<Inner>,
    accept: Mutex<Option<JoinHandle<()>>>,
    port: u16,
}

impl Engine {
    /// Binds the listener and starts the accept thread.
    ///
    /// # Errors
    ///
    /// Returns the bind error when the address is taken or invalid.
    pub fn start(config: RelayConfig) -> io::Result<Self> {
        Self::start_with_sink(config, None)
    }

    /// Binds the listener, starts the accept thread and forwards every event
    /// line to `sink` after the verbosity filter.
    ///
    /// # Errors
    ///
    /// Returns the bind error when the address is taken or invalid.
    pub fn start_with_sink(
        config: RelayConfig,
        sink: Option<Arc<dyn LogSink>>,
    ) -> io::Result<Self> {
        let listener = TcpListener::bind((config.host.as_str(), config.port))?;
        listener.set_nonblocking(true)?;
        let port = listener.local_addr()?.port();
        let pool = build_pool(&config);
        let inner = Arc::new(Inner {
            log: EngineLog::with_sink(config.log_level, sink),
            config,
            stats: EngineStats::default(),
            pool,
            stopping: AtomicBool::new(false),
            active: AtomicUsize::new(0),
        });

        inner.log.info(&format!(
            "listening on {}:{} ({} DC override(s), pool idle {}s)",
            inner.config.host,
            port,
            inner.config.dc_redirects.len(),
            inner.config.pool_idle_seconds
        ));

        let accept_inner = Arc::clone(&inner);
        let handle = thread::spawn(move || accept_loop(listener, accept_inner));

        Ok(Self {
            inner,
            accept: Mutex::new(Some(handle)),
            port,
        })
    }

    /// The bound port (useful when [`RelayConfig::port`] was zero).
    #[must_use]
    pub fn port(&self) -> u16 {
        self.port
    }

    /// The effective configuration.
    #[must_use]
    pub fn config(&self) -> &RelayConfig {
        &self.inner.config
    }

    /// Counter snapshot.
    #[must_use]
    pub fn stats(&self) -> EngineStatsSnapshot {
        self.inner.stats.snapshot()
    }

    /// Stops accepting, joins the accept thread and waits up to three seconds
    /// for live sessions to notice `stopping`.
    pub fn stop(&mut self) {
        if self.inner.stopping.swap(true, Ordering::SeqCst) {
            return;
        }
        if let Some(handle) = self.accept.lock().expect("accept mutex").take() {
            let _ = handle.join();
        }

        let deadline = Instant::now() + Duration::from_secs(3);
        while self.inner.active.load(Ordering::Relaxed) > 0 && Instant::now() < deadline {
            thread::sleep(ACCEPT_RETRY);
        }

        let stats = self.inner.stats.snapshot();
        self.inner.log.info(&format!(
            "stopped: {} conn, {} sessions, {} WS, {} TCP, {} errors",
            stats.connections_opened,
            stats.sessions,
            stats.ws_sessions,
            stats.tcp_fallbacks,
            stats.errors
        ));
    }
}

impl Drop for Engine {
    fn drop(&mut self) {
        self.stop();
    }
}

fn build_pool(config: &RelayConfig) -> Option<Arc<WsPool<WsConnection>>> {
    if config.pool_size <= 0 {
        return None;
    }
    let pool_config = config.clone();
    let connector: Arc<Connector<WsConnection>> =
        Arc::new(move |key: &PoolKey| routes::connect_warm(&pool_config, key));
    let size = usize::try_from(config.pool_size).unwrap_or(0);
    Some(Arc::new(WsPool::new(
        size,
        Duration::from_secs(config.pool_idle_seconds),
        connector,
        Arc::new(SystemTimeProvider),
    )))
}

fn accept_loop(listener: TcpListener, inner: Arc<Inner>) {
    while !inner.stopping.load(Ordering::Relaxed) {
        match listener.accept() {
            Ok((stream, peer)) => {
                inner.stats.connection_opened();
                inner.active.fetch_add(1, Ordering::SeqCst);
                let worker = Arc::clone(&inner);
                thread::spawn(move || {
                    handle_client(stream, peer, &worker);
                    worker.stats.connection_closed();
                    worker.active.fetch_sub(1, Ordering::SeqCst);
                });
            }
            Err(error) if error.kind() == io::ErrorKind::WouldBlock => {
                thread::sleep(ACCEPT_RETRY);
            }
            Err(error) if inner.stopping.load(Ordering::Relaxed) => {
                inner.log.debug(&format!("accept ended: {error}"));
                break;
            }
            Err(error) => {
                inner.log.warn(&format!("accept failed: {error}; retrying"));
                thread::sleep(Duration::from_millis(50));
            }
        }
    }
}

fn handle_client(stream: TcpStream, peer: SocketAddr, inner: &Inner) {
    let label = format!("{peer}");
    let _ = stream.set_nodelay(true);
    if let Err(error) = stream.set_read_timeout(Some(inner.config.read_timeout)) {
        inner
            .log
            .debug(&format!("[{label}] socket timeout setup failed: {error}"));
    }

    match client_handshake::accept(stream, &inner.config) {
        Ok((mut connection, handshake)) => {
            inner.stats.session();
            if handshake.fake_tls {
                inner.stats.masked();
            }
            inner.log.info(&format!(
                "[{label}] handshake ok: DC{}{} proto=0x{:08X}{}",
                handshake.dc,
                if handshake.is_media { " media" } else { "" },
                handshake.proto_tag,
                if handshake.fake_tls { " fake-tls" } else { "" }
            ));
            let outcome = session::run(
                &mut connection,
                &handshake,
                &inner.config,
                &inner.stats,
                &inner.log,
                &inner.stopping,
                inner.pool.as_deref(),
            );
            inner.log.debug(&format!(
                "[{label}] session finished: route={:?} up={} down={}",
                outcome.route, outcome.up_bytes, outcome.down_bytes
            ));
        }
        Err(HandshakeError::BadHandshake) => {
            inner.stats.bad_handshake();
            inner
                .log
                .warn(&format!("[{label}] bad handshake (wrong secret or proto)"));
        }
        Err(HandshakeError::FakeTls) => {
            inner.stats.bad_handshake();
            inner
                .log
                .warn(&format!("[{label}] malformed fake TLS hello"));
        }
        Err(HandshakeError::Io(error)) => {
            inner
                .log
                .debug(&format!("[{label}] handshake ended: {error}"));
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::{Read, Write};
    use std::net::TcpListener;

    use crate::proto::{AesCtr, MtProtoConstants};
    use crate::ws::client::compute_accept;
    use crate::ws::frame::{Endpoint, Frame, FrameParser};

    use super::super::client_handshake::test_support::client_handshake;

    fn silent_config(secret: &[u8]) -> RelayConfig {
        RelayConfig {
            port: 0,
            secret: secret.to_vec(),
            pool_size: 4,
            pool_idle_seconds: 0,
            read_timeout: Duration::from_secs(5),
            log_level: LogLevel::Silent,
            ..RelayConfig::default()
        }
    }

    fn intermediate_packet() -> Vec<u8> {
        let body: Vec<u8> = (0..24u8).collect();
        let mut packet = Vec::with_capacity(4 + body.len());
        packet.extend_from_slice(&(body.len() as i32).to_le_bytes());
        packet.extend_from_slice(&body);
        packet
    }

    fn server_relay_keys(relay_init: &[u8]) -> (AesCtr, AesCtr) {
        let mut decrypt = AesCtr::new(
            &relay_init[MtProtoConstants::SKIP_LENGTH
                ..MtProtoConstants::SKIP_LENGTH + MtProtoConstants::KEY_LENGTH],
            &relay_init[MtProtoConstants::SKIP_LENGTH + MtProtoConstants::PREKEY_LENGTH
                ..MtProtoConstants::SKIP_LENGTH
                    + MtProtoConstants::PREKEY_LENGTH
                    + MtProtoConstants::IV_LENGTH],
        )
        .unwrap();
        decrypt.skip(MtProtoConstants::HANDSHAKE_LENGTH);

        let mut reversed = relay_init[MtProtoConstants::SKIP_LENGTH
            ..MtProtoConstants::SKIP_LENGTH
                + MtProtoConstants::PREKEY_LENGTH
                + MtProtoConstants::IV_LENGTH]
            .to_vec();
        reversed.reverse();
        let encrypt = AesCtr::new(
            &reversed[..MtProtoConstants::KEY_LENGTH],
            &reversed[MtProtoConstants::KEY_LENGTH
                ..MtProtoConstants::KEY_LENGTH + MtProtoConstants::IV_LENGTH],
        )
        .unwrap();
        (decrypt, encrypt)
    }

    fn read_request(stream: &mut TcpStream) -> String {
        let mut buffer = Vec::new();
        let mut chunk = [0u8; 1024];
        loop {
            let read = stream.read(&mut chunk).expect("read request");
            assert!(read > 0, "client closed before the request head");
            buffer.extend_from_slice(&chunk[..read]);
            if buffer.windows(4).any(|window| window == b"\r\n\r\n") {
                break;
            }
        }
        String::from_utf8(buffer).expect("request is UTF-8")
    }

    fn read_frame(stream: &mut TcpStream, parser: &mut FrameParser) -> Frame {
        loop {
            if let Some(frame) = parser.next_frame().expect("parse frame") {
                return frame;
            }
            let mut buffer = [0u8; 4096];
            let read = stream.read(&mut buffer).expect("read frame");
            assert!(read > 0, "closed while a frame was expected");
            parser.push(&buffer[..read]);
        }
    }

    fn spawn_websocket_echo() -> (u16, JoinHandle<Vec<u8>>) {
        let listener = TcpListener::bind(("127.0.0.1", 0)).expect("bind");
        let port = listener.local_addr().unwrap().port();
        let handle = thread::spawn(move || {
            let (mut stream, _) = listener.accept().expect("accept");
            stream
                .set_read_timeout(Some(Duration::from_secs(5)))
                .unwrap();
            let request = read_request(&mut stream);
            let key = request
                .lines()
                .find_map(|line| {
                    let (name, value) = line.split_once(':')?;
                    name.eq_ignore_ascii_case("sec-websocket-key")
                        .then(|| value.trim().to_owned())
                })
                .expect("key header");
            let response = format!(
                "HTTP/1.1 101 Switching Protocols\r\n\
                 Upgrade: websocket\r\n\
                 Connection: Upgrade\r\n\
                 Sec-WebSocket-Accept: {}\r\n\r\n",
                compute_accept(&key)
            );
            stream.write_all(response.as_bytes()).unwrap();

            let mut parser = FrameParser::new(Endpoint::Server);
            let init = read_frame(&mut stream, &mut parser);
            assert_eq!(init.opcode, crate::ws::frame::OpCode::Binary);
            assert_eq!(init.payload.len(), MtProtoConstants::HANDSHAKE_LENGTH);
            let (mut decrypt, mut encrypt) = server_relay_keys(&init.payload);

            let cipher = read_frame(&mut stream, &mut parser).payload;
            let mut plain = vec![0u8; cipher.len()];
            decrypt.transform(&cipher, &mut plain);
            let mut reply = vec![0u8; plain.len()];
            encrypt.transform(&plain, &mut reply);
            let frame = Frame::binary(reply).encode_unmasked().unwrap();
            stream.write_all(&frame).unwrap();
            plain
        });
        (port, handle)
    }

    fn spawn_tcp_echo(packet_len: usize) -> (u16, JoinHandle<Vec<u8>>) {
        let listener = TcpListener::bind(("127.0.0.1", 0)).expect("bind");
        let port = listener.local_addr().unwrap().port();
        let handle = thread::spawn(move || {
            let (mut stream, _) = listener.accept().expect("accept");
            stream
                .set_read_timeout(Some(Duration::from_secs(5)))
                .unwrap();
            let mut init = [0u8; MtProtoConstants::HANDSHAKE_LENGTH];
            stream.read_exact(&mut init).unwrap();
            assert!(crate::proto::Obfuscated2::try_read_relay_init(&init).is_some());
            let (mut decrypt, mut encrypt) = server_relay_keys(&init);

            let mut cipher = vec![0u8; packet_len];
            stream.read_exact(&mut cipher).unwrap();
            let mut plain = vec![0u8; packet_len];
            decrypt.transform(&cipher, &mut plain);
            let mut reply = vec![0u8; packet_len];
            encrypt.transform(&plain, &mut reply);
            stream.write_all(&reply).unwrap();
            plain
        });
        (port, handle)
    }

    fn wait_for(
        engine: &Engine,
        mut condition: impl FnMut(&EngineStatsSnapshot) -> bool,
    ) -> EngineStatsSnapshot {
        let deadline = Instant::now() + Duration::from_secs(5);
        loop {
            let snapshot = engine.stats();
            if condition(&snapshot) {
                return snapshot;
            }
            assert!(
                Instant::now() < deadline,
                "condition not reached: {snapshot:?}"
            );
            thread::sleep(Duration::from_millis(10));
        }
    }

    #[test]
    fn full_path_websocket_roundtrip_and_counters() {
        let secret = [0x33u8; 16];
        let (upstream_port, server) = spawn_websocket_echo();
        let mut config = silent_config(&secret);
        config.dc_redirects.insert(2, "127.0.0.1".to_string());
        config.ws_port = upstream_port;
        config.ws_secure = false;
        config.pool_size = 1;

        let mut engine = Engine::start(config).unwrap();
        // The test helper above used a placeholder; use the real counters here.
        let expected = intermediate_packet();
        let (handshake, mut encrypt, mut decrypt) =
            client_handshake(&secret, MtProtoConstants::PROTO_INTERMEDIATE, 2);

        let mut client = TcpStream::connect(("127.0.0.1", engine.port())).unwrap();
        client
            .set_read_timeout(Some(Duration::from_secs(5)))
            .unwrap();
        client.write_all(&handshake).unwrap();
        let cipher = encrypt.transform_vec(&expected);
        client.write_all(&cipher).unwrap();

        let mut reply = vec![0u8; expected.len()];
        client.read_exact(&mut reply).unwrap();
        assert_eq!(decrypt.transform_vec(&reply), expected);
        drop(client);

        assert_eq!(server.join().unwrap(), expected);

        let snapshot = wait_for(&engine, |stats| {
            stats.sessions == 1 && stats.ws_sessions == 1 && stats.connections_closed == 1
        });
        assert_eq!(snapshot.connections_opened, 1);
        assert_eq!(snapshot.bad_handshakes, 0);
        assert_eq!(snapshot.tcp_fallbacks, 0);
        assert_eq!(snapshot.errors, 0);
        assert!(snapshot.bytes_up >= expected.len() as u64);
        assert!(snapshot.bytes_down >= expected.len() as u64);
        engine.stop();
    }

    #[test]
    fn fallback_tcp_roundtrip_is_used_when_ws_has_no_route() {
        let secret = [0x34u8; 16];
        let packet = intermediate_packet();
        let (fallback_port, server) = spawn_tcp_echo(packet.len());
        let mut config = silent_config(&secret);
        config.tcp_fallback_host_override = Some("127.0.0.1".to_string());
        config.tcp_fallback_port = fallback_port;

        let mut engine = Engine::start(config).unwrap();
        let (handshake, mut encrypt, mut decrypt) =
            client_handshake(&secret, MtProtoConstants::PROTO_INTERMEDIATE, 2);

        let mut client = TcpStream::connect(("127.0.0.1", engine.port())).unwrap();
        client
            .set_read_timeout(Some(Duration::from_secs(5)))
            .unwrap();
        client.write_all(&handshake).unwrap();
        client.write_all(&encrypt.transform_vec(&packet)).unwrap();

        let mut reply = vec![0u8; packet.len()];
        client.read_exact(&mut reply).unwrap();
        assert_eq!(decrypt.transform_vec(&reply), packet);
        drop(client);

        assert_eq!(server.join().unwrap(), packet);
        let snapshot = wait_for(&engine, |stats| {
            stats.sessions == 1 && stats.tcp_fallbacks == 1 && stats.connections_closed == 1
        });
        assert_eq!(snapshot.ws_sessions, 0);
        assert_eq!(snapshot.errors, 0);
        engine.stop();
    }

    #[test]
    fn wrong_secret_is_refused() {
        let secret = [0x35u8; 16];
        let mut config = silent_config(&secret);
        config.dc_redirects.insert(2, "127.0.0.1".to_string());
        let mut engine = Engine::start(config).unwrap();
        let (handshake, _, _) =
            client_handshake(&[0x36u8; 16], MtProtoConstants::PROTO_ABRIDGED, 2);

        let mut client = TcpStream::connect(("127.0.0.1", engine.port())).unwrap();
        client
            .set_read_timeout(Some(Duration::from_secs(2)))
            .unwrap();
        client.write_all(&handshake).unwrap();
        let mut byte = [0u8; 1];
        let read = client.read(&mut byte);
        assert!(matches!(read, Ok(0) | Err(_)), "unexpected read: {read:?}");
        drop(client);

        let snapshot = wait_for(&engine, |stats| stats.bad_handshakes == 1);
        assert_eq!(snapshot.sessions, 0);
        engine.stop();
    }

    #[test]
    fn stop_releases_the_listening_port() {
        let mut config = silent_config(&[0x37u8; 16]);
        let mut engine = Engine::start(config.clone()).unwrap();
        let port = engine.port();
        engine.stop();
        config.port = port;
        let listener = TcpListener::bind(("127.0.0.1", port)).expect("port is free");
        drop(listener);
    }
}
