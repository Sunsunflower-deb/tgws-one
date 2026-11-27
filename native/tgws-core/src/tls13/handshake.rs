//! Client-side TLS 1.3 handshake state machine (RFC 8446, section 2).
//!
//! [`Tls13Client`] ties the RT7a building blocks together:
//!
//! 1. [`Tls13Client::start`] assembles a ClientHello with the existing
//!    [`crate::tls::client_hello`] builder and remembers it for the transcript.
//! 2. [`Tls13Client::advance`] consumes received bytes, parses the plaintext
//!    `ServerHello`, derives the handshake traffic keys, decrypts
//!    `EncryptedExtensions`, `Certificate`, `CertificateVerify` and `Finished`,
//!    verifies the server `Finished` HMAC, then emits the encrypted client
//!    `Finished`.
//! 3. After the handshake, application records travel through
//!    [`Tls13Client::send_application_data`] and
//!    [`Tls13Client::take_application_data`].
//!
//! Transcript hashes cover every handshake message (header included) in wire
//! order.  The server certificate chain and `CertificateVerify` signature are
//! checked by the configured [`CertVerifier`]; the default policy uses the
//! [`crate::tls13::cert_verifier::WebPkiVerifier`] with Mozilla's roots.

use sha2::{Digest, Sha256};

use crate::tls::client_hello::{Chrome120Profile, ClientHelloBuilder, Random32};
use crate::tls::record::{
    CONTENT_TYPE_ALERT, CONTENT_TYPE_APPLICATION_DATA, CONTENT_TYPE_CHANGE_CIPHER_SPEC,
    CONTENT_TYPE_HANDSHAKE, MAX_PLAINTEXT,
};
use crate::tls13::TlsError;
use crate::tls13::cert_verifier::WebPkiVerifier;
use crate::tls13::keyschedule::{CipherSuite, HASH_LEN, KeySchedule, TrafficKeys, finished_key};
use crate::tls13::kx::X25519KeyPair;
use crate::tls13::messages::{
    ALERT_LEVEL_WARNING, Alert, Certificate, CertificateVerify, EncryptedExtensions, Finished,
    GROUP_X25519, HANDSHAKE_CERTIFICATE, HANDSHAKE_CERTIFICATE_VERIFY,
    HANDSHAKE_ENCRYPTED_EXTENSIONS, HANDSHAKE_FINISHED, HANDSHAKE_KEY_UPDATE,
    HANDSHAKE_SERVER_HELLO, ServerHello,
};
use crate::tls13::record_protection::{RecordProtector, RecordUnprotector};

const EXTENSION_ALPN: u16 = 0x0010;
const HANDSHAKE_NEW_SESSION_TICKET: u8 = 4;

/// How the client treats the server certificate chain.
///
/// TLS 1.3 peers authenticate with a certificate chain and a
/// `CertificateVerify` signature.  The policy selects whether that
/// authentication is enforced:
///
/// * [`ServerCertPolicy::WebPki`] is the default.  It invokes the configured
///   [`CertVerifier`], which by default is
///   [`crate::tls13::cert_verifier::WebPkiVerifier`] with Mozilla's root CA
///   bundle and the system clock.
/// * [`ServerCertPolicy::Skip`] accepts any certificate without checking the
///   chain, name or signature.  It exists only for tests and network
///   namespaces and must not be used for real traffic.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ServerCertPolicy {
    /// Verify the chain, name and `CertificateVerify` signature.
    WebPki,
    /// Accept any certificate.  Tests and network namespaces only.
    Skip,
}

/// Input handed to a [`CertVerifier`] for one `CertificateVerify` message.
#[derive(Debug, Clone, Copy)]
pub struct CertVerifyContext<'a> {
    /// `Transcript-Hash` through the server `Certificate` message, before the
    /// `CertificateVerify` message itself is added.
    pub transcript_hash: &'a [u8],
    /// SNI host name the client connected to.
    pub server_name: &'a str,
    /// DER-encoded certificate chain in the order it arrived on the wire.
    pub certificate_chain: &'a [Vec<u8>],
    /// Signature scheme from the `CertificateVerify` message.
    pub algorithm: u16,
    /// Raw signature bytes from the `CertificateVerify` message.
    pub signature: &'a [u8],
}

/// Certificate and `CertificateVerify` verification hook.
///
/// The default implementation is
/// [`crate::tls13::cert_verifier::WebPkiVerifier`].  [`SkipVerifier`] is kept
/// for tests and for the explicit [`ServerCertPolicy::Skip`] escape hatch.
///
/// `Send` is required because a pooled WebSocket owns its TLS client and the
/// pool may move that socket between client threads.
pub trait CertVerifier: Send {
    /// Checks one server certificate chain and its `CertificateVerify`
    /// signature.
    ///
    /// # Errors
    ///
    /// Returns [`TlsError`] when the chain is untrusted, the certificate is
    /// expired or not valid for the SNI name, or the signature is invalid.
    fn verify(&self, context: &CertVerifyContext<'_>) -> Result<(), TlsError>;
}

/// Test-only verifier that accepts everything.
///
/// It performs no chain validation, no name check and no signature check.  Use
/// it only with [`ServerCertPolicy::Skip`].
#[derive(Debug, Clone, Copy, Default)]
pub struct SkipVerifier;

impl CertVerifier for SkipVerifier {
    fn verify(&self, _context: &CertVerifyContext<'_>) -> Result<(), TlsError> {
        Ok(())
    }
}

/// Configuration for [`Tls13Client`].
pub struct Tls13ClientConfig {
    /// Server name used for SNI and the transcript.
    pub server_name: String,
    /// ALPN protocols in preference order; defaults to `h2` then `http/1.1`.
    pub alpn: Vec<Vec<u8>>,
    /// Certificate policy; defaults to [`ServerCertPolicy::WebPki`].
    pub cert_policy: ServerCertPolicy,
    /// Protected-record padding block; `0` disables padding.
    pub padding_block: usize,
    rng: Box<dyn Random32>,
    verifier: Box<dyn CertVerifier>,
}

impl Tls13ClientConfig {
    /// Creates a configuration with the default ALPN list, Mozilla's root CA
    /// bundle and a real certificate verifier.
    #[must_use]
    pub fn new(server_name: impl Into<String>, rng: Box<dyn Random32>) -> Self {
        Self {
            server_name: server_name.into(),
            alpn: vec![b"h2".to_vec(), b"http/1.1".to_vec()],
            cert_policy: ServerCertPolicy::WebPki,
            padding_block: 0,
            rng,
            verifier: Box::new(WebPkiVerifier::default()),
        }
    }

