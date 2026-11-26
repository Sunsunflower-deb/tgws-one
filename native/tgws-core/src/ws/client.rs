//! RFC 6455 client handshake and message loop.
//!
//! The client is generic over a blocking [`Stream`], so the same code runs on
//! [`RawTcpStream`] today and on RT7b's TLS stream later.  The handshake writes
//! the `Upgrade` request, reads the response head with a hard size limit and
//! verifies `Sec-WebSocket-Accept` (SHA-1 plus base64, RFC 6455, section 4.1).
//!
//! After the upgrade, [`WebSocket::receive`] assembles fragmented messages,
//! answers Pings automatically and echoes the Close handshake.  Keep-alive
//! timing is exposed through [`next_ping_delay`], a port of the C#
//! `RawWebSocket.NextPingDelay`.

use std::io::{self, Read, Write};
use std::time::Duration;

use base64::Engine as _;
use base64::engine::general_purpose::STANDARD;
use sha1::{Digest, Sha1};

use crate::tcp::RawTcpStream;
use crate::tls::client_hello::Random32;

use super::WsError;
use super::frame::{CloseFrame, Endpoint, Frame, FrameParser, MAX_FRAME_PAYLOAD, OpCode};

/// Default limit for the HTTP response head, mirroring the C# reader's 16 KiB
/// buffer plus its per-line cap.
pub const MAX_HANDSHAKE_BYTES: usize = 16 * 1024;

/// Read size used while draining frames from the transport.
const READ_CHUNK: usize = 16 * 1024;

/// MAGIC GUID appended to `Sec-WebSocket-Key` for the accept hash (RFC 6455).
const WS_GUID: &str = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";

/// A blocking byte stream usable for the WebSocket upgrade.
///
/// Every `Read + Write` type implements it, including [`std::net::TcpStream`]
/// and [`RawTcpStream`].  RT7b's TLS stream only needs the same two traits.
pub trait Stream: Read + Write {}

impl<T: Read + Write> Stream for T {}

impl Read for RawTcpStream {
    fn read(&mut self, buf: &mut [u8]) -> io::Result<usize> {
        if buf.is_empty() {
            return Ok(0);
        }
        loop {
            // SAFETY: the descriptor is owned by `self` and `buf` is a valid
            // writable slice of the declared length.
            let result =
                unsafe { libc::recv(self.as_raw_fd(), buf.as_mut_ptr().cast(), buf.len(), 0) };
            if result < 0 {
                let error = io::Error::last_os_error();
                if error.kind() == io::ErrorKind::Interrupted {
                    continue;
                }
                return Err(error);
            }
            return Ok(result as usize);
        }
    }
}

impl Write for RawTcpStream {
    fn write(&mut self, buf: &[u8]) -> io::Result<usize> {
        self.send_raw(buf)
    }

    fn flush(&mut self) -> io::Result<()> {
        Ok(())
    }
}

/// HTTP upgrade parameters.
#[derive(Debug, Clone)]
pub struct HandshakeConfig {
    /// Value of the `Host` header.  May include a port.
    pub host: String,
    /// Request target, for example `/apiws`.
    pub path: String,
    /// Optional `Sec-WebSocket-Protocol` value.
    pub subprotocol: Option<String>,
    /// Optional browser-like `User-Agent`.
    pub user_agent: Option<String>,
    /// Optional `Origin` header (origin masking).
    pub origin: Option<String>,
    /// Additional headers appended before the terminating CRLF.
    pub extra_headers: Vec<(String, String)>,
    /// Hard limit for the response head, including the final CRLFCRLF.
    pub max_header_bytes: usize,
}

impl HandshakeConfig {
    /// Creates a configuration with path `/` and the default header limit.
    #[must_use]
    pub fn new(host: impl Into<String>) -> Self {
        Self {
            host: host.into(),
            path: "/".to_owned(),
            subprotocol: None,
            user_agent: None,
            origin: None,
            extra_headers: Vec::new(),
            max_header_bytes: MAX_HANDSHAKE_BYTES,
        }
    }

    /// Sets the request target.
    #[must_use]
    pub fn with_path(mut self, path: impl Into<String>) -> Self {
        self.path = path.into();
        self
    }

