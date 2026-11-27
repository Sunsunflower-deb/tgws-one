//! The bidirectional relay with re-encryption.
//!
//! This is the port of `RelayBridge.WebSocketAsync` and `TcpAsync`: client
//! ciphertext is decrypted with the client key, re-encrypted with the relay
//! key and sent on; replies travel back the other way.  [`super::routes`] picks
//! the outbound route, so a failed `ws` attempt moves on to `ws-fronted`,
//! `cf-worker`, `cf-proxy` and finally direct TCP, matching
//! `ProxyEngine.FallbackAsync`.
//!
//! The C# versions run the two directions as two async tasks.  This crate is
//! synchronous, so the two directions share one poll loop with a short socket
//! read timeout (20 ms).  That is the documented behavioural difference:
//! latency instead of zero-copy wakeups, but the same bytes, re-encryption
//! order and close rules.

use std::io::{self, Read, Write};
use std::net::TcpStream;
use std::sync::atomic::{AtomicBool, Ordering};
use std::time::Instant;

use crate::mtproto::MsgSplitter;
use crate::proto::{CryptoContext, Obfuscated2};
use crate::tls::client_hello::Random32;
use crate::ws::client::Message;

use super::OsRandom;
use super::client_handshake::{ClientConnection, ClientHandshake};
use super::config::{POLL_INTERVAL, RelayConfig};
use super::engine::{EngineLog, EngineStats};
use super::pool::WsPool;
use super::routes::{self, Connected, RouteContext, Upstream, WsConnection};

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
    pool: Option<&WsPool<WsConnection>>,
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

    let plan = routes::plan(config, handshake);
    let route_context = RouteContext {
        config,
        handshake,
        pool,
    };
    let mut last_error = String::new();

    for route in plan {
        let started = Instant::now();
        match routes::connect(route, &route_context) {
            Ok(Connected {
                upstream: Upstream::Ws(mut socket),
                route: actual,
            }) => {
                log.info(&routes::egress_line(actual, "ok", started.elapsed()));
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
                    "DC{}{} -> WS [{}]",
                    handshake.dc,
                    if handshake.is_media { " media" } else { "" },
                    actual.as_str()
                ));
                return relay_websocket(client, socket, context, handshake, stats, log, stopping);
            }
            Ok(Connected {
                upstream: Upstream::Tcp(remote),
                route: actual,
            }) => {
                log.info(&routes::egress_line(actual, "ok", started.elapsed()));
                return relay_tcp(
                    client, remote, relay_init, context, handshake, stats, log, stopping,
                );
            }
            Err(error) => {
                log.info(&routes::egress_line(route, "error", started.elapsed()));
                log.warn(&format!(
                    "DC{}{} route {} failed: {error}",
                    handshake.dc,
                    if handshake.is_media { " media" } else { "" },
                    route.as_str()
                ));
                last_error = error;
            }
        }
    }

    log.warn(&format!(
        "DC{} no route available{}",
        handshake.dc,
        if last_error.is_empty() {
            String::new()
        } else {
            format!(": {last_error}")
        }
    ));
    SessionOutcome {
        route: SessionRoute::None,
        up_bytes: 0,
        down_bytes: 0,
    }
}

fn relay_websocket(
    client: &mut ClientConnection,
    mut socket: WsConnection,
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
            Err(error) if routes::is_timeout(&error) => {}
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
            Err(error) if routes::ws_timeout(&error) => {}
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
    socket: &mut WsConnection,
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
fn relay_tcp(
    client: &mut ClientConnection,
    mut remote: TcpStream,
    relay_init: Vec<u8>,
    mut context: CryptoContext,
    handshake: &ClientHandshake,
    stats: &EngineStats,
    log: &EngineLog,
    stopping: &AtomicBool,
) -> SessionOutcome {
    let peer = remote
        .peer_addr()
        .map_or_else(|_| "?".to_string(), |address| address.to_string());
    log.info(&format!(
        "DC{}{} TCP fallback to {peer}",
        handshake.dc,
        if handshake.is_media { " media" } else { "" }
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
            Err(error) if routes::is_timeout(&error) => {}
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
            Err(error) if routes::is_timeout(&error) => {}
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

fn human_bytes(bytes: u64) -> String {
    if bytes < 1024 {
        format!("{bytes}B")
    } else if bytes < 1024 * 1024 {
        format!("{:.1}KB", bytes as f64 / 1024.0)
    } else {
        format!("{:.1}MB", bytes as f64 / (1024.0 * 1024.0))
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
