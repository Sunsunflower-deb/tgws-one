//! Own TLS 1.3 client: handshake messages, key schedule and record primitives.
//!
//! This module is the TLS 1.3 client.  It provides:
//!
//! * [`messages`] parses and builds the structural handshake messages
//!   (RFC 8446, section 4) without performing any cryptographic verification.
//! * [`keyschedule`] implements the TLS 1.3 key schedule (RFC 8446, section
//!   7.1) over SHA-256, including HKDF-Expand-Label and Derive-Secret.
//! * [`ciphers`] wraps AES-128-GCM and ChaCha20-Poly1305 with the TLS record
//!   nonce construction (RFC 8446, section 5.2).
//! * [`kx`] provides X25519 key pairs and the Diffie-Hellman operation used by
//!   the `key_share` extension.
//! * [`record_protection`] frames and protects records with the sequence
//!   counter and optional padding (RFC 8446, section 5.2).
//! * [`handshake`] is the client state machine that ties the pieces together.
//!
//! The cryptography lives in audited RustCrypto crates.  "Own stack" here means
//! that TgwsOne controls the handshake transcript, the wire bytes and the
//! fingerprints instead of delegating to .NET `SslStream`; it does not mean
//! re-implementing AES or X25519.
//!
//! Deliberately absent (RT7c): certificate-chain and signature verification,
//! session resumption and 0-RTT.

use core::fmt;

pub mod ciphers;
pub mod handshake;
pub mod keyschedule;
pub mod kx;
pub mod messages;
pub mod record_protection;

pub use handshake::{
    CertVerifier, CertVerifyContext, ServerCertPolicy, SkipVerifier, Tls13Client, Tls13ClientConfig,
};
pub use record_protection::{RecordProtector, RecordUnprotector};

/// Errors produced by the TLS 1.3 building blocks.
///
/// The enum is hand-written instead of using `thiserror` to keep the crate's
/// dependency set small.  It covers wire-format mistakes, unsupported
/// parameters and cryptographic-operation failures.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum TlsError {
    /// The input ends before a declared field or body is complete.
    ShortInput,
    /// A handshake message has an unexpected type.
    InvalidMessageType,
    /// A length field does not match the available bytes or exceeds its limit.
    LengthMismatch,
    /// An extensions block or one of its entries is malformed.
    MalformedExtensions,
    /// A TLS version other than TLS 1.3 was selected.
    UnsupportedVersion(u16),
    /// A cipher suite without a TLS 1.3 implementation was selected.
    UnsupportedCipherSuite(u16),
    /// A key-exchange group other than X25519 was selected.
    UnsupportedGroup(u16),
    /// A key has the wrong length for the requested cipher suite.
    InvalidKeyLength { expected: usize, actual: usize },
    /// An AEAD nonce/IV does not have the TLS 1.3 length of 12 bytes.
    InvalidNonceLength(usize),
    /// HKDF-Expand rejected the requested output length.
    HkdfExpand,
    /// AEAD encryption or authentication failed.
    AeadFailure,
    /// The peer's key share produced an all-zero X25519 shared secret.
    NonContributoryKeyShare,
    /// A `KeyUpdate` message carried an invalid `request_update` value.
    InvalidKeyUpdate,
    /// An alert level is neither warning nor fatal.
    InvalidAlertLevel(u8),
    /// A traffic secret was requested before its predecessor was derived.
    SecretNotDerived,
}

impl fmt::Display for TlsError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::ShortInput => f.write_str("input is shorter than the declared TLS structure"),
            Self::InvalidMessageType => f.write_str("unexpected TLS handshake message type"),
            Self::LengthMismatch => f.write_str("TLS length field does not match its container"),
            Self::MalformedExtensions => f.write_str("malformed TLS extensions block"),
            Self::UnsupportedVersion(version) => {
                write!(f, "unsupported TLS version 0x{version:04x}")
            }
            Self::UnsupportedCipherSuite(suite) => {
                write!(f, "unsupported TLS cipher suite 0x{suite:04x}")
            }
            Self::UnsupportedGroup(group) => {
                write!(f, "unsupported key-exchange group 0x{group:04x}")
            }
            Self::InvalidKeyLength { expected, actual } => {
                write!(f, "invalid key length: expected {expected}, got {actual}")
            }
            Self::InvalidNonceLength(length) => {
                write!(f, "invalid AEAD nonce length: expected 12, got {length}")
            }
            Self::HkdfExpand => f.write_str("HKDF-Expand rejected the output length"),
            Self::AeadFailure => f.write_str("AEAD authentication failed"),
            Self::NonContributoryKeyShare => {
                f.write_str("peer key share produced an all-zero shared secret")
            }
            Self::InvalidKeyUpdate => f.write_str("invalid KeyUpdate request_update value"),
            Self::InvalidAlertLevel(level) => write!(f, "invalid TLS alert level {level}"),
            Self::SecretNotDerived => f.write_str("TLS traffic secret was not derived yet"),
        }
    }
}

impl std::error::Error for TlsError {}