    /// Sets `Sec-WebSocket-Protocol`.
    #[must_use]
    pub fn with_subprotocol(mut self, subprotocol: impl Into<String>) -> Self {
        self.subprotocol = Some(subprotocol.into());
        self
    }

    /// Sets a browser-like `User-Agent`.
    #[must_use]
    pub fn with_user_agent(mut self, user_agent: impl Into<String>) -> Self {
        self.user_agent = Some(user_agent.into());
        self
    }

    /// Sets the `Origin` header.
    #[must_use]
    pub fn with_origin(mut self, origin: impl Into<String>) -> Self {
        self.origin = Some(origin.into());
        self
    }

    /// Appends one custom header.
    #[must_use]
    pub fn with_header(mut self, name: impl Into<String>, value: impl Into<String>) -> Self {
        self.extra_headers.push((name.into(), value.into()));
        self
    }

    /// Replaces the response-head size limit.
    #[must_use]
    pub fn with_max_header_bytes(mut self, limit: usize) -> Self {
        self.max_header_bytes = limit;
        self
    }

    fn validate(&self) -> Result<(), WsError> {
        if self.host.is_empty()
            || self.host.bytes().any(|byte| byte.is_ascii_whitespace())
            || !valid_header_value(&self.host)
            || self.max_header_bytes == 0
        {
            return Err(WsError::InvalidHeader);
        }
        if !self.path.starts_with('/')
            || self
                .path
                .bytes()
                .any(|byte| byte.is_ascii_whitespace() || byte == b'\r' || byte == b'\n')
        {
            return Err(WsError::InvalidHeader);
        }
        for value in [&self.subprotocol, &self.user_agent, &self.origin]
            .into_iter()
            .flatten()
        {
            if !valid_header_value(value) {
                return Err(WsError::InvalidHeader);
            }
        }
        for (name, value) in &self.extra_headers {
            if !valid_header_name(name) || !valid_header_value(value) {
                return Err(WsError::InvalidHeader);
            }
        }
        Ok(())
    }
}

/// A complete WebSocket message.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Message {
    /// A validated UTF-8 text message.
    Text(String),
    /// A binary message.
    Binary(Vec<u8>),
}

#[derive(Debug, Default)]
struct FragmentState {
    opcode: Option<OpCode>,
    data: Vec<u8>,
}

/// A WebSocket client sitting on top of an already-connected stream.
pub struct WebSocket<S: Stream> {
    stream: S,
    parser: FrameParser,
    rng: Box<dyn Random32>,
    closed: bool,
    close_frame: Option<CloseFrame>,
    fragment: FragmentState,
}

impl<S: Stream> WebSocket<S> {
    /// Performs the HTTP upgrade and returns a framed client.
    ///
    /// `rng` must be cryptographically secure in production; the deterministic
    /// generators in [`crate::tls::client_hello`] are intended for tests.
    ///
    /// # Errors
    ///
    /// Returns I/O errors, malformed/non-101 responses, oversized headers and a
    /// mismatching `Sec-WebSocket-Accept` value.
    pub fn connect(
        mut stream: S,
        config: &HandshakeConfig,
        mut rng: Box<dyn Random32>,
    ) -> Result<Self, WsError> {
        config.validate()?;

        let mut key_bytes = [0u8; 16];
        rng.fill_bytes(&mut key_bytes);
        let key = STANDARD.encode(key_bytes);
        let request = build_request(config, &key);

        stream.write_all(request.as_bytes())?;
        stream.flush()?;

        let (head, leftover) = read_http_head(&mut stream, config.max_header_bytes)?;
        let response = parse_http_head(&head)?;
        if response.code != 101 {
            return Err(WsError::HttpStatus {
                code: response.code,
                line: response.line,
            });
        }

        let accept = response
            .headers
            .iter()
            .find_map(|(name, value)| (name == "sec-websocket-accept").then_some(value.as_str()))
            .ok_or(WsError::MissingAccept)?;
        if accept != compute_accept(&key) {
            return Err(WsError::InvalidAccept);
        }

        let mut parser = FrameParser::new(Endpoint::Client);
        parser.push(&leftover);

        Ok(Self {
            stream,
            parser,
            rng,
            closed: false,
            close_frame: None,
            fragment: FragmentState::default(),
        })
    }

    /// Whether the close handshake finished (or the transport ended).
    #[must_use]
    pub fn is_closed(&self) -> bool {
        self.closed
    }

