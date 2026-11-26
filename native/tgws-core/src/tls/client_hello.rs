//! ClientHello construction with a Chrome-like fingerprint.
//!
//! The builder does not perform a key exchange.  It produces a syntactically
//! valid TLS 1.3 ClientHello whose extension set and ordering are modelled on
//! client greetings observed from Chrome 120 (the profile is our own description
//! of the wire format, not copied code).  This is enough for DPI experiments and
//! for a later uTLS-style layer.
//!
//! The random material comes from [`Random32`].  The trait has two deterministic
//! implementations so tests can compare exact bytes; a production seed source is
//! intentionally not wired up yet.

use super::parse::{EXTENSION_SERVER_NAME, SERVER_NAME_HOST_NAME};

const EXTENSION_SUPPORTED_GROUPS: u16 = 0x000a;
const EXTENSION_EC_POINT_FORMATS: u16 = 0x000b;
const EXTENSION_SESSION_TICKET: u16 = 0x0023;
const EXTENSION_KEY_SHARE: u16 = 0x0033;
const EXTENSION_PSK_KEY_EXCHANGE_MODES: u16 = 0x002d;
const EXTENSION_SIGNATURE_ALGORITHMS: u16 = 0x000d;
const EXTENSION_ALPN: u16 = 0x0010;
const EXTENSION_SUPPORTED_VERSIONS: u16 = 0x002b;
const EXTENSION_COMPRESS_CERTIFICATE: u16 = 0x001b;
const EXTENSION_APPLICATION_SETTINGS: u16 = 0x4469;
const EXTENSION_PADDING: u16 = 0x0015;

const TLS_1_3: u16 = 0x0304;
const TLS_1_2: u16 = 0x0303;

const CHROME_CIPHER_SUITES: &[u16] = &[
    0x1301, // TLS_AES_128_GCM_SHA256
    0x1302, // TLS_AES_256_GCM_SHA384
    0x1303, // TLS_CHACHA20_POLY1305_SHA256
    0xc02b, // TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256
    0xc02f, // TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256
    0xc02c, // TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384
    0xc030, // TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384
    0xcca9, // TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256
    0xcca8, // TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256
    0xc013, // TLS_ECDHE_RSA_WITH_AES_128_CBC_SHA
    0xc014, // TLS_ECDHE_RSA_WITH_AES_256_CBC_SHA
    0x009c, // TLS_RSA_WITH_AES_128_GCM_SHA256
    0x009d, // TLS_RSA_WITH_AES_256_GCM_SHA384
    0x002f, // TLS_RSA_WITH_AES_128_CBC_SHA
    0x0035, // TLS_RSA_WITH_AES_256_CBC_SHA
    0x1304, // TLS_AES_128_CCM_SHA256
    0x1305, // TLS_AES_128_CCM_8_SHA256
];

const CHROME_SIGNATURE_ALGORITHMS: &[u16] = &[
    0x0403, // ecdsa_secp256r1_sha256
    0x0804, // rsa_pss_rsae_sha256
    0x0401, // rsa_pkcs1_sha256
    0x0503, // ecdsa_secp384r1_sha384
    0x0805, // rsa_pss_rsae_sha384
    0x0501, // rsa_pkcs1_sha384
    0x0806, // rsa_pss_rsae_sha512
    0x0601, // rsa_pkcs1_sha512
    0x0807, // ed25519
    0x0809, // rsa_pss_pss_sha256
    0x080a, // rsa_pss_pss_sha384
    0x080b, // rsa_pss_pss_sha512
];

const GREASE_VALUES: [u16; 16] = [
    0x0a0a, 0x1a1a, 0x2a2a, 0x3a3a, 0x4a4a, 0x5a5a, 0x6a6a, 0x7a7a, 0x8a8a, 0x9a9a, 0xaaaa, 0xbaba,
    0xcaca, 0xdada, 0xeaea, 0xfafa,
];

