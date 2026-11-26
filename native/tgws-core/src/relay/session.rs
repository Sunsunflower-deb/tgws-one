//! The bidirectional relay with re-encryption.
//!
//! This is the port of `RelayBridge.WebSocketAsync` and `TcpAsync`: client
//! ciphertext is decrypted with the client key, re-encrypted with the relay
//! key and sent on; replies travel back the other way.  A successful WebSocket
//! connect is used directly, a failed one falls back to one direct-TCP attempt,
//! matching `ProxyEngine.FallbackAsync`.
//!
//! The C# versions run the two directions as two async tasks.  This crate is
//! synchronous, and the existing `WebSocket` client is not `Send`, so the two
//! directions share one poll loop with a short socket read timeout (20 ms).
//! That is the documented behavioural difference: latency instead of
//! zero-copy wakeups, but the same bytes, re-encryption order and close rules.

use std::io::{self, Read, Write};
use std::mem::size_of;
use std::net::{TcpStream, ToSocketAddrs};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::time::Duration;

use crate::mtproto::MsgSplitter;
use crate::proto::{CryptoContext, Obfuscated2};
use crate::tcp::RawTcpStream;
use crate::tls::client_hello::Random32;
use crate::tls13::{ServerCertPolicy, Tls13Client, Tls13ClientConfig};
use crate::ws::WsError;
use crate::ws::client::{HandshakeConfig, Message, WebSocket};

use super::OsRandom;
use super::client_handshake::{ClientConnection, ClientHandshake};
use super::config::{POLL_INTERVAL, RelayConfig};
use super::engine::{EngineLog, EngineStats};

const READ_SIZE: usize = 64 * 1024;

/// Which outbound route carried the session.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SessionRoute {
    /// Telegram's `kws<dc>` WebSocket endpoint.
    WebSocket,
    /// Direct MTProto TCP to the DC.
    TcpFallback,
    /// Nothing was reachable.
    None,
}

/// Summary of a finished session, used for the event line and tests.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SessionOutcome {
    /// Route that was used.
    pub route: SessionRoute,
    /// Client-to-Telegram bytes.
    pub up_bytes: u64,
    /// Telegram-to-client bytes.
    pub down_bytes: u64,
}

/// Runs one accepted client to completion.
pub fn run(
    client: &mut ClientConnection,
    handshake: &ClientHandshake,
    config: &RelayConfig,
    stats: &EngineStats,
    log: &EngineLog,
    stopping: &AtomicBool,
) -> SessionOutcome {
    let mut rng = match OsRandom::new() {
        Ok(rng) => rng,
        Err(error) => {
            log.warn(&format!("relay: /dev/urandom unavailable: {error}"));
            stats.error();
            return SessionOutcome {
                route: SessionRoute::None,
                up_bytes: 0,
                down_bytes: 0,
            };
        }
    };

    let relay_init = Obfuscated2::generate_relay_init(
        |buffer| rng.fill_bytes(buffer),
        handshake.proto_tag,
        handshake.relay_dc_index(),
    );
    let context = match CryptoContext::new(&handshake.client_prekey_iv, &config.secret, &relay_init)
    {
        Ok(context) => context,
        Err(error) => {
            log.warn(&format!("relay: crypto context failed: {error}"));
            stats.error();
            return SessionOutcome {
                route: SessionRoute::None,
                up_bytes: 0,
                down_bytes: 0,
            };
        }
    };

    let target = if config.pool_size > 0 {
        config.dc_redirects.get(&handshake.dc).cloned()
    } else {
        None
    };

    if let Some(target) = target {
        match connect_websocket(config, &target, handshake) {
            Ok(mut socket) => {
                if let Err(error) = socket.send_binary(&relay_init) {
                    log.warn(&format!("relay: WS init send failed: {error}"));
                    stats.error();
                    return SessionOutcome {
                        route: SessionRoute::WebSocket,
                        up_bytes: 0,
                        down_bytes: 0,
                    };
                }
                stats.ws_session();
                log.info(&format!(
                    "DC{}{} -> WS {}",
                    handshake.dc,
                    if handshake.is_media { " media" } else { "" },
                    target
                ));
                return relay_websocket(client, socket, context, handshake, stats, log, stopping);
            }
            Err(error) => {
                log.warn(&format!(
                    "DC{}{} WS unavailable ({error}) -> fallback",
                    handshake.dc,
                    if handshake.is_media { " media" } else { "" }
                ));
            }
        }
    } else {
        log.info(&format!(
            "DC{}{} has no WS route -> fallback",
            handshake.dc,
            if handshake.is_media { " media" } else { "" }
        ));
    }

    match config.fallback_host(handshake.dc, handshake.is_test_dc) {
        Some(destination) => relay_tcp_fallback(
            client,
            &destination,
            relay_init,
            context,
            handshake,
            config,
            stats,
            log,
            stopping,
        ),
        None => {
            log.warn(&format!("DC{} no fallback available", handshake.dc));
            SessionOutcome {
                route: SessionRoute::None,
                up_bytes: 0,
                down_bytes: 0,
            }
        }
    }
}

