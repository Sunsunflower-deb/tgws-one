//! MTProto transport framing, ported from `src/TgwsOne.Engine/MtProto/` and
//! `src/TgwsOne.Engine/Transport/FakeTls.cs`.
//!
//! * [`msg_splitter`] cuts an obfuscated2 stream into transport packets.
//! * [`packet_codec`] reads and writes one whole packet for the HTTP/2 media
//!   route, where a request carries exactly one packet.
//! * [`fake_tls`] is the masking handshake of the `ee` secret. It lives here
//!   rather than in `tls/` because it is not TLS: it only shapes records that
//!   look like TLS. Keeping it in this module also means `lib.rs` is the only
//!   existing file that needs a new `mod` line.
//!
//! Only the byte-level pieces are ported. The C# versions read from a `Stream`
//! and use `RandomNumberGenerator`/`Random.Shared`; the Rust versions take
//! slices and a caller-supplied RNG, so the transport and the entropy source
//! stay in the platform shell.

pub mod fake_tls;
pub mod msg_splitter;
pub mod packet_codec;

pub use fake_tls::{
    ALERT_HANDSHAKE_FAILURE, ALERT_UNEXPECTED_MESSAGE, APP_DATA_FALLBACK_HIGH,
    APP_DATA_FALLBACK_LOW, APP_DATA_MIN, ClientHelloInfo, FakeTls, FakeTlsClientHello,
    FakeTlsError, MAX_RECORD_PAYLOAD, RECORD_ALERT, RECORD_APP_DATA, RECORD_CCS, RECORD_HANDSHAKE,
    TIMESTAMP_TOLERANCE,
};
pub use msg_splitter::{MsgSplitter, SplitError};
pub use packet_codec::{PacketCodec, PacketError};
