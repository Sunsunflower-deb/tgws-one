//! TLS record framing.
//!
//! This module is a skeleton: it knows how the five-byte record header is laid
//! out (RFC 8446, section 5.1) and how to re-fragment an already encoded
//! ClientHello, but it deliberately does not implement record protection.  Key
//! exchange, AEAD and the TLS 1.3 encrypted record layout are future work.
//!
//! Re-fragmentation is the building block for the future `tlsrec` strategy:
//! splitting one handshake record into several records changes what a
//! middlebox sees in the first packet without changing the plaintext that the
//! peer eventually reassembles.

use core::fmt;

/// TLS record content type for change_cipher_spec (RFC 8446, section 5.1).
pub const CONTENT_TYPE_CHANGE_CIPHER_SPEC: u8 = 0x14;
/// TLS record content type for alert (RFC 8446, section 5.1).
pub const CONTENT_TYPE_ALERT: u8 = 0x15;
/// TLS record content type for handshake (RFC 8446, section 5.1).
pub const CONTENT_TYPE_HANDSHAKE: u8 = 0x16;
/// TLS record content type for application_data (RFC 8446, section 5.1).
pub const CONTENT_TYPE_APPLICATION_DATA: u8 = 0x17;

/// Legacy record layer version.
///
/// Chrome sends a ClientHello record with `0x0301` even when the handshake
/// negotiates TLS 1.3; receivers ignore this field for compatibility.
pub const LEGACY_RECORD_VERSION: [u8; 2] = [0x03, 0x01];

/// Maximum TLSPlaintext fragment length (RFC 8446, section 5.1).
pub const MAX_PLAINTEXT: usize = 1 << 14;

/// Maximum TLSCiphertext fragment length.
///
/// TLS 1.2 and earlier allow up to `2^14` bytes of plaintext plus a MAC and
/// padding.  TLS 1.3 permits content type and padding to add one byte for
/// application_data records, so `2^14 + 1` is the useful upper bound for
/// encrypted records.  Kept separate from [`MAX_PLAINTEXT`] because a plaintext
/// handshake record must still obey the smaller limit.
pub const MAX_CIPHERTEXT: usize = MAX_PLAINTEXT + 1;

/// Reasons why a record cannot be encoded or re-fragmented.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum RecordError {
    /// The input ends before the declared record header or payload.
    ShortInput,
    /// The input is not a handshake record, so it is not a ClientHello flight.
    NotHandshakeRecord,
    /// A split boundary is zero, out of range, or not strictly increasing.
    InvalidBoundary,
    /// No boundary was supplied, so there is nothing to split.
    EmptySplit,
}

impl fmt::Display for RecordError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let message = match self {
            Self::ShortInput => "input is shorter than the declared TLS record",
            Self::NotHandshakeRecord => "record is not a TLS handshake record",
            Self::InvalidBoundary => "split boundary is out of range or not increasing",
            Self::EmptySplit => "at least one split boundary is required",
        };
        f.write_str(message)
    }
}

impl std::error::Error for RecordError {}

/// Encodes one TLS record with [`LEGACY_RECORD_VERSION`].
///
/// The record header stores a 16-bit length, so payloads longer than
/// `u16::MAX` are clipped to keep the returned frame self-consistent.  Callers
/// that need to send more than [`MAX_PLAINTEXT`] bytes must split the payload
/// first; [`split_record`] does exactly that for a ClientHello.
#[must_use]
pub fn encode_record(content_type: u8, payload: &[u8]) -> Vec<u8> {
    let len = payload.len().min(u16::MAX as usize);
    let mut out = Vec::with_capacity(5 + len);
    push_record(
        &mut out,
        content_type,
        LEGACY_RECORD_VERSION,
        &payload[..len],
    );
    out
}

/// Re-fragments a single handshake record into two or more records.
///
/// `boundaries` are offsets into the plaintext (the bytes after the five-byte
/// record header) at which a new record starts.  They must be strictly
/// increasing and lie in `1..plaintext_len`; the plaintext itself is preserved
/// byte for byte.  The record version of the input is reused so the output looks
/// like the original first flight apart from the fragmentation.
///
/// The first output record will claim a shorter length than the handshake
/// message needs.  Consequently [`crate::tls::parse::parse_client_hello`] on
/// that record alone returns `HandshakeNotInFirstRecord`, which is the E2.1
/// rule: a parser must not accept a ClientHello spread across records.
///
/// # Errors
///
/// Returns [`RecordError`] when the input is truncated, is not a handshake
/// record, contains no boundary, or a boundary is invalid.
pub fn split_record(record: &[u8], boundaries: &[usize]) -> Result<Vec<u8>, RecordError> {
    if record.len() < 5 {
        return Err(RecordError::ShortInput);
    }
    if record[0] != CONTENT_TYPE_HANDSHAKE {
        return Err(RecordError::NotHandshakeRecord);
    }

    let declared = u16::from_be_bytes([record[3], record[4]]) as usize;
    let payload_end = 5usize
        .checked_add(declared)
        .ok_or(RecordError::ShortInput)?;
    if record.len() < payload_end {
        return Err(RecordError::ShortInput);
    }
    let payload = &record[5..payload_end];

    let mut cuts = Vec::with_capacity(boundaries.len() + 2);
    cuts.push(0);
    for &boundary in boundaries {
        if boundary == 0 || boundary >= payload.len() {
            return Err(RecordError::InvalidBoundary);
        }
        if cuts.last().is_some_and(|&last| boundary <= last) {
            return Err(RecordError::InvalidBoundary);
        }
        cuts.push(boundary);
    }
    cuts.push(payload.len());

    if cuts.len() < 3 {
        return Err(RecordError::EmptySplit);
    }

    let version = [record[1], record[2]];
    let extra_headers = (cuts.len() - 2) * 5;
    let mut out = Vec::with_capacity(payload_end + extra_headers);
    for window in cuts.windows(2) {
        push_record(
            &mut out,
            CONTENT_TYPE_HANDSHAKE,
            version,
            &payload[window[0]..window[1]],
        );
    }

    Ok(out)
}

