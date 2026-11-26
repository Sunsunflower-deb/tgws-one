//! Client acceptance: the obfuscated2 and fake-TLS handshakes.
//!
//! This is the port of the top of `ProxyEngine.HandleClientAsync`:
//!
//! 1. read the first byte; `0x16` with a masking domain configured selects the
//!    fake-TLS path, anything else the obfuscated2 path;
//! 2. fake-TLS verifies the ClientHello HMAC and timestamp, answers with
//!    ServerHello + ChangeCipherSpec + ApplicationData and then reads the inner
//!    64-byte obfuscated2 handshake from ApplicationData records;
//! 3. obfuscated2 reads the remaining 63 bytes and decodes the secret, DC,
//!    media flag and transport tag;
//! 4. test DCs (`>= 10000`) are folded back onto the real DC number.
//!
//! Divergence from C# (documented because RT9a stops at the relay core):
//! a ClientHello that is not an `ee` client is refused with a TLS alert
//! instead of being relayed to the masking site.  Dialing the masking domain is
//! part of the route/camouflage work that RT9b owns.

use std::io::{self, Read, Write};
use std::net::TcpStream;
use std::time::Duration;

use crate::mtproto::fake_tls::{
    FakeTls, MAX_RECORD_PAYLOAD, RECORD_APP_DATA, RECORD_CCS, RECORD_HANDSHAKE,
};
use crate::proto::{MtProtoConstants, Obfuscated2};
use crate::tls::client_hello::Random32;

use super::config::RelayConfig;

/// A decoded client handshake.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ClientHandshake {
    /// Real DC number (test DCs already folded back).
    pub dc: i32,
    /// Whether the client asked for the media DC (`-dc` on the wire).
    pub is_media: bool,
    /// MTProto transport tag (`0xEFEF_EFEF`, `0xEEEE_EEEE` or `0xDDDD_DDDD`).
    pub proto_tag: u32,
    /// The client prekey+IV slice the relay keys are derived from.
    pub client_prekey_iv: Vec<u8>,
    /// Whether the DC was a test DC (or `--force-test-dc`).
    pub is_test_dc: bool,
    /// Whether the handshake arrived inside fake TLS records.
    pub fake_tls: bool,
}

impl ClientHandshake {
    /// `-dc` on the wire for media, `dc` otherwise; used to seal relay init.
    #[must_use]
    pub fn relay_dc_index(&self) -> i32 {
        if self.is_media { -self.dc } else { self.dc }
    }
}

/// Why a client was not accepted.
#[derive(Debug)]
pub enum HandshakeError {
    /// The socket failed or the client disappeared.
    Io(io::Error),
    /// The first TLS record was malformed.
    FakeTls,
    /// The 64 bytes did not decode with the configured secret.
    BadHandshake,
}

impl std::fmt::Display for HandshakeError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            Self::Io(error) => write!(f, "client I/O error: {error}"),
            Self::FakeTls => f.write_str("malformed fake TLS ClientHello"),
            Self::BadHandshake => f.write_str("bad handshake (wrong secret or protocol tag)"),
        }
    }
}

impl std::error::Error for HandshakeError {}

impl From<io::Error> for HandshakeError {
    fn from(error: io::Error) -> Self {
        Self::Io(error)
    }
}

/// The accepted client connection: a plain socket or a fake-TLS record tunnel.
pub enum ClientConnection {
    /// Obfuscated2 directly on the TCP stream.
    Plain(TcpStream),
    /// Obfuscated2 inside fake-TLS ApplicationData records.
    FakeTls(FakeTlsStream<TcpStream>),
}

impl ClientConnection {
    /// Changes the read timeout used by the relay poll loop.
    ///
    /// # Errors
    ///
    /// Propagates `setsockopt`/`set_read_timeout` failures.
    pub fn set_read_timeout(&mut self, timeout: Option<Duration>) -> io::Result<()> {
        match self {
            Self::Plain(stream) => stream.set_read_timeout(timeout),
            Self::FakeTls(stream) => stream.inner.set_read_timeout(timeout),
        }
    }
}

impl Read for ClientConnection {
    fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
        match self {
            Self::Plain(stream) => stream.read(buf),
            Self::FakeTls(stream) => stream.read(buf),
        }
    }
}

impl Write for ClientConnection {
    fn write(&mut self, buf: &[u8]) -> io::Result<usize> {
        match self {
            Self::Plain(stream) => stream.write(buf),
            Self::FakeTls(stream) => stream.write(buf),
        }
    }