/// Extension slots understood by the builder.
///
/// A profile returns these in wire order.  `Grease` and `Padding` may appear
/// more than once or not at all; the builder skips GREASE when the profile
/// disables it and only emits padding during [`ClientHelloBuilder::build_padded`].
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ExtensionKind {
    /// A GREASE extension value (RFC 8701).
    Grease,
    /// `server_name` (RFC 6066).
    ServerName,
    /// `supported_groups`.
    SupportedGroups,
    /// `ec_point_formats`.
    EcPointFormats,
    /// `session_ticket`.
    SessionTicket,
    /// `key_share` with one X25519 share.
    KeyShare,
    /// `psk_key_exchange_modes`.
    PskKeyExchangeModes,
    /// `signature_algorithms`.
    SignatureAlgorithms,
    /// `application_layer_protocol_negotiation`.
    Alpn,
    /// `supported_versions`.
    SupportedVersions,
    /// `compress_certificate`.
    CompressCertificate,
    /// Chrome's `application_settings` (0x4469).
    ApplicationSettings,
    /// `padding` (RFC 7685).
    Padding,
}

/// A source of pseudo-random 32-bit words.
///
/// Implementations only need to provide [`Random32::next_u32`]; the default
/// [`Random32::fill_bytes`] expands words into arbitrary byte slices.
pub trait Random32 {
    /// Returns the next 32-bit word.
    fn next_u32(&mut self) -> u32;

    /// Fills `dst` with bytes derived from successive words.
    fn fill_bytes(&mut self, dst: &mut [u8]) {
        let mut chunks = dst.chunks_exact_mut(4);
        for chunk in &mut chunks {
            chunk.copy_from_slice(&self.next_u32().to_le_bytes());
        }
        let remainder = chunks.into_remainder();
        if !remainder.is_empty() {
            let bytes = self.next_u32().to_le_bytes();
            remainder.copy_from_slice(&bytes[..remainder.len()]);
        }
    }
}

/// Deterministic `xorshift64*` generator (Marsaglia / Vigna variant).
///
/// Suitable for tests and reproducible fingerprint experiments.  It is not a
/// cryptographic RNG and must not be used for key material.
#[derive(Debug, Clone)]
pub struct XorShift64Star {
    state: u64,
}

impl XorShift64Star {
    /// Creates a generator from `seed`.  A zero seed is replaced by a non-zero
    /// constant because the xorshift state must never become zero.
    #[must_use]
    pub fn new(seed: u64) -> Self {
        Self {
            state: if seed == 0 {
                0x9e37_79b9_7f4a_7c15
            } else {
                seed
            },
        }
    }

    fn next_u64(&mut self) -> u64 {
        let mut x = self.state;
        x ^= x >> 12;
        x ^= x << 25;
        x ^= x >> 27;
        self.state = x;
        x.wrapping_mul(0x2545_f491_4f6c_dd1d)
    }
}

impl Random32 for XorShift64Star {
    fn next_u32(&mut self) -> u32 {
        (self.next_u64() >> 32) as u32
    }
}

/// Deterministic `splitmix64` generator.
///
/// Useful when a simple, well-distributed seed expander is needed in tests.
/// It is not a cryptographic RNG.
#[derive(Debug, Clone)]
pub struct SplitMix64 {
    state: u64,
}

impl SplitMix64 {
    /// Creates a generator from `seed`.
    #[must_use]
    pub fn new(seed: u64) -> Self {
        Self { state: seed }
    }

    fn next_u64(&mut self) -> u64 {
        self.state = self.state.wrapping_add(0x9e37_79b9_7f4a_7c15);
        let mut z = self.state;
        z = (z ^ (z >> 30)).wrapping_mul(0xbf58_476d_1ce4_e5b9);
        z = (z ^ (z >> 27)).wrapping_mul(0x94d0_49bb_1331_11eb);
        z ^ (z >> 31)
    }
}

impl Random32 for SplitMix64 {
    fn next_u32(&mut self) -> u32 {
        (self.next_u64() >> 32) as u32
    }
}

