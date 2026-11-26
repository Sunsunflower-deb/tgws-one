//! AEAD primitives for TLS 1.3 record protection (RFC 8446, section 5.2).
//!
//! This module covers the cryptographic half of a protected record: it builds
//! the per-record nonce from the static IV and the 64-bit sequence number and
//! runs AES-128-GCM or ChaCha20-Poly1305 with the record header as associated
//! data.  Record framing and sequence-number bookkeeping belong to RT7b.
//!
//! The nonce is `iv XOR seq`, where `seq` is encoded as a 64-bit big-endian
//! integer in the *last* eight bytes of the 12-byte nonce (RFC 8446,
//! section 5.3).  A sequence number must never be reused with the same key.

use core::fmt;

use aes_gcm::aead::{Aead, KeyInit, Payload};
use aes_gcm::{Aes128Gcm, Nonce as AesNonce};
use chacha20poly1305::{ChaCha20Poly1305, Nonce as ChachaNonce};

use super::TlsError;
use super::keyschedule::{CipherSuite, IV_LEN, TrafficKeys};

/// A TLS 1.3 record-protection key with its static IV.
pub struct AeadKey {
    cipher: Cipher,
    iv: [u8; IV_LEN],
}

enum Cipher {
    // The AES key schedule makes `Aes128Gcm` large, so both variants are
    // boxed to keep `AeadKey` compact.
    Aes128Gcm(Box<Aes128Gcm>),
    ChaCha20Poly1305(Box<ChaCha20Poly1305>),
}

impl fmt::Debug for AeadKey {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match &self.cipher {
            Cipher::Aes128Gcm(_) => f.write_str("AeadKey::Aes128Gcm(..)"),
            Cipher::ChaCha20Poly1305(_) => f.write_str("AeadKey::ChaCha20Poly1305(..)"),
        }
    }
}

impl AeadKey {
    /// Builds an AEAD key from raw key and IV bytes.
    ///
    /// # Errors
    ///
    /// Returns [`TlsError::InvalidKeyLength`] when the key does not match the
    /// suite, and [`TlsError::InvalidNonceLength`] when the IV is not 12 bytes.
    pub fn new(suite: CipherSuite, key: &[u8], iv: &[u8]) -> Result<Self, TlsError> {
        if key.len() != suite.key_len() {
            return Err(TlsError::InvalidKeyLength {
                expected: suite.key_len(),
                actual: key.len(),
            });
        }
        if iv.len() != IV_LEN {
            return Err(TlsError::InvalidNonceLength(iv.len()));
        }

        let cipher = match suite {
            CipherSuite::Aes128GcmSha256 => {
                Cipher::Aes128Gcm(Box::new(Aes128Gcm::new_from_slice(key).map_err(|_| {
                    TlsError::InvalidKeyLength {
                        expected: suite.key_len(),
                        actual: key.len(),
                    }
                })?))
            }
            CipherSuite::Chacha20Poly1305Sha256 => Cipher::ChaCha20Poly1305(Box::new(
                ChaCha20Poly1305::new_from_slice(key).map_err(|_| TlsError::InvalidKeyLength {
                    expected: suite.key_len(),
                    actual: key.len(),
                })?,
            )),
        };

        let mut iv_bytes = [0u8; IV_LEN];
        iv_bytes.copy_from_slice(iv);
        Ok(Self {
            cipher,
            iv: iv_bytes,
        })
    }

    /// Builds an AEAD key from a derived [`TrafficKeys`] pair.
    ///
    /// # Errors
    ///
    /// Propagates length errors from [`AeadKey::new`].
    pub fn from_traffic_keys(keys: &TrafficKeys, suite: CipherSuite) -> Result<Self, TlsError> {
        Self::new(suite, keys.key(), keys.iv())
    }

    /// Encrypts `plaintext` with the TLS nonce for `seq`.
    ///
    /// `aad` is the five-byte TLS record header.  The return value is the
    /// ciphertext followed by the 16-byte AEAD tag, which is exactly the
    /// `TLSCiphertext.encrypted_record` content.
    ///
    /// # Errors
    ///
    /// Returns [`TlsError::AeadFailure`] when the AEAD operation fails.
    pub fn encrypt_with_seq(
        &self,
        seq: u64,
        aad: &[u8],
        plaintext: &[u8],
    ) -> Result<Vec<u8>, TlsError> {
        let nonce = tls_nonce(&self.iv, seq)?;
        let payload = Payload {
            aad,
            msg: plaintext,
        };
        match &self.cipher {
            Cipher::Aes128Gcm(cipher) => cipher
                .encrypt(AesNonce::from_slice(&nonce), payload)
                .map_err(|_| TlsError::AeadFailure),
            Cipher::ChaCha20Poly1305(cipher) => cipher
                .encrypt(ChachaNonce::from_slice(&nonce), payload)
                .map_err(|_| TlsError::AeadFailure),
        }
    }