    /// Replaces the offered ALPN protocols.
    #[must_use]
    pub fn with_alpn(mut self, protocols: &[&[u8]]) -> Self {
        self.alpn = protocols.iter().map(|protocol| protocol.to_vec()).collect();
        self
    }

    /// Selects the certificate policy.
    #[must_use]
    pub fn with_cert_policy(mut self, policy: ServerCertPolicy) -> Self {
        self.cert_policy = policy;
        self
    }

    /// Sets the protected-record padding block.
    #[must_use]
    pub fn with_padding_block(mut self, block: usize) -> Self {
        self.padding_block = block;
        self
    }

    /// Installs a certificate verifier used by [`ServerCertPolicy::WebPki`].
    #[must_use]
    pub fn with_verifier(mut self, verifier: Box<dyn CertVerifier>) -> Self {
        self.verifier = verifier;
        self
    }
}

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum State {
    Start,
    WaitServerHello,
    Handshake,
    Connected,
}

/// TLS 1.3 client handshake and record engine.
pub struct Tls13Client {
    config: Tls13ClientConfig,
    state: State,
    key_pair: Option<X25519KeyPair>,
    transcript: Vec<u8>,
    read_buffer: Vec<u8>,
    handshake_buffer: Vec<u8>,
    pending_outgoing: Vec<u8>,
    application_data: Vec<u8>,
    schedule: Option<KeySchedule>,
    suite: Option<CipherSuite>,
    client_hs_secret: [u8; HASH_LEN],
    server_hs_secret: [u8; HASH_LEN],
    client_application_secret: Option<[u8; HASH_LEN]>,
    server_application_secret: Option<[u8; HASH_LEN]>,
    client_hs_protector: Option<RecordProtector>,
    outgoing: Option<RecordProtector>,
    incoming: Option<RecordUnprotector>,
    negotiated_alpn: Option<Vec<u8>>,
    certificate_chain: Vec<Vec<u8>>,
    certificate_verified: bool,
}

impl Tls13Client {
    /// Creates a client that is ready for [`Tls13Client::start`].
    #[must_use]
    pub fn new(config: Tls13ClientConfig) -> Self {
        Self {
            config,
            state: State::Start,
            key_pair: None,
            transcript: Vec::new(),
            read_buffer: Vec::new(),
            handshake_buffer: Vec::new(),
            pending_outgoing: Vec::new(),
            application_data: Vec::new(),
            schedule: None,
            suite: None,
            client_hs_secret: [0u8; HASH_LEN],
            server_hs_secret: [0u8; HASH_LEN],
            client_application_secret: None,
            server_application_secret: None,
            client_hs_protector: None,
            outgoing: None,
            incoming: None,
            negotiated_alpn: None,
            certificate_chain: Vec::new(),
            certificate_verified: false,
        }
    }

    /// Builds the ClientHello record and starts the handshake.
    ///
    /// The returned bytes must be sent to the server unchanged.  The key share
    /// in the ClientHello is the public half of the ephemeral X25519 key the
    /// client keeps for the rest of the handshake.
    ///
    /// # Errors
    ///
    /// Returns [`TlsError::InvalidMessageType`] when called more than once.
    pub fn start(&mut self) -> Result<Vec<u8>, TlsError> {
        if self.state != State::Start {
            return Err(TlsError::InvalidMessageType);
        }

        let server_name = self.config.server_name.clone();
        let protocols = self.config.alpn.clone();
        let protocol_refs: Vec<&[u8]> = protocols.iter().map(Vec::as_slice).collect();

        let key_pair = X25519KeyPair::generate(&mut *self.config.rng);
        let public = key_pair.public_key();
        let mut injector = KeyShareInjector {
            inner: &mut *self.config.rng,
            public,
            fill_calls: 0,
        };
        let flight = ClientHelloBuilder::new(&server_name)
            .with_profile(Chrome120Profile)
            .with_alpn(&protocol_refs)
            .build(&mut injector);
        // The ClientHello builder historically encoded each ALPN protocol with
        // a two-byte length.  RFC 7301 uses one byte; rewrite only that
        // extension so the fingerprint builder stays the single source of the
        // greeting layout.
        let flight = rewrite_alpn(&flight, &protocols)?;

        // The builder always emits the ClientHello as one handshake record.
        // The transcript covers the handshake message, not the record header.
        self.transcript.extend_from_slice(&flight[5..]);
        self.key_pair = Some(key_pair);
        self.state = State::WaitServerHello;
        Ok(flight)
    }