/// Describes the extension fingerprint used by [`ClientHelloBuilder`].
pub trait FingerprintProfile {
    /// Whether GREASE values are emitted (RFC 8701).
    fn grease(&self) -> bool;
    /// Extension slots in the exact wire order.
    fn extension_order(&self) -> &'static [ExtensionKind];
}

/// A Chrome 120-like TLS 1.3 profile with GREASE enabled.
#[derive(Debug, Clone, Copy, Default)]
pub struct Chrome120Profile;

impl FingerprintProfile for Chrome120Profile {
    fn grease(&self) -> bool {
        true
    }

    fn extension_order(&self) -> &'static [ExtensionKind] {
        CHROME_120_ORDER
    }
}

/// A conservative profile with the same ordering but without GREASE.
#[derive(Debug, Clone, Copy, Default)]
pub struct DefaultProfile;

impl FingerprintProfile for DefaultProfile {
    fn grease(&self) -> bool {
        false
    }

    fn extension_order(&self) -> &'static [ExtensionKind] {
        CHROME_120_ORDER
    }
}

const CHROME_120_ORDER: &[ExtensionKind] = &[
    ExtensionKind::Grease,
    ExtensionKind::ServerName,
    ExtensionKind::SupportedGroups,
    ExtensionKind::EcPointFormats,
    ExtensionKind::SessionTicket,
    ExtensionKind::KeyShare,
    ExtensionKind::PskKeyExchangeModes,
    ExtensionKind::SignatureAlgorithms,
    ExtensionKind::Alpn,
    ExtensionKind::SupportedVersions,
    ExtensionKind::CompressCertificate,
    ExtensionKind::ApplicationSettings,
    ExtensionKind::Grease,
    ExtensionKind::Padding,
];

/// Builds a complete TLS record containing one ClientHello.
///
/// The builder owns its SNI and ALPN strings so the returned bytes never borrow
/// from it.  Random fields are filled from the caller-supplied [`Random32`].
pub struct ClientHelloBuilder {
    sni: String,
    alpn: Vec<Vec<u8>>,
    profile: Box<dyn FingerprintProfile>,
}

impl ClientHelloBuilder {
    /// Creates a builder for `sni` with the [`DefaultProfile`] and ALPN
    /// `h2,http/1.1`.
    #[must_use]
    pub fn new(sni: &str) -> Self {
        Self {
            sni: sni.to_owned(),
            alpn: vec![b"h2".to_vec(), b"http/1.1".to_vec()],
            profile: Box::new(DefaultProfile),
        }
    }

    /// Replaces the fingerprint profile.
    #[must_use]
    pub fn with_profile<P>(mut self, profile: P) -> Self
    where
        P: FingerprintProfile + 'static,
    {
        self.profile = Box::new(profile);
        self
    }

    /// Replaces the ALPN protocol list.
    #[must_use]
    pub fn with_alpn(mut self, protocols: &[&[u8]]) -> Self {
        self.alpn = protocols.iter().map(|protocol| protocol.to_vec()).collect();
        self
    }

    /// Builds a complete TLS record (`0x16`, legacy version, length, handshake).
    #[must_use]
    pub fn build(&self, rng: &mut dyn Random32) -> Vec<u8> {
        self.build_inner(rng, None)
    }

    /// Builds a complete TLS record of exactly `target_len` bytes when possible.
    ///
    /// Padding is carried by the `padding` extension (RFC 7685).  A target that
    /// is shorter than the minimal ClientHello, or longer than a single TLS
    /// plaintext record, cannot be represented; in those cases the minimal
    /// record is returned unchanged.  Valid targets must leave room for the
    /// four-byte padding extension header.
    #[must_use]
    pub fn build_padded(&self, rng: &mut dyn Random32, target_len: usize) -> Vec<u8> {
        let base = self.build_inner(rng, None);
        let max_len = 5 + super::record::MAX_PLAINTEXT;

        if target_len <= base.len() || target_len > max_len {
            return base;
        }

        let delta = target_len - base.len();
        if delta < 4 {
            return base;
        }

        self.build_inner(rng, Some(delta - 4))
    }