fn connect_websocket(
    config: &RelayConfig,
    target: &str,
    handshake: &ClientHandshake,
) -> Result<WebSocket<UpstreamStream>, String> {
    let path = config.ws_path_for(handshake.is_test_dc);
    let timeout_ms = u64::try_from(config.read_timeout.as_millis()).unwrap_or(u64::MAX);

    let mut last_error = String::new();
    for domain in config.ws_domains(handshake.dc, handshake.is_media) {
        let timeout = Arc::new(AtomicU64::new(timeout_ms));
        let control = Arc::clone(&timeout);
        let stream = if config.ws_secure {
            let raw =
                RawTcpStream::connect(target, config.ws_port).map_err(|error| error.to_string())?;
            let rng = OsRandom::new().map_err(|error| error.to_string())?;
            let tls = TlsClientStream::connect(raw, &domain, config.read_timeout, Box::new(rng))
                .map_err(|error| error.to_string())?;
            UpstreamStream::Tls {
                stream: Box::new(tls),
                timeout,
                applied_ms: timeout_ms,
            }
        } else {
            let stream =
                TcpStream::connect((target, config.ws_port)).map_err(|error| error.to_string())?;
            stream
                .set_nodelay(true)
                .map_err(|error| error.to_string())?;
            stream
                .set_read_timeout(Some(config.read_timeout))
                .map_err(|error| error.to_string())?;
            UpstreamStream::Plain {
                stream,
                timeout,
                applied_ms: timeout_ms,
            }
        };

        let handshake_config = HandshakeConfig::new(domain.clone())
            .with_path(path)
            .with_subprotocol("binary");
        let rng = OsRandom::new().map_err(|error| error.to_string())?;
        match WebSocket::connect(stream, &handshake_config, Box::new(rng)) {
            Ok(socket) => {
                let poll = u64::try_from(POLL_INTERVAL.as_millis()).unwrap_or(20);
                control.store(poll, Ordering::Relaxed);
                return Ok(socket);
            }
            Err(error) => last_error = format!("{domain}: {error}"),
        }
    }
    Err(last_error)
}

fn relay_websocket(
    client: &mut ClientConnection,
    mut socket: WebSocket<UpstreamStream>,
    mut context: CryptoContext,
    handshake: &ClientHandshake,
    stats: &EngineStats,
    log: &EngineLog,
    stopping: &AtomicBool,
) -> SessionOutcome {
    if let Err(error) = client.set_read_timeout(Some(POLL_INTERVAL)) {
        log.warn(&format!("relay: client timeout setup failed: {error}"));
    }

    let mut splitter = MsgSplitter::new(handshake.proto_tag);
    let mut buffer = vec![0u8; READ_SIZE];
    let mut up_bytes = 0u64;
    let mut down_bytes = 0u64;

    loop {
        if stopping.load(Ordering::Relaxed) {
            break;
        }

        match client.read(&mut buffer) {
            Ok(0) => {
                for tail in splitter.flush() {
                    if socket.send_binary(&tail).is_err() {
                        break;
                    }
                }
                break;
            }
            Ok(read) => {
                stats.up(read);
                up_bytes += read as u64;
                let mut plain = vec![0u8; read];
                context.clt_dec.transform(&buffer[..read], &mut plain);
                let mut cipher = vec![0u8; read];
                context.tg_enc.transform(&plain, &mut cipher);
                match splitter.split(&cipher, &plain) {
                    Ok(parts) => {
                        for part in parts {
                            if let Err(error) = socket.send_binary(&part) {
                                log.debug(&format!("relay: WS send failed: {error}"));
                                return finish_websocket(
                                    &mut socket,
                                    up_bytes,
                                    down_bytes,
                                    "send error",
                                    log,
                                );
                            }
                        }
                    }
                    Err(error) => {
                        log.warn(&format!("relay: splitter failed: {error}"));
                        stats.error();
                        break;
                    }
                }
            }
            Err(error) if is_timeout(&error) => {}
            Err(error) => {
                log.debug(&format!("relay: client read ended: {error}"));
                break;
            }
        }

        match socket.receive() {
            Ok(Some(Message::Binary(data))) => {
                stats.down(data.len());
                down_bytes += data.len() as u64;
                if let Err(error) = write_downstream(client, &mut context, &data) {
                    log.debug(&format!("relay: client write ended: {error}"));
                    break;
                }
            }
            Ok(Some(Message::Text(text))) => {
                let data = text.into_bytes();
                stats.down(data.len());
                down_bytes += data.len() as u64;
                if let Err(error) = write_downstream(client, &mut context, &data) {
                    log.debug(&format!("relay: client write ended: {error}"));
                    break;
                }
            }
            Ok(None) => {
                log.debug("relay: upstream closed the WebSocket");
                break;
            }
            Err(error) if ws_timeout(&error) => {}
            Err(error) => {
                stats.error();
                log.warn(&format!("relay: WS receive failed: {error}"));
                break;
            }
        }
    }

    finish_websocket(&mut socket, up_bytes, down_bytes, "normal", log)
}