    /// Consumes received bytes and returns bytes that must be written back.
    ///
    /// The method absorbs partial records and may be called with as little as
    /// one byte at a time.  Once the server `Finished` has been verified, the
    /// returned buffer contains the encrypted client `Finished`; later calls
    /// normally return an empty vector.
    ///
    /// # Errors
    ///
    /// Returns [`TlsError`] when a record or handshake message is malformed,
    /// authentication fails, or the server selects an unsupported parameter.
    pub fn advance(&mut self, bytes: &[u8]) -> Result<Vec<u8>, TlsError> {
        self.read_buffer.extend_from_slice(bytes);
        let mut outgoing = Vec::new();

        loop {
            if self.read_buffer.len() < 5 {
                break;
            }
            let record_len =
                u16::from_be_bytes([self.read_buffer[3], self.read_buffer[4]]) as usize;
            let total = 5usize
                .checked_add(record_len)
                .ok_or(TlsError::LengthMismatch)?;
            if self.read_buffer.len() < total {
                break;
            }
            let record: Vec<u8> = self.read_buffer.drain(..total).collect();
            self.process_record(&record)?;
            outgoing.append(&mut self.pending_outgoing);
        }

        Ok(outgoing)
    }

    /// Encrypts application data with the negotiated client application key.
    ///
    /// Large payloads are split into records of at most [`MAX_PLAINTEXT`]
    /// bytes.
    ///
    /// # Errors
    ///
    /// Returns [`TlsError::SecretNotDerived`] before the handshake completes and
    /// propagates record-protection errors.
    pub fn send_application_data(&mut self, data: &[u8]) -> Result<Vec<u8>, TlsError> {
        if self.state != State::Connected {
            return Err(TlsError::SecretNotDerived);
        }
        let protector = self.outgoing.as_mut().ok_or(TlsError::SecretNotDerived)?;
        let mut out = Vec::new();
        for chunk in data.chunks(MAX_PLAINTEXT) {
            out.extend_from_slice(&protector.protect(CONTENT_TYPE_APPLICATION_DATA, chunk)?);
        }
        Ok(out)
    }

    /// Removes and returns application data decrypted since the last call.
    #[must_use]
    pub fn take_application_data(&mut self) -> Vec<u8> {
        std::mem::take(&mut self.application_data)
    }

    /// Reports whether the handshake has completed.
    #[must_use]
    pub fn is_connected(&self) -> bool {
        self.state == State::Connected
    }

    /// Returns the ALPN protocol selected by the server, if any.
    #[must_use]
    pub fn negotiated_alpn(&self) -> Option<&[u8]> {
        self.negotiated_alpn.as_deref()
    }

    /// Returns the cipher suite selected by the server once known.
    #[must_use]
    pub fn cipher_suite(&self) -> Option<CipherSuite> {
        self.suite
    }

    /// Returns the client handshake traffic secret (test and diagnostics use).
    #[must_use]
    pub fn client_handshake_secret(&self) -> &[u8; HASH_LEN] {
        &self.client_hs_secret
    }

    /// Returns the server handshake traffic secret (test and diagnostics use).
    #[must_use]
    pub fn server_handshake_secret(&self) -> &[u8; HASH_LEN] {
        &self.server_hs_secret
    }

    /// Returns the client application traffic secret after the handshake.
    #[must_use]
    pub fn client_application_secret(&self) -> Option<&[u8; HASH_LEN]> {
        self.client_application_secret.as_ref()
    }

    /// Returns the server application traffic secret after the handshake.
    #[must_use]
    pub fn server_application_secret(&self) -> Option<&[u8; HASH_LEN]> {
        self.server_application_secret.as_ref()
    }

    fn process_record(&mut self, record: &[u8]) -> Result<(), TlsError> {
        match record[0] {
            CONTENT_TYPE_CHANGE_CIPHER_SPEC => Ok(()),
            CONTENT_TYPE_ALERT => self.handle_alert(&record[5..]),
            CONTENT_TYPE_HANDSHAKE => {
                if self.state != State::WaitServerHello {
                    return Err(TlsError::InvalidMessageType);
                }
                self.feed_handshake(&record[5..])
            }
            CONTENT_TYPE_APPLICATION_DATA => {
                let (content_type, plaintext) = {
                    let incoming = self.incoming.as_mut().ok_or(TlsError::SecretNotDerived)?;
                    incoming.unprotect(record)?
                };
                match content_type {
                    CONTENT_TYPE_HANDSHAKE => self.feed_handshake(&plaintext),
                    CONTENT_TYPE_APPLICATION_DATA => {
                        self.application_data.extend_from_slice(&plaintext);
                        Ok(())
                    }
                    CONTENT_TYPE_ALERT => self.handle_alert(&plaintext),
                    _ => Err(TlsError::InvalidMessageType),
                }
            }
            _ => Err(TlsError::InvalidMessageType),
        }
    }

    fn handle_alert(&self, payload: &[u8]) -> Result<(), TlsError> {
        let alert = Alert::parse(payload).map_err(|_| TlsError::LengthMismatch)?;
        // close_notify (0) and warning alerts end the stream without an error.
        if alert.description == 0 || alert.level == ALERT_LEVEL_WARNING {
            Ok(())
        } else {
            Err(TlsError::AeadFailure)
        }
    }

    fn feed_handshake(&mut self, bytes: &[u8]) -> Result<(), TlsError> {
        self.handshake_buffer.extend_from_slice(bytes);
        loop {
            if self.handshake_buffer.len() < 4 {
                break;
            }
            let length = ((self.handshake_buffer[1] as usize) << 16)
                | ((self.handshake_buffer[2] as usize) << 8)
                | self.handshake_buffer[3] as usize;
            let total = 4usize.checked_add(length).ok_or(TlsError::LengthMismatch)?;
            if self.handshake_buffer.len() < total {
                break;
            }
            let message: Vec<u8> = self.handshake_buffer.drain(..total).collect();
            self.handle_handshake_message(&message)?;
        }
        Ok(())
    }

