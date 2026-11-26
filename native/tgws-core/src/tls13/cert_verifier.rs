//! Real server authentication for the TLS 1.3 client (RFC 8446, section 4.4).
//!
//! [`WebPkiVerifier`] is the default [`CertVerifier`] used by
//! [`crate::tls13::Tls13ClientConfig`].  For every server `Certificate` and
//! `CertificateVerify` pair it:
//!
//! 1. parses the DER certificate chain carried by the handshake,
//! 2. builds a path to one of the configured trust anchors and checks the
//!    certificate signatures, validity period, key usage and name constraints,
//! 3. checks that the leaf certificate is valid for the SNI host name, and
//! 4. verifies the `CertificateVerify` signature over the TLS 1.3 signed
//!    message (`0x20 * 64 || context string || 0x00 || Transcript-Hash`).
//!
//! The cryptography is delegated to `rustls-webpki` (the maintained successor
//! of the historical `webpki` crate, whose library name is still `webpki`) and
//! its `ring` backend.  Revocation (OCSP/CRL), client certificates and the
//! platform trust store are intentionally out of scope.

use std::time::Duration;

pub use rustls_pki_types::UnixTime;
use rustls_pki_types::{CertificateDer, ServerName, SignatureVerificationAlgorithm, TrustAnchor};
use webpki::ring as webpki_algs;
use webpki::{EndEntityCert, KeyUsage};

use crate::tls13::TlsError;
use crate::tls13::handshake::{CertVerifier, CertVerifyContext};

/// The fixed 64-byte padding that prefixes every TLS 1.3 `CertificateVerify`
/// signed message (RFC 8446, section 4.4.3).
const CERTIFICATE_VERIFY_PADDING: [u8; 64] = [0x20; 64];

/// Context string for a server `CertificateVerify`, including the trailing NUL.
const SERVER_CERTIFICATE_VERIFY_CONTEXT: &[u8] = b"TLS 1.3, server CertificateVerify\x00";

/// Supplies the wall-clock time used for certificate validity checks.
///
/// Injecting the clock keeps expiry tests deterministic.  Production code uses
/// [`SystemTimeProvider`].
pub trait TimeProvider: Send + Sync {
    /// Returns the current time.
    fn now(&self) -> UnixTime;
}

/// [`TimeProvider`] backed by the system clock.
#[derive(Debug, Clone, Copy, Default)]
pub struct SystemTimeProvider;

impl TimeProvider for SystemTimeProvider {
    fn now(&self) -> UnixTime {
        UnixTime::now()
    }
}

/// [`TimeProvider`] that always returns the same instant.
///
/// This is used by tests to make certificate expiry deterministic.
#[derive(Debug, Clone, Copy)]
pub struct FixedTimeProvider {
    now: UnixTime,
}

impl FixedTimeProvider {
    /// Creates a provider for an absolute [`UnixTime`].
    #[must_use]
    pub fn new(now: UnixTime) -> Self {
        Self { now }
    }

    /// Creates a provider from a number of seconds since the Unix epoch.
    #[must_use]
    pub fn from_unix_seconds(seconds: u64) -> Self {
        Self {
            now: UnixTime::since_unix_epoch(Duration::from_secs(seconds)),
        }
    }
}

impl TimeProvider for FixedTimeProvider {
    fn now(&self) -> UnixTime {
        self.now
    }
}

/// WebPKI-backed [`CertVerifier`].
///
/// `roots` is the set of trust anchors the client accepts.  The default
/// instance uses Mozilla's compiled-in bundle ([`webpki_roots`]) and the system
/// clock; tests and the end-to-end script install a throw-away CA through
/// [`WebPkiVerifier::from_der_roots`].
pub struct WebPkiVerifier {
    roots: Vec<TrustAnchor<'static>>,
    time: Box<dyn TimeProvider>,
}

impl WebPkiVerifier {
    /// Creates a verifier from already parsed trust anchors and a time source.
    #[must_use]
    pub fn new(roots: Vec<TrustAnchor<'static>>, time: Box<dyn TimeProvider>) -> Self {
        Self { roots, time }
    }

