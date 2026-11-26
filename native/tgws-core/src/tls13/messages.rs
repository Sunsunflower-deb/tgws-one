//! TLS 1.3 handshake message structures (RFC 8446, section 4).
//!
//! The parsers split a complete handshake message (four-byte header plus body)
//! into borrowed fields so callers do not copy certificate chains or keys.  The
//! encoders rebuild the same wire layout.  No signature, certificate or MAC
//! verification happens here; that belongs to RT7b.
//!
//! Only the messages needed by a 1-RTT client handshake are modelled:
//! `ServerHello`, `EncryptedExtensions`, `Certificate`, `CertificateVerify`,
//! `Finished` and `KeyUpdate`, plus the `Alert` record payload.

use core::fmt;

use super::TlsError;

/// Handshake message type for `ServerHello` (RFC 8446, section 4).
pub const HANDSHAKE_SERVER_HELLO: u8 = 2;
/// Handshake message type for `EncryptedExtensions` (RFC 8446, section 4).
pub const HANDSHAKE_ENCRYPTED_EXTENSIONS: u8 = 8;
/// Handshake message type for `Certificate` (RFC 8446, section 4).
pub const HANDSHAKE_CERTIFICATE: u8 = 11;
/// Handshake message type for `CertificateRequest` (RFC 8446, section 4).
pub const HANDSHAKE_CERTIFICATE_REQUEST: u8 = 13;
/// Handshake message type for `CertificateVerify` (RFC 8446, section 4).
pub const HANDSHAKE_CERTIFICATE_VERIFY: u8 = 15;
/// Handshake message type for `Finished` (RFC 8446, section 4).
pub const HANDSHAKE_FINISHED: u8 = 20;
/// Handshake message type for `KeyUpdate` (RFC 8446, section 4).
pub const HANDSHAKE_KEY_UPDATE: u8 = 24;

/// Extension type for `supported_versions` (RFC 8446, section 4.2.1).
pub const EXTENSION_SUPPORTED_VERSIONS: u16 = 0x002b;
/// Extension type for `key_share` (RFC 8446, section 4.2.8).
pub const EXTENSION_KEY_SHARE: u16 = 0x0033;

/// TLS 1.3 version code point (RFC 8446, section 4.2.1).
pub const TLS_1_3: u16 = 0x0304;

/// X25519 key-exchange group (RFC 8446, section 4.2.7).
pub const GROUP_X25519: u16 = 0x001d;

/// Alert level `warning` (RFC 8446, section 6).
pub const ALERT_LEVEL_WARNING: u8 = 1;
/// Alert level `fatal` (RFC 8446, section 6).
pub const ALERT_LEVEL_FATAL: u8 = 2;

/// Header of a TLS handshake message.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct HandshakeHeader {
    /// Handshake message type.
    pub msg_type: u8,
    /// Length of the handshake body in bytes.
    pub length: usize,
}

impl HandshakeHeader {
    /// Returns the four-byte wire encoding of this header.
    ///
    /// # Errors
    ///
    /// Returns [`TlsError::LengthMismatch`] when the body length exceeds the
    /// 24-bit handshake length field.
    pub fn encode(self) -> Result<[u8; 4], TlsError> {
        if self.length > 0x00ff_ffff {
            return Err(TlsError::LengthMismatch);
        }
        let length = self.length as u32;
        Ok([
            self.msg_type,
            (length >> 16) as u8,
            (length >> 8) as u8,
            length as u8,
        ])
    }
}

/// Splits one complete handshake message into its header and body.
///
/// The buffer must contain exactly one message: trailing bytes are rejected so
/// callers cannot accidentally parse the first message of a larger flight.
///
/// # Errors
///
/// Returns [`TlsError::ShortInput`] when the header or body is truncated and
/// [`TlsError::LengthMismatch`] when trailing bytes remain.
pub fn split_handshake(buf: &[u8]) -> Result<(HandshakeHeader, &[u8]), TlsError> {
    let mut reader = Reader::new(buf);
    let msg_type = reader.u8()?;
    let length = reader.u24()?;
    if reader.remaining() < length {
        return Err(TlsError::ShortInput);
    }
    if reader.remaining() != length {
        return Err(TlsError::LengthMismatch);
    }
    let body = reader.take(length)?;
    Ok((HandshakeHeader { msg_type, length }, body))
}