    fn handle_handshake_message(&mut self, message: &[u8]) -> Result<(), TlsError> {
        if message.len() < 4 {
            return Err(TlsError::ShortInput);
        }
        match message[0] {
            HANDSHAKE_SERVER_HELLO => {
                if self.state != State::WaitServerHello {
                    return Err(TlsError::InvalidMessageType);
                }
                let hello = ServerHello::parse(message)?;
                self.handle_server_hello(&hello, message)
            }
            HANDSHAKE_ENCRYPTED_EXTENSIONS => {
                self.require_handshake()?;
                let extensions = EncryptedExtensions::parse(message)?;
                self.handle_encrypted_extensions(&extensions)?;
                self.transcript.extend_from_slice(message);
                Ok(())
            }
            HANDSHAKE_CERTIFICATE => {
                self.require_handshake()?;
                let certificate = Certificate::parse(message)?;
                self.certificate_chain = certificate
                    .entries
                    .iter()
                    .map(|entry| entry.certificate.to_vec())
                    .collect();
                self.certificate_verified = false;
                self.transcript.extend_from_slice(message);
                Ok(())
            }
            HANDSHAKE_CERTIFICATE_VERIFY => {
                self.require_handshake()?;
                let verify = CertificateVerify::parse(message)?;
                self.verify_certificate_verify(verify.algorithm, verify.signature)?;
                self.transcript.extend_from_slice(message);
                Ok(())
            }
            HANDSHAKE_FINISHED => {
                self.require_handshake()?;
                let finished = Finished::parse(message)?;
                self.handle_server_finished(finished.verify_data, message)
            }
            HANDSHAKE_NEW_SESSION_TICKET | HANDSHAKE_KEY_UPDATE => {
                if self.state != State::Connected {
                    return Err(TlsError::InvalidMessageType);
                }
                Ok(())
            }
            _ => Err(TlsError::InvalidMessageType),
        }
    }

    fn require_handshake(&self) -> Result<(), TlsError> {
        if self.state == State::Handshake {
            Ok(())
        } else {
            Err(TlsError::InvalidMessageType)
        }
    }

    fn handle_server_hello(
        &mut self,
        hello: &ServerHello<'_>,
        message: &[u8],
    ) -> Result<(), TlsError> {
        if !hello.is_tls13() {
            return Err(TlsError::UnsupportedVersion(
                hello.supported_version.unwrap_or(hello.legacy_version),
            ));
        }
        let suite = CipherSuite::from_code(hello.cipher_suite)
            .ok_or(TlsError::UnsupportedCipherSuite(hello.cipher_suite))?;
        let key_share = hello.key_share.ok_or(TlsError::MalformedExtensions)?;
        if key_share.group != GROUP_X25519 {
            return Err(TlsError::UnsupportedGroup(key_share.group));
        }
        let peer_public: [u8; 32] = key_share
            .key_exchange
            .try_into()
            .map_err(|_| TlsError::LengthMismatch)?;
        let key_pair = self.key_pair.as_ref().ok_or(TlsError::SecretNotDerived)?;
        let shared = key_pair.diffie_hellman(&peer_public)?;

        self.transcript.extend_from_slice(message);
        let transcript_hash = self.transcript_hash();

        let mut schedule = KeySchedule::new(suite);
        schedule.advance_handshake(&shared)?;
        let client_hs_secret = schedule.client_handshake_traffic_secret(&transcript_hash)?;
        let server_hs_secret = schedule.server_handshake_traffic_secret(&transcript_hash)?;

        let client_keys = TrafficKeys::derive(&client_hs_secret, suite)?;
        let server_keys = TrafficKeys::derive(&server_hs_secret, suite)?;
        self.client_hs_protector = Some(RecordProtector::from_traffic_keys(
            &client_keys,
            suite,
            self.config.padding_block,
        )?);
        self.incoming = Some(RecordUnprotector::from_traffic_keys(&server_keys, suite)?);

        self.client_hs_secret = client_hs_secret;
        self.server_hs_secret = server_hs_secret;
        self.schedule = Some(schedule);
        self.suite = Some(suite);
        self.state = State::Handshake;
        Ok(())
    }

    fn handle_encrypted_extensions(
        &mut self,
        extensions: &EncryptedExtensions<'_>,
    ) -> Result<(), TlsError> {
        let Some(data) = extensions.extensions.find(EXTENSION_ALPN) else {
            return Ok(());
        };
        let selected = parse_alpn(data)?;
        if !self
            .config
            .alpn
            .iter()
            .any(|protocol| protocol == &selected)
        {
            return Err(TlsError::MalformedExtensions);
        }
        self.negotiated_alpn = Some(selected);
        Ok(())
    }

    fn verify_certificate_verify(
        &mut self,
        algorithm: u16,
        signature: &[u8],
    ) -> Result<(), TlsError> {
        if self.config.cert_policy == ServerCertPolicy::Skip {
            self.certificate_verified = true;
            return Ok(());
        }
        let result = {
            let transcript_hash = self.transcript_hash();
            let context = CertVerifyContext {
                transcript_hash: &transcript_hash,
                server_name: &self.config.server_name,
                certificate_chain: &self.certificate_chain,
                algorithm,
                signature,
            };
            self.config.verifier.verify(&context)
        };
        if result.is_ok() {
            self.certificate_verified = true;
        }
        result
    }