    /// Creates a verifier from DER-encoded trust anchor certificates.
    ///
    /// # Errors
    ///
    /// Returns [`TlsError::CertificateVerificationFailed`] when a root is not a
    /// valid X.509 certificate that can serve as a trust anchor.
    pub fn from_der_roots(
        roots: &[Vec<u8>],
        time: Box<dyn TimeProvider>,
    ) -> Result<Self, TlsError> {
        let mut anchors = Vec::with_capacity(roots.len());
        for root in roots {
            let certificate = CertificateDer::from(root.as_slice());
            let anchor = webpki::anchor_from_trusted_cert(&certificate)
                .map_err(|_| TlsError::CertificateVerificationFailed)?;
            anchors.push(anchor.to_owned());
        }
        Ok(Self::new(anchors, time))
    }

    /// Creates a verifier for Mozilla's root CA bundle and the system clock.
    #[must_use]
    pub fn mozilla_roots() -> Self {
        Self::new(
            webpki_roots::TLS_SERVER_ROOTS.to_vec(),
            Box::new(SystemTimeProvider),
        )
    }
}

impl Default for WebPkiVerifier {
    fn default() -> Self {
        Self::mozilla_roots()
    }
}

impl CertVerifier for WebPkiVerifier {
    fn verify(&self, context: &CertVerifyContext<'_>) -> Result<(), TlsError> {
        let leaf_der = context
            .certificate_chain
            .first()
            .ok_or(TlsError::CertificateVerificationFailed)?;
        let leaf = CertificateDer::from(leaf_der.as_slice());
        let end_entity =
            EndEntityCert::try_from(&leaf).map_err(|_| TlsError::CertificateVerificationFailed)?;

        let intermediates: Vec<CertificateDer<'_>> = context.certificate_chain[1..]
            .iter()
            .map(|der| CertificateDer::from(der.as_slice()))
            .collect();

        end_entity
            .verify_for_usage(
                webpki::ALL_VERIFICATION_ALGS,
                &self.roots,
                &intermediates,
                self.time.now(),
                KeyUsage::server_auth(),
                None,
                None,
            )
            .map_err(|_| TlsError::CertificateVerificationFailed)?;

        let server_name = ServerName::try_from(context.server_name)
            .map_err(|_| TlsError::CertificateVerificationFailed)?;
        end_entity
            .verify_is_valid_for_subject_name(&server_name)
            .map_err(|_| TlsError::CertificateVerificationFailed)?;

        let algorithm = certificate_verify_algorithm(context.algorithm)
            .ok_or(TlsError::UnsupportedSignatureScheme(context.algorithm))?;
        let mut message = Vec::with_capacity(
            CERTIFICATE_VERIFY_PADDING.len()
                + SERVER_CERTIFICATE_VERIFY_CONTEXT.len()
                + context.transcript_hash.len(),
        );
        message.extend_from_slice(&CERTIFICATE_VERIFY_PADDING);
        message.extend_from_slice(SERVER_CERTIFICATE_VERIFY_CONTEXT);
        message.extend_from_slice(context.transcript_hash);

        end_entity
            .verify_signature(algorithm, &message, context.signature)
            .map_err(|_| TlsError::CertificateVerificationFailed)
    }
}

