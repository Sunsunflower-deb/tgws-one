//! Outbound route selection, in the C# engine's order.
//!
//! A client tries `ws` first (the warm pool when one is available), then the
//! same Telegram endpoint with the masking site as its TLS server name
//! (`ws-fronted`), then the configured Cloudflare Worker (`cf-worker`), then
//! the Cloudflare proxy (`cf-proxy`), and finally plain TCP to the DC.  The
//! first successful route wins; a failure simply moves on to the next one.
//!
//! This is the synchronous counterpart of `ProxyEngine.HandleClientAsync` plus
//! `WebSocketPool.ConnectOneAsync`, `CfWorkerFallbackAsync` and
//! `CfProxyFallbackAsync`.  HTTP/2 media is deliberately absent (a separate
//! ticket) and the CF-proxy domain order is fixed, with no `DomainBalancer`
//! shuffle.

use std::io::{self, Read, Write};
use std::mem::size_of;
use std::net::{TcpStream, ToSocketAddrs};
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::Duration;

use crate::tcp::RawTcpStream;
use crate::tls::client_hello::Random32;
use crate::tls13::{ServerCertPolicy, Tls13Client, Tls13ClientConfig};
use crate::ws::WsError;
use crate::ws::client::{HandshakeConfig, WebSocket};

use super::OsRandom;
use super::client_handshake::ClientHandshake;
use super::config::{DEFAULT_WS_PORT, POLL_INTERVAL, RelayConfig};
use super::pool::{PoolKey, WsPool};

/// One outbound way to reach Telegram, tried from left to right.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Route {
    /// Telegram's own `kws<dc>` WebSocket, through the warm pool.
    Ws,
    /// The same endpoint with the masking domain as the TLS server name.
    WsFronted,
    /// A user-run Cloudflare Worker (`/apiws?dst=...&dc=...`).
    CfWorker,
    /// `kws<dc>.<cloudflare-domain>` behind Cloudflare.
    CfProxy,
    /// Direct MTProto TCP to the data centre.
    TcpFallback,
}

impl Route {
    /// The label used in the `EGRESS route=...` log line.
    #[must_use]
    pub fn as_str(self) -> &'static str {
        match self {
            Self::Ws => "ws",
            Self::WsFronted => "ws-fronted",
            Self::CfWorker => "cf-worker",
            Self::CfProxy => "cf-proxy",
            Self::TcpFallback => "tcp",
        }
    }
}

/// A connected WebSocket ready for the relay loop.
pub type WsConnection = WebSocket<UpstreamStream>;

/// What a route produced.
pub enum Upstream {
    /// A WebSocket, over TLS or plain TCP.
    Ws(WsConnection),
    /// A direct MTProto TCP stream that still needs the relay init.
    Tcp(TcpStream),
}

/// A successful route and the actual route label that made it.
pub struct Connected {
    /// The connected transport.
    pub upstream: Upstream,
    /// `ws` or `ws-fronted` when the pool chose the variant.
    pub route: Route,
}

/// Everything a route needs from the session.
pub struct RouteContext<'a> {
    /// Runtime configuration.
    pub config: &'a RelayConfig,
    /// The decoded client handshake.
    pub handshake: &'a ClientHandshake,
    /// The warm pool, present when `poolsize > 0`.
    pub pool: Option<&'a WsPool<WsConnection>>,
}

/// The ordered route plan for this connection.
#[must_use]
pub fn plan(config: &RelayConfig, handshake: &ClientHandshake) -> Vec<Route> {
    let mut routes = Vec::new();
    let has_ws_target = config.dc_redirects.contains_key(&handshake.dc);

    if config.pool_size > 0 && has_ws_target {
        routes.push(Route::Ws);
        if !config.fake_tls_domain.is_empty() {
            routes.push(Route::WsFronted);
        }
    }

    let fallback = config.fallback_host(handshake.dc, handshake.is_test_dc);
    if config.worker_url.is_some() && fallback.is_some() {
        routes.push(Route::CfWorker);
    }
    if config.fallback_cf_proxy && !handshake.is_test_dc && !config.cf_proxy_domains.is_empty() {
        routes.push(Route::CfProxy);
    }
    if fallback.is_some() {
        routes.push(Route::TcpFallback);
    }
    routes
}