/// Builds a complete handshake message from a type and body.
///
/// # Errors
///
/// Returns [`TlsError::LengthMismatch`] when the body exceeds the 24-bit
/// handshake length field.
pub fn encode_handshake(msg_type: u8, body: &[u8]) -> Result<Vec<u8>, TlsError> {
    let header = HandshakeHeader {
        msg_type,
        length: body.len(),
    }
    .encode()?;
    let mut out = Vec::with_capacity(4 + body.len());
    out.extend_from_slice(&header);
    out.extend_from_slice(body);
    Ok(out)
}

/// One extension entry inside an extensions block.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Extension<'a> {
    /// Extension type.
    pub extension_type: u16,
    /// Extension payload, excluding the four-byte type/length header.
    pub data: &'a [u8],
}

impl Extension<'_> {
    /// Encodes the extension into `out`.
    ///
    /// # Errors
    ///
    /// Returns [`TlsError::LengthMismatch`] when the payload is longer than
    /// `u16::MAX`.
    pub fn encode(&self, out: &mut Vec<u8>) -> Result<(), TlsError> {
        if self.data.len() > u16::MAX as usize {
            return Err(TlsError::LengthMismatch);
        }
        out.extend_from_slice(&self.extension_type.to_be_bytes());
        out.extend_from_slice(&(self.data.len() as u16).to_be_bytes());
        out.extend_from_slice(self.data);
        Ok(())
    }
}

/// A validated TLS extensions block.
///
/// Parsing only proves that every `type/length` pair fits inside the block; the
/// entries are kept as a slice and decoded on demand.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Extensions<'a> {
    raw: &'a [u8],
}

impl<'a> Extensions<'a> {
    /// Validates and wraps a raw extensions block.
    ///
    /// # Errors
    ///
    /// Returns [`TlsError::MalformedExtensions`] when an entry header is
    /// truncated or an entry length runs past the block.
    pub fn parse(raw: &'a [u8]) -> Result<Self, TlsError> {
        let mut rest = raw;
        while !rest.is_empty() {
            if rest.len() < 4 {
                return Err(TlsError::MalformedExtensions);
            }
            let length = u16::from_be_bytes([rest[2], rest[3]]) as usize;
            let end = 4usize
                .checked_add(length)
                .ok_or(TlsError::MalformedExtensions)?;
            if rest.len() < end {
                return Err(TlsError::MalformedExtensions);
            }
            rest = &rest[end..];
        }
        Ok(Self { raw })
    }

    /// Returns the raw extensions block.
    #[must_use]
    pub fn raw(&self) -> &'a [u8] {
        self.raw
    }

    /// Iterates over the extensions in wire order.
    #[must_use]
    pub fn iter(&self) -> ExtensionsIter<'a> {
        ExtensionsIter { rest: self.raw }
    }

    /// Returns the payload of the first extension of `extension_type`.
    #[must_use]
    pub fn find(&self, extension_type: u16) -> Option<&'a [u8]> {
        self.iter()
            .find(|extension| extension.extension_type == extension_type)
            .map(|extension| extension.data)
    }

    /// Re-encodes the extensions block without the outer length prefix.
    #[must_use]
    pub fn encode(&self) -> Vec<u8> {
        self.raw.to_vec()
    }
}

/// Iterator over a validated [`Extensions`] block.
#[derive(Debug, Clone)]
pub struct ExtensionsIter<'a> {
    rest: &'a [u8],
}

impl<'a> Iterator for ExtensionsIter<'a> {
    type Item = Extension<'a>;

    fn next(&mut self) -> Option<Self::Item> {
        if self.rest.len() < 4 {
            return None;
        }
        let extension_type = u16::from_be_bytes([self.rest[0], self.rest[1]]);
        let length = u16::from_be_bytes([self.rest[2], self.rest[3]]) as usize;
        let end = 4usize.checked_add(length)?;
        if self.rest.len() < end {
            return None;
        }
        let data = &self.rest[4..end];
        self.rest = &self.rest[end..];
        Some(Extension {
            extension_type,
            data,
        })
    }
}

/// One `KeyShareEntry` (RFC 8446, section 4.2.8).
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct KeyShareEntry<'a> {
    /// Named group of the share.
    pub group: u16,
    /// Ephemeral public key bytes.
    pub key_exchange: &'a [u8],
}

impl<'a> KeyShareEntry<'a> {
    /// Parses exactly one `KeyShareEntry`.
    ///
    /// # Errors
    ///
    /// Returns [`TlsError::ShortInput`] when the header or key is truncated and
    /// [`TlsError::LengthMismatch`] when extra bytes follow the key.
    pub fn parse(data: &'a [u8]) -> Result<Self, TlsError> {
        if data.len() < 4 {
            return Err(TlsError::ShortInput);
        }
        let group = u16::from_be_bytes([data[0], data[1]]);
        let key_len = u16::from_be_bytes([data[2], data[3]]) as usize;
        let expected = 4usize
            .checked_add(key_len)
            .ok_or(TlsError::LengthMismatch)?;
        if data.len() != expected {
            return Err(TlsError::LengthMismatch);
        }
        Ok(Self {
            group,
            key_exchange: &data[4..],
        })
    }