    /// The close frame received from the peer, if any.
    #[must_use]
    pub fn close_frame(&self) -> Option<&CloseFrame> {
        self.close_frame.as_ref()
    }

    /// Sends a single text frame.
    ///
    /// # Errors
    ///
    /// Returns [`WsError::Closed`] after the close handshake and propagates I/O
    /// errors.
    pub fn send_text(&mut self, text: &str) -> Result<(), WsError> {
        self.write_frame(true, OpCode::Text, text.as_bytes())
    }

    /// Sends a single binary frame.
    ///
    /// # Errors
    ///
    /// Returns [`WsError::Closed`] after the close handshake and propagates I/O
    /// errors.
    pub fn send_binary(&mut self, data: &[u8]) -> Result<(), WsError> {
        self.write_frame(true, OpCode::Binary, data)
    }

    /// Sends a Ping with a random payload of 4-12 bytes.
    ///
    /// The random length follows the C# keep-alive loop so that a fixed payload
    /// does not become one more constant on the wire.
    ///
    /// # Errors
    ///
    /// Returns [`WsError::Closed`] after the close handshake and propagates I/O
    /// errors.
    pub fn send_ping(&mut self) -> Result<(), WsError> {
        let length = 4 + (self.rng.next_u32() % 9) as usize;
        let mut payload = vec![0u8; length];
        self.rng.fill_bytes(&mut payload);
        self.write_frame(true, OpCode::Ping, &payload)
    }

    /// Sends a Ping with an explicit payload (at most 125 bytes).
    ///
    /// # Errors
    ///
    /// Rejects oversized payloads and propagates I/O errors.
    pub fn send_ping_with_payload(&mut self, payload: &[u8]) -> Result<(), WsError> {
        self.write_frame(true, OpCode::Ping, payload)
    }

    /// Returns the delay before the next keep-alive Ping.
    ///
    /// This is the port of `RawWebSocket.NextPingDelay`: at least one second,
    /// plus a random number of milliseconds in `0..=jitter * 1000`.
    pub fn next_ping_delay(&mut self, interval_seconds: u32, jitter_seconds: u32) -> Duration {
        next_ping_delay(interval_seconds, jitter_seconds, self.rng.as_mut())
    }

    /// Receives the next data message.
    ///
    /// Pings are answered with a Pong carrying the same payload, Pongs are
    /// ignored, and fragmented data frames are reassembled.  Returns `Ok(None)`
    /// after receiving a Close frame (which is echoed first) or when the
    /// transport reaches EOF.
    ///
    /// # Errors
    ///
    /// Returns protocol errors for malformed frames, invalid UTF-8 text,
    /// message-size overflows and I/O failures.
    pub fn receive(&mut self) -> Result<Option<Message>, WsError> {
        if self.closed {
            return Ok(None);
        }

        loop {
            let frame = if let Some(frame) = self.parser.next_frame()? {
                frame
            } else {
                let mut buffer = [0u8; READ_CHUNK];
                let read = self.stream.read(&mut buffer)?;
                if read == 0 {
                    self.closed = true;
                    return Ok(None);
                }
                self.parser.push(&buffer[..read]);
                continue;
            };

            match frame.opcode {
                OpCode::Close => {
                    let close = CloseFrame::parse(&frame.payload)?;
                    let _ = self.echo_close(&close);
                    self.closed = true;
                    self.close_frame = Some(close);
                    return Ok(None);
                }
                OpCode::Ping => {
                    self.write_frame(true, OpCode::Pong, &frame.payload)?;
                }
                OpCode::Pong => {}
                OpCode::Text | OpCode::Binary | OpCode::Continuation => {
                    if let Some(message) = self.accept_data_frame(frame)? {
                        return Ok(Some(message));
                    }
                }
            }
        }
    }

    /// Sends a Close frame and marks the client closed.
    ///
    /// An empty payload is sent when `code` is `None`; a reason requires a
    /// code.
    ///
    /// # Errors
    ///
    /// Rejects invalid close codes or reasons, and propagates I/O errors.
    pub fn close(&mut self, code: Option<u16>, reason: &str) -> Result<(), WsError> {
        if self.closed {
            return Ok(());
        }
        let close = CloseFrame::new(code, reason)?;
        let payload = close.encode()?;
        let result = self.write_frame(true, OpCode::Close, &payload);
        self.closed = true;
        result
    }