    fn flush(&mut self) -> io::Result<()> {
        match self {
            Self::Plain(stream) => stream.flush(),
            Self::FakeTls(stream) => stream.flush(),
        }
    }
}

/// Accepts the client handshake on an already-accepted socket.
///
/// The caller is responsible for socket options (Nagle, buffers, handshake
/// timeout).  This function may write the fake-TLS answer before returning.
///
/// # Errors
///
/// Returns [`HandshakeError::BadHandshake`] for a wrong secret or tag,
/// [`HandshakeError::FakeTls`] for a malformed TLS-looking greeting and
/// [`HandshakeError::Io`] for a transport failure.
pub fn accept(
    mut stream: TcpStream,
    config: &RelayConfig,
) -> Result<(ClientConnection, ClientHandshake), HandshakeError> {
    let mut first = [0u8; 1];
    stream.read_exact(&mut first)?;

    if first[0] == RECORD_HANDSHAKE && !config.fake_tls_domain.is_empty() {
        let hello = read_first_record(&mut stream, first[0])?;
        let Some(parsed) = FakeTls::try_verify_client_hello(&hello, &config.secret, None) else {
            // C# relays this hello to the masking site.  RT9a has no masking
            // route yet, so it answers as a TLS endpoint that refused instead
            // of closing silently (same visible shape for a prober).
            let _ = stream.write_all(&FakeTls::alert(FakeTls::ALERT_HANDSHAKE_FAILURE));
            let _ = stream.flush();
            return Err(HandshakeError::BadHandshake);
        };

        let info = FakeTls::parse_client_hello(&hello);
        let mut rng = super::OsRandom::new()?;
        let answer = FakeTls::build_response(
            &config.secret,
            &parsed.client_random,
            &parsed.session_id,
            &info,
            None,
            |buffer| rng.fill_bytes(buffer),
        );
        stream.write_all(&answer)?;
        stream.flush()?;

        let mut tunnel = FakeTlsStream::new(stream);
        let mut inner = [0u8; MtProtoConstants::HANDSHAKE_LENGTH];
        tunnel.read_exact(&mut inner)?;
        let mut handshake = decode_handshake(&inner, config)?;
        handshake.fake_tls = true;
        Ok((ClientConnection::FakeTls(tunnel), handshake))
    } else {
        let mut rest = [0u8; MtProtoConstants::HANDSHAKE_LENGTH - 1];
        stream.read_exact(&mut rest)?;
        let mut handshake = [0u8; MtProtoConstants::HANDSHAKE_LENGTH];
        handshake[0] = first[0];
        handshake[1..].copy_from_slice(&rest);
        let decoded = decode_handshake(&handshake, config)?;
        Ok((ClientConnection::Plain(stream), decoded))
    }
}

fn decode_handshake(bytes: &[u8], config: &RelayConfig) -> Result<ClientHandshake, HandshakeError> {
    let Some((dc, is_media, proto_tag, prekey_iv)) =
        Obfuscated2::try_parse_client_handshake(bytes, &config.secret)
    else {
        return Err(HandshakeError::BadHandshake);
    };

    let mut dc = dc;
    let is_test_dc = config.force_test_dc || dc >= 10000;
    if dc >= 10000 {
        dc -= 10000;
    }

    Ok(ClientHandshake {
        dc,
        is_media,
        proto_tag,
        client_prekey_iv: prekey_iv,
        is_test_dc,
        fake_tls: false,
    })
}

fn read_first_record(stream: &mut TcpStream, first: u8) -> Result<Vec<u8>, HandshakeError> {
    let mut header = [0u8; 5];
    header[0] = first;
    stream.read_exact(&mut header[1..])?;
    let length = FakeTls::try_read_record_length(&header).map_err(|_| HandshakeError::FakeTls)?;
    if usize::from(length) > MAX_RECORD_PAYLOAD {
        return Err(HandshakeError::FakeTls);
    }

    let mut record = Vec::with_capacity(5 + usize::from(length));
    record.extend_from_slice(&header);
    record.resize(5 + usize::from(length), 0);
    stream.read_exact(&mut record[5..])?;
    Ok(record)
}

/// `FakeTlsStream` from `Transport/FakeTls.cs`: strips ChangeCipherSpec and
/// returns ApplicationData payloads on read, wraps every write in an
/// ApplicationData record of at most [`MAX_RECORD_PAYLOAD`] bytes.
pub struct FakeTlsStream<S> {
    inner: S,
    header: [u8; 5],
    header_len: usize,
    remaining: usize,
}