    /// Encodes the entry into `out`.
    ///
    /// # Errors
    ///
    /// Returns [`TlsError::LengthMismatch`] when the key is longer than
    /// `u16::MAX`.
    pub fn encode(&self, out: &mut Vec<u8>) -> Result<(), TlsError> {
        if self.key_exchange.len() > u16::MAX as usize {
            return Err(TlsError::LengthMismatch);
        }
        out.extend_from_slice(&self.group.to_be_bytes());
        out.extend_from_slice(&(self.key_exchange.len() as u16).to_be_bytes());
        out.extend_from_slice(self.key_exchange);
        Ok(())
    }
}

/// A parsed `ServerHello` message.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ServerHello<'a> {
    /// Legacy version field, expected to be `0x0303` for TLS 1.3.
    pub legacy_version: u16,
    /// 32-byte server random.
    pub random: [u8; 32],
    /// Legacy session identifier echoed by the server.
    pub session_id: &'a [u8],
    /// Selected cipher suite.
    pub cipher_suite: u16,
    /// Selected compression method, expected to be zero.
    pub compression_method: u8,
    /// Value of the `supported_versions` extension, if present.
    pub supported_version: Option<u16>,
    /// Value of the `key_share` extension, if present.
    pub key_share: Option<KeyShareEntry<'a>>,
}

impl<'a> ServerHello<'a> {
    /// Parses a complete `ServerHello` handshake message.
    ///
    /// # Errors
    ///
    /// Returns an error when the message is not a `ServerHello`, is truncated,
    /// or contains a malformed extension.
    pub fn parse(message: &'a [u8]) -> Result<Self, TlsError> {
        let (header, body) = split_handshake(message)?;
        if header.msg_type != HANDSHAKE_SERVER_HELLO {
            return Err(TlsError::InvalidMessageType);
        }

        let mut reader = Reader::new(body);
        let legacy_version = reader.u16()?;
        let mut random = [0u8; 32];
        random.copy_from_slice(reader.take(32)?);
        let session_id = reader.vec_u8()?;
        let cipher_suite = reader.u16()?;
        let compression_method = reader.u8()?;
        let extensions = Extensions::parse(reader.vec_u16()?)?;
        reader.finish()?;

        let supported_version = match extensions.find(EXTENSION_SUPPORTED_VERSIONS) {
            Some(data) => {
                if data.len() != 2 {
                    return Err(TlsError::MalformedExtensions);
                }
                Some(u16::from_be_bytes([data[0], data[1]]))
            }
            None => None,
        };

        let key_share = match extensions.find(EXTENSION_KEY_SHARE) {
            Some(data) => Some(KeyShareEntry::parse(data)?),
            None => None,
        };

        Ok(Self {
            legacy_version,
            random,
            session_id,
            cipher_suite,
            compression_method,
            supported_version,
            key_share,
        })
    }

    /// Builds a complete `ServerHello` handshake message.
    ///
    /// # Errors
    ///
    /// Returns [`TlsError::LengthMismatch`] when a length field overflows.
    pub fn encode(&self) -> Result<Vec<u8>, TlsError> {
        if self.session_id.len() > u8::MAX as usize {
            return Err(TlsError::LengthMismatch);
        }

        let mut extensions = Vec::new();
        if let Some(version) = self.supported_version {
            Extension {
                extension_type: EXTENSION_SUPPORTED_VERSIONS,
                data: &version.to_be_bytes(),
            }
            .encode(&mut extensions)?;
        }
        if let Some(key_share) = self.key_share {
            let mut data = Vec::new();
            key_share.encode(&mut data)?;
            Extension {
                extension_type: EXTENSION_KEY_SHARE,
                data: &data,
            }
            .encode(&mut extensions)?;
        }
        if extensions.len() > u16::MAX as usize {
            return Err(TlsError::LengthMismatch);
        }

        let mut body = Vec::with_capacity(38 + self.session_id.len() + extensions.len());
        body.extend_from_slice(&self.legacy_version.to_be_bytes());
        body.extend_from_slice(&self.random);
        body.push(self.session_id.len() as u8);
        body.extend_from_slice(self.session_id);
        body.extend_from_slice(&self.cipher_suite.to_be_bytes());
        body.push(self.compression_method);
        body.extend_from_slice(&(extensions.len() as u16).to_be_bytes());
        body.extend_from_slice(&extensions);

        encode_handshake(HANDSHAKE_SERVER_HELLO, &body)
    }

