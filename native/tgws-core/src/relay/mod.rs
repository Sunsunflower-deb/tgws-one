//! The synchronous MTProto-over-WebSocket relay core.
//!
//! The module structure follows the C# `TgwsOne.Engine.Relay` and
//! `TgwsOne.Engine.Transport` namespaces: [`config`] mirrors `EngineConfig`,
//! [`client_handshake`] is the top of `ProxyEngine.HandleClientAsync`,
//! [`routes`] is the `ws`/`ws-fronted`/`cf-worker`/`cf-proxy`/TCP order,
//! [`pool`] is the warm `WebSocketPool`, [`session`] is `RelayBridge`, and
//! [`engine`] is the accept loop plus `EngineStats`.
//!
//! Everything runs on blocking sockets and `std::thread`; there is no async
//! runtime in this crate.

pub mod client_handshake;
pub mod config;
pub mod engine;
pub mod pool;
pub mod routes;
pub mod session;

use std::fs::File;
use std::io::{self, Read};

use crate::tls::client_hello::Random32;

pub use client_handshake::{ClientConnection, ClientHandshake, HandshakeError};
pub use config::{LogLevel, RelayConfig};
pub use engine::{Engine, EngineStats, EngineStatsSnapshot};
pub use pool::{ManualTimeProvider, PoolKey, SystemTimeProvider, TimeProvider, WsPool};
pub use routes::{Route, WsConnection};
pub use session::{SessionOutcome, SessionRoute};

/// A `/dev/urandom`-backed [`Random32`], used where the C# engine uses
/// `RandomNumberGenerator`.
pub(crate) struct OsRandom {
    source: File,
}

impl OsRandom {
    pub(crate) fn new() -> io::Result<Self> {
        Ok(Self {
            source: File::open("/dev/urandom")?,
        })
    }
}

impl Random32 for OsRandom {
    fn next_u32(&mut self) -> u32 {
        let mut bytes = [0u8; 4];
        self.fill_bytes(&mut bytes);
        u32::from_le_bytes(bytes)
    }

    fn fill_bytes(&mut self, dst: &mut [u8]) {
        // `File::read_exact` is the only fallible path; on a healthy
        // `/dev/urandom` it never fails, and a panic is better than silently
        // handing out predictable bytes where keys are expected.
        self.source
            .read_exact(dst)
            .expect("/dev/urandom must provide random bytes");
    }
}
