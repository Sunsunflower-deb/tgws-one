//! Protected TLS 1.3 records (RFC 8446, section 5.2).
//!
//! After the `ServerHello` both peers wrap every record in
//! `TLSCiphertext`:
//!
//! ```text
//! TLSCiphertext {
//!     opaque_type = application_data;   // 0x17
//!     legacy_record_version = 0x0303;
//!     uint16 length;                    // ciphertext + tag
//!     AEADEncrypted(TLSInnerPlaintext)
//! }
//!
//! TLSInnerPlaintext {
//!     content;
//!     ContentType type;                 // 0x16, 0x17 or 0x15
//!     zeros[length_of_padding];
//! }
//! ```
//!
//! The AEAD associated data is the five-byte `TLSCiphertext` header, including
//! the length of the encrypted payload.  [`RecordProtector`] and
//! [`RecordUnprotector`] own the 64-bit sequence counter for one direction of
//! one traffic secret; a fresh pair is built for every key change.
//!
//! Padding is expressed as a block size: with `padding_block = 0` records are
//! not padded, and with any other value the encrypted payload length (that is,
//! the record length field) is rounded up to a multiple of the block.  This is
//! the hook later fingerprint profiles use to shape record sizes.

use crate::tls13::TlsError;
use crate::tls13::ciphers::AeadKey;
use crate::tls13::keyschedule::{CipherSuite, TrafficKeys};

/// Content type of every protected TLS 1.3 record.
pub const CONTENT_TYPE_APPLICATION_DATA: u8 = 0x17;

/// Legacy record version used by TLS 1.3 `TLSCiphertext` (RFC 8446, 5.1).
pub const LEGACY_RECORD_VERSION: [u8; 2] = [0x03, 0x03];

/// Length of the cleartext TLS record header.
pub const RECORD_HEADER_LEN: usize = 5;

/// AEAD tag length of both supported cipher suites.
pub const TAG_LEN: usize = 16;

/// Encrypts outgoing TLS 1.3 records with a monotonically increasing sequence.
pub struct RecordProtector {
    key: AeadKey,
    sequence: u64,
    padding_block: usize,
}

impl RecordProtector {
    /// Creates a protector from an already configured AEAD key.
    ///
    /// `padding_block` is the record-length multiple used for padding; `0`
    /// disables padding.
    #[must_use]
    pub fn new(key: AeadKey, padding_block: usize) -> Self {
        Self {
            key,
            sequence: 0,
            padding_block,
        }
    }

    /// Creates a protector from a derived traffic secret.
    ///
    /// # Errors
    ///
    /// Propagates HKDF and key-length errors from [`TrafficKeys::derive`] and
    /// [`AeadKey::from_traffic_keys`].
    pub fn from_traffic_keys(
        keys: &TrafficKeys,
        suite: CipherSuite,
        padding_block: usize,
    ) -> Result<Self, TlsError> {
        Ok(Self::new(
            AeadKey::from_traffic_keys(keys, suite)?,
            padding_block,
        ))
    }

    /// Returns the sequence number the next record will use.
    #[must_use]
    pub fn sequence(&self) -> u64 {
        self.sequence
    }

    /// Wraps `content` into one protected record.
    ///
    /// The returned buffer is the complete record: five-byte header followed by
    /// ciphertext and AEAD tag.  `content_type` is carried inside the encrypted
    /// `TLSInnerPlaintext`, so a `Finished` message uses the handshake content
    /// type while its outer record type is always application data.
    ///
    /// # Errors
    ///
    /// Returns [`TlsError::LengthMismatch`] when the protected record cannot fit
    /// a 16-bit length field, and propagates AEAD failures.
    pub fn protect(&mut self, content_type: u8, content: &[u8]) -> Result<Vec<u8>, TlsError> {
        let plaintext_len = content
            .len()
            .checked_add(1)
            .ok_or(TlsError::LengthMismatch)?;
        let unpadded_len = plaintext_len
            .checked_add(TAG_LEN)
            .ok_or(TlsError::LengthMismatch)?;
        let padding = padding_for(self.padding_block, unpadded_len);
        let record_len = unpadded_len
            .checked_add(padding)
            .ok_or(TlsError::LengthMismatch)?;
        if record_len > u16::MAX as usize {
            return Err(TlsError::LengthMismatch);
        }

        let header = record_header(record_len);
        let mut inner = Vec::with_capacity(plaintext_len + padding);
        inner.extend_from_slice(content);
        inner.push(content_type);
        inner.resize(plaintext_len + padding, 0);

        let ciphertext = self.key.encrypt_with_seq(self.sequence, &header, &inner)?;
        self.sequence = self.sequence.checked_add(1).ok_or(TlsError::AeadFailure)?;

        let mut record = Vec::with_capacity(RECORD_HEADER_LEN + ciphertext.len());
        record.extend_from_slice(&header);
        record.extend_from_slice(&ciphertext);
        Ok(record)
    }
}