impl<S> FakeTlsStream<S> {
    pub(crate) fn new(inner: S) -> Self {
        Self {
            inner,
            header: [0; 5],
            header_len: 0,
            remaining: 0,
        }
    }
}

impl<S: Read> Read for FakeTlsStream<S> {
    fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
        if buf.is_empty() {
            return Ok(0);
        }
        loop {
            if self.remaining > 0 {
                let want = buf.len().min(self.remaining);
                let read = self.inner.read(&mut buf[..want])?;
                if read == 0 {
                    return Ok(0);
                }
                self.remaining -= read;
                return Ok(read);
            }

            while self.header_len < self.header.len() {
                let read = self.inner.read(&mut self.header[self.header_len..])?;
                if read == 0 {
                    return Ok(0);
                }
                self.header_len += read;
            }

            let record_type = self.header[0];
            let length = u16::from_be_bytes([self.header[3], self.header[4]]) as usize;
            self.header_len = 0;
            if length == 0 {
                continue;
            }
            if record_type == RECORD_CCS {
                self.discard(length)?;
                continue;
            }
            if record_type != RECORD_APP_DATA {
                // Alert or plain handshake: the peer is not tunnelling.
                return Ok(0);
            }
            self.remaining = length;
        }
    }
}

impl<S: Read> FakeTlsStream<S> {
    fn discard(&mut self, mut left: usize) -> io::Result<()> {
        let mut scratch = [0u8; 256];
        while left > 0 {
            let want = left.min(scratch.len());
            let read = self.inner.read(&mut scratch[..want])?;
            if read == 0 {
                return Ok(());
            }
            left -= read;
        }
        Ok(())
    }
}

impl<S: Write> Write for FakeTlsStream<S> {
    fn write(&mut self, buf: &[u8]) -> io::Result<usize> {
        if buf.is_empty() {
            return Ok(0);
        }
        let mut offset = 0;
        while offset < buf.len() {
            let chunk = (buf.len() - offset).min(MAX_RECORD_PAYLOAD);
            let header = [RECORD_APP_DATA, 0x03, 0x03, (chunk >> 8) as u8, chunk as u8];
            self.inner.write_all(&header)?;
            self.inner.write_all(&buf[offset..offset + chunk])?;
            offset += chunk;
        }
        Ok(buf.len())
    }

    fn flush(&mut self) -> io::Result<()> {
        self.inner.flush()
    }
}

#[cfg(test)]
pub(crate) mod test_support {
    use sha2::{Digest, Sha256};

    use crate::proto::{AesCtr, MtProtoConstants};
    use crate::tls::client_hello::{Random32, SplitMix64};

    /// Builds the 64-byte client handshake the way `ClientHandshake.cs` does.
    pub(crate) fn client_handshake(
        secret: &[u8],
        proto_tag: u32,
        dc_idx: i16,
    ) -> (Vec<u8>, AesCtr, AesCtr) {
        let mut rng = SplitMix64::new(0x5eed);
        let mut bytes = [0u8; MtProtoConstants::HANDSHAKE_LENGTH];
        rng.fill_bytes(&mut bytes);

        let prekey = bytes[MtProtoConstants::SKIP_LENGTH
            ..MtProtoConstants::SKIP_LENGTH + MtProtoConstants::PREKEY_LENGTH]
            .to_vec();
        let iv = bytes[MtProtoConstants::SKIP_LENGTH + MtProtoConstants::PREKEY_LENGTH
            ..MtProtoConstants::SKIP_LENGTH
                + MtProtoConstants::PREKEY_LENGTH
                + MtProtoConstants::IV_LENGTH]
            .to_vec();
        let mut hasher = Sha256::new();
        hasher.update(&prekey);
        hasher.update(secret);
        let key: [u8; 32] = hasher.finalize().into();

        let mut tail = [0u8; 8];
        tail[..4].copy_from_slice(&proto_tag.to_le_bytes());
        tail[4..6].copy_from_slice(&dc_idx.to_le_bytes());
        rng.fill_bytes(&mut tail[6..8]);

        let mut ctr = AesCtr::new(&key, &iv).unwrap();
        let mut encrypted = [0u8; MtProtoConstants::HANDSHAKE_LENGTH];
        ctr.transform(&bytes, &mut encrypted);
        for (index, tail_byte) in tail.iter().enumerate() {
            let at = MtProtoConstants::PROTO_TAG_POSITION + index;
            bytes[at] ^= tail_byte ^ encrypted[at];
        }

        let mut encrypt = AesCtr::new(&key, &iv).unwrap();
        encrypt.skip(MtProtoConstants::HANDSHAKE_LENGTH);

        let mut reversed = bytes[MtProtoConstants::SKIP_LENGTH
            ..MtProtoConstants::SKIP_LENGTH
                + MtProtoConstants::PREKEY_LENGTH
                + MtProtoConstants::IV_LENGTH]
            .to_vec();
        reversed.reverse();
        let mut hasher = Sha256::new();
        hasher.update(&reversed[..MtProtoConstants::PREKEY_LENGTH]);
        hasher.update(secret);
        let dec_key: [u8; 32] = hasher.finalize().into();
        let decrypt = AesCtr::new(
            &dec_key,
            &reversed[MtProtoConstants::PREKEY_LENGTH
                ..MtProtoConstants::PREKEY_LENGTH + MtProtoConstants::IV_LENGTH],
        )
        .unwrap();

        (bytes.to_vec(), encrypt, decrypt)
    }

