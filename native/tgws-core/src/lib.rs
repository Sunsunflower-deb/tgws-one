//! Native core for TgwsOne.
//!
//! The crate is deliberately dependency-light: the only external dependency is
//! `libc`, and the TLS/TCP pieces are written from the RFCs rather than wrapped
//! around an existing stack.  The owner chose this direction on 2026-11-26 so
//! that later work (QUIC/HTTP-3, ECH, uTLS-like fingerprints) can reuse the same
//! transport instead of being constrained by the .NET stack on Android.
//!
//! The crate is a work in progress: only parsing/building of TLS records and a
//! thin blocking TCP wrapper exist today.  Nothing here performs cryptography
//! or key exchange yet.

#![forbid(unsafe_op_in_unsafe_fn)]

pub mod config;
pub mod ffi;
pub mod mtproto;
pub mod net;
pub mod proto;
pub mod settings;
pub mod strategy;
pub mod tcp;
pub mod tls;
pub mod zapret;

pub use ffi::tgws_core_version;
pub use net::telegram_network::{DC_DEFAULT_IPS, DC_TEST_IPS, WS_PATH, WS_PATH_TEST, ws_domains};
pub use proto::{AesCtr, CryptoContext, MtProtoConstants, Obfuscated2};
pub use strategy::{
    AutoHost, Blob, DesyncPart, expand_cf_proxy_hosts, expand_rotation, is_valid_custom_name,
    is_valid_provider, list_providers, provider_rotation, read_auto_state,
};
pub use tcp::RawTcpStream;
pub use tls::client_hello::{
    Chrome120Profile, ClientHelloBuilder, DefaultProfile, FingerprintProfile, Random32, SplitMix64,
    XorShift64Star,
};
pub use tls::parse::{ClientHelloLayout, DesyncPosition, ParseError, parse_client_hello};
pub use tls::record::{
    CONTENT_TYPE_ALERT, CONTENT_TYPE_APPLICATION_DATA, CONTENT_TYPE_CHANGE_CIPHER_SPEC,
    CONTENT_TYPE_HANDSHAKE, LEGACY_RECORD_VERSION, MAX_CIPHERTEXT, MAX_PLAINTEXT, RecordError,
    encode_record, split_record,
};