fn push_record(out: &mut Vec<u8>, content_type: u8, version: [u8; 2], payload: &[u8]) {
    let len = payload.len().min(u16::MAX as usize);
    out.push(content_type);
    out.extend_from_slice(&version);
    out.extend_from_slice(&(len as u16).to_be_bytes());
    out.extend_from_slice(&payload[..len]);
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tls::client_hello::ClientHelloBuilder;
    use crate::tls::client_hello::SplitMix64;
    use crate::tls::parse::{DesyncPosition, ParseError, parse_client_hello};

    fn build_flight() -> Vec<u8> {
        let mut rng = SplitMix64::new(9);
        ClientHelloBuilder::new("split.example.com").build(&mut rng)
    }

    fn payload_of(record: &[u8]) -> &[u8] {
        assert!(record.len() >= 5, "record header");
        let len = u16::from_be_bytes([record[3], record[4]]) as usize;
        &record[5..5 + len]
    }

    #[test]
    fn encode_record_has_expected_header() {
        let record = encode_record(CONTENT_TYPE_HANDSHAKE, b"abc");
        assert_eq!(record[0], CONTENT_TYPE_HANDSHAKE);
        assert_eq!(&record[1..3], &LEGACY_RECORD_VERSION);
        assert_eq!(&record[3..5], &3u16.to_be_bytes());
        assert_eq!(&record[5..], b"abc");
    }

    #[test]
    fn split_record_preserves_plaintext_across_records() {
        let flight = build_flight();
        let first_boundary = 16;
        let second_boundary = 64;
        let split = split_record(&flight, &[first_boundary, second_boundary])
            .expect("valid boundaries must split");

        let mut reassembled = Vec::new();
        let mut offset = 0;
        let mut records = 0;
        while offset < split.len() {
            assert_eq!(split[offset], CONTENT_TYPE_HANDSHAKE);
            let len = u16::from_be_bytes([split[offset + 3], split[offset + 4]]) as usize;
            let record = &split[offset..offset + 5 + len];
            reassembled.extend_from_slice(payload_of(record));
            offset += 5 + len;
            records += 1;
        }

        assert_eq!(records, 3);
        assert_eq!(reassembled, payload_of(&flight));
    }

    #[test]
    fn first_split_record_does_not_contain_complete_client_hello() {
        let flight = build_flight();
        let layout = parse_client_hello(&flight).expect("source ClientHello must parse");
        let sni_end = layout.sni_end.expect("source has SNI");

        let boundary = sni_end - 5 - 1;
        let split = split_record(&flight, &[boundary]).expect("split before SNI end");
        let first = &split[..5 + boundary];

        assert_eq!(
            parse_client_hello(first),
            Err(ParseError::HandshakeNotInFirstRecord)
        );
    }

    #[test]
    fn split_record_keeps_sni_addressable_in_first_record_when_it_fits() {
        let flight = build_flight();
        let layout = parse_client_hello(&flight).expect("source ClientHello must parse");
        let sni_end = layout.sni_end.expect("source has SNI");

        let boundary = sni_end - 5 + 1;
        let split = split_record(&flight, &[boundary]).expect("split after SNI");
        let first = &split[..5 + boundary];

        assert_eq!(
            parse_client_hello(first),
            Err(ParseError::HandshakeNotInFirstRecord)
        );
        assert_eq!(first[0], CONTENT_TYPE_HANDSHAKE);
        assert_eq!(
            layout.resolve(&DesyncPosition::SniStart(0), first),
            Some(layout.sni_start.expect("SNI start"))
        );
    }

    #[test]
    fn rejects_non_handshake_input() {
        let mut flight = build_flight();
        flight[0] = CONTENT_TYPE_APPLICATION_DATA;
        assert_eq!(
            split_record(&flight, &[10]),
            Err(RecordError::NotHandshakeRecord)
        );
    }

    #[test]
    fn rejects_invalid_boundaries() {
        let flight = build_flight();
        let payload_len = flight.len() - 5;
        assert_eq!(split_record(&flight, &[]), Err(RecordError::EmptySplit));
        assert_eq!(
            split_record(&flight, &[0]),
            Err(RecordError::InvalidBoundary)
        );
        assert_eq!(
            split_record(&flight, &[payload_len]),
            Err(RecordError::InvalidBoundary)
        );
        assert_eq!(
            split_record(&flight, &[20, 10]),
            Err(RecordError::InvalidBoundary)
        );
    }

    #[test]
    fn rejects_truncated_record() {
        let flight = build_flight();
        assert_eq!(
            split_record(&flight[..4], &[10]),
            Err(RecordError::ShortInput)
        );

        let mut truncated = flight.clone();
        let declared = u16::from_be_bytes([truncated[3], truncated[4]]) as usize;
        truncated.truncate(5 + declared - 1);
        assert_eq!(
            split_record(&truncated, &[10]),
            Err(RecordError::ShortInput)
        );
    }
}