/// Connects one route.
///
/// `Err` means this route failed; the caller logs it and tries the next one.
///
/// # Errors
///
/// Returns the last connector error, for example a refused TCP connection or
/// a non-101 WebSocket answer.
pub fn connect(route: Route, context: &RouteContext<'_>) -> Result<Connected, String> {
    match route {
        Route::Ws => {
            if let Some(pool) = context.pool {
                let key = PoolKey::new(
                    context.handshake.dc,
                    context.handshake.is_media,
                    context.handshake.is_test_dc,
                );
                pool.get(&key)
                    .map(|(socket, route)| Connected {
                        upstream: Upstream::Ws(socket),
                        route,
                    })
                    .ok_or_else(|| format!("DC{}: no warm WS connection", context.handshake.dc))
            } else {
                connect_ws(context).map(|socket| Connected {
                    upstream: Upstream::Ws(socket),
                    route: Route::Ws,
                })
            }
        }
        Route::WsFronted => {
            if context.pool.is_some() {
                // The pool's `ws` attempt already tried the fronted variant.
                return Err("ws-fronted already attempted by the WS pool".to_string());
            }
            connect_ws_fronted(context).map(|socket| Connected {
                upstream: Upstream::Ws(socket),
                route: Route::WsFronted,
            })
        }
        Route::CfWorker => connect_cf_worker(context).map(|socket| Connected {
            upstream: Upstream::Ws(socket),
            route: Route::CfWorker,
        }),
        Route::CfProxy => connect_cf_proxy(context).map(|socket| Connected {
            upstream: Upstream::Ws(socket),
            route: Route::CfProxy,
        }),
        Route::TcpFallback => connect_tcp(context).map(|stream| Connected {
            upstream: Upstream::Tcp(stream),
            route: Route::TcpFallback,
        }),
    }
}

/// Runs the routes in order until one succeeds.
///
/// This is the seam the ordering tests use with stand-in route functions.
pub fn first_success<T, E>(
    routes: &[Route],
    mut attempt: impl FnMut(Route) -> Result<T, E>,
) -> Option<(Route, T)> {
    for &route in routes {
        if let Ok(value) = attempt(route) {
            return Some((route, value));
        }
    }
    None
}

/// The `EGRESS` line shape used by the C# engine.
#[must_use]
pub fn egress_line(route: Route, result: &str, elapsed: Duration) -> String {
    format!(
        "EGRESS route={} result={} ms={}",
        route.as_str(),
        result,
        elapsed.as_millis()
    )
}

/// Builds a warm connection for a pool bucket: `ws`, then `ws-fronted`.
///
/// # Errors
///
/// Returns the last error when neither variant could connect.
pub fn connect_warm(config: &RelayConfig, key: &PoolKey) -> Result<(WsConnection, Route), String> {
    let Some(target) = config.dc_redirects.get(&key.dc) else {
        return Err(format!("DC{} has no WS target", key.dc));
    };
    let path = config.ws_path_for(key.is_test_dc);
    let mut last_error = String::new();

    for domain in config.ws_domains(key.dc, key.is_media) {
        match connect_candidate(
            config,
            target,
            &domain,
            None,
            path,
            config.ws_port,
            config.ws_secure,
        ) {
            Ok(socket) => return Ok((socket, Route::Ws)),
            Err(error) => last_error = error,
        }
        if !config.fake_tls_domain.is_empty() {
            match connect_candidate(
                config,
                target,
                &domain,
                Some(&config.fake_tls_domain),
                path,
                config.ws_port,
                config.ws_secure,
            ) {
                Ok(socket) => return Ok((socket, Route::WsFronted)),
                Err(error) => last_error = error,
            }
        }
    }
    Err(last_error)
}

fn connect_ws(context: &RouteContext<'_>) -> Result<WsConnection, String> {
    let config = context.config;
    let handshake = context.handshake;
    let Some(target) = config.dc_redirects.get(&handshake.dc) else {
        return Err(format!("DC{} has no WS target", handshake.dc));
    };
    let path = config.ws_path_for(handshake.is_test_dc);
    let mut last_error = String::new();
    for domain in config.ws_domains(handshake.dc, handshake.is_media) {
        match connect_candidate(
            config,
            target,
            &domain,
            None,
            path,
            config.ws_port,
            config.ws_secure,
        ) {
            Ok(socket) => return Ok(socket),
            Err(error) => last_error = error,
        }
    }
    Err(last_error)
}