    /// HMAC-SHA256, test-only, to build a valid fake-TLS ClientHello.
    pub(crate) fn hmac_sha256(key: &[u8], message: &[u8]) -> [u8; 32] {
        let mut block = [0u8; 64];
        if key.len() > 64 {
            let digest = Sha256::digest(key);
            block[..digest.len()].copy_from_slice(&digest);
        } else {
            block[..key.len()].copy_from_slice(key);
        }
        let mut inner = Vec::with_capacity(64 + message.len());
        inner.extend(block.iter().map(|byte| byte ^ 0x36));
        inner.extend_from_slice(message);
        let inner_hash = Sha256::digest(&inner);
        let mut outer = Vec::with_capacity(64 + inner_hash.len());
        outer.extend(block.iter().map(|byte| byte ^ 0x5c));
        outer.extend_from_slice(&inner_hash);
        Sha256::digest(&outer).into()
    }

    /// A minimal TLS-looking ClientHello that carries the fake-TLS HMAC.
    pub(crate) fn fake_tls_hello(secret: &[u8], timestamp: u32) -> Vec<u8> {
        let mut hello = vec![0u8; 76];
        hello[0] = super::RECORD_HANDSHAKE;
        hello[1] = 0x03;
        hello[2] = 0x03;
        let record_length = (hello.len() - 5) as u16;
        hello[3..5].copy_from_slice(&record_length.to_be_bytes());
        hello[5] = 0x01;
        let mac = hmac_sha256(secret, &hello);
        hello[11..39].copy_from_slice(&mac[..28]);
        let timestamp_bytes = timestamp.to_le_bytes();
        for index in 0..4 {
            hello[39 + index] = timestamp_bytes[index] ^ mac[28 + index];
        }
        hello
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::net::TcpListener;
    use std::thread;

    use crate::proto::MtProtoConstants;

    use super::super::config::DEFAULT_READ_TIMEOUT;
    use super::test_support::{client_handshake, fake_tls_hello};

    fn config(secret: &[u8], fake_tls_domain: &str) -> RelayConfig {
        RelayConfig {
            secret: secret.to_vec(),
            fake_tls_domain: fake_tls_domain.to_string(),
            read_timeout: DEFAULT_READ_TIMEOUT,
            ..RelayConfig::default()
        }
    }

    #[test]
    fn obfuscated2_handshake_decodes_dc_media_and_test_dc() {
        let secret = [0x42u8; 16];
        let config = config(&secret, "");

        let (bytes, _, _) = client_handshake(&secret, MtProtoConstants::PROTO_INTERMEDIATE, -2);
        let mode = {
            let listener = TcpListener::bind(("127.0.0.1", 0)).unwrap();
            let addr = listener.local_addr().unwrap();
            let server_config = config.clone();
            let server = thread::spawn(move || {
                let (stream, _) = listener.accept().unwrap();
                stream
                    .set_read_timeout(Some(Duration::from_secs(5)))
                    .unwrap();
                accept(stream, &server_config).map(|(_, hs)| hs)
            });
            let mut client = TcpStream::connect(addr).unwrap();
            client.write_all(&bytes).unwrap();
            server.join().unwrap().unwrap()
        };
        assert_eq!(mode.dc, 2);
        assert!(mode.is_media);
        assert!(!mode.is_test_dc);
        assert_eq!(mode.proto_tag, MtProtoConstants::PROTO_INTERMEDIATE);
        assert!(!mode.fake_tls);

        let (bytes, _, _) = client_handshake(&secret, MtProtoConstants::PROTO_ABRIDGED, 10003);
        let mode = {
            let listener = TcpListener::bind(("127.0.0.1", 0)).unwrap();
            let addr = listener.local_addr().unwrap();
            let server_config = config.clone();
            let server = thread::spawn(move || {
                let (stream, _) = listener.accept().unwrap();
                accept(stream, &server_config).map(|(_, hs)| hs)
            });
            let mut client = TcpStream::connect(addr).unwrap();
            client.write_all(&bytes).unwrap();
            server.join().unwrap().unwrap()
        };
        assert_eq!(mode.dc, 3);
        assert!(mode.is_test_dc);
    }

    #[test]
    fn wrong_secret_is_refused() {
        let secret = [0x42u8; 16];
        let (bytes, _, _) = client_handshake(&secret, MtProtoConstants::PROTO_ABRIDGED, 1);
        let wrong = config(&[0x43u8; 16], "");
        let listener = TcpListener::bind(("127.0.0.1", 0)).unwrap();
        let addr = listener.local_addr().unwrap();
        let server = thread::spawn(move || {
            let (stream, _) = listener.accept().unwrap();
            accept(stream, &wrong).map(|_| ())
        });
        let mut client = TcpStream::connect(addr).unwrap();
        client.write_all(&bytes).unwrap();
        let result = server.join().unwrap();
        assert!(matches!(result, Err(HandshakeError::BadHandshake)));
    }

    #[test]
    fn fake_tls_answers_and_reads_the_inner_handshake() {
        let secret = [0x24u8; 16];
        let config = config(&secret, "www.example.com");
        let timestamp = std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .unwrap()
            .as_secs() as u32;
        let hello = fake_tls_hello(&secret, timestamp);
        let (inner, _, _) = client_handshake(&secret, MtProtoConstants::PROTO_INTERMEDIATE, 4);

        let listener = TcpListener::bind(("127.0.0.1", 0)).unwrap();
        let addr = listener.local_addr().unwrap();
        let server = thread::spawn(move || {
            let (stream, _) = listener.accept().unwrap();
            stream
                .set_read_timeout(Some(Duration::from_secs(5)))
                .unwrap();
            accept(stream, &config)
        });

        let mut client = TcpStream::connect(addr).unwrap();
        client
            .set_read_timeout(Some(Duration::from_secs(5)))
            .unwrap();
        client.write_all(&hello).unwrap();
        // Let the answer arrive; the content itself was covered by the C#
        // FakeTls tests, here we only need the tunnel to proceed.
        std::thread::sleep(Duration::from_millis(30));
        let mut record = vec![
            RECORD_APP_DATA,
            0x03,
            0x03,
            (inner.len() >> 8) as u8,
            inner.len() as u8,
        ];
        record.extend_from_slice(&inner);
        client.write_all(&record).unwrap();

        let (connection, handshake) = server.join().unwrap().unwrap();
        assert!(matches!(connection, ClientConnection::FakeTls(_)));
        assert_eq!(handshake.dc, 4);
        assert!(!handshake.is_media);
        assert!(handshake.fake_tls);
        assert_eq!(handshake.proto_tag, MtProtoConstants::PROTO_INTERMEDIATE);
    }

    #[test]
    fn fake_tls_stream_wraps_and_unwraps_application_data() {
        let listener = TcpListener::bind(("127.0.0.1", 0)).unwrap();
        let addr = listener.local_addr().unwrap();
        let server = thread::spawn(move || {
            let (mut stream, _) = listener.accept().unwrap();
            let mut tunnel = FakeTlsStream::new(stream.try_clone().unwrap());
            let mut receipt = vec![0u8; 4];
            tunnel.read_exact(&mut receipt).unwrap();
            assert_eq!(&receipt, b"ping");
            stream
                .write_all(&[0x14, 0x03, 0x03, 0x00, 0x01, 0x01])
                .unwrap();
            stream
                .write_all(&[RECORD_APP_DATA, 0x03, 0x03, 0x00, 0x04])
                .unwrap();
            stream.write_all(b"pong").unwrap();
        });

        let stream = TcpStream::connect(addr).unwrap();
        let mut tunnel = FakeTlsStream::new(stream);
        tunnel.write_all(b"ping").unwrap();
        tunnel.flush().unwrap();
        let mut receipt = vec![0u8; 4];
        tunnel.read_exact(&mut receipt).unwrap();
        assert_eq!(&receipt, b"pong");
        server.join().unwrap();
    }
}
