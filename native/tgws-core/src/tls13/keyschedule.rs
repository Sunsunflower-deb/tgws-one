//! TLS 1.3 key schedule over SHA-256 (RFC 8446, section 7.1).
//!
//! The key schedule is a chain of HKDF extractions:
//!
//! ```text
//! early_secret = HKDF-Extract(0, PSK)
//! handshake_secret = HKDF-Extract(Derive-Secret(early_secret, "derived", ""),
//!                                 ECDHE)
//! master_secret = HKDF-Extract(Derive-Secret(handshake_secret, "derived", ""),
//!                             0)
//! ```
//!
//! Traffic secrets are then derived with [`derive_secret`] and turned into AEAD
//! keys with [`TrafficKeys::derive`].  Only the two SHA-256 TLS 1.3 cipher
//! suites are supported because both use a 32-byte hash; AES-256-GCM-SHA384 is
//! outside this task.

use core::fmt;

use hkdf::Hkdf;
use sha2::{Digest, Sha256};

use super::TlsError;

/// Output length of SHA-256, the hash used by both supported cipher suites.
pub const HASH_LEN: usize = 32;

/// Length of a TLS 1.3 AEAD IV/nonce.
pub const IV_LEN: usize = 12;

/// Prefix applied to every HKDF-Expand-Label label (RFC 8446, section 7.1).
pub const LABEL_PREFIX: &[u8] = b"tls13 ";

/// TLS 1.3 cipher suites with a SHA-256 transcript hash.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CipherSuite {
    /// `TLS_AES_128_GCM_SHA256` (0x1301).
    Aes128GcmSha256,
    /// `TLS_CHACHA20_POLY1305_SHA256` (0x1303).
    Chacha20Poly1305Sha256,
}

impl CipherSuite {
    /// Maps a wire code point to a supported suite.
    #[must_use]
    pub const fn from_code(code: u16) -> Option<Self> {
        match code {
            0x1301 => Some(Self::Aes128GcmSha256),
            0x1303 => Some(Self::Chacha20Poly1305Sha256),
            _ => None,
        }
    }

    /// Returns the wire code point of the suite.
    #[must_use]
    pub const fn code(self) -> u16 {
        match self {
            Self::Aes128GcmSha256 => 0x1301,
            Self::Chacha20Poly1305Sha256 => 0x1303,
        }
    }

    /// Returns the AEAD key length in bytes.
    #[must_use]
    pub const fn key_len(self) -> usize {
        match self {
            Self::Aes128GcmSha256 => 16,
            Self::Chacha20Poly1305Sha256 => 32,
        }
    }

    /// Returns the AEAD IV/nonce length in bytes.
    #[must_use]
    pub const fn iv_len(self) -> usize {
        IV_LEN
    }

    /// Returns the AEAD tag length in bytes.
    #[must_use]
    pub const fn tag_len(self) -> usize {
        16
    }

    /// Returns the transcript hash length in bytes.
    #[must_use]
    pub const fn hash_len(self) -> usize {
        HASH_LEN
    }
}

/// AEAD key and IV derived from one traffic secret.
#[derive(Clone, PartialEq, Eq)]
pub struct TrafficKeys {
    key: Vec<u8>,
    iv: Vec<u8>,
}

impl TrafficKeys {
    /// Derives `key` and `iv` from `secret` with HKDF-Expand-Label.
    ///
    /// # Errors
    ///
    /// Propagates [`TlsError::HkdfExpand`] when the suite requests an output
    /// length HKDF refuses.
    pub fn derive(secret: &[u8], suite: CipherSuite) -> Result<Self, TlsError> {
        let mut key = vec![0u8; suite.key_len()];
        hkdf_expand_label(secret, b"key", b"", &mut key)?;
        let mut iv = vec![0u8; suite.iv_len()];
        hkdf_expand_label(secret, b"iv", b"", &mut iv)?;
        Ok(Self { key, iv })
    }

    /// Returns the AEAD key bytes.
    #[must_use]
    pub fn key(&self) -> &[u8] {
        &self.key
    }

    /// Returns the AEAD IV bytes.
    #[must_use]
    pub fn iv(&self) -> &[u8] {
        &self.iv
    }
}

impl fmt::Debug for TrafficKeys {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("TrafficKeys")
            .field("key_len", &self.key.len())
            .field("iv_len", &self.iv.len())
            .finish()
    }
}