    /// Reports whether the server selected TLS 1.3.
    #[must_use]
    pub fn is_tls13(&self) -> bool {
        self.supported_version == Some(TLS_1_3)
    }
}

/// A parsed `EncryptedExtensions` message.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct EncryptedExtensions<'a> {
    /// Extensions carried by the message.
    pub extensions: Extensions<'a>,
}

impl<'a> EncryptedExtensions<'a> {
    /// Wraps a validated extensions block.
    ///
    /// # Errors
    ///
    /// Returns [`TlsError::MalformedExtensions`] when the block is malformed.
    pub fn new(raw: &'a [u8]) -> Result<Self, TlsError> {
        Ok(Self {
            extensions: Extensions::parse(raw)?,
        })
    }

    /// Parses a complete `EncryptedExtensions` handshake message.
    ///
    /// # Errors
    ///
    /// Returns an error when the message is not `EncryptedExtensions`, is
    /// truncated, or carries malformed extensions.
    pub fn parse(message: &'a [u8]) -> Result<Self, TlsError> {
        let (header, body) = split_handshake(message)?;
        if header.msg_type != HANDSHAKE_ENCRYPTED_EXTENSIONS {
            return Err(TlsError::InvalidMessageType);
        }
        let mut reader = Reader::new(body);
        let raw = reader.vec_u16()?;
        reader.finish()?;
        Self::new(raw)
    }

    /// Builds a complete `EncryptedExtensions` handshake message.
    ///
    /// # Errors
    ///
    /// Returns [`TlsError::LengthMismatch`] when the extensions block is longer
    /// than `u16::MAX`.
    pub fn encode(&self) -> Result<Vec<u8>, TlsError> {
        let raw = self.extensions.raw();
        if raw.len() > u16::MAX as usize {
            return Err(TlsError::LengthMismatch);
        }
        let mut body = Vec::with_capacity(2 + raw.len());
        body.extend_from_slice(&(raw.len() as u16).to_be_bytes());
        body.extend_from_slice(raw);
        encode_handshake(HANDSHAKE_ENCRYPTED_EXTENSIONS, &body)
    }
}

/// One `CertificateEntry` inside a `Certificate` message.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct CertificateEntry<'a> {
    /// DER-encoded certificate.
    pub certificate: &'a [u8],
    /// Per-certificate extensions, excluding the two-byte length prefix.
    pub extensions: &'a [u8],
}

/// A parsed `Certificate` message.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Certificate<'a> {
    /// Context for a certificate request; empty in a normal 1-RTT handshake.
    pub certificate_request_context: &'a [u8],
    /// Certificate chain in wire order.
    pub entries: Vec<CertificateEntry<'a>>,
}

impl<'a> Certificate<'a> {
    /// Parses a complete `Certificate` handshake message.
    ///
    /// # Errors
    ///
    /// Returns an error when the message is not `Certificate`, is truncated, or
    /// a nested length runs past its container.
    pub fn parse(message: &'a [u8]) -> Result<Self, TlsError> {
        let (header, body) = split_handshake(message)?;
        if header.msg_type != HANDSHAKE_CERTIFICATE {
            return Err(TlsError::InvalidMessageType);
        }

        let mut reader = Reader::new(body);
        let certificate_request_context = reader.vec_u8()?;
        let entries_block = reader.vec_u24()?;
        reader.finish()?;

        let mut entries_reader = Reader::new(entries_block);
        let mut entries = Vec::new();
        while entries_reader.remaining() > 0 {
            let certificate = entries_reader.vec_u24()?;
            let extensions = entries_reader.vec_u16()?;
            entries.push(CertificateEntry {
                certificate,
                extensions,
            });
        }

        Ok(Self {
            certificate_request_context,
            entries,
        })
    }