/// Decrypts incoming TLS 1.3 records with a monotonically increasing sequence.
pub struct RecordUnprotector {
    key: AeadKey,
    sequence: u64,
}

impl RecordUnprotector {
    /// Creates an unprotector from an already configured AEAD key.
    #[must_use]
    pub fn new(key: AeadKey) -> Self {
        Self { key, sequence: 0 }
    }

    /// Creates an unprotector from a derived traffic secret.
    ///
    /// # Errors
    ///
    /// Propagates HKDF and key-length errors from [`TrafficKeys::derive`] and
    /// [`AeadKey::from_traffic_keys`].
    pub fn from_traffic_keys(keys: &TrafficKeys, suite: CipherSuite) -> Result<Self, TlsError> {
        Ok(Self::new(AeadKey::from_traffic_keys(keys, suite)?))
    }

    /// Returns the sequence number the next record is expected to use.
    #[must_use]
    pub fn sequence(&self) -> u64 {
        self.sequence
    }

    /// Authenticates and decrypts one complete record.
    ///
    /// Returns the inner content type (handshake, application data or alert)
    /// together with the content bytes.  Trailing zero padding is removed by
    /// scanning backwards to the last non-zero byte, exactly as RFC 8446,
    /// section 5.4 describes.
    ///
    /// # Errors
    ///
    /// Returns [`TlsError::ShortInput`] for a truncated record,
    /// [`TlsError::InvalidMessageType`] for a non-application-data outer type,
    /// [`TlsError::UnsupportedVersion`] for a wrong legacy version,
    /// [`TlsError::LengthMismatch`] when the header length disagrees with the
    /// buffer or the inner plaintext has no content type, and
    /// [`TlsError::AeadFailure`] when authentication fails.
    pub fn unprotect(&mut self, record: &[u8]) -> Result<(u8, Vec<u8>), TlsError> {
        if record.len() < RECORD_HEADER_LEN + TAG_LEN {
            return Err(TlsError::ShortInput);
        }
        if record[0] != CONTENT_TYPE_APPLICATION_DATA {
            return Err(TlsError::InvalidMessageType);
        }
        if record[1..3] != LEGACY_RECORD_VERSION {
            return Err(TlsError::UnsupportedVersion(u16::from_be_bytes([
                record[1], record[2],
            ])));
        }
        let record_len = u16::from_be_bytes([record[3], record[4]]) as usize;
        if record.len() != RECORD_HEADER_LEN + record_len {
            return Err(TlsError::LengthMismatch);
        }

        let header = &record[..RECORD_HEADER_LEN];
        let inner =
            self.key
                .decrypt_with_seq(self.sequence, header, &record[RECORD_HEADER_LEN..])?;
        self.sequence = self.sequence.checked_add(1).ok_or(TlsError::AeadFailure)?;

        let mut index = inner.len();
        while index > 0 {
            index -= 1;
            if inner[index] != 0 {
                let content_type = inner[index];
                return Ok((content_type, inner[..index].to_vec()));
            }
        }
        Err(TlsError::LengthMismatch)
    }
}

fn record_header(payload_len: usize) -> [u8; RECORD_HEADER_LEN] {
    let length = payload_len as u16;
    [
        CONTENT_TYPE_APPLICATION_DATA,
        LEGACY_RECORD_VERSION[0],
        LEGACY_RECORD_VERSION[1],
        (length >> 8) as u8,
        length as u8,
    ]
}

fn padding_for(block: usize, unpadded_len: usize) -> usize {
    if block <= 1 {
        return 0;
    }
    let remainder = unpadded_len % block;
    if remainder == 0 { 0 } else { block - remainder }
}

#[cfg(test)]
mod tests {
    use super::*;

    use crate::tls13::messages::HANDSHAKE_FINISHED;
    use crate::tls13::messages::encode_handshake;

    fn traffic_keys(suite: CipherSuite, secret_byte: u8) -> TrafficKeys {
        TrafficKeys::derive(&[secret_byte; 32], suite).expect("traffic keys")
    }