fn finish_websocket(
    socket: &mut WebSocket<UpstreamStream>,
    up_bytes: u64,
    down_bytes: u64,
    reason: &str,
    log: &EngineLog,
) -> SessionOutcome {
    log.info(&format!(
        "WS session closed ({reason}): ^{} v{}",
        human_bytes(up_bytes),
        human_bytes(down_bytes)
    ));
    let _ = socket.close(Some(1000), "");
    SessionOutcome {
        route: SessionRoute::WebSocket,
        up_bytes,
        down_bytes,
    }
}

fn write_downstream(
    client: &mut ClientConnection,
    context: &mut CryptoContext,
    data: &[u8],
) -> io::Result<()> {
    let mut plain = vec![0u8; data.len()];
    context.tg_dec.transform(data, &mut plain);
    let mut cipher = vec![0u8; data.len()];
    context.clt_enc.transform(&plain, &mut cipher);
    client.write_all(&cipher)?;
    client.flush()
}

#[allow(clippy::too_many_arguments)]
fn relay_tcp_fallback(
    client: &mut ClientConnection,
    destination: &str,
    relay_init: Vec<u8>,
    mut context: CryptoContext,
    handshake: &ClientHandshake,
    config: &RelayConfig,
    stats: &EngineStats,
    log: &EngineLog,
    stopping: &AtomicBool,
) -> SessionOutcome {
    let mut remote =
        match connect_with_timeout(destination, config.tcp_fallback_port, config.read_timeout) {
            Ok(remote) => remote,
            Err(error) => {
                stats.error();
                log.warn(&format!(
                    "DC{} TCP fallback to {destination}:{} failed: {error}",
                    handshake.dc, config.tcp_fallback_port
                ));
                return SessionOutcome {
                    route: SessionRoute::None,
                    up_bytes: 0,
                    down_bytes: 0,
                };
            }
        };

    log.info(&format!(
        "DC{}{} TCP fallback to {destination}:{}",
        handshake.dc,
        if handshake.is_media { " media" } else { "" },
        config.tcp_fallback_port
    ));

    if let Err(error) = remote.write_all(&relay_init) {
        stats.error();
        log.warn(&format!(
            "DC{} TCP fallback init failed: {error}",
            handshake.dc
        ));
        return SessionOutcome {
            route: SessionRoute::TcpFallback,
            up_bytes: 0,
            down_bytes: 0,
        };
    }
    let _ = remote.set_nodelay(true);
    let _ = remote.set_read_timeout(Some(POLL_INTERVAL));
    if let Err(error) = client.set_read_timeout(Some(POLL_INTERVAL)) {
        log.debug(&format!("relay: client timeout setup failed: {error}"));
    }
    stats.tcp_fallback();

    let mut buffer = vec![0u8; READ_SIZE];
    let mut up_bytes = 0u64;
    let mut down_bytes = 0u64;

    loop {
        if stopping.load(Ordering::Relaxed) {
            break;
        }

        match client.read(&mut buffer) {
            Ok(0) => break,
            Ok(read) => {
                stats.up(read);
                up_bytes += read as u64;
                let mut plain = vec![0u8; read];
                context.clt_dec.transform(&buffer[..read], &mut plain);
                let mut cipher = vec![0u8; read];
                context.tg_enc.transform(&plain, &mut cipher);
                if remote.write_all(&cipher).is_err() {
                    break;
                }
            }
            Err(error) if is_timeout(&error) => {}
            Err(error) => {
                log.debug(&format!("relay: client read ended: {error}"));
                break;
            }
        }

        match remote.read(&mut buffer) {
            Ok(0) => break,
            Ok(read) => {
                stats.down(read);
                down_bytes += read as u64;
                let mut plain = vec![0u8; read];
                context.tg_dec.transform(&buffer[..read], &mut plain);
                let mut cipher = vec![0u8; read];
                context.clt_enc.transform(&plain, &mut cipher);
                if client.write_all(&cipher).is_err() || client.flush().is_err() {
                    break;
                }
            }
            Err(error) if is_timeout(&error) => {}
            Err(error) => {
                log.debug(&format!("relay: DC read ended: {error}"));
                break;
            }
        }
    }

    log.info(&format!(
        "TCP session closed: ^{} v{}",
        human_bytes(up_bytes),
        human_bytes(down_bytes)
    ));
    SessionOutcome {
        route: SessionRoute::TcpFallback,
        up_bytes,
        down_bytes,
    }
}