    fn build_inner(&self, rng: &mut dyn Random32, padding_len: Option<usize>) -> Vec<u8> {
        let mut random = [0u8; 32];
        rng.fill_bytes(&mut random);
        let mut session_id = [0u8; 32];
        rng.fill_bytes(&mut session_id);
        let mut key_share = [0u8; 32];
        rng.fill_bytes(&mut key_share);

        let grease = if self.profile.grease() {
            Some(next_grease(rng))
        } else {
            None
        };

        let mut extensions = Vec::new();
        let mut padding_emitted = false;
        for kind in self.profile.extension_order() {
            match *kind {
                ExtensionKind::Grease => {
                    if let Some(value) = grease {
                        push_extension(&mut extensions, value, &[]);
                    }
                }
                ExtensionKind::ServerName => {
                    push_extension(
                        &mut extensions,
                        EXTENSION_SERVER_NAME,
                        &self.server_name_data(),
                    );
                }
                ExtensionKind::SupportedGroups => {
                    push_extension(
                        &mut extensions,
                        EXTENSION_SUPPORTED_GROUPS,
                        &supported_groups_data(grease),
                    );
                }
                ExtensionKind::EcPointFormats => {
                    push_extension(&mut extensions, EXTENSION_EC_POINT_FORMATS, &[1, 0]);
                }
                ExtensionKind::SessionTicket => {
                    push_extension(&mut extensions, EXTENSION_SESSION_TICKET, &[]);
                }
                ExtensionKind::KeyShare => {
                    push_extension(
                        &mut extensions,
                        EXTENSION_KEY_SHARE,
                        &key_share_data(&key_share),
                    );
                }
                ExtensionKind::PskKeyExchangeModes => {
                    push_extension(&mut extensions, EXTENSION_PSK_KEY_EXCHANGE_MODES, &[1, 1]);
                }
                ExtensionKind::SignatureAlgorithms => {
                    push_extension(
                        &mut extensions,
                        EXTENSION_SIGNATURE_ALGORITHMS,
                        &signature_algorithms_data(),
                    );
                }
                ExtensionKind::Alpn => {
                    push_extension(&mut extensions, EXTENSION_ALPN, &self.alpn_data());
                }
                ExtensionKind::SupportedVersions => {
                    push_extension(
                        &mut extensions,
                        EXTENSION_SUPPORTED_VERSIONS,
                        &supported_versions_data(grease),
                    );
                }
                ExtensionKind::CompressCertificate => {
                    push_extension(
                        &mut extensions,
                        EXTENSION_COMPRESS_CERTIFICATE,
                        &[4, 0x00, 0x02, 0x00, 0x01],
                    );
                }
                ExtensionKind::ApplicationSettings => {
                    push_extension(
                        &mut extensions,
                        EXTENSION_APPLICATION_SETTINGS,
                        &self.alpn_data(),
                    );
                }
                ExtensionKind::Padding => {
                    if let Some(len) = padding_len {
                        push_extension(&mut extensions, EXTENSION_PADDING, &vec![0u8; len]);
                        padding_emitted = true;
                    }
                }
            }
        }

        if let Some(len) = padding_len {
            if !padding_emitted {
                push_extension(&mut extensions, EXTENSION_PADDING, &vec![0u8; len]);
            }
        }

        let ciphers = cipher_suites(grease);

        let mut body = Vec::new();
        body.extend_from_slice(&[0x03, 0x03]); // legacy_version
        body.extend_from_slice(&random);
        body.push(session_id.len() as u8);
        body.extend_from_slice(&session_id);
        body.extend_from_slice(&(ciphers.len() as u16).to_be_bytes());
        body.extend_from_slice(&ciphers);
        body.push(1);
        body.push(0);
        body.extend_from_slice(&(extensions.len() as u16).to_be_bytes());
        body.extend_from_slice(&extensions);

        let mut record = Vec::with_capacity(5 + 4 + body.len());
        record.push(super::parse::TLS_RECORD_HANDSHAKE);
        record.extend_from_slice(&[0x03, 0x01]); // legacy record version, as Chrome sends
        record.extend_from_slice(&((4 + body.len()) as u16).to_be_bytes());
        record.push(super::parse::HANDSHAKE_CLIENT_HELLO);
        record.extend_from_slice(&[
            ((body.len() >> 16) & 0xff) as u8,
            ((body.len() >> 8) & 0xff) as u8,
            (body.len() & 0xff) as u8,
        ]);
        record.extend_from_slice(&body);
        record
    }