    #[test]
    fn aes_round_trip_and_sequence_increment() {
        let suite = CipherSuite::Aes128GcmSha256;
        let keys = traffic_keys(suite, 1);
        let mut protector = RecordProtector::from_traffic_keys(&keys, suite, 0).expect("protector");
        let mut unprotector =
            RecordUnprotector::from_traffic_keys(&keys, suite).expect("unprotector");

        assert_eq!(protector.sequence(), 0);
        assert_eq!(unprotector.sequence(), 0);

        let first = protector
            .protect(CONTENT_TYPE_APPLICATION_DATA, b"first")
            .expect("protect");
        let second = protector
            .protect(CONTENT_TYPE_APPLICATION_DATA, b"second")
            .expect("protect");
        assert_eq!(protector.sequence(), 2);

        assert_eq!(
            unprotector.unprotect(&first).expect("unprotect"),
            (CONTENT_TYPE_APPLICATION_DATA, b"first".to_vec())
        );
        assert_eq!(
            unprotector.unprotect(&second).expect("unprotect"),
            (CONTENT_TYPE_APPLICATION_DATA, b"second".to_vec())
        );
        assert_eq!(unprotector.sequence(), 2);
    }

    #[test]
    fn chacha_round_trip_and_handshake_content_type() {
        let suite = CipherSuite::Chacha20Poly1305Sha256;
        let keys = traffic_keys(suite, 2);
        let mut protector = RecordProtector::from_traffic_keys(&keys, suite, 0).expect("protector");
        let mut unprotector =
            RecordUnprotector::from_traffic_keys(&keys, suite).expect("unprotector");

        let message = encode_handshake(HANDSHAKE_FINISHED, &[0x9b; 32]).expect("encode");
        let record = protector
            .protect(0x16, &message)
            .expect("protect handshake");

        assert_eq!(record[0], CONTENT_TYPE_APPLICATION_DATA);
        assert_eq!(&record[1..3], &LEGACY_RECORD_VERSION);
        assert_eq!(
            unprotector.unprotect(&record).expect("unprotect"),
            (0x16, message)
        );
    }

    #[test]
    fn padding_rounds_the_record_length_to_the_block() {
        let suite = CipherSuite::Aes128GcmSha256;
        let keys = traffic_keys(suite, 3);
        let mut protector =
            RecordProtector::from_traffic_keys(&keys, suite, 32).expect("protector");
        let mut unprotector =
            RecordUnprotector::from_traffic_keys(&keys, suite).expect("unprotector");

        for length in [0usize, 1, 7, 31, 32, 100] {
            let content = vec![0x5a; length];
            let record = protector
                .protect(CONTENT_TYPE_APPLICATION_DATA, &content)
                .expect("protect");
            let record_len = u16::from_be_bytes([record[3], record[4]]) as usize;
            assert_eq!(record_len % 32, 0, "content length {length}");
            assert_eq!(
                unprotector.unprotect(&record).expect("unprotect"),
                (CONTENT_TYPE_APPLICATION_DATA, content)
            );
        }
    }

    #[test]
    fn tampering_and_wrong_sequence_are_rejected() {
        let suite = CipherSuite::Aes128GcmSha256;
        let keys = traffic_keys(suite, 4);
        let mut protector = RecordProtector::from_traffic_keys(&keys, suite, 0).expect("protector");
        let mut unprotector =
            RecordUnprotector::from_traffic_keys(&keys, suite).expect("unprotector");

        let record = protector
            .protect(CONTENT_TYPE_APPLICATION_DATA, b"payload")
            .expect("protect");

        let mut tampered = record.clone();
        let last = tampered.len() - 1;
        tampered[last] ^= 0x01;
        assert_eq!(unprotector.unprotect(&tampered), Err(TlsError::AeadFailure));
        // The sequence number only advances after successful authentication.
        assert_eq!(unprotector.sequence(), 0);
        assert_eq!(
            unprotector.unprotect(&record).expect("original record"),
            (CONTENT_TYPE_APPLICATION_DATA, b"payload".to_vec())
        );
        assert_eq!(unprotector.sequence(), 1);

        let mut wrong_header = record;
        wrong_header[3] ^= 0x01;
        assert!(unprotector.unprotect(&wrong_header).is_err());
    }

    #[test]
    fn malformed_records_are_rejected() {
        let suite = CipherSuite::Aes128GcmSha256;
        let keys = traffic_keys(suite, 5);
        let mut unprotector =
            RecordUnprotector::from_traffic_keys(&keys, suite).expect("unprotector");

        assert_eq!(unprotector.unprotect(&[0u8; 4]), Err(TlsError::ShortInput));

        let mut record = vec![0u8; RECORD_HEADER_LEN + TAG_LEN];
        record[0] = 0x16;
        assert_eq!(
            unprotector.unprotect(&record),
            Err(TlsError::InvalidMessageType)
        );

        record[0] = CONTENT_TYPE_APPLICATION_DATA;
        record[1] = 0x03;
        record[2] = 0x01;
        assert_eq!(
            unprotector.unprotect(&record),
            Err(TlsError::UnsupportedVersion(0x0301))
        );
    }
}
