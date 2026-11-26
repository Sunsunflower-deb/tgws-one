//! RFC 6455 WebSocket client over the crate's own transport.
//!
//! The module is split the same way as the C# reference implementation
//! (`TgwsOne.Engine.Transport.RawWebSocket`):
//!
//! * [`frame`] encodes and incrementally parses data/control frames.  Client
//!   frames are always masked; mask bytes and payloads come from an injected
//!   [`crate::tls::client_hello::Random32`] source.
//! * [`client`] performs the HTTP upgrade handshake over any `Read + Write`
//!   stream, verifies `Sec-WebSocket-Accept`, and runs the message/close state
//!   machine on top of the frame parser.
//!
//! Deliberately absent (other tickets): TLS wiring (RT7b), `permessage-deflate`
//! and redirect handling.  Pooling and balancing belong to RT9.

use std::fmt;
use std::io;

pub mod client;
pub mod frame;

pub use client::{HandshakeConfig, Message, Stream, WebSocket, next_ping_delay};
pub use frame::{CloseFrame, Endpoint, Frame, FrameParser, OpCode, is_valid_close_code};

/// Errors produced by the WebSocket client and frame codec.
#[derive(Debug)]
pub enum WsError {
    /// The underlying stream failed.
    Io(io::Error),
    /// The stream ended before a complete HTTP response head was read.
    UnexpectedEof,
    /// A frame set one of the RSV bits although no extension was negotiated.
    ReservedBits,
    /// The frame opcode is neither a data nor a control opcode.
    InvalidOpcode(u8),
    /// A client-to-server frame arrived without the mandatory mask bit.
    MissingMask,
    /// A server-to-client frame carried a mask, which RFC 6455 forbids.
    UnexpectedMask,
    /// A control frame had the FIN bit cleared.
    FragmentedControl,
    /// A control frame payload exceeded 125 bytes.
    ControlTooLong,
    /// A length used an extended form although the short form would suffice.
    NonMinimalLength,
    /// A 64-bit length had its most significant bit set.
    UnsupportedLength,
    /// A frame or assembled message exceeds the configured limit.
    PayloadTooLarge {
        /// Declared payload length.
        length: u64,
        /// Maximum accepted length.
        limit: usize,
    },
    /// A close frame had a one-byte payload or an unencodable reason.
    InvalidCloseFrame,
    /// A close frame used a status code that must not appear on the wire.
    InvalidCloseCode(u16),
    /// A text message was not valid UTF-8.
    InvalidUtf8,
    /// A continuation frame arrived without an open fragmented message.
    UnexpectedContinuation,
    /// A new data frame arrived while a fragmented message was still open.
    NestedFragment,
    /// The HTTP header block exceeded its size limit.
    HeaderTooLarge {
        /// Maximum accepted header size.
        limit: usize,
    },
    /// The HTTP response head was not a valid status line plus headers.
    MalformedHandshake,
    /// The server answered with a status other than 101.
    HttpStatus {
        /// Numeric status code.
        code: u16,
        /// The complete status line for diagnostics.
        line: String,
    },
    /// The 101 response did not contain `Sec-WebSocket-Accept`.
    MissingAccept,
    /// `Sec-WebSocket-Accept` did not match the key we sent.
    InvalidAccept,
    /// A configured header name or value would break the HTTP request.
    InvalidHeader,
    /// The WebSocket was already closed.
    Closed,
}

impl fmt::Display for WsError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::Io(error) => write!(f, "websocket I/O error: {error}"),
            Self::UnexpectedEof => f.write_str("unexpected end of stream"),
            Self::ReservedBits => f.write_str("frame uses reserved bits without an extension"),
            Self::InvalidOpcode(opcode) => write!(f, "invalid WebSocket opcode 0x{opcode:x}"),
            Self::MissingMask => f.write_str("client frame is not masked"),
            Self::UnexpectedMask => f.write_str("server frame is masked"),
            Self::FragmentedControl => f.write_str("control frame is fragmented"),
            Self::ControlTooLong => f.write_str("control frame payload exceeds 125 bytes"),
            Self::NonMinimalLength => f.write_str("WebSocket length is not minimally encoded"),
            Self::UnsupportedLength => f.write_str("WebSocket length has the 64-bit sign bit set"),
            Self::PayloadTooLarge { length, limit } => {
                write!(f, "payload length {length} exceeds the limit {limit}")
            }
            Self::InvalidCloseFrame => f.write_str("invalid WebSocket close frame"),
            Self::InvalidCloseCode(code) => {
                write!(f, "close code {code} must not appear on the wire")
            }
            Self::InvalidUtf8 => f.write_str("text message is not valid UTF-8"),
            Self::UnexpectedContinuation => {
                f.write_str("continuation frame without an open message")
            }
            Self::NestedFragment => {
                f.write_str("new data frame while a fragmented message is open")
            }
            Self::HeaderTooLarge { limit } => {
                write!(f, "HTTP header block exceeds {limit} bytes")
            }
            Self::MalformedHandshake => f.write_str("malformed HTTP upgrade response"),
            Self::HttpStatus { code, line } => {
                write!(f, "HTTP {code}: {line}")
            }
            Self::MissingAccept => f.write_str("missing Sec-WebSocket-Accept header"),
            Self::InvalidAccept => f.write_str("Sec-WebSocket-Accept does not match the key"),
            Self::InvalidHeader => f.write_str("invalid HTTP header name or value"),
            Self::Closed => f.write_str("WebSocket is closed"),
        }
    }
}

impl std::error::Error for WsError {
    fn source(&self) -> Option<&(dyn std::error::Error + 'static)> {
        match self {
            Self::Io(error) => Some(error),
            _ => None,
        }
    }
}

impl From<io::Error> for WsError {
    fn from(error: io::Error) -> Self {
        Self::Io(error)
    }
}