    /// Builds a complete `Certificate` handshake message.
    ///
    /// # Errors
    ///
    /// Returns [`TlsError::LengthMismatch`] when any nested length overflows.
    pub fn encode(&self) -> Result<Vec<u8>, TlsError> {
        if self.certificate_request_context.len() > u8::MAX as usize {
            return Err(TlsError::LengthMismatch);
        }

        let mut entries_block = Vec::new();
        for entry in &self.entries {
            if entry.certificate.len() > 0x00ff_ffff || entry.extensions.len() > u16::MAX as usize {
                return Err(TlsError::LengthMismatch);
            }
            push_u24(&mut entries_block, entry.certificate.len())?;
            entries_block.extend_from_slice(entry.certificate);
            entries_block.extend_from_slice(&(entry.extensions.len() as u16).to_be_bytes());
            entries_block.extend_from_slice(entry.extensions);
        }
        if entries_block.len() > 0x00ff_ffff {
            return Err(TlsError::LengthMismatch);
        }

        let mut body = Vec::with_capacity(1 + self.certificate_request_context.len() + 3);
        body.push(self.certificate_request_context.len() as u8);
        body.extend_from_slice(self.certificate_request_context);
        push_u24(&mut body, entries_block.len())?;
        body.extend_from_slice(&entries_block);
        encode_handshake(HANDSHAKE_CERTIFICATE, &body)
    }
}

/// A parsed `CertificateVerify` message.
///
/// The signature is kept as bytes; verifying it against the transcript is part
/// of RT7b.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct CertificateVerify<'a> {
    /// Signature scheme (for example `rsa_pss_rsae_sha256`).
    pub algorithm: u16,
    /// Signature bytes.
    pub signature: &'a [u8],
}

impl<'a> CertificateVerify<'a> {
    /// Parses a complete `CertificateVerify` handshake message.
    ///
    /// # Errors
    ///
    /// Returns an error when the message is not `CertificateVerify`, is
    /// truncated, or a length runs past the body.
    pub fn parse(message: &'a [u8]) -> Result<Self, TlsError> {
        let (header, body) = split_handshake(message)?;
        if header.msg_type != HANDSHAKE_CERTIFICATE_VERIFY {
            return Err(TlsError::InvalidMessageType);
        }
        let mut reader = Reader::new(body);
        let algorithm = reader.u16()?;
        let signature = reader.vec_u16()?;
        reader.finish()?;
        Ok(Self {
            algorithm,
            signature,
        })
    }

    /// Builds a complete `CertificateVerify` handshake message.
    ///
    /// # Errors
    ///
    /// Returns [`TlsError::LengthMismatch`] when the signature is longer than
    /// `u16::MAX`.
    pub fn encode(&self) -> Result<Vec<u8>, TlsError> {
        if self.signature.len() > u16::MAX as usize {
            return Err(TlsError::LengthMismatch);
        }
        let mut body = Vec::with_capacity(4 + self.signature.len());
        body.extend_from_slice(&self.algorithm.to_be_bytes());
        body.extend_from_slice(&(self.signature.len() as u16).to_be_bytes());
        body.extend_from_slice(self.signature);
        encode_handshake(HANDSHAKE_CERTIFICATE_VERIFY, &body)
    }
}

/// A parsed `Finished` message.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Finished<'a> {
    /// `verify_data` bytes (SHA-256 yields 32 bytes).
    pub verify_data: &'a [u8],
}

impl<'a> Finished<'a> {
    /// Parses a complete `Finished` handshake message.
    ///
    /// # Errors
    ///
    /// Returns [`TlsError::InvalidMessageType`] when the type is wrong and
    /// propagates a truncated header/body error.
    pub fn parse(message: &'a [u8]) -> Result<Self, TlsError> {
        let (header, body) = split_handshake(message)?;
        if header.msg_type != HANDSHAKE_FINISHED {
            return Err(TlsError::InvalidMessageType);
        }
        Ok(Self { verify_data: body })
    }

    /// Builds a complete `Finished` handshake message.
    ///
    /// # Errors
    ///
    /// Returns [`TlsError::LengthMismatch`] when `verify_data` exceeds the
    /// 24-bit handshake length field.
    pub fn encode(&self) -> Result<Vec<u8>, TlsError> {
        encode_handshake(HANDSHAKE_FINISHED, self.verify_data)
    }
}

/// A parsed `KeyUpdate` message.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct KeyUpdate {
    /// `request_update` value: `update_not_requested` (0) or
    /// `update_requested` (1).
    pub request_update: u8,
}

impl KeyUpdate {
    /// Parses a complete `KeyUpdate` handshake message.
    ///
    /// # Errors
    ///
    /// Returns [`TlsError::InvalidKeyUpdate`] for an unknown value and
    /// [`TlsError::LengthMismatch`] for a body that is not exactly one byte.
    pub fn parse(message: &[u8]) -> Result<Self, TlsError> {
        let (header, body) = split_handshake(message)?;
        if header.msg_type != HANDSHAKE_KEY_UPDATE {
            return Err(TlsError::InvalidMessageType);
        }
        if body.len() != 1 {
            return Err(TlsError::LengthMismatch);
        }
        let request_update = body[0];
        if request_update > 1 {
            return Err(TlsError::InvalidKeyUpdate);
        }
        Ok(Self { request_update })
    }