/// HKDF-Extract with SHA-256 (RFC 5869, section 2.2).
///
/// An empty `salt` is treated as `Hash.length` zero bytes, matching the TLS 1.3
/// usage where the salt is always an explicit PRK.  HMAC cannot fail, so the
/// result is returned directly.
#[must_use]
pub fn hkdf_extract(salt: &[u8], ikm: &[u8]) -> [u8; HASH_LEN] {
    let salt = if salt.is_empty() {
        Some(&[0u8; HASH_LEN][..])
    } else {
        Some(salt)
    };
    let (prk, _) = Hkdf::<Sha256>::extract(salt, ikm);
    let mut out = [0u8; HASH_LEN];
    out.copy_from_slice(&prk);
    out
}

/// HKDF-Expand-Label (RFC 8446, section 7.1).
///
/// `out.len()` becomes the two-byte `length` field of `HkdfLabel`; `label` is
/// prefixed with `"tls13 "`.  `context` is normally a transcript hash.
///
/// # Errors
///
/// Returns [`TlsError::LengthMismatch`] when the label, context or output length
/// does not fit the wire structure, and [`TlsError::HkdfExpand`] when HKDF
/// rejects the output length.
pub fn hkdf_expand_label(
    secret: &[u8],
    label: &[u8],
    context: &[u8],
    out: &mut [u8],
) -> Result<(), TlsError> {
    if out.len() > u16::MAX as usize
        || label.len() + LABEL_PREFIX.len() > u8::MAX as usize
        || context.len() > u8::MAX as usize
    {
        return Err(TlsError::LengthMismatch);
    }

    let mut info = Vec::with_capacity(2 + 1 + LABEL_PREFIX.len() + label.len() + 1 + context.len());
    info.extend_from_slice(&(out.len() as u16).to_be_bytes());
    info.push((LABEL_PREFIX.len() + label.len()) as u8);
    info.extend_from_slice(LABEL_PREFIX);
    info.extend_from_slice(label);
    info.push(context.len() as u8);
    info.extend_from_slice(context);

    let hkdf = Hkdf::<Sha256>::from_prk(secret).map_err(|_| TlsError::HkdfExpand)?;
    hkdf.expand(&info, out).map_err(|_| TlsError::HkdfExpand)
}

/// Derive-Secret (RFC 8446, section 7.1).
///
/// # Errors
///
/// Propagates [`TlsError::HkdfExpand`] for an invalid secret or output length.
pub fn derive_secret(
    secret: &[u8],
    label: &[u8],
    transcript_hash: &[u8],
) -> Result<[u8; HASH_LEN], TlsError> {
    let mut out = [0u8; HASH_LEN];
    hkdf_expand_label(secret, label, transcript_hash, &mut out)?;
    Ok(out)
}

/// Derives the handshake secret from the early secret and the ECDHE secret.
///
/// # Errors
///
/// Propagates HKDF errors.
pub fn derive_handshake_secret(
    early_secret: &[u8],
    shared_secret: &[u8],
) -> Result<[u8; HASH_LEN], TlsError> {
    let derived = derive_secret(early_secret, b"derived", &empty_hash())?;
    Ok(hkdf_extract(&derived, shared_secret))
}

/// Derives the master secret from the handshake secret.
///
/// # Errors
///
/// Propagates HKDF errors.
pub fn derive_master_secret(handshake_secret: &[u8]) -> Result<[u8; HASH_LEN], TlsError> {
    let derived = derive_secret(handshake_secret, b"derived", &empty_hash())?;
    Ok(hkdf_extract(&derived, &[0u8; HASH_LEN]))
}

/// Derives the `finished_key` used to compute `verify_data`.
///
/// # Errors
///
/// Propagates HKDF errors.
pub fn finished_key(base_key: &[u8]) -> Result<[u8; HASH_LEN], TlsError> {
    let mut out = [0u8; HASH_LEN];
    hkdf_expand_label(base_key, b"finished", b"", &mut out)?;
    Ok(out)
}

/// Returns `Transcript-Hash` of the empty message for SHA-256.
#[must_use]
pub fn empty_hash() -> [u8; HASH_LEN] {
    let mut out = [0u8; HASH_LEN];
    out.copy_from_slice(&Sha256::digest(b""));
    out
}