/// Maps a TLS 1.3 `SignatureScheme` code point to a webpki verifier.
///
/// RSA PKCS#1 and `rsa_pss_pss_*` schemes are deliberately absent: RFC 8446
/// requires RSASSA-PSS with an `rsaEncryption` key for `CertificateVerify`.
/// `ecdsa_secp521r1_sha512` and `ed448` are not implemented by webpki's ring
/// backend.  Every supported code point is advertised in the ClientHello
/// `signature_algorithms` extension.
fn certificate_verify_algorithm(
    scheme: u16,
) -> Option<&'static dyn SignatureVerificationAlgorithm> {
    match scheme {
        0x0403 => Some(webpki_algs::ECDSA_P256_SHA256),
        0x0503 => Some(webpki_algs::ECDSA_P384_SHA384),
        0x0804 => Some(webpki_algs::RSA_PSS_2048_8192_SHA256_LEGACY_KEY),
        0x0805 => Some(webpki_algs::RSA_PSS_2048_8192_SHA384_LEGACY_KEY),
        0x0806 => Some(webpki_algs::RSA_PSS_2048_8192_SHA512_LEGACY_KEY),
        0x0807 => Some(webpki_algs::ED25519),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    use crate::tls13::handshake::SkipVerifier;

    const CA_DER: &[u8] = include_bytes!("../../tests/fixtures/tls13-certs/ca.der");
    const SERVER_DER: &[u8] = include_bytes!("../../tests/fixtures/tls13-certs/server.der");
    const EXPIRED_DER: &[u8] = include_bytes!("../../tests/fixtures/tls13-certs/expired.der");
    const WRONG_NAME_DER: &[u8] = include_bytes!("../../tests/fixtures/tls13-certs/wrong.der");
    const SERVER_SIGNATURE: &[u8] = include_bytes!("../../tests/fixtures/tls13-certs/server.sig");

    fn test_verifier() -> WebPkiVerifier {
        WebPkiVerifier::from_der_roots(
            &[CA_DER.to_vec()],
            Box::new(FixedTimeProvider::from_unix_seconds(1_800_000_000)),
        )
        .expect("test CA is a valid trust anchor")
    }

    fn transcript_hash() -> Vec<u8> {
        (0u8..32).collect()
    }

    fn context<'a>(
        hash: &'a [u8],
        chain: &'a [Vec<u8>],
        algorithm: u16,
        signature: &'a [u8],
    ) -> CertVerifyContext<'a> {
        CertVerifyContext {
            transcript_hash: hash,
            server_name: "localhost",
            certificate_chain: chain,
            algorithm,
            signature,
        }
    }

    #[test]
    fn accepts_valid_chain_name_and_signature() {
        let hash = transcript_hash();
        let chain = vec![SERVER_DER.to_vec()];
        let ctx = context(&hash, &chain, 0x0804, SERVER_SIGNATURE);
        assert_eq!(test_verifier().verify(&ctx), Ok(()));
    }

    #[test]
    fn rejects_expired_certificate() {
        let hash = transcript_hash();
        let chain = vec![EXPIRED_DER.to_vec()];
        let ctx = context(&hash, &chain, 0x0804, SERVER_SIGNATURE);
        assert_eq!(
            test_verifier().verify(&ctx),
            Err(TlsError::CertificateVerificationFailed)
        );
    }

    #[test]
    fn rejects_name_mismatch() {
        let hash = transcript_hash();
        let chain = vec![WRONG_NAME_DER.to_vec()];
        let ctx = context(&hash, &chain, 0x0804, SERVER_SIGNATURE);
        assert_eq!(
            test_verifier().verify(&ctx),
            Err(TlsError::CertificateVerificationFailed)
        );
    }

    #[test]
    fn rejects_invalid_certificate_verify_signature() {
        let hash = [0u8; 32];
        let chain = vec![SERVER_DER.to_vec()];
        let ctx = context(&hash, &chain, 0x0804, SERVER_SIGNATURE);
        assert_eq!(
            test_verifier().verify(&ctx),
            Err(TlsError::CertificateVerificationFailed)
        );
    }

    #[test]
    fn rejects_unsupported_signature_scheme() {
        let hash = transcript_hash();
        let chain = vec![SERVER_DER.to_vec()];
        let ctx = context(&hash, &chain, 0x0401, SERVER_SIGNATURE);
        assert_eq!(
            test_verifier().verify(&ctx),
            Err(TlsError::UnsupportedSignatureScheme(0x0401))
        );
    }

    #[test]
    fn rejects_empty_certificate_chain() {
        let hash = transcript_hash();
        let chain = Vec::new();
        let ctx = context(&hash, &chain, 0x0804, SERVER_SIGNATURE);
        assert_eq!(
            test_verifier().verify(&ctx),
            Err(TlsError::CertificateVerificationFailed)
        );
    }

    #[test]
    fn skip_verifier_still_accepts_everything() {
        let hash = transcript_hash();
        let chain = vec![vec![0x30, 0x01, 0x00]];
        let ctx = context(&hash, &chain, 0x0401, &[0x00]);
        assert_eq!(SkipVerifier.verify(&ctx), Ok(()));
    }
}