fn connect_with_timeout(host: &str, port: u16, timeout: Duration) -> io::Result<TcpStream> {
    let mut last_error = None;
    for address in (host, port).to_socket_addrs()? {
        match TcpStream::connect_timeout(&address, timeout) {
            Ok(stream) => return Ok(stream),
            Err(error) => last_error = Some(error),
        }
    }
    Err(last_error
        .unwrap_or_else(|| io::Error::new(io::ErrorKind::InvalidInput, "host did not resolve")))
}

fn is_timeout(error: &io::Error) -> bool {
    matches!(
        error.kind(),
        io::ErrorKind::WouldBlock | io::ErrorKind::TimedOut | io::ErrorKind::Interrupted
    )
}

fn ws_timeout(error: &WsError) -> bool {
    matches!(error, WsError::Io(io) if is_timeout(io))
}

fn human_bytes(bytes: u64) -> String {
    if bytes < 1024 {
        format!("{bytes}B")
    } else if bytes < 1024 * 1024 {
        format!("{:.1}KB", bytes as f64 / 1024.0)
    } else {
        format!("{:.1}MB", bytes as f64 / (1024.0 * 1024.0))
    }
}

/// A blocking stream under the WebSocket client with a settable read timeout.
enum UpstreamStream {
    Plain {
        stream: TcpStream,
        timeout: Arc<AtomicU64>,
        applied_ms: u64,
    },
    Tls {
        stream: Box<TlsClientStream>,
        timeout: Arc<AtomicU64>,
        applied_ms: u64,
    },
}

impl UpstreamStream {
    fn apply_timeout(&mut self) -> io::Result<()> {
        let millis = self.timeout();
        let applied = match self {
            Self::Plain { applied_ms, .. } | Self::Tls { applied_ms, .. } => *applied_ms,
        };
        if millis == applied {
            return Ok(());
        }
        let duration = if millis == 0 {
            None
        } else {
            Some(Duration::from_millis(millis))
        };
        match self {
            Self::Plain {
                stream, applied_ms, ..
            } => {
                stream.set_read_timeout(duration)?;
                *applied_ms = millis;
            }
            Self::Tls {
                stream, applied_ms, ..
            } => {
                stream.set_read_timeout(duration)?;
                *applied_ms = millis;
            }
        }
        Ok(())
    }

    fn timeout(&self) -> u64 {
        match self {
            Self::Plain { timeout, .. } | Self::Tls { timeout, .. } => {
                timeout.load(Ordering::Relaxed)
            }
        }
    }
}

impl Read for UpstreamStream {
    fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
        self.apply_timeout()?;
        match self {
            Self::Plain { stream, .. } => stream.read(buf),
            Self::Tls { stream, .. } => stream.read(buf),
        }
    }
}

impl Write for UpstreamStream {
    fn write(&mut self, buf: &[u8]) -> io::Result<usize> {
        match self {
            Self::Plain { stream, .. } => stream.write(buf),
            Self::Tls { stream, .. } => stream.write(buf),
        }
    }

    fn flush(&mut self) -> io::Result<()> {
        match self {
            Self::Plain { stream, .. } => stream.flush(),
            Self::Tls { stream, .. } => stream.flush(),
        }
    }
}

/// TLS 1.3 record stream over [`RawTcpStream`].
struct TlsClientStream {
    raw: RawTcpStream,
    client: Tls13Client,
    pending: Vec<u8>,
}