/// The TLS 1.3 secret chain for one handshake.
#[derive(Debug, Clone)]
pub struct KeySchedule {
    suite: CipherSuite,
    early_secret: [u8; HASH_LEN],
    handshake_secret: Option<[u8; HASH_LEN]>,
    master_secret: Option<[u8; HASH_LEN]>,
}

impl KeySchedule {
    /// Starts a key schedule without a PSK.
    ///
    /// RFC 8446 treats the absence of a PSK as a string of `Hash.length` zero
    /// bytes, so the early secret is `HKDF-Extract(0, 0)`.
    #[must_use]
    pub fn new(suite: CipherSuite) -> Self {
        Self {
            suite,
            early_secret: hkdf_extract(&[0u8; HASH_LEN], &[0u8; HASH_LEN]),
            handshake_secret: None,
            master_secret: None,
        }
    }

    /// Starts a key schedule with an external PSK.
    #[must_use]
    pub fn with_psk(suite: CipherSuite, psk: &[u8]) -> Self {
        Self {
            suite,
            early_secret: hkdf_extract(&[0u8; HASH_LEN], psk),
            handshake_secret: None,
            master_secret: None,
        }
    }

    /// Returns the cipher suite this schedule was created for.
    #[must_use]
    pub fn suite(&self) -> CipherSuite {
        self.suite
    }

    /// Returns the early secret.
    #[must_use]
    pub fn early_secret(&self) -> &[u8; HASH_LEN] {
        &self.early_secret
    }

    /// Returns the handshake secret once derived.
    #[must_use]
    pub fn handshake_secret(&self) -> Option<&[u8; HASH_LEN]> {
        self.handshake_secret.as_ref()
    }

    /// Returns the master secret once derived.
    #[must_use]
    pub fn master_secret(&self) -> Option<&[u8; HASH_LEN]> {
        self.master_secret.as_ref()
    }

    /// Derives and stores the handshake secret from the ECDHE shared secret.
    ///
    /// # Errors
    ///
    /// Propagates HKDF errors.
    pub fn advance_handshake(&mut self, shared_secret: &[u8]) -> Result<[u8; HASH_LEN], TlsError> {
        let secret = derive_handshake_secret(&self.early_secret, shared_secret)?;
        self.handshake_secret = Some(secret);
        Ok(secret)
    }

    /// Derives and stores the master secret.
    ///
    /// # Errors
    ///
    /// Returns [`TlsError::SecretNotDerived`] when the handshake secret has not
    /// been derived yet, and propagates HKDF errors.
    pub fn advance_master(&mut self) -> Result<[u8; HASH_LEN], TlsError> {
        let handshake = self.handshake_secret.ok_or(TlsError::SecretNotDerived)?;
        let secret = derive_master_secret(&handshake)?;
        self.master_secret = Some(secret);
        Ok(secret)
    }

    /// Derives the client handshake traffic secret.
    ///
    /// # Errors
    ///
    /// Returns [`TlsError::SecretNotDerived`] before the handshake secret is
    /// available, and propagates HKDF errors.
    pub fn client_handshake_traffic_secret(
        &self,
        transcript_hash: &[u8],
    ) -> Result<[u8; HASH_LEN], TlsError> {
        let secret = self.handshake_secret.ok_or(TlsError::SecretNotDerived)?;
        derive_secret(&secret, b"c hs traffic", transcript_hash)
    }

    /// Derives the server handshake traffic secret.
    ///
    /// # Errors
    ///
    /// Returns [`TlsError::SecretNotDerived`] before the handshake secret is
    /// available, and propagates HKDF errors.
    pub fn server_handshake_traffic_secret(
        &self,
        transcript_hash: &[u8],
    ) -> Result<[u8; HASH_LEN], TlsError> {
        let secret = self.handshake_secret.ok_or(TlsError::SecretNotDerived)?;
        derive_secret(&secret, b"s hs traffic", transcript_hash)
    }

    /// Derives the client application traffic secret.
    ///
    /// # Errors
    ///
    /// Returns [`TlsError::SecretNotDerived`] before the master secret is
    /// available, and propagates HKDF errors.
    pub fn client_application_traffic_secret(
        &self,
        transcript_hash: &[u8],
    ) -> Result<[u8; HASH_LEN], TlsError> {
        let secret = self.master_secret.ok_or(TlsError::SecretNotDerived)?;
        derive_secret(&secret, b"c ap traffic", transcript_hash)
    }