    /// Builds a complete `KeyUpdate` handshake message.
    ///
    /// # Errors
    ///
    /// Returns [`TlsError::InvalidKeyUpdate`] for a value other than 0 or 1.
    pub fn encode(&self) -> Result<Vec<u8>, TlsError> {
        if self.request_update > 1 {
            return Err(TlsError::InvalidKeyUpdate);
        }
        encode_handshake(HANDSHAKE_KEY_UPDATE, &[self.request_update])
    }
}

/// A TLS alert payload (RFC 8446, section 6).
///
/// In TLS 1.3 an alert is a two-byte record payload; after the handshake it is
/// protected like application data.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Alert {
    /// Alert level: [`ALERT_LEVEL_WARNING`] or [`ALERT_LEVEL_FATAL`].
    pub level: u8,
    /// Alert description code point.
    pub description: u8,
}

impl Alert {
    /// Parses a two-byte alert payload.
    ///
    /// # Errors
    ///
    /// Returns [`TlsError::LengthMismatch`] when the payload is not two bytes
    /// and [`TlsError::InvalidAlertLevel`] for an unknown level.
    pub fn parse(payload: &[u8]) -> Result<Self, TlsError> {
        if payload.len() != 2 {
            return Err(TlsError::LengthMismatch);
        }
        let level = payload[0];
        if level != ALERT_LEVEL_WARNING && level != ALERT_LEVEL_FATAL {
            return Err(TlsError::InvalidAlertLevel(level));
        }
        Ok(Self {
            level,
            description: payload[1],
        })
    }

    /// Encodes the two-byte alert payload.
    ///
    /// # Errors
    ///
    /// Returns [`TlsError::InvalidAlertLevel`] for an unknown level.
    pub fn encode(&self) -> Result<[u8; 2], TlsError> {
        if self.level != ALERT_LEVEL_WARNING && self.level != ALERT_LEVEL_FATAL {
            return Err(TlsError::InvalidAlertLevel(self.level));
        }
        Ok([self.level, self.description])
    }
}

impl fmt::Display for Alert {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        write!(
            f,
            "TLS alert level {} description {}",
            self.level, self.description
        )
    }
}

fn push_u24(out: &mut Vec<u8>, value: usize) -> Result<(), TlsError> {
    if value > 0x00ff_ffff {
        return Err(TlsError::LengthMismatch);
    }
    out.push((value >> 16) as u8);
    out.push((value >> 8) as u8);
    out.push(value as u8);
    Ok(())
}

struct Reader<'a> {
    buf: &'a [u8],
    pos: usize,
}

impl<'a> Reader<'a> {
    fn new(buf: &'a [u8]) -> Self {
        Self { buf, pos: 0 }
    }

    fn remaining(&self) -> usize {
        self.buf.len() - self.pos
    }