    fn write_frame(&mut self, fin: bool, opcode: OpCode, payload: &[u8]) -> Result<(), WsError> {
        if self.closed {
            return Err(WsError::Closed);
        }
        let frame = Frame::new(fin, opcode, payload.to_vec());
        let bytes = frame.encode(self.rng.as_mut())?;
        self.stream.write_all(&bytes)?;
        self.stream.flush()?;
        Ok(())
    }

    fn echo_close(&mut self, close: &CloseFrame) -> Result<(), WsError> {
        let payload = match close.code {
            Some(code) => code.to_be_bytes().to_vec(),
            None => Vec::new(),
        };
        self.write_frame(true, OpCode::Close, &payload)
    }

    fn accept_data_frame(&mut self, frame: Frame) -> Result<Option<Message>, WsError> {
        match frame.opcode {
            OpCode::Continuation => {
                let Some(opcode) = self.fragment.opcode else {
                    return Err(WsError::UnexpectedContinuation);
                };
                self.fragment.data.extend_from_slice(&frame.payload);
                if self.fragment.data.len() > MAX_FRAME_PAYLOAD {
                    return Err(WsError::PayloadTooLarge {
                        length: self.fragment.data.len() as u64,
                        limit: MAX_FRAME_PAYLOAD,
                    });
                }
                if frame.fin {
                    self.fragment.opcode = None;
                    let data = std::mem::take(&mut self.fragment.data);
                    return message_from(opcode, data).map(Some);
                }
                Ok(None)
            }
            opcode @ (OpCode::Text | OpCode::Binary) => {
                if self.fragment.opcode.is_some() {
                    return Err(WsError::NestedFragment);
                }
                if frame.fin {
                    if frame.payload.len() > MAX_FRAME_PAYLOAD {
                        return Err(WsError::PayloadTooLarge {
                            length: frame.payload.len() as u64,
                            limit: MAX_FRAME_PAYLOAD,
                        });
                    }
                    return message_from(opcode, frame.payload).map(Some);
                }
                self.fragment.opcode = Some(opcode);
                self.fragment.data = frame.payload;
                Ok(None)
            }
            _ => Err(WsError::InvalidOpcode(frame.opcode.as_u8())),
        }
    }
}

fn message_from(opcode: OpCode, payload: Vec<u8>) -> Result<Message, WsError> {
    match opcode {
        OpCode::Text => String::from_utf8(payload)
            .map(Message::Text)
            .map_err(|_| WsError::InvalidUtf8),
        _ => Ok(Message::Binary(payload)),
    }
}

/// Computes the delay before the next Ping.
///
/// Port of `RawWebSocket.NextPingDelay`: the interval is raised to at least one
/// second, and a random jitter of `0..=jitter_seconds * 1000` milliseconds is
/// added so a fixed rhythm does not show on the wire.
#[must_use]
pub fn next_ping_delay(
    interval_seconds: u32,
    jitter_seconds: u32,
    rng: &mut dyn Random32,
) -> Duration {
    let jitter_ms = jitter_seconds.saturating_mul(1_000);
    let extra_ms = if jitter_ms == 0 {
        0
    } else {
        rng.next_u32() % jitter_ms.saturating_add(1)
    };
    Duration::from_secs(u64::from(interval_seconds.max(1)))
        + Duration::from_millis(u64::from(extra_ms))
}

fn valid_header_value(value: &str) -> bool {
    !value.bytes().any(|byte| byte == b'\r' || byte == b'\n')
}

fn valid_header_name(name: &str) -> bool {
    !name.is_empty()
        && name.bytes().all(|byte| {
            byte.is_ascii_alphanumeric()
                || matches!(
                    byte,
                    b'!' | b'#'
                        | b'$'
                        | b'%'
                        | b'&'
                        | b'\''
                        | b'*'
                        | b'+'
                        | b'-'
                        | b'.'
                        | b'^'
                        | b'_'
                        | b'`'
                        | b'|'
                        | b'~'
                )
        })
}