    /// Derives the server application traffic secret.
    ///
    /// # Errors
    ///
    /// Returns [`TlsError::SecretNotDerived`] before the master secret is
    /// available, and propagates HKDF errors.
    pub fn server_application_traffic_secret(
        &self,
        transcript_hash: &[u8],
    ) -> Result<[u8; HASH_LEN], TlsError> {
        let secret = self.master_secret.ok_or(TlsError::SecretNotDerived)?;
        derive_secret(&secret, b"s ap traffic", transcript_hash)
    }

    /// Derives the exporter master secret.
    ///
    /// # Errors
    ///
    /// Returns [`TlsError::SecretNotDerived`] before the master secret is
    /// available, and propagates HKDF errors.
    pub fn exporter_master_secret(
        &self,
        transcript_hash: &[u8],
    ) -> Result<[u8; HASH_LEN], TlsError> {
        let secret = self.master_secret.ok_or(TlsError::SecretNotDerived)?;
        derive_secret(&secret, b"exp master", transcript_hash)
    }

    /// Derives the resumption master secret.
    ///
    /// # Errors
    ///
    /// Returns [`TlsError::SecretNotDerived`] before the master secret is
    /// available, and propagates HKDF errors.
    pub fn resumption_master_secret(
        &self,
        transcript_hash: &[u8],
    ) -> Result<[u8; HASH_LEN], TlsError> {
        let secret = self.master_secret.ok_or(TlsError::SecretNotDerived)?;
        derive_secret(&secret, b"res master", transcript_hash)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn bytes(hex_string: &str) -> Vec<u8> {
        hex::decode(hex_string).expect("test vector is valid hex")
    }

    fn array32(hex_string: &str) -> [u8; HASH_LEN] {
        let decoded = bytes(hex_string);
        let mut out = [0u8; HASH_LEN];
        out.copy_from_slice(&decoded);
        out
    }

    // RFC 8448, section 3 ("Simple 1-RTT Handshake").  Secrets, transcript
    // hashes and traffic keys are copied verbatim from the trace.
    const EMPTY_HASH: &str = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    const CH_SH_HASH: &str = "860c06edc07858ee8e78f0e7428c58edd6b43f2ca3e6e95f02ed063cf0e1cad8";
    const FULL_TRANSCRIPT_HASH: &str =
        "9608102a0f1ccc6db6250b7b7e417b1a000eaada3daae4777a7686c9ff83df13";
    const RESUMPTION_TRANSCRIPT_HASH: &str =
        "209145a96ee8e2a122ff810047cc952684658d6049e86429426db87c54ad143d";

    const EARLY_SECRET: &str = "33ad0a1c607ec03b09e6cd9893680ce210adf300aa1f2660e1b22e10f170f92a";
    const DERIVED_HANDSHAKE: &str =
        "6f2615a108c702c5678f54fc9dbab69716c076189c48250cebeac3576c3611ba";
    const SHARED_SECRET: &str = "8bd4054fb55b9d63fdfbacf9f04b9f0d35e6d63f537563efd46272900f89492d";
    const HANDSHAKE_SECRET: &str =
        "1dc826e93606aa6fdc0aadc12f741b01046aa6b99f691ed221a9f0ca043fbeac";
    const DERIVED_MASTER: &str = "43de77e0c77713859a944db9db2590b53190a65b3ee2e4f12dd7a0bb7ce254b4";
    const MASTER_SECRET: &str = "18df06843d13a08bf2a449844c5f8a478001bc4d4c627984d5a41da8d0402919";

    const CLIENT_HS_TRAFFIC: &str =
        "b3eddb126e067f35a780b3abf45e2d8f3b1a950738f52e9600746a0e27a55a21";
    const SERVER_HS_TRAFFIC: &str =
        "b67b7d690cc16c4e75e54213cb2d37b4e9c912bcded9105d42befd59d391ad38";
    const CLIENT_AP_TRAFFIC: &str =
        "9e40646ce79a7f9dc05af8889bce6552875afa0b06df0087f792ebb7c17504a5";
    const SERVER_AP_TRAFFIC: &str =
        "a11af9f05531f856ad47116b45a950328204b4f44bfb6b3a4b4f1f3fcb631643";
    const EXPORTER_MASTER: &str =
        "fe22f881176eda18eb8f44529e6792c50c9a3f89452f68d8ae311b4309d3cf50";
    const RESUMPTION_MASTER: &str =
        "7df235f2031d2a051287d02b0241b0bfdaf86cc856231f2d5aba46c434ec196c";

    const SERVER_HS_KEY: &str = "3fce516009c21727d0f2e4e86ee403bc";
    const SERVER_HS_IV: &str = "5d313eb2671276ee13000b30";
    const CLIENT_HS_KEY: &str = "dbfaa693d1762c5b666af5d950258d01";
    const CLIENT_HS_IV: &str = "5bd3c71b836e0b76bb73265f";
    const SERVER_AP_KEY: &str = "9f02283b6c9c07efc26bb9f2ac92e356";
    const SERVER_AP_IV: &str = "cf782b88dd83549aadf1e984";
    const CLIENT_AP_KEY: &str = "17422dda596ed5d9acd890e3c63f5051";
    const CLIENT_AP_IV: &str = "5b78923dee08579033e523d9";

    #[test]
    fn rfc8448_empty_hash() {
        assert_eq!(empty_hash().as_slice(), bytes(EMPTY_HASH));
    }

    #[test]
    fn rfc8448_early_secret_and_predictable_chain() {
        assert_eq!(
            hkdf_extract(&[0u8; HASH_LEN], &[0u8; HASH_LEN]),
            array32(EARLY_SECRET)
        );

        let derived = derive_secret(&array32(EARLY_SECRET), b"derived", &empty_hash())
            .expect("Derive-Secret");
        assert_eq!(derived, array32(DERIVED_HANDSHAKE));

        let handshake = hkdf_extract(&derived, &bytes(SHARED_SECRET));
        assert_eq!(handshake, array32(HANDSHAKE_SECRET));

        let derived_master =
            derive_secret(&handshake, b"derived", &empty_hash()).expect("Derive-Secret");
        assert_eq!(derived_master, array32(DERIVED_MASTER));

        let master = hkdf_extract(&derived_master, &[0u8; HASH_LEN]);
        assert_eq!(master, array32(MASTER_SECRET));
    }

    #[test]
    fn rfc8448_traffic_secrets() {
        let handshake = array32(HANDSHAKE_SECRET);
        let master = array32(MASTER_SECRET);
        let ch_sh = bytes(CH_SH_HASH);
        let full = bytes(FULL_TRANSCRIPT_HASH);
        let resumption = bytes(RESUMPTION_TRANSCRIPT_HASH);

        assert_eq!(
            derive_secret(&handshake, b"c hs traffic", &ch_sh).expect("c hs"),
            array32(CLIENT_HS_TRAFFIC)
        );
        assert_eq!(
            derive_secret(&handshake, b"s hs traffic", &ch_sh).expect("s hs"),
            array32(SERVER_HS_TRAFFIC)
        );
        assert_eq!(
            derive_secret(&master, b"c ap traffic", &full).expect("c ap"),
            array32(CLIENT_AP_TRAFFIC)
        );
        assert_eq!(
            derive_secret(&master, b"s ap traffic", &full).expect("s ap"),
            array32(SERVER_AP_TRAFFIC)
        );
        assert_eq!(
            derive_secret(&master, b"exp master", &full).expect("exp master"),
            array32(EXPORTER_MASTER)
        );
        assert_eq!(
            derive_secret(&master, b"res master", &resumption).expect("res master"),
            array32(RESUMPTION_MASTER)
        );
    }

    #[test]
    fn rfc8448_key_schedule_chain() {
        let mut schedule = KeySchedule::new(CipherSuite::Aes128GcmSha256);
        assert_eq!(schedule.early_secret(), &array32(EARLY_SECRET));

        let handshake = schedule
            .advance_handshake(&bytes(SHARED_SECRET))
            .expect("handshake secret");
        assert_eq!(handshake, array32(HANDSHAKE_SECRET));

        let ch_sh = bytes(CH_SH_HASH);
        assert_eq!(
            schedule
                .client_handshake_traffic_secret(&ch_sh)
                .expect("c hs"),
            array32(CLIENT_HS_TRAFFIC)
        );
        assert_eq!(
            schedule
                .server_handshake_traffic_secret(&ch_sh)
                .expect("s hs"),
            array32(SERVER_HS_TRAFFIC)
        );

        let master = schedule.advance_master().expect("master secret");
        assert_eq!(master, array32(MASTER_SECRET));

        let full = bytes(FULL_TRANSCRIPT_HASH);
        assert_eq!(
            schedule
                .client_application_traffic_secret(&full)
                .expect("c ap"),
            array32(CLIENT_AP_TRAFFIC)
        );
        assert_eq!(
            schedule
                .server_application_traffic_secret(&full)
                .expect("s ap"),
            array32(SERVER_AP_TRAFFIC)
        );
        assert_eq!(
            schedule.exporter_master_secret(&full).expect("exp master"),
            array32(EXPORTER_MASTER)
        );
        assert_eq!(
            schedule
                .resumption_master_secret(&bytes(RESUMPTION_TRANSCRIPT_HASH))
                .expect("res master"),
            array32(RESUMPTION_MASTER)
        );
    }

    #[test]
    fn rfc8448_traffic_keys_aes_128_gcm() {
        let suite = CipherSuite::Aes128GcmSha256;

        let server_hs = TrafficKeys::derive(&array32(SERVER_HS_TRAFFIC), suite).expect("keys");
        assert_eq!(server_hs.key(), bytes(SERVER_HS_KEY).as_slice());
        assert_eq!(server_hs.iv(), bytes(SERVER_HS_IV).as_slice());

        let client_hs = TrafficKeys::derive(&array32(CLIENT_HS_TRAFFIC), suite).expect("keys");
        assert_eq!(client_hs.key(), bytes(CLIENT_HS_KEY).as_slice());
        assert_eq!(client_hs.iv(), bytes(CLIENT_HS_IV).as_slice());

        let server_ap = TrafficKeys::derive(&array32(SERVER_AP_TRAFFIC), suite).expect("keys");
        assert_eq!(server_ap.key(), bytes(SERVER_AP_KEY).as_slice());
        assert_eq!(server_ap.iv(), bytes(SERVER_AP_IV).as_slice());

        let client_ap = TrafficKeys::derive(&array32(CLIENT_AP_TRAFFIC), suite).expect("keys");
        assert_eq!(client_ap.key(), bytes(CLIENT_AP_KEY).as_slice());
        assert_eq!(client_ap.iv(), bytes(CLIENT_AP_IV).as_slice());
    }

    #[test]
    fn rfc8448_finished_key() {
        // The trace prints the expanded "finished" label as 008d3b66...
        let key = finished_key(&array32(SERVER_HS_TRAFFIC)).expect("finished key");
        assert_eq!(
            key.as_slice(),
            bytes("008d3b66f816ea559f96b537e885c31fc068bf492c652f01f288a1d8cdc19fc8")
        );
        // ... and the resulting verify_data as 9b9b141d...
        // (HMAC verification itself is part of RT7b.)
    }

    #[test]
    fn chacha_traffic_keys_have_expected_lengths() {
        let keys = TrafficKeys::derive(
            &array32(CLIENT_AP_TRAFFIC),
            CipherSuite::Chacha20Poly1305Sha256,
        )
        .expect("keys");
        assert_eq!(keys.key().len(), 32);
        assert_eq!(keys.iv().len(), 12);
        // The IV derivation is cipher-independent, so it matches AES above.
        assert_eq!(keys.iv(), bytes(CLIENT_AP_IV).as_slice());
    }

    #[test]
    fn secrets_are_not_derived_out_of_order() {
        let schedule = KeySchedule::new(CipherSuite::Aes128GcmSha256);
        assert_eq!(
            schedule.client_handshake_traffic_secret(&empty_hash()),
            Err(TlsError::SecretNotDerived)
        );
        assert_eq!(
            schedule.client_application_traffic_secret(&empty_hash()),
            Err(TlsError::SecretNotDerived)
        );
        let mut schedule = schedule;
        assert_eq!(schedule.advance_master(), Err(TlsError::SecretNotDerived));
    }

    #[test]
    fn cipher_suite_codes_round_trip() {
        for suite in [
            CipherSuite::Aes128GcmSha256,
            CipherSuite::Chacha20Poly1305Sha256,
        ] {
            assert_eq!(CipherSuite::from_code(suite.code()), Some(suite));
            assert_eq!(suite.hash_len(), HASH_LEN);
            assert_eq!(suite.iv_len(), IV_LEN);
            assert_eq!(suite.tag_len(), 16);
        }
        assert_eq!(CipherSuite::from_code(0x1302), None);
    }
}