    fn handle_server_finished(
        &mut self,
        verify_data: &[u8],
        message: &[u8],
    ) -> Result<(), TlsError> {
        if !self.certificate_verified {
            return Err(TlsError::CertificateVerificationFailed);
        }
        let expected = self.server_verify_data()?;
        if !constant_time_eq(&expected, verify_data) {
            return Err(TlsError::AeadFailure);
        }

        self.transcript.extend_from_slice(message);
        let transcript_hash = self.transcript_hash();
        let suite = self.suite.ok_or(TlsError::SecretNotDerived)?;

        let (client_application_secret, server_application_secret) = {
            let schedule = self.schedule.as_mut().ok_or(TlsError::SecretNotDerived)?;
            schedule.advance_master()?;
            (
                schedule.client_application_traffic_secret(&transcript_hash)?,
                schedule.server_application_traffic_secret(&transcript_hash)?,
            )
        };

        let server_keys = TrafficKeys::derive(&server_application_secret, suite)?;
        let client_keys = TrafficKeys::derive(&client_application_secret, suite)?;
        self.incoming = Some(RecordUnprotector::from_traffic_keys(&server_keys, suite)?);

        let finished_base = finished_key(&self.client_hs_secret)?;
        let client_verify_data = hmac_sha256(&finished_base, &transcript_hash);
        let client_finished = Finished {
            verify_data: &client_verify_data,
        }
        .encode()?;

        let mut handshake_protector = self
            .client_hs_protector
            .take()
            .ok_or(TlsError::SecretNotDerived)?;
        let finished_record =
            handshake_protector.protect(CONTENT_TYPE_HANDSHAKE, &client_finished)?;
        self.pending_outgoing.extend_from_slice(&finished_record);
        self.transcript.extend_from_slice(&client_finished);

        self.outgoing = Some(RecordProtector::from_traffic_keys(
            &client_keys,
            suite,
            self.config.padding_block,
        )?);
        self.client_application_secret = Some(client_application_secret);
        self.server_application_secret = Some(server_application_secret);
        self.state = State::Connected;
        Ok(())
    }

    fn server_verify_data(&self) -> Result<[u8; HASH_LEN], TlsError> {
        let base = finished_key(&self.server_hs_secret)?;
        Ok(hmac_sha256(&base, &self.transcript_hash()))
    }

    fn transcript_hash(&self) -> [u8; HASH_LEN] {
        let digest = Sha256::digest(&self.transcript);
        let mut out = [0u8; HASH_LEN];
        out.copy_from_slice(&digest);
        out
    }
}

/// Fills the three 32-byte draws of the ClientHello builder, replacing the
/// key_share draw with the public key of the already generated key pair.
struct KeyShareInjector<'a> {
    inner: &'a mut dyn Random32,
    public: [u8; 32],
    fill_calls: usize,
}

impl Random32 for KeyShareInjector<'_> {
    fn next_u32(&mut self) -> u32 {
        self.inner.next_u32()
    }

    fn fill_bytes(&mut self, dst: &mut [u8]) {
        let call = self.fill_calls;
        self.fill_calls += 1;
        if call == 2 && dst.len() == 32 {
            dst.copy_from_slice(&self.public);
        } else {
            self.inner.fill_bytes(dst);
        }
    }
}

/// Rewrites the ALPN extension of a single-record ClientHello using the RFC
/// 7301 one-byte protocol lengths.
fn rewrite_alpn(flight: &[u8], protocols: &[Vec<u8>]) -> Result<Vec<u8>, TlsError> {
    if flight.len() < 9 || flight[0] != CONTENT_TYPE_HANDSHAKE || flight[5] != 0x01 {
        return Err(TlsError::ShortInput);
    }
    let record_len = u16::from_be_bytes([flight[3], flight[4]]) as usize;
    if flight.len() != 5 + record_len {
        return Err(TlsError::LengthMismatch);
    }
    let handshake_len =
        ((flight[6] as usize) << 16) | ((flight[7] as usize) << 8) | flight[8] as usize;
    if record_len != 4 + handshake_len {
        return Err(TlsError::LengthMismatch);
    }

    let body = &flight[9..];
    let mut position = 2 + 32;
    let session_id_len = *body.get(position).ok_or(TlsError::ShortInput)? as usize;
    position += 1 + session_id_len;
    let cipher_len_bytes = body
        .get(position..position + 2)
        .ok_or(TlsError::ShortInput)?;
    let cipher_len = u16::from_be_bytes([cipher_len_bytes[0], cipher_len_bytes[1]]) as usize;
    position += 2 + cipher_len;
    let compression_len = *body.get(position).ok_or(TlsError::ShortInput)? as usize;
    position += 1 + compression_len;
    let extension_len_bytes = body
        .get(position..position + 2)
        .ok_or(TlsError::ShortInput)?;
    let extension_len =
        u16::from_be_bytes([extension_len_bytes[0], extension_len_bytes[1]]) as usize;
    position += 2;
    if position + extension_len != body.len() {
        return Err(TlsError::LengthMismatch);
    }

    let extensions = &body[position..position + extension_len];
    let mut rewritten = Vec::with_capacity(extensions.len() + 8);
    let mut cursor = 0;
    let mut replaced = false;
    while cursor < extensions.len() {
        let header = extensions
            .get(cursor..cursor + 4)
            .ok_or(TlsError::MalformedExtensions)?;
        let extension_type = u16::from_be_bytes([header[0], header[1]]);
        let data_len = u16::from_be_bytes([header[2], header[3]]) as usize;
        let data_start = cursor + 4;
        let data_end = data_start
            .checked_add(data_len)
            .ok_or(TlsError::MalformedExtensions)?;
        if data_end > extensions.len() {
            return Err(TlsError::MalformedExtensions);
        }

        if extension_type == EXTENSION_ALPN {
            let data = encode_alpn(protocols)?;
            push_extension(&mut rewritten, EXTENSION_ALPN, &data)?;
            replaced = true;
        } else {
            rewritten.extend_from_slice(&extensions[cursor..data_end]);
        }
        cursor = data_end;
    }
    if !replaced && !protocols.is_empty() {
        let data = encode_alpn(protocols)?;
        push_extension(&mut rewritten, EXTENSION_ALPN, &data)?;
    }
    if rewritten.len() > u16::MAX as usize {
        return Err(TlsError::LengthMismatch);
    }

    let mut new_body = Vec::with_capacity(position - 2 + 2 + rewritten.len());
    new_body.extend_from_slice(&body[..position - 2]);
    new_body.extend_from_slice(&(rewritten.len() as u16).to_be_bytes());
    new_body.extend_from_slice(&rewritten);

    let new_handshake_len = 4 + new_body.len();
    if new_handshake_len > u16::MAX as usize {
        return Err(TlsError::LengthMismatch);
    }
    let mut out = Vec::with_capacity(5 + new_handshake_len);
    out.push(flight[0]);
    out.extend_from_slice(&flight[1..3]);
    out.extend_from_slice(&(new_handshake_len as u16).to_be_bytes());
    out.push(0x01);
    out.push(((new_body.len() >> 16) & 0xff) as u8);
    out.push(((new_body.len() >> 8) & 0xff) as u8);
    out.push((new_body.len() & 0xff) as u8);
    out.extend_from_slice(&new_body);
    Ok(out)
}