    /// Decrypts and authenticates `ciphertext` (ciphertext followed by tag).
    ///
    /// # Errors
    ///
    /// Returns [`TlsError::AeadFailure`] when authentication fails or the input
    /// is shorter than the tag.
    pub fn decrypt_with_seq(
        &self,
        seq: u64,
        aad: &[u8],
        ciphertext: &[u8],
    ) -> Result<Vec<u8>, TlsError> {
        let nonce = tls_nonce(&self.iv, seq)?;
        let payload = Payload {
            aad,
            msg: ciphertext,
        };
        match &self.cipher {
            Cipher::Aes128Gcm(cipher) => cipher
                .decrypt(AesNonce::from_slice(&nonce), payload)
                .map_err(|_| TlsError::AeadFailure),
            Cipher::ChaCha20Poly1305(cipher) => cipher
                .decrypt(ChachaNonce::from_slice(&nonce), payload)
                .map_err(|_| TlsError::AeadFailure),
        }
    }
}

/// Builds the TLS 1.3 per-record nonce: `iv XOR seq`.
///
/// `seq` is placed in the last eight bytes of the nonce in network byte order,
/// so record 0 uses the static IV unchanged.
///
/// # Errors
///
/// Returns [`TlsError::InvalidNonceLength`] when `iv` is not 12 bytes.
pub fn tls_nonce(iv: &[u8], seq: u64) -> Result<[u8; IV_LEN], TlsError> {
    if iv.len() != IV_LEN {
        return Err(TlsError::InvalidNonceLength(iv.len()));
    }
    let mut nonce = [0u8; IV_LEN];
    nonce.copy_from_slice(iv);
    let seq_bytes = seq.to_be_bytes();
    for (byte, seq_byte) in nonce[IV_LEN - seq_bytes.len()..].iter_mut().zip(seq_bytes) {
        *byte ^= seq_byte;
    }
    Ok(nonce)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn bytes(hex_string: &str) -> Vec<u8> {
        hex::decode(hex_string).expect("test vector is valid hex")
    }

    const RECORD_HEADER: &[u8] = &[0x17, 0x03, 0x03, 0x00, 0x30];

    #[test]
    fn rfc8439_chacha20_poly1305_vector() {
        // RFC 8439, section 2.8.2 (AEAD_CHACHA20_POLY1305).
        let key = bytes("808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f");
        let nonce = bytes("070000004041424344454647");
        let aad = bytes("50515253c0c1c2c3c4c5c6c7");
        let plaintext = bytes(
            "4c616469657320616e642047656e746c656d656e206f662074686520636c6173\
             73206f66202739393a204966204920636f756c64206f6666657220796f75206f\
             6e6c79206f6e652074697020666f7220746865206675747572652c2073756e73\
             637265656e20776f756c642062652069742e",
        );
        let ciphertext = bytes(
            "d31a8d34648e60db7b86afbc53ef7ec2a4aded51296e08fea9e2b5a736ee62d6\
             3dbea45e8ca9671282fafb69da92728b1a71de0a9e060b2905d6a5b67ecd3b36\
             92ddbd7f2d778b8c9803aee328091b58fab324e4fad675945585808b4831d7bc\
             3ff4def08e4b7a9de576d26586cec64b6116",
        );
        let tag = bytes("1ae10b594f09e26a7e902ecbd0600691");

        let mut expected = ciphertext.clone();
        expected.extend_from_slice(&tag);

        // seq = 0 makes the TLS nonce equal to the RFC nonce.
        let cipher =
            AeadKey::new(CipherSuite::Chacha20Poly1305Sha256, &key, &nonce).expect("key is valid");
        let sealed = cipher
            .encrypt_with_seq(0, &aad, &plaintext)
            .expect("encryption succeeds");
        assert_eq!(sealed, expected);

        let opened = cipher
            .decrypt_with_seq(0, &aad, &sealed)
            .expect("decryption succeeds");
        assert_eq!(opened, plaintext);
    }

    #[test]
    fn aes_128_gcm_nist_vector() {
        // NIST/McGrew-Viega GCM test case 2 (AES-128, zero key and IV).
        let key = [0u8; 16];
        let nonce = [0u8; 12];
        let plaintext = [0u8; 16];
        let expected = bytes("0388dace60b6a392f328c2b971b2fe78ab6e47d42cec13bdf53a67b21257bddf");

        let cipher =
            AeadKey::new(CipherSuite::Aes128GcmSha256, &key, &nonce).expect("key is valid");
        let sealed = cipher
            .encrypt_with_seq(0, &[], &plaintext)
            .expect("encryption succeeds");
        assert_eq!(sealed, expected);
        assert_eq!(
            cipher
                .decrypt_with_seq(0, &[], &sealed)
                .expect("decryption succeeds"),
            plaintext
        );
    }

    #[test]
    fn nonce_xor_increments_only_the_sequence_bytes() {
        let iv = bytes("5d313eb2671276ee13000b30");
        let nonce0 = tls_nonce(&iv, 0).expect("nonce");
        let nonce1 = tls_nonce(&iv, 1).expect("nonce");
        let nonce_max = tls_nonce(&iv, u64::MAX).expect("nonce");

        assert_eq!(nonce0.as_slice(), iv.as_slice());
        // The last eight bytes XOR the big-endian sequence number.
        assert_eq!(nonce1[11], nonce0[11] ^ 1);
        for index in 4..IV_LEN {
            assert_eq!(nonce_max[index], iv[index] ^ 0xff);
        }
        assert_eq!(&nonce_max[..4], &iv[..4]);
    }

    #[test]
    fn aes_round_trip_and_sequence_uniqueness() {
        let key = bytes("3fce516009c21727d0f2e4e86ee403bc");
        let iv = bytes("5d313eb2671276ee13000b30");
        let cipher = AeadKey::new(CipherSuite::Aes128GcmSha256, &key, &iv).expect("key is valid");
        let plaintext = b"handshake message";

        let first = cipher
            .encrypt_with_seq(0, RECORD_HEADER, plaintext)
            .expect("encryption succeeds");
        let second = cipher
            .encrypt_with_seq(1, RECORD_HEADER, plaintext)
            .expect("encryption succeeds");

        assert_ne!(
            first, second,
            "different sequence numbers use different nonces"
        );
        assert_eq!(first.len(), plaintext.len() + 16);

        assert_eq!(
            cipher
                .decrypt_with_seq(0, RECORD_HEADER, &first)
                .expect("decryption succeeds"),
            plaintext
        );
        assert_eq!(
            cipher
                .decrypt_with_seq(1, RECORD_HEADER, &second)
                .expect("decryption succeeds"),
            plaintext
        );
    }

    #[test]
    fn aead_rejects_wrong_sequence_aad_and_tampering() {
        let key = bytes("3fce516009c21727d0f2e4e86ee403bc");
        let iv = bytes("5d313eb2671276ee13000b30");
        let cipher = AeadKey::new(CipherSuite::Aes128GcmSha256, &key, &iv).expect("key is valid");
        let sealed = cipher
            .encrypt_with_seq(7, RECORD_HEADER, b"payload")
            .expect("encryption succeeds");

        assert_eq!(
            cipher.decrypt_with_seq(8, RECORD_HEADER, &sealed),
            Err(TlsError::AeadFailure)
        );
        assert_eq!(
            cipher.decrypt_with_seq(7, b"other", &sealed),
            Err(TlsError::AeadFailure)
        );

        let mut tampered = sealed;
        tampered[0] ^= 0x01;
        assert_eq!(
            cipher.decrypt_with_seq(7, RECORD_HEADER, &tampered),
            Err(TlsError::AeadFailure)
        );
    }

    #[test]
    fn cipher_construction_validates_lengths() {
        assert!(matches!(
            AeadKey::new(CipherSuite::Aes128GcmSha256, &[0u8; 15], &[0u8; 12]),
            Err(TlsError::InvalidKeyLength {
                expected: 16,
                actual: 15
            })
        ));
        assert!(matches!(
            AeadKey::new(CipherSuite::Aes128GcmSha256, &[0u8; 16], &[0u8; 11]),
            Err(TlsError::InvalidNonceLength(11))
        ));
        assert!(AeadKey::new(CipherSuite::Chacha20Poly1305Sha256, &[0u8; 32], &[0u8; 12]).is_ok());
    }

    #[test]
    fn traffic_keys_feed_the_cipher() {
        // Server handshake traffic key from RFC 8448, section 3.
        let secret = bytes("b67b7d690cc16c4e75e54213cb2d37b4e9c912bcded9105d42befd59d391ad38");
        let keys = TrafficKeys::derive(&secret, CipherSuite::Aes128GcmSha256).expect("keys derive");
        let cipher = AeadKey::from_traffic_keys(&keys, CipherSuite::Aes128GcmSha256)
            .expect("keys are valid");
        let sealed = cipher
            .encrypt_with_seq(0, RECORD_HEADER, b"abc")
            .expect("encryption succeeds");
        assert_eq!(
            cipher
                .decrypt_with_seq(0, RECORD_HEADER, &sealed)
                .expect("decryption succeeds"),
            b"abc"
        );
    }
}