fn build_request(config: &HandshakeConfig, key: &str) -> String {
    let mut request = String::new();
    request.push_str("GET ");
    request.push_str(&config.path);
    request.push_str(" HTTP/1.1\r\n");
    request.push_str("Host: ");
    request.push_str(&config.host);
    request.push_str("\r\n");
    request.push_str("Upgrade: websocket\r\n");
    request.push_str("Connection: Upgrade\r\n");
    request.push_str("Sec-WebSocket-Key: ");
    request.push_str(key);
    request.push_str("\r\n");
    request.push_str("Sec-WebSocket-Version: 13\r\n");
    if let Some(subprotocol) = &config.subprotocol {
        request.push_str("Sec-WebSocket-Protocol: ");
        request.push_str(subprotocol);
        request.push_str("\r\n");
    }
    if let Some(user_agent) = &config.user_agent {
        request.push_str("User-Agent: ");
        request.push_str(user_agent);
        request.push_str("\r\n");
    }
    if let Some(origin) = &config.origin {
        request.push_str("Origin: ");
        request.push_str(origin);
        request.push_str("\r\n");
    }
    for (name, value) in &config.extra_headers {
        request.push_str(name);
        request.push_str(": ");
        request.push_str(value);
        request.push_str("\r\n");
    }
    request.push_str("\r\n");
    request
}

pub(crate) fn compute_accept(key: &str) -> String {
    let mut hasher = Sha1::new();
    hasher.update(key.as_bytes());
    hasher.update(WS_GUID.as_bytes());
    STANDARD.encode(hasher.finalize())
}

struct HttpResponse {
    code: u16,
    line: String,
    headers: Vec<(String, String)>,
}

fn parse_http_head(head: &[u8]) -> Result<HttpResponse, WsError> {
    let text = core::str::from_utf8(head).map_err(|_| WsError::MalformedHandshake)?;
    let mut lines = text.split("\r\n");
    let status_line = lines.next().ok_or(WsError::MalformedHandshake)?;
    let mut parts = status_line.split_whitespace();
    let version = parts.next().ok_or(WsError::MalformedHandshake)?;
    if !version.starts_with("HTTP/") {
        return Err(WsError::MalformedHandshake);
    }
    let code = parts
        .next()
        .ok_or(WsError::MalformedHandshake)?
        .parse::<u16>()
        .map_err(|_| WsError::MalformedHandshake)?;

    let mut headers = Vec::new();
    for line in lines {
        if line.is_empty() {
            break;
        }
        let (name, value) = line.split_once(':').ok_or(WsError::MalformedHandshake)?;
        if name.is_empty() {
            return Err(WsError::MalformedHandshake);
        }
        headers.push((name.trim().to_ascii_lowercase(), value.trim().to_owned()));
    }

    Ok(HttpResponse {
        code,
        line: status_line.to_owned(),
        headers,
    })
}