fn encode_alpn(protocols: &[Vec<u8>]) -> Result<Vec<u8>, TlsError> {
    let mut entries = Vec::new();
    for protocol in protocols {
        if protocol.len() > u8::MAX as usize {
            return Err(TlsError::LengthMismatch);
        }
        entries.push(protocol.len() as u8);
        entries.extend_from_slice(protocol);
    }
    if entries.len() > u16::MAX as usize {
        return Err(TlsError::LengthMismatch);
    }
    let mut data = Vec::with_capacity(2 + entries.len());
    data.extend_from_slice(&(entries.len() as u16).to_be_bytes());
    data.extend_from_slice(&entries);
    Ok(data)
}

fn push_extension(out: &mut Vec<u8>, extension_type: u16, data: &[u8]) -> Result<(), TlsError> {
    if data.len() > u16::MAX as usize {
        return Err(TlsError::LengthMismatch);
    }
    out.extend_from_slice(&extension_type.to_be_bytes());
    out.extend_from_slice(&(data.len() as u16).to_be_bytes());
    out.extend_from_slice(data);
    Ok(())
}

fn parse_alpn(data: &[u8]) -> Result<Vec<u8>, TlsError> {
    if data.len() < 2 {
        return Err(TlsError::MalformedExtensions);
    }
    let list_len = u16::from_be_bytes([data[0], data[1]]) as usize;
    if data.len() != 2 + list_len {
        return Err(TlsError::MalformedExtensions);
    }

    let mut position = 2;
    let mut selected = None;
    while position < data.len() {
        let length = data[position] as usize;
        position += 1;
        let end = position
            .checked_add(length)
            .ok_or(TlsError::MalformedExtensions)?;
        if end > data.len() {
            return Err(TlsError::MalformedExtensions);
        }
        if selected.is_none() {
            selected = Some(data[position..end].to_vec());
        }
        position = end;
    }
    selected.ok_or(TlsError::MalformedExtensions)
}

fn constant_time_eq(left: &[u8], right: &[u8]) -> bool {
    if left.len() != right.len() {
        return false;
    }
    let mut difference = 0u8;
    for (left, right) in left.iter().zip(right) {
        difference |= left ^ right;
    }
    difference == 0
}