fn connect_ws_fronted(context: &RouteContext<'_>) -> Result<WsConnection, String> {
    let config = context.config;
    let handshake = context.handshake;
    let Some(target) = config.dc_redirects.get(&handshake.dc) else {
        return Err(format!("DC{} has no WS target", handshake.dc));
    };
    if config.fake_tls_domain.is_empty() {
        return Err("no masking domain configured".to_string());
    }
    let path = config.ws_path_for(handshake.is_test_dc);
    let mut last_error = String::new();
    for domain in config.ws_domains(handshake.dc, handshake.is_media) {
        match connect_candidate(
            config,
            target,
            &domain,
            Some(&config.fake_tls_domain),
            path,
            config.ws_port,
            config.ws_secure,
        ) {
            Ok(socket) => return Ok(socket),
            Err(error) => last_error = error,
        }
    }
    Err(last_error)
}

fn connect_cf_worker(context: &RouteContext<'_>) -> Result<WsConnection, String> {
    let config = context.config;
    let handshake = context.handshake;
    let Some(url) = config.worker_url.as_deref() else {
        return Err("no CF worker configured".to_string());
    };
    let Some(destination) = config.fallback_host(handshake.dc, handshake.is_test_dc) else {
        return Err(format!("DC{} has no worker destination", handshake.dc));
    };
    let endpoint = WorkerEndpoint::parse(url, config.ws_port)?;
    let path = worker_path(handshake.dc, &destination);
    connect_candidate(
        config,
        &endpoint.host,
        &endpoint.host,
        None,
        &path,
        endpoint.port,
        endpoint.secure,
    )
}

fn connect_cf_proxy(context: &RouteContext<'_>) -> Result<WsConnection, String> {
    let config = context.config;
    let handshake = context.handshake;
    let path = config.ws_path_for(false);
    let mut last_error = String::new();
    for domain in &config.cf_proxy_domains {
        let host = cf_proxy_host(handshake.dc, domain);
        match connect_candidate(
            config,
            &host,
            &host,
            None,
            path,
            config.ws_port,
            config.ws_secure,
        ) {
            Ok(socket) => return Ok(socket),
            Err(error) => last_error = error,
        }
    }
    if last_error.is_empty() {
        Err("no CF proxy domain configured".to_string())
    } else {
        Err(last_error)
    }
}

fn cf_proxy_host(dc: i32, domain: &str) -> String {
    format!("kws{dc}.{domain}")
}

fn connect_tcp(context: &RouteContext<'_>) -> Result<TcpStream, String> {
    let config = context.config;
    let handshake = context.handshake;
    let destination = config
        .fallback_host(handshake.dc, handshake.is_test_dc)
        .ok_or_else(|| format!("DC{} has no fallback", handshake.dc))?;
    connect_with_timeout(&destination, config.tcp_fallback_port, config.read_timeout)
        .map_err(|error| error.to_string())
}