impl TlsClientStream {
    fn connect(
        mut raw: RawTcpStream,
        server_name: &str,
        timeout: Duration,
        rng: Box<dyn Random32>,
    ) -> io::Result<Self> {
        set_recv_timeout(raw.as_raw_fd(), Some(timeout))?;
        let config = Tls13ClientConfig::new(server_name, rng)
            .with_alpn(&[b"http/1.1"])
            .with_cert_policy(ServerCertPolicy::Skip);
        let mut client = Tls13Client::new(config);
        let flight = client.start().map_err(tls_error)?;
        raw.write_all(&flight)?;

        let mut buffer = [0u8; 16 * 1024];
        while !client.is_connected() {
            let read = raw.read(&mut buffer)?;
            if read == 0 {
                return Err(io::Error::new(
                    io::ErrorKind::UnexpectedEof,
                    "TLS handshake ended early",
                ));
            }
            let outgoing = client.advance(&buffer[..read]).map_err(tls_error)?;
            if !outgoing.is_empty() {
                raw.write_all(&outgoing)?;
                raw.flush()?;
            }
        }

        Ok(Self {
            raw,
            client,
            pending: Vec::new(),
        })
    }

    fn set_read_timeout(&self, timeout: Option<Duration>) -> io::Result<()> {
        set_recv_timeout(self.raw.as_raw_fd(), timeout)
    }
}

impl Read for TlsClientStream {
    fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
        if !self.pending.is_empty() {
            let count = buf.len().min(self.pending.len());
            buf[..count].copy_from_slice(&self.pending[..count]);
            self.pending.drain(..count);
            return Ok(count);
        }

        let mut raw_buffer = [0u8; 16 * 1024];
        loop {
            let data = self.client.take_application_data();
            if !data.is_empty() {
                let count = buf.len().min(data.len());
                buf[..count].copy_from_slice(&data[..count]);
                if data.len() > count {
                    self.pending.extend_from_slice(&data[count..]);
                }
                return Ok(count);
            }
            let read = self.raw.read(&mut raw_buffer)?;
            if read == 0 {
                return Ok(0);
            }
            let outgoing = self
                .client
                .advance(&raw_buffer[..read])
                .map_err(tls_error)?;
            if !outgoing.is_empty() {
                self.raw.write_all(&outgoing)?;
                self.raw.flush()?;
            }
        }
    }
}

impl Write for TlsClientStream {
    fn write(&mut self, buf: &[u8]) -> io::Result<usize> {
        let records = self.client.send_application_data(buf).map_err(tls_error)?;
        self.raw.write_all(&records)?;
        Ok(buf.len())
    }

    fn flush(&mut self) -> io::Result<()> {
        self.raw.flush()
    }
}

fn tls_error(error: crate::tls13::TlsError) -> io::Error {
    io::Error::other(format!("TLS: {error}"))
}

fn set_recv_timeout(fd: i32, timeout: Option<Duration>) -> io::Result<()> {
    let value = timeout.map_or(
        libc::timeval {
            tv_sec: 0,
            tv_usec: 0,
        },
        |duration| libc::timeval {
            tv_sec: duration.as_secs() as libc::time_t,
            tv_usec: duration.subsec_micros() as libc::suseconds_t,
        },
    );
    // SAFETY: `fd` is an open socket owned by the caller and `value` is a live
    // `timeval` for the duration of the call.
    let result = unsafe {
        libc::setsockopt(
            fd,
            libc::SOL_SOCKET,
            libc::SO_RCVTIMEO,
            (&value as *const libc::timeval).cast(),
            size_of::<libc::timeval>() as libc::socklen_t,
        )
    };
    if result < 0 {
        Err(io::Error::last_os_error())
    } else {
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::proto::MtProtoConstants;

    #[test]
    fn human_bytes_matches_the_log_shape() {
        assert_eq!(human_bytes(0), "0B");
        assert_eq!(human_bytes(1023), "1023B");
        assert_eq!(human_bytes(1536), "1.5KB");
        assert_eq!(human_bytes(2 * 1024 * 1024), "2.0MB");
    }

    #[test]
    fn known_protocol_tags_are_all_splitter_compatible() {
        for tag in [
            MtProtoConstants::PROTO_ABRIDGED,
            MtProtoConstants::PROTO_INTERMEDIATE,
            MtProtoConstants::PROTO_SECURE,
        ] {
            let mut splitter = MsgSplitter::new(tag);
            assert!(splitter.split(&[], &[]).unwrap().is_empty());
        }
    }
}