    fn sni_bytes(&self) -> &[u8] {
        let bytes = self.sni.as_bytes();
        let max = u16::MAX as usize - 3;
        &bytes[..bytes.len().min(max)]
    }

    fn server_name_data(&self) -> Vec<u8> {
        let sni = self.sni_bytes();
        let mut data = Vec::with_capacity(3 + sni.len());
        data.extend_from_slice(&((3 + sni.len()) as u16).to_be_bytes());
        data.push(SERVER_NAME_HOST_NAME);
        data.extend_from_slice(&(sni.len() as u16).to_be_bytes());
        data.extend_from_slice(sni);
        data
    }

    fn alpn_data(&self) -> Vec<u8> {
        let mut entries = Vec::new();
        for protocol in &self.alpn {
            let protocol = &protocol[..protocol.len().min(u16::MAX as usize)];
            if entries.len() + 2 + protocol.len() > u16::MAX as usize {
                break;
            }
            entries.extend_from_slice(&(protocol.len() as u16).to_be_bytes());
            entries.extend_from_slice(protocol);
        }

        let mut data = Vec::with_capacity(2 + entries.len());
        data.extend_from_slice(&(entries.len() as u16).to_be_bytes());
        data.extend_from_slice(&entries);
        data
    }
}

fn push_extension(out: &mut Vec<u8>, extension_type: u16, data: &[u8]) {
    debug_assert!(data.len() <= u16::MAX as usize);
    out.extend_from_slice(&extension_type.to_be_bytes());
    out.extend_from_slice(&(data.len() as u16).to_be_bytes());
    out.extend_from_slice(data);
}

fn next_grease(rng: &mut dyn Random32) -> u16 {
    let index = (rng.next_u32() as usize) % GREASE_VALUES.len();
    GREASE_VALUES[index]
}

fn cipher_suites(grease: Option<u16>) -> Vec<u8> {
    let mut suites = Vec::with_capacity(2 * (CHROME_CIPHER_SUITES.len() + 1));
    if let Some(value) = grease {
        suites.extend_from_slice(&value.to_be_bytes());
    }
    for suite in CHROME_CIPHER_SUITES {
        suites.extend_from_slice(&suite.to_be_bytes());
    }
    suites
}

fn supported_groups_data(grease: Option<u16>) -> Vec<u8> {
    let mut groups = Vec::with_capacity(5);
    if let Some(value) = grease {
        groups.push(value);
    }
    groups.extend_from_slice(&[
        0x001d, // x25519
        0x0017, // secp256r1
        0x0018, // secp384r1
        0x0019, // secp521r1
    ]);

    let mut data = Vec::with_capacity(2 + groups.len() * 2);
    data.extend_from_slice(&((groups.len() * 2) as u16).to_be_bytes());
    for group in groups {
        data.extend_from_slice(&group.to_be_bytes());
    }
    data
}

fn key_share_data(key: &[u8; 32]) -> Vec<u8> {
    let mut data = Vec::with_capacity(2 + 2 + 2 + key.len());
    data.extend_from_slice(&((4 + key.len()) as u16).to_be_bytes());
    data.extend_from_slice(&0x001d_u16.to_be_bytes()); // x25519
    data.extend_from_slice(&(key.len() as u16).to_be_bytes());
    data.extend_from_slice(key);
    data
}

