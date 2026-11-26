//! TLS wire-format helpers.
//!
//! The modules here are split by responsibility:
//!
//! * [`parse`] reads a ClientHello from the first flight without allocating.
//! * [`client_hello`] builds a plausible ClientHello with a Chrome-like
//!   extension fingerprint.
//! * [`record`] encodes and re-fragments TLS records; it intentionally does not
//!   implement record protection (encryption/decryption) yet.

pub mod client_hello;
pub mod parse;
pub mod record;