fn hmac_sha256(key: &[u8], data: &[u8]) -> [u8; HASH_LEN] {
    const BLOCK_SIZE: usize = 64;
    let mut key_block = [0u8; BLOCK_SIZE];
    if key.len() > BLOCK_SIZE {
        let digest = Sha256::digest(key);
        key_block[..HASH_LEN].copy_from_slice(&digest);
    } else {
        key_block[..key.len()].copy_from_slice(key);
    }

    let mut inner_pad = [0u8; BLOCK_SIZE];
    let mut outer_pad = [0u8; BLOCK_SIZE];
    for ((inner, outer), key) in inner_pad.iter_mut().zip(&mut outer_pad).zip(&key_block) {
        *inner = key ^ 0x36;
        *outer = key ^ 0x5c;
    }

    let mut inner = Sha256::new();
    inner.update(inner_pad);
    inner.update(data);
    let inner_digest = inner.finalize();

    let mut outer = Sha256::new();
    outer.update(outer_pad);
    outer.update(inner_digest);
    let result = outer.finalize();

    let mut out = [0u8; HASH_LEN];
    out.copy_from_slice(&result);
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    use crate::tls::client_hello::SplitMix64;
    use crate::tls::parse::parse_client_hello;
    use crate::tls13::messages::{CertificateEntry, Extension, KeyShareEntry, TLS_1_3};

    struct ServerFlight {
        bytes: Vec<u8>,
        schedule: KeySchedule,
        transcript_through_server_finished: Vec<u8>,
        client_hs_secret: [u8; HASH_LEN],
        suite: CipherSuite,
    }

    fn sha256(data: &[u8]) -> [u8; HASH_LEN] {
        let digest = Sha256::digest(data);
        let mut out = [0u8; HASH_LEN];
        out.copy_from_slice(&digest);
        out
    }

    fn extract_client_key_share(flight: &[u8]) -> [u8; 32] {
        let layout = parse_client_hello(flight).expect("ClientHello parses");
        let extensions = &flight[layout.extensions_start..layout.extensions_end];
        let mut position = 0;
        while position + 4 <= extensions.len() {
            let extension_type =
                u16::from_be_bytes([extensions[position], extensions[position + 1]]);
            let length =
                u16::from_be_bytes([extensions[position + 2], extensions[position + 3]]) as usize;
            let data = &extensions[position + 4..position + 4 + length];
            if extension_type == 0x0033 {
                let entry = KeyShareEntry::parse(&data[2..]).expect("key share entry");
                return entry.key_exchange.try_into().expect("x25519 key");
            }
            position += 4 + length;
        }
        panic!("ClientHello has no key_share extension");
    }

    fn build_server_flight(client_hello: &[u8], tamper_finished: bool) -> ServerFlight {
        let client_public = extract_client_key_share(client_hello);
        let server_pair = X25519KeyPair::from_seed([0x5a; 32]);
        let server_public = server_pair.public_key();
        let shared = server_pair
            .diffie_hellman(&client_public)
            .expect("server DH");

        let suite = CipherSuite::Aes128GcmSha256;
        let mut transcript = client_hello[5..].to_vec();

        let server_hello = ServerHello {
            legacy_version: 0x0303,
            random: [0x33; 32],
            session_id: &[],
            cipher_suite: suite.code(),
            compression_method: 0,
            supported_version: Some(TLS_1_3),
            key_share: Some(KeyShareEntry {
                group: GROUP_X25519,
                key_exchange: &server_public,
            }),
        }
        .encode()
        .expect("ServerHello encodes");
        transcript.extend_from_slice(&server_hello);
        let ch_sh_hash = sha256(&transcript);

        let mut schedule = KeySchedule::new(suite);
        schedule
            .advance_handshake(&shared)
            .expect("handshake secret");
        let client_hs_secret = schedule
            .client_handshake_traffic_secret(&ch_sh_hash)
            .expect("client hs secret");
        let server_hs_secret = schedule
            .server_handshake_traffic_secret(&ch_sh_hash)
            .expect("server hs secret");

        let server_hs_keys = TrafficKeys::derive(&server_hs_secret, suite).expect("server keys");
        let mut server_protector = RecordProtector::from_traffic_keys(&server_hs_keys, suite, 0)
            .expect("server protector");

        let mut alpn_data = vec![0x00, 0x09, 0x08];
        alpn_data.extend_from_slice(b"http/1.1");
        let mut encrypted_extensions = Vec::new();
        Extension {
            extension_type: EXTENSION_ALPN,
            data: &alpn_data,
        }
        .encode(&mut encrypted_extensions)
        .expect("ALPN extension");
        let encrypted_extensions = EncryptedExtensions::new(&encrypted_extensions)
            .expect("EncryptedExtensions")
            .encode()
            .expect("EncryptedExtensions encodes");

        let certificate = Certificate {
            certificate_request_context: &[],
            entries: vec![CertificateEntry {
                certificate: &[0x30, 0x03, 0x02, 0x01, 0x01],
                extensions: &[],
            }],
        }
        .encode()
        .expect("Certificate encodes");

        let certificate_verify = CertificateVerify {
            algorithm: 0x0804,
            signature: &[0xaa; 16],
        }
        .encode()
        .expect("CertificateVerify encodes");

        transcript.extend_from_slice(&encrypted_extensions);
        transcript.extend_from_slice(&certificate);
        transcript.extend_from_slice(&certificate_verify);

        let before_finished = sha256(&transcript);
        let server_finished_key = finished_key(&server_hs_secret).expect("finished key");
        let mut verify_data = hmac_sha256(&server_finished_key, &before_finished);
        if tamper_finished {
            verify_data[0] ^= 0x01;
        }
        let server_finished = Finished {
            verify_data: &verify_data,
        }
        .encode()
        .expect("Finished encodes");
        transcript.extend_from_slice(&server_finished);

        let mut handshake_messages = encrypted_extensions;
        handshake_messages.extend_from_slice(&certificate);
        handshake_messages.extend_from_slice(&certificate_verify);

        let mut bytes = vec![
            CONTENT_TYPE_HANDSHAKE,
            0x03,
            0x03,
            (server_hello.len() >> 8) as u8,
            server_hello.len() as u8,
        ];
        bytes.extend_from_slice(&server_hello);
        bytes.extend_from_slice(
            &server_protector
                .protect(CONTENT_TYPE_HANDSHAKE, &handshake_messages)
                .expect("protect handshake"),
        );
        bytes.extend_from_slice(
            &server_protector
                .protect(CONTENT_TYPE_HANDSHAKE, &server_finished)
                .expect("protect Finished"),
        );

        ServerFlight {
            bytes,
            schedule,
            transcript_through_server_finished: transcript,
            client_hs_secret,
            suite,
        }
    }

    #[test]
    fn full_handshake_against_mini_server() {
        let config = Tls13ClientConfig::new("example.test", Box::new(SplitMix64::new(7)))
            .with_alpn(&[b"h2", b"http/1.1"])
            .with_cert_policy(ServerCertPolicy::Skip);
        let mut client = Tls13Client::new(config);

        let client_hello = client.start().expect("start");
        let layout = parse_client_hello(&client_hello).expect("ClientHello parses");
        assert_eq!(layout.sni(&client_hello), Some(b"example.test".as_slice()));

        let flight = build_server_flight(&client_hello, false);
        let outgoing = client.advance(&flight.bytes).expect("server flight");

        assert!(client.is_connected());
        assert_eq!(client.negotiated_alpn(), Some(b"http/1.1".as_slice()));
        assert_eq!(client.cipher_suite(), Some(flight.suite));

        assert_eq!(client.client_handshake_secret(), &flight.client_hs_secret);
        assert_ne!(client.server_handshake_secret(), &[0u8; HASH_LEN]);

        let client_hs_keys =
            TrafficKeys::derive(&flight.client_hs_secret, flight.suite).expect("client hs keys");
        let mut client_hs_unprotector =
            RecordUnprotector::from_traffic_keys(&client_hs_keys, flight.suite)
                .expect("client hs unprotector");
        let (content_type, plaintext) = client_hs_unprotector
            .unprotect(&outgoing)
            .expect("client Finished decrypts");
        assert_eq!(content_type, CONTENT_TYPE_HANDSHAKE);

        let client_finished = Finished::parse(&plaintext).expect("client Finished parses");
        let client_finished_key =
            finished_key(&flight.client_hs_secret).expect("client finished key");
        let expected_client_verify = hmac_sha256(
            &client_finished_key,
            &sha256(&flight.transcript_through_server_finished),
        );
        assert_eq!(client_finished.verify_data, &expected_client_verify);

        let application_hash = sha256(&flight.transcript_through_server_finished);
        let mut schedule = flight.schedule;
        schedule.advance_master().expect("master secret");
        let client_application_secret = schedule
            .client_application_traffic_secret(&application_hash)
            .expect("client app secret");
        let server_application_secret = schedule
            .server_application_traffic_secret(&application_hash)
            .expect("server app secret");
        assert_eq!(
            client.client_application_secret(),
            Some(&client_application_secret)
        );
        assert_eq!(
            client.server_application_secret(),
            Some(&server_application_secret)
        );

        let client_application_keys =
            TrafficKeys::derive(&client_application_secret, flight.suite).expect("client app keys");
        let mut client_application_unprotector =
            RecordUnprotector::from_traffic_keys(&client_application_keys, flight.suite)
                .expect("client app unprotector");

        let client_record = client
            .send_application_data(b"ping from client")
            .expect("client app data");
        assert_eq!(
            client_application_unprotector
                .unprotect(&client_record)
                .expect("server decrypts client app data"),
            (CONTENT_TYPE_APPLICATION_DATA, b"ping from client".to_vec())
        );

        let server_application_keys =
            TrafficKeys::derive(&server_application_secret, flight.suite).expect("server app keys");
        let mut server_application_protector =
            RecordProtector::from_traffic_keys(&server_application_keys, flight.suite, 0)
                .expect("server app protector");
        let server_record = server_application_protector
            .protect(CONTENT_TYPE_APPLICATION_DATA, b"pong from server")
            .expect("server app data");

        assert!(
            client
                .advance(&server_record)
                .expect("client app data")
                .is_empty()
        );
        assert_eq!(client.take_application_data(), b"pong from server");
    }

    #[test]
    fn client_rejects_tampered_server_finished() {
        let config = Tls13ClientConfig::new("example.test", Box::new(SplitMix64::new(11)))
            .with_cert_policy(ServerCertPolicy::Skip);
        let mut client = Tls13Client::new(config);
        let client_hello = client.start().expect("start");
        let flight = build_server_flight(&client_hello, true);

        assert_eq!(client.advance(&flight.bytes), Err(TlsError::AeadFailure));
        assert!(!client.is_connected());
    }

    #[test]
    fn default_config_rejects_untrusted_server_certificate() {
        let config = Tls13ClientConfig::new("example.test", Box::new(SplitMix64::new(19)));
        assert_eq!(config.cert_policy, ServerCertPolicy::WebPki);
        let mut client = Tls13Client::new(config);
        let client_hello = client.start().expect("start");
        let flight = build_server_flight(&client_hello, false);

        assert_eq!(
            client.advance(&flight.bytes),
            Err(TlsError::CertificateVerificationFailed)
        );
        assert!(!client.is_connected());
    }

    #[test]
    fn client_hello_alpn_uses_rfc7301_lengths() {
        let config = Tls13ClientConfig::new("alpn.test", Box::new(SplitMix64::new(17)))
            .with_alpn(&[b"h2", b"http/1.1"]);
        let mut client = Tls13Client::new(config);
        let client_hello = client.start().expect("start");
        let layout = parse_client_hello(&client_hello).expect("ClientHello parses");
        let extensions = &client_hello[layout.extensions_start..layout.extensions_end];

        let mut position = 0;
        while position + 4 <= extensions.len() {
            let extension_type =
                u16::from_be_bytes([extensions[position], extensions[position + 1]]);
            let length =
                u16::from_be_bytes([extensions[position + 2], extensions[position + 3]]) as usize;
            let data = &extensions[position + 4..position + 4 + length];
            if extension_type == EXTENSION_ALPN {
                assert_eq!(&data[..2], &[0x00, 0x0c]);
                assert_eq!(&data[2..], b"\x02h2\x08http/1.1");
                return;
            }
            position += 4 + length;
        }
        panic!("ClientHello has no ALPN extension");
    }

    #[test]
    fn handshake_rejects_a_second_start() {
        let config = Tls13ClientConfig::new("example.test", Box::new(SplitMix64::new(13)));
        let mut client = Tls13Client::new(config);
        client.start().expect("first start");
        assert_eq!(client.start(), Err(TlsError::InvalidMessageType));
    }
}