    fn take(&mut self, len: usize) -> Result<&'a [u8], TlsError> {
        let end = self.pos.checked_add(len).ok_or(TlsError::ShortInput)?;
        if end > self.buf.len() {
            return Err(TlsError::ShortInput);
        }
        let slice = &self.buf[self.pos..end];
        self.pos = end;
        Ok(slice)
    }

    fn u8(&mut self) -> Result<u8, TlsError> {
        Ok(self.take(1)?[0])
    }

    fn u16(&mut self) -> Result<u16, TlsError> {
        let bytes = self.take(2)?;
        Ok(u16::from_be_bytes([bytes[0], bytes[1]]))
    }

    fn u24(&mut self) -> Result<usize, TlsError> {
        let bytes = self.take(3)?;
        Ok(((bytes[0] as usize) << 16) | ((bytes[1] as usize) << 8) | bytes[2] as usize)
    }

    fn vec_u8(&mut self) -> Result<&'a [u8], TlsError> {
        let len = self.u8()? as usize;
        self.take(len)
    }

    fn vec_u16(&mut self) -> Result<&'a [u8], TlsError> {
        let len = self.u16()? as usize;
        self.take(len)
    }

    fn vec_u24(&mut self) -> Result<&'a [u8], TlsError> {
        let len = self.u24()?;
        self.take(len)
    }

    fn finish(&self) -> Result<(), TlsError> {
        if self.remaining() == 0 {
            Ok(())
        } else {
            Err(TlsError::LengthMismatch)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn server_hello() -> ServerHello<'static> {
        ServerHello {
            legacy_version: 0x0303,
            random: [0x42; 32],
            session_id: &[0x01, 0x02, 0x03, 0x04],
            cipher_suite: 0x1301,
            compression_method: 0,
            supported_version: Some(TLS_1_3),
            key_share: Some(KeyShareEntry {
                group: GROUP_X25519,
                key_exchange: &[0x11; 32],
            }),
        }
    }

    #[test]
    fn server_hello_round_trip() {
        let message = server_hello().encode().expect("ServerHello encodes");
        let parsed = ServerHello::parse(&message).expect("ServerHello parses");
        assert_eq!(parsed, server_hello());
        assert!(parsed.is_tls13());
        assert_eq!(parsed.key_share.expect("key share").key_exchange.len(), 32);
    }

    #[test]
    fn server_hello_without_extensions_round_trip() {
        let hello = ServerHello {
            supported_version: None,
            key_share: None,
            ..server_hello()
        };
        let message = hello.encode().expect("ServerHello encodes");
        let parsed = ServerHello::parse(&message).expect("ServerHello parses");
        assert_eq!(parsed, hello);
        assert!(!parsed.is_tls13());
    }

    #[test]
    fn server_hello_rejects_truncation_and_wrong_type() {
        let mut message = server_hello().encode().expect("ServerHello encodes");
        message[0] = HANDSHAKE_ENCRYPTED_EXTENSIONS;
        assert_eq!(
            ServerHello::parse(&message),
            Err(TlsError::InvalidMessageType)
        );

        let message = server_hello().encode().expect("ServerHello encodes");
        for end in 0..message.len() {
            assert!(ServerHello::parse(&message[..end]).is_err(), "prefix {end}");
        }
    }

    #[test]
    fn server_hello_rejects_bad_supported_versions_length() {
        let mut extensions = Vec::new();
        Extension {
            extension_type: EXTENSION_SUPPORTED_VERSIONS,
            data: &[0x03],
        }
        .encode(&mut extensions)
        .expect("extension encodes");
        let mut body = Vec::new();
        body.extend_from_slice(&[0x03, 0x03]);
        body.extend_from_slice(&[0x00; 32]);
        body.push(0);
        body.extend_from_slice(&0x1301u16.to_be_bytes());
        body.push(0);
        body.extend_from_slice(&(extensions.len() as u16).to_be_bytes());
        body.extend_from_slice(&extensions);
        let message = encode_handshake(HANDSHAKE_SERVER_HELLO, &body).expect("handshake encodes");
        assert_eq!(
            ServerHello::parse(&message),
            Err(TlsError::MalformedExtensions)
        );
    }

    #[test]
    fn encrypted_extensions_round_trip() {
        let mut raw = Vec::new();
        Extension {
            extension_type: EXTENSION_KEY_SHARE,
            data: &[0x00, 0x1d],
        }
        .encode(&mut raw)
        .expect("extension encodes");
        Extension {
            extension_type: 0x0010,
            data: b"\x00\x02h2",
        }
        .encode(&mut raw)
        .expect("extension encodes");

        let message = EncryptedExtensions::new(&raw)
            .expect("extensions parse")
            .encode()
            .expect("EncryptedExtensions encodes");
        let parsed = EncryptedExtensions::parse(&message).expect("EncryptedExtensions parses");
        assert_eq!(parsed.extensions.raw(), raw.as_slice());
        assert_eq!(
            parsed.extensions.find(EXTENSION_KEY_SHARE),
            Some([0x00, 0x1d].as_slice())
        );
    }

    #[test]
    fn encrypted_extensions_reject_bad_block() {
        let message = encode_handshake(
            HANDSHAKE_ENCRYPTED_EXTENSIONS,
            &[0x00, 0x04, 0x00, 0x00, 0x00, 0x04],
        )
        .expect("handshake encodes");
        assert_eq!(
            EncryptedExtensions::parse(&message),
            Err(TlsError::MalformedExtensions)
        );
    }

    fn certificate() -> Certificate<'static> {
        Certificate {
            certificate_request_context: &[0xaa, 0xbb],
            entries: vec![
                CertificateEntry {
                    certificate: &[0x30, 0x01, 0x02],
                    extensions: &[],
                },
                CertificateEntry {
                    certificate: &[0x30, 0x03],
                    extensions: &[0x00, 0x00],
                },
            ],
        }
    }

    #[test]
    fn certificate_round_trip() {
        let message = certificate().encode().expect("Certificate encodes");
        let parsed = Certificate::parse(&message).expect("Certificate parses");
        assert_eq!(parsed, certificate());
    }

    #[test]
    fn certificate_rejects_entry_overrun() {
        let mut entries = Vec::new();
        push_u24(&mut entries, 5).expect("length fits");
        entries.extend_from_slice(&[0x30, 0x01]);
        let mut body = vec![0];
        push_u24(&mut body, entries.len()).expect("length fits");
        body.extend_from_slice(&entries);
        let message = encode_handshake(HANDSHAKE_CERTIFICATE, &body).expect("handshake encodes");
        assert_eq!(Certificate::parse(&message), Err(TlsError::ShortInput));
    }

    #[test]
    fn certificate_verify_round_trip() {
        let verify = CertificateVerify {
            algorithm: 0x0804,
            signature: &[0x5a; 16],
        };
        let message = verify.encode().expect("CertificateVerify encodes");
        assert_eq!(
            CertificateVerify::parse(&message).expect("CertificateVerify parses"),
            verify
        );
    }

    #[test]
    fn certificate_verify_rejects_trailing_bytes() {
        let mut body = Vec::new();
        body.extend_from_slice(&0x0804u16.to_be_bytes());
        body.extend_from_slice(&2u16.to_be_bytes());
        body.extend_from_slice(&[0xaa, 0xbb, 0xcc]);
        let message =
            encode_handshake(HANDSHAKE_CERTIFICATE_VERIFY, &body).expect("handshake encodes");
        assert_eq!(
            CertificateVerify::parse(&message),
            Err(TlsError::LengthMismatch)
        );
    }

    #[test]
    fn finished_round_trip() {
        let finished = Finished {
            verify_data: &[0x9b; 32],
        };
        let message = finished.encode().expect("Finished encodes");
        assert_eq!(
            Finished::parse(&message).expect("Finished parses"),
            finished
        );
    }

    #[test]
    fn finished_rejects_wrong_type() {
        let message = encode_handshake(HANDSHAKE_KEY_UPDATE, &[0x9b; 32]).expect("encodes");
        assert_eq!(Finished::parse(&message), Err(TlsError::InvalidMessageType));
    }

    #[test]
    fn key_update_round_trip_and_validation() {
        for request_update in 0..=1 {
            let update = KeyUpdate { request_update };
            let message = update.encode().expect("KeyUpdate encodes");
            assert_eq!(
                KeyUpdate::parse(&message).expect("KeyUpdate parses"),
                update
            );
        }

        let invalid = encode_handshake(HANDSHAKE_KEY_UPDATE, &[2]).expect("encodes");
        assert_eq!(KeyUpdate::parse(&invalid), Err(TlsError::InvalidKeyUpdate));

        let truncated = encode_handshake(HANDSHAKE_KEY_UPDATE, &[]).expect("encodes");
        assert_eq!(KeyUpdate::parse(&truncated), Err(TlsError::LengthMismatch));
    }

    #[test]
    fn alert_round_trip_and_validation() {
        let alert = Alert {
            level: ALERT_LEVEL_FATAL,
            description: 40,
        };
        let bytes = alert.encode().expect("Alert encodes");
        assert_eq!(bytes, [ALERT_LEVEL_FATAL, 40]);
        assert_eq!(Alert::parse(&bytes).expect("Alert parses"), alert);

        assert_eq!(Alert::parse(&[0, 0]), Err(TlsError::InvalidAlertLevel(0)));
        assert_eq!(
            Alert::parse(&[ALERT_LEVEL_FATAL]),
            Err(TlsError::LengthMismatch)
        );
    }

    #[test]
    fn handshake_header_and_body_are_checked() {
        let message = encode_handshake(HANDSHAKE_FINISHED, &[1, 2, 3]).expect("encodes");
        let (header, body) = split_handshake(&message).expect("splits");
        assert_eq!(
            header,
            HandshakeHeader {
                msg_type: HANDSHAKE_FINISHED,
                length: 3
            }
        );
        assert_eq!(body, &[1, 2, 3]);

        let mut trailing = message;
        trailing.push(0);
        assert_eq!(split_handshake(&trailing), Err(TlsError::LengthMismatch));
    }

    #[test]
    fn key_share_entry_validation() {
        let entry = KeyShareEntry {
            group: GROUP_X25519,
            key_exchange: &[0x22; 32],
        };
        let mut data = Vec::new();
        entry.encode(&mut data).expect("entry encodes");
        assert_eq!(KeyShareEntry::parse(&data).expect("entry parses"), entry);

        data.push(0);
        assert_eq!(KeyShareEntry::parse(&data), Err(TlsError::LengthMismatch));
        assert_eq!(KeyShareEntry::parse(&[0x00]), Err(TlsError::ShortInput));
    }
}