fn signature_algorithms_data() -> Vec<u8> {
    let mut data = Vec::with_capacity(2 + CHROME_SIGNATURE_ALGORITHMS.len() * 2);
    data.extend_from_slice(&((CHROME_SIGNATURE_ALGORITHMS.len() * 2) as u16).to_be_bytes());
    for algorithm in CHROME_SIGNATURE_ALGORITHMS {
        data.extend_from_slice(&algorithm.to_be_bytes());
    }
    data
}

fn supported_versions_data(grease: Option<u16>) -> Vec<u8> {
    let mut versions = Vec::with_capacity(3);
    if let Some(value) = grease {
        versions.push(value);
    }
    versions.push(TLS_1_3);
    versions.push(TLS_1_2);

    let mut data = Vec::with_capacity(1 + versions.len() * 2);
    data.push((versions.len() * 2) as u8);
    for version in versions {
        data.extend_from_slice(&version.to_be_bytes());
    }
    data
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tls::parse::parse_client_hello;

    #[test]
    fn rng_is_deterministic() {
        let mut a = SplitMix64::new(42);
        let mut b = SplitMix64::new(42);
        let mut first = [0u8; 64];
        let mut second = [0u8; 64];
        a.fill_bytes(&mut first);
        b.fill_bytes(&mut second);
        assert_eq!(first, second);

        let mut c = XorShift64Star::new(7);
        let mut d = XorShift64Star::new(7);
        assert_eq!(c.next_u32(), d.next_u32());
    }

    #[test]
    fn build_parses_and_keeps_sni() {
        let mut rng = SplitMix64::new(1);
        let flight = ClientHelloBuilder::new("www.example.com")
            .with_profile(Chrome120Profile)
            .build(&mut rng);

        let layout = parse_client_hello(&flight).expect("built ClientHello must parse");
        assert_eq!(layout.sni(&flight), Some(b"www.example.com".as_slice()));
        assert_eq!(layout.record_end, flight.len());
    }

    #[test]
    fn default_profile_omits_grease() {
        let mut rng = SplitMix64::new(2);
        let flight = ClientHelloBuilder::new("example.com").build(&mut rng);
        assert!(!contains_grease_extension(&flight));
    }

    #[test]
    fn chrome_profile_contains_grease() {
        let mut rng = SplitMix64::new(3);
        let flight = ClientHelloBuilder::new("example.com")
            .with_profile(Chrome120Profile)
            .build(&mut rng);
        assert!(contains_grease_extension(&flight));
    }

    #[test]
    fn build_padded_has_exact_length_and_parses() {
        for target in [517usize, 1024, 4096] {
            let mut rng = SplitMix64::new(target as u64);
            let flight = ClientHelloBuilder::new("padded.example")
                .with_profile(Chrome120Profile)
                .build_padded(&mut rng, target);

            assert_eq!(flight.len(), target, "target {target}");
            let layout = parse_client_hello(&flight).expect("padded ClientHello must parse");
            assert_eq!(layout.sni(&flight), Some(b"padded.example".as_slice()));
        }
    }

    fn contains_grease_extension(flight: &[u8]) -> bool {
        let layout = parse_client_hello(flight).expect("ClientHello must parse");
        let extensions = &flight[layout.extensions_start..layout.extensions_end];
        let mut pos = 0;
        while pos + 4 <= extensions.len() {
            let extension_type = u16::from_be_bytes([extensions[pos], extensions[pos + 1]]);
            let extension_len =
                u16::from_be_bytes([extensions[pos + 2], extensions[pos + 3]]) as usize;
            if is_grease(extension_type) {
                return true;
            }
            pos += 4 + extension_len;
        }
        false
    }

    fn is_grease(value: u16) -> bool {
        value & 0x0f0f == 0x0a0a && value >> 8 == value & 0xff
    }
}