#[allow(clippy::too_many_arguments)]
fn connect_candidate(
    config: &RelayConfig,
    target: &str,
    domain: &str,
    sni: Option<&str>,
    path: &str,
    port: u16,
    secure: bool,
) -> Result<WsConnection, String> {
    let timeout_ms = u64::try_from(config.read_timeout.as_millis()).unwrap_or(u64::MAX);
    let timeout = Arc::new(AtomicU64::new(timeout_ms));
    let control = Arc::clone(&timeout);

    let stream = if secure {
        let raw = RawTcpStream::connect(target, port).map_err(|error| error.to_string())?;
        let rng = OsRandom::new().map_err(|error| error.to_string())?;
        let server_name = sni.unwrap_or(domain);
        let tls = TlsClientStream::connect(raw, server_name, config.read_timeout, Box::new(rng))
            .map_err(|error| error.to_string())?;
        UpstreamStream::Tls {
            stream: Box::new(tls),
            timeout,
            applied_ms: timeout_ms,
        }
    } else {
        let stream = TcpStream::connect((target, port)).map_err(|error| error.to_string())?;
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

    let handshake_config = HandshakeConfig::new(domain.to_string())
        .with_path(path)
        .with_subprotocol("binary");
    let rng = OsRandom::new().map_err(|error| error.to_string())?;
    match WebSocket::connect(stream, &handshake_config, Box::new(rng)) {
        Ok(socket) => {
            let poll = u64::try_from(POLL_INTERVAL.as_millis()).unwrap_or(20);
            control.store(poll, Ordering::Relaxed);
            Ok(socket)
        }
        Err(error) => Err(format!("{domain}: {error}")),
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

/// A Cloudflare Worker URL split into the pieces the WebSocket dial needs.
#[derive(Debug, Clone, PartialEq, Eq)]
struct WorkerEndpoint {
    host: String,
    port: u16,
    secure: bool,
}

impl WorkerEndpoint {
    fn parse(url: &str, ws_port: u16) -> Result<Self, String> {
        let (secure, rest) = if let Some((scheme, rest)) = url.split_once("://") {
            let secure = match scheme.to_ascii_lowercase().as_str() {
                "https" => true,
                "http" => false,
                other => return Err(format!("unsupported worker scheme «{other}»")),
            };
            (secure, rest)
        } else {
            (true, url)
        };
        let authority = rest.split('/').next().unwrap_or(rest);
        if authority.is_empty() {
            return Err("worker URL has no host".to_string());
        }
        let (host, port) = match authority.rsplit_once(':') {
            Some((host, port)) if !host.is_empty() => {
                let port = port
                    .parse::<u16>()
                    .map_err(|_| format!("bad worker port «{port}»"))?;
                (host.to_string(), port)
            }
            _ => {
                let port = if ws_port != DEFAULT_WS_PORT {
                    ws_port
                } else if secure {
                    443
                } else {
                    80
                };
                (authority.to_string(), port)
            }
        };
        Ok(Self { host, port, secure })
    }
}

fn worker_path(dc: i32, destination: &str) -> String {
    format!("/apiws?dst={}&dc={dc}", escape_query(destination))
}

fn escape_query(value: &str) -> String {
    let mut escaped = String::with_capacity(value.len());
    for byte in value.bytes() {
        if byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'_' | b'.' | b'~') {
            escaped.push(char::from(byte));
        } else {
            escaped.push('%');
            escaped.push_str(&format!("{byte:02X}"));
        }
    }
    escaped
}

/// A blocking stream under the WebSocket client with a settable read timeout.
pub enum UpstreamStream {
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
pub struct TlsClientStream {
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

/// Whether an I/O error is just the 20 ms poll timeout.
pub(crate) fn is_timeout(error: &io::Error) -> bool {
    matches!(
        error.kind(),
        io::ErrorKind::WouldBlock | io::ErrorKind::TimedOut | io::ErrorKind::Interrupted
    )
}

/// Whether a WebSocket error wraps the 20 ms poll timeout.
pub(crate) fn ws_timeout(error: &WsError) -> bool {
    matches!(error, WsError::Io(io) if is_timeout(io))
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Read;
    use std::net::TcpListener;
    use std::thread;

    fn handshake(is_test_dc: bool) -> ClientHandshake {
        ClientHandshake {
            dc: 2,
            is_media: false,
            proto_tag: 0xEEEE_EEEE,
            client_prekey_iv: vec![0u8; 48],
            is_test_dc,
            fake_tls: false,
        }
    }

    fn full_config() -> RelayConfig {
        RelayConfig {
            dc_redirects: [(2, "127.0.0.1".to_string())].into(),
            fake_tls_domain: "www.microsoft.com".to_string(),
            cf_proxy_domains: vec!["proxy.example".to_string()],
            worker_url: Some("https://worker.example/apiws".to_string()),
            ..RelayConfig::default()
        }
    }

    #[test]
    fn plan_keeps_the_csharp_route_order() {
        let config = full_config();
        assert_eq!(
            plan(&config, &handshake(false)),
            [
                Route::Ws,
                Route::WsFronted,
                Route::CfWorker,
                Route::CfProxy,
                Route::TcpFallback,
            ]
        );
    }

    #[test]
    fn plan_skips_unconfigured_routes() {
        let config = RelayConfig {
            pool_size: 0,
            ..RelayConfig::default()
        };
        assert_eq!(plan(&config, &handshake(false)), [Route::TcpFallback]);

        let test_dc = full_config();
        let planned = plan(&test_dc, &handshake(true));
        assert!(!planned.contains(&Route::CfProxy));
    }

    #[test]
    fn first_success_moves_past_two_failures() {
        let routes = plan(&full_config(), &handshake(false));
        let mut calls = Vec::new();
        let result = first_success(&routes, |route| {
            calls.push(route);
            if calls.len() < 3 {
                Err("down".to_string())
            } else {
                Ok(calls.len())
            }
        });
        assert_eq!(result, Some((Route::CfWorker, 3)));
        assert_eq!(calls, [Route::Ws, Route::WsFronted, Route::CfWorker]);
    }

    #[test]
    fn cf_worker_url_and_path_match_the_engine() {
        assert_eq!(
            worker_path(2, "149.154.167.51"),
            "/apiws?dst=149.154.167.51&dc=2"
        );
        assert_eq!(escape_query("2001:db8::1"), "2001%3Adb8%3A%3A1");
        assert_eq!(
            WorkerEndpoint::parse("https://worker.example/apiws", DEFAULT_WS_PORT).unwrap(),
            WorkerEndpoint {
                host: "worker.example".to_string(),
                port: 443,
                secure: true,
            }
        );
        assert_eq!(
            WorkerEndpoint::parse("http://127.0.0.1:8080", DEFAULT_WS_PORT).unwrap(),
            WorkerEndpoint {
                host: "127.0.0.1".to_string(),
                port: 8080,
                secure: false,
            }
        );
        assert_eq!(
            WorkerEndpoint::parse("worker.example", 9443).unwrap(),
            WorkerEndpoint {
                host: "worker.example".to_string(),
                port: 9443,
                secure: true,
            }
        );
    }

    #[test]
    fn fronted_route_puts_the_masking_domain_in_the_client_hello() {
        let listener = TcpListener::bind(("127.0.0.1", 0)).expect("bind");
        let port = listener.local_addr().unwrap().port();
        let server = thread::spawn(move || {
            let (mut stream, _) = listener.accept().expect("accept");
            stream
                .set_read_timeout(Some(Duration::from_secs(5)))
                .unwrap();
            let mut header = [0u8; 5];
            stream.read_exact(&mut header).unwrap();
            let length = usize::from(u16::from_be_bytes([header[3], header[4]]));
            let mut record = vec![0u8; 5 + length];
            record[..5].copy_from_slice(&header);
            stream.read_exact(&mut record[5..]).unwrap();
            let layout = crate::tls::parse::parse_client_hello(&record).expect("ClientHello");
            layout
                .sni(&record)
                .map(|sni| String::from_utf8_lossy(sni).into_owned())
        });

        let mut config = full_config();
        config.ws_port = port;
        config.ws_secure = true;
        config.read_timeout = Duration::from_secs(2);
        let context = RouteContext {
            config: &config,
            handshake: &handshake(false),
            pool: None,
        };
        // The server stops after the ClientHello, so the route must fail.
        assert!(connect_ws_fronted(&context).is_err());
        assert_eq!(server.join().unwrap().as_deref(), Some("www.microsoft.com"));
    }

    #[test]
    fn cf_proxy_hosts_keep_the_dc_number() {
        assert_eq!(cf_proxy_host(2, "proxy.example"), "kws2.proxy.example");
        assert_eq!(cf_proxy_host(203, "proxy.example"), "kws203.proxy.example");
    }

    #[test]
    fn cf_worker_dials_the_worker_url_and_sends_the_dc_path() {
        use crate::ws::client::compute_accept;

        let listener = TcpListener::bind(("127.0.0.1", 0)).expect("bind");
        let port = listener.local_addr().unwrap().port();
        let server = thread::spawn(move || {
            let (mut stream, _) = listener.accept().expect("accept");
            stream
                .set_read_timeout(Some(Duration::from_secs(5)))
                .unwrap();
            let mut buffer = Vec::new();
            let mut chunk = [0u8; 1024];
            loop {
                let read = stream.read(&mut chunk).expect("read request");
                assert!(read > 0, "worker closed before the request head");
                buffer.extend_from_slice(&chunk[..read]);
                if buffer.windows(4).any(|window| window == b"\r\n\r\n") {
                    break;
                }
            }
            let request = String::from_utf8(buffer).expect("request is UTF-8");
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
            request
        });

        let mut config = full_config();
        config.worker_url = Some(format!("http://127.0.0.1:{port}/ignored"));
        config.tcp_fallback_host_override = Some("149.154.167.51".to_string());
        config.read_timeout = Duration::from_secs(2);
        let context = RouteContext {
            config: &config,
            handshake: &handshake(false),
            pool: None,
        };
        let socket = connect_cf_worker(&context).expect("worker handshake");
        drop(socket);
        let request = server.join().unwrap();
        assert!(
            request.starts_with("GET /apiws?dst=149.154.167.51&dc=2 HTTP/1.1\r\n"),
            "{request}"
        );
        assert!(request.contains("Host: 127.0.0.1\r\n"), "{request}");
    }
}