fn read_http_head<S: Read>(stream: &mut S, limit: usize) -> Result<(Vec<u8>, Vec<u8>), WsError> {
    let mut buffer = Vec::new();
    let mut chunk = [0u8; 1024];
    loop {
        if let Some(position) = buffer.windows(4).position(|window| window == b"\r\n\r\n") {
            let tail = buffer.split_off(position + 4);
            return Ok((buffer, tail));
        }
        if buffer.len() >= limit {
            return Err(WsError::HeaderTooLarge { limit });
        }
        let to_read = (limit - buffer.len()).min(chunk.len());
        let read = stream.read(&mut chunk[..to_read])?;
        if read == 0 {
            return Err(WsError::UnexpectedEof);
        }
        buffer.extend_from_slice(&chunk[..read]);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::net::{TcpListener, TcpStream};
    use std::thread::{self, JoinHandle};

    use crate::tls::client_hello::SplitMix64;

    fn read_request(stream: &mut TcpStream) -> String {
        let mut buffer = Vec::new();
        let mut chunk = [0u8; 1024];
        loop {
            let read = stream.read(&mut chunk).expect("read request");
            assert!(read > 0, "client closed before the request head");
            buffer.extend_from_slice(&chunk[..read]);
            if buffer.windows(4).any(|window| window == b"\r\n\r\n") {
                break;
            }
        }
        String::from_utf8(buffer).expect("request is UTF-8")
    }

    fn header_value(request: &str, name: &str) -> Option<String> {
        request.lines().find_map(|line| {
            let (key, value) = line.split_once(':')?;
            key.eq_ignore_ascii_case(name)
                .then(|| value.trim().to_owned())
        })
    }

    fn handshake(stream: &mut TcpStream) -> String {
        let request = read_request(stream);
        let key = header_value(&request, "sec-websocket-key").expect("key header");
        let accept = compute_accept(&key);
        let response = format!(
            "HTTP/1.1 101 Switching Protocols\r\n\
             Upgrade: websocket\r\n\
             Connection: Upgrade\r\n\
             Sec-WebSocket-Accept: {accept}\r\n\
             \r\n"
        );
        stream.write_all(response.as_bytes()).expect("write 101");
        request
    }

    fn spawn_server<F>(handler: F) -> (u16, JoinHandle<()>)
    where
        F: FnOnce(TcpStream) + Send + 'static,
    {
        let listener = TcpListener::bind(("127.0.0.1", 0)).expect("bind loopback");
        let port = listener.local_addr().expect("local addr").port();
        let handle = thread::spawn(move || {
            let (stream, _) = listener.accept().expect("accept");
            stream
                .set_read_timeout(Some(Duration::from_secs(5)))
                .expect("read timeout");
            stream
                .set_write_timeout(Some(Duration::from_secs(5)))
                .expect("write timeout");
            handler(stream);
        });
        (port, handle)
    }

    fn connect_client(port: u16, config: &HandshakeConfig) -> WebSocket<TcpStream> {
        let stream = TcpStream::connect(("127.0.0.1", port)).expect("connect");
        stream
            .set_read_timeout(Some(Duration::from_secs(5)))
            .expect("read timeout");
        stream
            .set_write_timeout(Some(Duration::from_secs(5)))
            .expect("write timeout");
        WebSocket::connect(stream, config, Box::new(SplitMix64::new(7))).expect("client handshake")
    }

    fn read_client_frame(stream: &mut TcpStream, parser: &mut FrameParser) -> Frame {
        loop {
            if let Some(frame) = parser.next_frame().expect("parse client frame") {
                return frame;
            }
            let mut buffer = [0u8; 4096];
            let read = stream.read(&mut buffer).expect("read client frame");
            assert!(read > 0, "client closed while a frame was expected");
            parser.push(&buffer[..read]);
        }
    }

    fn write_server_frame(stream: &mut TcpStream, frame: Frame) {
        let bytes = frame.encode_unmasked().expect("encode server frame");
        stream.write_all(&bytes).expect("write server frame");
    }

    #[test]
    fn handshake_sends_expected_headers() {
        let (port, handle) = spawn_server(|mut stream| {
            let request = handshake(&mut stream);
            assert!(request.starts_with("GET /apiws HTTP/1.1\r\n"));
            assert_eq!(header_value(&request, "host").as_deref(), Some("localhost"));
            assert_eq!(
                header_value(&request, "upgrade").as_deref(),
                Some("websocket")
            );
            assert_eq!(
                header_value(&request, "connection").as_deref(),
                Some("Upgrade")
            );
            assert_eq!(
                header_value(&request, "sec-websocket-version").as_deref(),
                Some("13")
            );
            assert_eq!(
                header_value(&request, "sec-websocket-protocol").as_deref(),
                Some("binary")
            );
            assert_eq!(
                header_value(&request, "user-agent").as_deref(),
                Some("TgwsOne/9")
            );
            assert_eq!(
                header_value(&request, "origin").as_deref(),
                Some("https://example.com")
            );
            assert_eq!(header_value(&request, "x-custom").as_deref(), Some("value"));
            let key = header_value(&request, "sec-websocket-key").expect("key");
            assert_eq!(
                base64::engine::general_purpose::STANDARD
                    .decode(key)
                    .expect("base64 key")
                    .len(),
                16
            );
        });

        let config = HandshakeConfig::new("localhost")
            .with_path("/apiws")
            .with_subprotocol("binary")
            .with_user_agent("TgwsOne/9")
            .with_origin("https://example.com")
            .with_header("X-Custom", "value");
        let _client = connect_client(port, &config);
        handle.join().expect("server thread");
    }

    #[test]
    fn echo_roundtrip_and_large_binary_frame() {
        let (port, handle) = spawn_server(|mut stream| {
            handshake(&mut stream);
            let mut parser = FrameParser::new(Endpoint::Server);
            for _ in 0..2 {
                let frame = read_client_frame(&mut stream, &mut parser);
                write_server_frame(&mut stream, Frame::new(true, frame.opcode, frame.payload));
            }
        });

        let mut client = connect_client(port, &HandshakeConfig::new("localhost"));
        client.send_text("hello").expect("send text");
        assert_eq!(
            client.receive().expect("receive text"),
            Some(Message::Text("hello".to_owned()))
        );

        let large: Vec<u8> = (0..100_000).map(|value| (value % 251) as u8).collect();
        client.send_binary(&large).expect("send large binary");
        assert_eq!(
            client.receive().expect("receive large binary"),
            Some(Message::Binary(large))
        );
        handle.join().expect("server thread");
    }

    #[test]
    fn answers_server_ping_and_ignores_pong() {
        let (port, handle) = spawn_server(|mut stream| {
            handshake(&mut stream);
            write_server_frame(&mut stream, Frame::ping(b"probe".to_vec()));

            let mut parser = FrameParser::new(Endpoint::Server);
            let pong = read_client_frame(&mut stream, &mut parser);
            assert_eq!(pong.opcode, OpCode::Pong);
            assert_eq!(pong.payload, b"probe");

            write_server_frame(&mut stream, Frame::pong(b"ignored".to_vec()));
            write_server_frame(&mut stream, Frame::text("after ping"));
        });

        let mut client = connect_client(port, &HandshakeConfig::new("localhost"));
        assert_eq!(
            client.receive().expect("receive after ping"),
            Some(Message::Text("after ping".to_owned()))
        );
        handle.join().expect("server thread");
    }

    #[test]
    fn client_ping_is_answered_with_pong() {
        let (port, handle) = spawn_server(|mut stream| {
            handshake(&mut stream);
            let mut parser = FrameParser::new(Endpoint::Server);
            let ping = read_client_frame(&mut stream, &mut parser);
            assert_eq!(ping.opcode, OpCode::Ping);
            assert!((4..=12).contains(&ping.payload.len()));
            write_server_frame(&mut stream, Frame::pong(ping.payload));
            write_server_frame(&mut stream, Frame::text("ponged"));
        });

        let mut client = connect_client(port, &HandshakeConfig::new("localhost"));
        client.send_ping().expect("send ping");
        assert_eq!(
            client.receive().expect("receive after pong"),
            Some(Message::Text("ponged".to_owned()))
        );
        handle.join().expect("server thread");
    }

    #[test]
    fn reassembles_fragmented_messages_with_interleaved_control_frame() {
        let (port, handle) = spawn_server(|mut stream| {
            handshake(&mut stream);
            write_server_frame(
                &mut stream,
                Frame::new(false, OpCode::Text, b"hel".to_vec()),
            );
            write_server_frame(&mut stream, Frame::ping(b"k".to_vec()));

            let mut parser = FrameParser::new(Endpoint::Server);
            let pong = read_client_frame(&mut stream, &mut parser);
            assert_eq!(pong.opcode, OpCode::Pong);

            write_server_frame(
                &mut stream,
                Frame::new(true, OpCode::Continuation, b"lo".to_vec()),
            );
            write_server_frame(&mut stream, Frame::new(false, OpCode::Binary, vec![1, 2]));
            write_server_frame(
                &mut stream,
                Frame::new(true, OpCode::Continuation, vec![3, 4]),
            );
        });

        let mut client = connect_client(port, &HandshakeConfig::new("localhost"));
        assert_eq!(
            client.receive().expect("receive text"),
            Some(Message::Text("hello".to_owned()))
        );
        assert_eq!(
            client.receive().expect("receive binary"),
            Some(Message::Binary(vec![1, 2, 3, 4]))
        );
        handle.join().expect("server thread");
    }

    #[test]
    fn server_close_with_code_is_reported_and_echoed() {
        let (port, handle) = spawn_server(|mut stream| {
            handshake(&mut stream);
            let close = CloseFrame::new(Some(1001), "going away").expect("close");
            write_server_frame(
                &mut stream,
                Frame::new(true, OpCode::Close, close.encode().expect("payload")),
            );

            let mut parser = FrameParser::new(Endpoint::Server);
            let echo = read_client_frame(&mut stream, &mut parser);
            assert_eq!(echo.opcode, OpCode::Close);
            let close = CloseFrame::parse(&echo.payload).expect("echo close");
            assert_eq!(close.code, Some(1001));
        });

        let mut client = connect_client(port, &HandshakeConfig::new("localhost"));
        assert_eq!(client.receive().expect("receive close"), None);
        assert!(client.is_closed());
        let close = client.close_frame().expect("close frame");
        assert_eq!(close.code, Some(1001));
        assert_eq!(close.reason, "going away");
        handle.join().expect("server thread");
    }

    #[test]
    fn client_close_sends_code_and_reason() {
        let (port, handle) = spawn_server(|mut stream| {
            handshake(&mut stream);
            let mut parser = FrameParser::new(Endpoint::Server);
            let close = read_client_frame(&mut stream, &mut parser);
            assert_eq!(close.opcode, OpCode::Close);
            let close = CloseFrame::parse(&close.payload).expect("close payload");
            assert_eq!(close.code, Some(1000));
            assert_eq!(close.reason, "bye");
        });

        let mut client = connect_client(port, &HandshakeConfig::new("localhost"));
        client.close(Some(1000), "bye").expect("client close");
        assert!(client.is_closed());
        handle.join().expect("server thread");
    }

    #[test]
    fn rejects_mismatched_accept() {
        let (port, handle) = spawn_server(|mut stream| {
            let _request = read_request(&mut stream);
            stream
                .write_all(
                    b"HTTP/1.1 101 Switching Protocols\r\n\
                      Sec-WebSocket-Accept: wrong\r\n\r\n",
                )
                .expect("write bad response");
        });

        let stream = TcpStream::connect(("127.0.0.1", port)).expect("connect");
        let result = WebSocket::connect(
            stream,
            &HandshakeConfig::new("localhost"),
            Box::new(SplitMix64::new(1)),
        );
        assert!(matches!(result, Err(WsError::InvalidAccept)));
        handle.join().expect("server thread");
    }

    #[test]
    fn reports_non_101_status() {
        let (port, handle) = spawn_server(|mut stream| {
            let _request = read_request(&mut stream);
            stream
                .write_all(b"HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n")
                .expect("write 403");
        });

        let stream = TcpStream::connect(("127.0.0.1", port)).expect("connect");
        let result = WebSocket::connect(
            stream,
            &HandshakeConfig::new("localhost"),
            Box::new(SplitMix64::new(1)),
        );
        assert!(matches!(result, Err(WsError::HttpStatus { code: 403, .. })));
        handle.join().expect("server thread");
    }

    #[test]
    fn enforces_http_header_limit() {
        let (port, handle) = spawn_server(|mut stream| {
            let _request = read_request(&mut stream);
            let _ = stream.write_all(&[b'A'; 256]);
        });

        let stream = TcpStream::connect(("127.0.0.1", port)).expect("connect");
        let config = HandshakeConfig::new("localhost").with_max_header_bytes(64);
        let result = WebSocket::connect(stream, &config, Box::new(SplitMix64::new(1)));
        assert!(matches!(result, Err(WsError::HeaderTooLarge { limit: 64 })));
        handle.join().expect("server thread");
    }

    #[test]
    fn rejects_invalid_request_headers() {
        let config = HandshakeConfig::new("localhost").with_header("X-Bad", "a\r\nInjected: yes");
        let error = config.validate().expect_err("header injection must fail");
        assert!(matches!(error, WsError::InvalidHeader));

        let config = HandshakeConfig::new("host with space");
        assert!(matches!(config.validate(), Err(WsError::InvalidHeader)));

        let config = HandshakeConfig::new("localhost").with_path("apiws");
        assert!(matches!(config.validate(), Err(WsError::InvalidHeader)));
    }

    #[test]
    fn next_ping_delay_matches_reference_bounds() {
        let mut rng = SplitMix64::new(5);
        for _ in 0..100 {
            let delay = next_ping_delay(25, 10, &mut rng);
            assert!(delay >= Duration::from_secs(25));
            assert!(delay <= Duration::from_millis(35_000));
        }

        assert_eq!(next_ping_delay(0, 0, &mut rng), Duration::from_secs(1));
        assert_eq!(next_ping_delay(5, 0, &mut rng), Duration::from_secs(5));
    }
}
