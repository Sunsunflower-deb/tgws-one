//! RFC 6455 frame encoding and incremental parsing.
//!
//! The encoder always masks client frames (RFC 6455, section 5.3) using bytes
//! drawn from an injected [`Random32`].  The parser accepts arbitrary chunks and
//! returns [`FrameParser::next_frame`] only once a complete frame is buffered,
//! so callers can feed it directly from a `Read` implementation.
//!
//! A [`FrameParser`] is told which side of the connection it runs on.  That is
//! how the RFC masking rules become a hard error instead of a silent mismatch:
//! a server-side parser rejects unmasked client frames and a client-side parser
//! rejects masked server frames.

use core::fmt;

use crate::tls::client_hello::Random32;

use super::WsError;

/// Maximum accepted payload length for one frame or one assembled message.
///
/// The value matches the C# reference (`RawWebSocket.MaxMessageLength`) and
/// keeps a malicious length field from forcing a huge allocation.
pub const MAX_FRAME_PAYLOAD: usize = 16 * 1024 * 1024;

/// Maximum payload length of a control frame (RFC 6455, section 5.5).
pub const MAX_CONTROL_PAYLOAD: usize = 125;

/// Maximum reason length in a close frame (125 bytes minus the status code).
pub const MAX_CLOSE_REASON: usize = 123;

/// RFC 6455 opcodes.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum OpCode {
    /// Continuation of a fragmented data message.
    Continuation,
    /// UTF-8 text message.
    Text,
    /// Binary message.
    Binary,
    /// Closing handshake.
    Close,
    /// Liveness probe; the peer must answer with a Pong.
    Ping,
    /// Answer to a Ping.
    Pong,
}

impl OpCode {
    /// Decodes the four opcode bits.
    #[must_use]
    pub const fn from_u8(value: u8) -> Option<Self> {
        match value {
            0x0 => Some(Self::Continuation),
            0x1 => Some(Self::Text),
            0x2 => Some(Self::Binary),
            0x8 => Some(Self::Close),
            0x9 => Some(Self::Ping),
            0xa => Some(Self::Pong),
            _ => None,
        }
    }

    /// Returns the wire value of this opcode.
    #[must_use]
    pub const fn as_u8(self) -> u8 {
        match self {
            Self::Continuation => 0x0,
            Self::Text => 0x1,
            Self::Binary => 0x2,
            Self::Close => 0x8,
            Self::Ping => 0x9,
            Self::Pong => 0xa,
        }
    }

    /// Whether this is a control opcode (Close, Ping or Pong).
    #[must_use]
    pub const fn is_control(self) -> bool {
        matches!(self, Self::Close | Self::Ping | Self::Pong)
    }
}

impl fmt::Display for OpCode {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let name = match self {
            Self::Continuation => "continuation",
            Self::Text => "text",
            Self::Binary => "binary",
            Self::Close => "close",
            Self::Ping => "ping",
            Self::Pong => "pong",
        };
        f.write_str(name)
    }
}

/// Which end of the connection a [`FrameParser`] runs on.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum Endpoint {
    /// Incoming frames come from a server and must be unmasked.
    Client,
    /// Incoming frames come from a client and must be masked.
    Server,
}

/// One complete WebSocket frame.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Frame {
    /// FIN bit: this is the last (or only) fragment of a message.
    pub fin: bool,
    /// The frame opcode.
    pub opcode: OpCode,
    /// Unmasked payload bytes.
    pub payload: Vec<u8>,
}

impl Frame {
    /// Builds a frame from its parts.
    #[must_use]
    pub fn new(fin: bool, opcode: OpCode, payload: Vec<u8>) -> Self {
        Self {
            fin,
            opcode,
            payload,
        }
    }

    /// Builds a single text frame.
    #[must_use]
    pub fn text(text: &str) -> Self {
        Self::new(true, OpCode::Text, text.as_bytes().to_vec())
    }

    /// Builds a single binary frame.
    #[must_use]
    pub fn binary(data: Vec<u8>) -> Self {
        Self::new(true, OpCode::Binary, data)
    }

    /// Builds a ping frame.
    #[must_use]
    pub fn ping(payload: Vec<u8>) -> Self {
        Self::new(true, OpCode::Ping, payload)
    }

    /// Builds a pong frame.
    #[must_use]
    pub fn pong(payload: Vec<u8>) -> Self {
        Self::new(true, OpCode::Pong, payload)
    }

    /// Encodes the frame as a masked client frame.
    ///
    /// # Errors
    ///
    /// Rejects oversized payloads, fragmented/oversized control frames and
    /// close frames whose status code must not appear on the wire.
    pub fn encode(&self, rng: &mut dyn Random32) -> Result<Vec<u8>, WsError> {
        self.validate()?;
        let mut mask = [0u8; 4];
        rng.fill_bytes(&mut mask);
        Ok(self.encode_with_mask(Some(mask)))
    }

    /// Encodes the frame without a mask.
    ///
    /// This is the server side of the wire (and the test server); a client must
    /// use [`Frame::encode`] instead.
    ///
    /// # Errors
    ///
    /// Same validation as [`Frame::encode`].
    pub fn encode_unmasked(&self) -> Result<Vec<u8>, WsError> {
        self.validate()?;
        Ok(self.encode_with_mask(None))
    }

    fn validate(&self) -> Result<(), WsError> {
        if self.opcode.is_control() {
            if !self.fin {
                return Err(WsError::FragmentedControl);
            }
            if self.payload.len() > MAX_CONTROL_PAYLOAD {
                return Err(WsError::ControlTooLong);
            }
        }
        if self.payload.len() > MAX_FRAME_PAYLOAD {
            return Err(WsError::PayloadTooLarge {
                length: self.payload.len() as u64,
                limit: MAX_FRAME_PAYLOAD,
            });
        }
        if self.opcode == OpCode::Close {
            CloseFrame::parse(&self.payload)?;
        }
        Ok(())
    }

    fn encode_with_mask(&self, mask: Option<[u8; 4]>) -> Vec<u8> {
        let length = self.payload.len();
        let mut header = Vec::with_capacity(14);
        header.push(if self.fin {
            0x80 | self.opcode.as_u8()
        } else {
            self.opcode.as_u8()
        });

        let mask_bit = if mask.is_some() { 0x80 } else { 0 };
        if length < 126 {
            header.push(mask_bit | length as u8);
        } else if length <= usize::from(u16::MAX) {
            header.push(mask_bit | 126);
            header.extend_from_slice(&(length as u16).to_be_bytes());
        } else {
            header.push(mask_bit | 127);
            header.extend_from_slice(&(length as u64).to_be_bytes());
        }

        if let Some(key) = mask {
            header.extend_from_slice(&key);
            let mut frame = header;
            frame.reserve(length);
            frame.extend(
                self.payload
                    .iter()
                    .enumerate()
                    .map(|(index, byte)| byte ^ key[index & 3]),
            );
            frame
        } else {
            let mut frame = header;
            frame.extend_from_slice(&self.payload);
            frame
        }
    }
}

/// Incremental parser for RFC 6455 frames.
///
/// Push bytes as they arrive from the transport; each call to
/// [`FrameParser::next_frame`] returns either a complete frame or `None` when
/// more bytes are needed.
#[derive(Debug)]
pub struct FrameParser {
    role: Endpoint,
    buffer: Vec<u8>,
}

impl FrameParser {
    /// Creates a parser for the given side of the connection.
    #[must_use]
    pub fn new(role: Endpoint) -> Self {
        Self {
            role,
            buffer: Vec::new(),
        }
    }

    /// Appends raw bytes received from the transport.
    pub fn push(&mut self, data: &[u8]) {
        self.buffer.extend_from_slice(data);
    }

    /// Number of bytes buffered but not yet returned as a frame.
    #[must_use]
    pub fn buffered_len(&self) -> usize {
        self.buffer.len()
    }

    /// Returns the next complete frame, or `None` when more bytes are needed.
    ///
    /// # Errors
    ///
    /// Returns a protocol error as soon as the buffered header is invalid; the
    /// parser then stays in a failed state and the caller must close the
    /// connection.
    pub fn next_frame(&mut self) -> Result<Option<Frame>, WsError> {
        let Some((frame, consumed)) = self.parse_next()? else {
            return Ok(None);
        };
        self.buffer.drain(..consumed);
        Ok(Some(frame))
    }

    fn parse_next(&self) -> Result<Option<(Frame, usize)>, WsError> {
        let data = &self.buffer;
        if data.len() < 2 {
            return Ok(None);
        }

        let first = data[0];
        let second = data[1];
        if first & 0x70 != 0 {
            return Err(WsError::ReservedBits);
        }
        let fin = first & 0x80 != 0;
        let opcode_value = first & 0x0f;
        let opcode = OpCode::from_u8(opcode_value).ok_or(WsError::InvalidOpcode(opcode_value))?;

        let masked = second & 0x80 != 0;
        match (self.role, masked) {
            (Endpoint::Server, false) => return Err(WsError::MissingMask),
            (Endpoint::Client, true) => return Err(WsError::UnexpectedMask),
            _ => {}
        }

        let short_length = second & 0x7f;
        let mut offset = 2;
        let length = match short_length {
            126 => {
                if data.len() < offset + 2 {
                    return Ok(None);
                }
                let value = u64::from(u16::from_be_bytes([data[offset], data[offset + 1]]));
                if value < 126 {
                    return Err(WsError::NonMinimalLength);
                }
                offset += 2;
                value
            }
            127 => {
                if data.len() < offset + 8 {
                    return Ok(None);
                }
                let mut bytes = [0u8; 8];
                bytes.copy_from_slice(&data[offset..offset + 8]);
                let value = u64::from_be_bytes(bytes);
                if value & (1u64 << 63) != 0 {
                    return Err(WsError::UnsupportedLength);
                }
                if value <= u64::from(u16::MAX) {
                    return Err(WsError::NonMinimalLength);
                }
                offset += 8;
                value
            }
            other => u64::from(other),
        };

        if opcode.is_control() {
            if !fin {
                return Err(WsError::FragmentedControl);
            }
            if length > MAX_CONTROL_PAYLOAD as u64 {
                return Err(WsError::ControlTooLong);
            }
        }

        let length = usize::try_from(length).map_err(|_| WsError::PayloadTooLarge {
            length,
            limit: MAX_FRAME_PAYLOAD,
        })?;
        if length > MAX_FRAME_PAYLOAD {
            return Err(WsError::PayloadTooLarge {
                length: length as u64,
                limit: MAX_FRAME_PAYLOAD,
            });
        }

        let mask = if masked {
            if data.len() < offset + 4 {
                return Ok(None);
            }
            let mut key = [0u8; 4];
            key.copy_from_slice(&data[offset..offset + 4]);
            offset += 4;
            Some(key)
        } else {
            None
        };

        if data.len() < offset + length {
            return Ok(None);
        }

        let mut payload = data[offset..offset + length].to_vec();
        if let Some(key) = mask {
            for (index, byte) in payload.iter_mut().enumerate() {
                *byte ^= key[index & 3];
            }
        }

        if opcode == OpCode::Close {
            CloseFrame::parse(&payload)?;
        }

        Ok(Some((Frame::new(fin, opcode, payload), offset + length)))
    }
}

/// Parsed close frame payload (RFC 6455, section 5.5.1).
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CloseFrame {
    /// Two-byte status code, or `None` when the payload was empty.
    pub code: Option<u16>,
    /// UTF-8 reason following the code.
    pub reason: String,
}

impl CloseFrame {
    /// Builds a close payload, validating the code and the total length.
    ///
    /// # Errors
    ///
    /// Rejects codes reserved for internal use, a reason without a code, and
    /// payloads longer than 125 bytes.
    pub fn new(code: Option<u16>, reason: &str) -> Result<Self, WsError> {
        let frame = Self {
            code,
            reason: reason.to_owned(),
        };
        frame.encode()?;
        Ok(frame)
    }

    /// Parses a close frame payload.
    ///
    /// # Errors
    ///
    /// Rejects a one-byte payload, invalid status codes and invalid UTF-8.
    pub fn parse(payload: &[u8]) -> Result<Self, WsError> {
        match payload.len() {
            0 => Ok(Self {
                code: None,
                reason: String::new(),
            }),
            1 => Err(WsError::InvalidCloseFrame),
            _ => {
                let code = u16::from_be_bytes([payload[0], payload[1]]);
                if !is_valid_close_code(code) {
                    return Err(WsError::InvalidCloseCode(code));
                }
                let reason = core::str::from_utf8(&payload[2..])
                    .map_err(|_| WsError::InvalidUtf8)?
                    .to_owned();
                Ok(Self {
                    code: Some(code),
                    reason,
                })
            }
        }
    }

    /// Encodes the payload for a close frame.
    ///
    /// # Errors
    ///
    /// Rejects codes reserved for internal use, a reason without a code, and
    /// payloads longer than 125 bytes.
    pub fn encode(&self) -> Result<Vec<u8>, WsError> {
        match self.code {
            None => {
                if !self.reason.is_empty() {
                    return Err(WsError::InvalidCloseFrame);
                }
                Ok(Vec::new())
            }
            Some(code) => {
                if !is_valid_close_code(code) {
                    return Err(WsError::InvalidCloseCode(code));
                }
                if self.reason.len() > MAX_CLOSE_REASON {
                    return Err(WsError::ControlTooLong);
                }
                let mut payload = Vec::with_capacity(2 + self.reason.len());
                payload.extend_from_slice(&code.to_be_bytes());
                payload.extend_from_slice(self.reason.as_bytes());
                Ok(payload)
            }
        }
    }
}

/// Whether `code` may appear in a close frame on the wire.
///
/// 1005, 1006 and 1015 are reserved for local reporting and must not be sent;
/// codes 0-999, 1004, 1016-2999 and 5000+ are unassigned or reserved.
#[must_use]
pub fn is_valid_close_code(code: u16) -> bool {
    matches!(code, 1000..=1003 | 1007..=1014 | 3000..=4999)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tls::client_hello::SplitMix64;

    fn masked_frame(first: u8, short_length: u8, extended: &[u8], payload: &[u8]) -> Vec<u8> {
        let mut out = vec![first, 0x80 | short_length];
        out.extend_from_slice(extended);
        out.extend_from_slice(&[0, 0, 0, 0]);
        out.extend_from_slice(payload);
        out
    }

    fn unmasked_frame(first: u8, short_length: u8, extended: &[u8], payload: &[u8]) -> Vec<u8> {
        let mut out = vec![first, short_length];
        out.extend_from_slice(extended);
        out.extend_from_slice(payload);
        out
    }

    #[test]
    fn client_frame_roundtrip_for_every_opcode() {
        let mut rng = SplitMix64::new(1);
        let close = CloseFrame::new(Some(1000), "bye").expect("valid close frame");
        let frames = [
            Frame::text("hello"),
            Frame::binary(vec![0, 1, 2, 255]),
            Frame::ping(b"ping".to_vec()),
            Frame::pong(b"pong".to_vec()),
            Frame::new(true, OpCode::Close, close.encode().expect("close payload")),
            Frame::new(false, OpCode::Text, b"frag".to_vec()),
            Frame::new(true, OpCode::Continuation, b"ment".to_vec()),
        ];

        for frame in frames {
            let bytes = frame.encode(&mut rng).expect("encode");
            assert_ne!(bytes[1] & 0x80, 0, "client frame must be masked");
            let mut parser = FrameParser::new(Endpoint::Server);
            parser.push(&bytes);
            assert_eq!(parser.next_frame().expect("parse"), Some(frame));
            assert_eq!(parser.next_frame().expect("drained"), None);
            assert_eq!(parser.buffered_len(), 0);
        }
    }

    #[test]
    fn server_frame_roundtrip_is_unmasked() {
        let frame = Frame::text("from server");
        let bytes = frame.encode_unmasked().expect("encode");
        assert_eq!(bytes[1] & 0x80, 0);
        let mut parser = FrameParser::new(Endpoint::Client);
        parser.push(&bytes);
        assert_eq!(parser.next_frame().expect("parse"), Some(frame));
    }

    #[test]
    fn length_boundaries_use_minimal_encoding() {
        let mut rng = SplitMix64::new(2);
        for length in [0usize, 1, 125, 126, 127, 65_535, 65_536, 70_000] {
            let frame = Frame::binary(vec![0x5a; length]);
            let bytes = frame.encode(&mut rng).expect("encode");
            let short = bytes[1] & 0x7f;
            match length {
                0..=125 => assert_eq!(usize::from(short), length),
                126..=65_535 => assert_eq!(short, 126),
                _ => assert_eq!(short, 127),
            }

            let mut parser = FrameParser::new(Endpoint::Server);
            parser.push(&bytes);
            assert_eq!(parser.next_frame().expect("parse"), Some(frame));
        }
    }

    #[test]
    fn accepts_frame_delivered_one_byte_at_a_time() {
        let mut rng = SplitMix64::new(3);
        let frame = Frame::binary((0..200).map(|value| value as u8).collect());
        let bytes = frame.encode(&mut rng).expect("encode");
        let mut parser = FrameParser::new(Endpoint::Server);
        for (index, byte) in bytes.iter().enumerate() {
            parser.push(&[*byte]);
            let result = parser.next_frame().expect("partial parse");
            if index + 1 == bytes.len() {
                assert_eq!(result, Some(frame.clone()));
            } else {
                assert_eq!(result, None, "frame completed too early at byte {index}");
            }
        }
    }

    #[test]
    fn returns_back_to_back_frames_in_order() {
        let mut rng = SplitMix64::new(4);
        let first = Frame::text("one");
        let second = Frame::binary(vec![2, 2, 2]);
        let mut bytes = first.encode(&mut rng).expect("encode first");
        bytes.extend_from_slice(&second.encode(&mut rng).expect("encode second"));

        let mut parser = FrameParser::new(Endpoint::Server);
        parser.push(&bytes);
        assert_eq!(parser.next_frame().expect("first"), Some(first));
        assert_eq!(parser.next_frame().expect("second"), Some(second));
        assert_eq!(parser.next_frame().expect("empty"), None);
    }

    #[test]
    fn rejects_unmasked_client_frame() {
        let mut parser = FrameParser::new(Endpoint::Server);
        parser.push(&unmasked_frame(0x82, 1, &[], b"a"));
        assert!(matches!(parser.next_frame(), Err(WsError::MissingMask)));
    }

    #[test]
    fn rejects_masked_server_frame() {
        let mut parser = FrameParser::new(Endpoint::Client);
        parser.push(&masked_frame(0x82, 1, &[], b"a"));
        assert!(matches!(parser.next_frame(), Err(WsError::UnexpectedMask)));
    }

    #[test]
    fn rejects_invalid_control_frames() {
        let fragmented = masked_frame(0x09, 0, &[], &[]);
        let oversized = masked_frame(0x89, 126, &[0, 126], &[0; 126]);

        for bytes in [fragmented, oversized] {
            let mut parser = FrameParser::new(Endpoint::Server);
            parser.push(&bytes);
            assert!(parser.next_frame().is_err());
        }

        let mut parser = FrameParser::new(Endpoint::Server);
        parser.push(&masked_frame(0x09, 0, &[], &[]));
        assert!(matches!(
            parser.next_frame(),
            Err(WsError::FragmentedControl)
        ));

        let mut parser = FrameParser::new(Endpoint::Server);
        parser.push(&masked_frame(0x89, 126, &[0, 126], &[0; 126]));
        assert!(matches!(parser.next_frame(), Err(WsError::ControlTooLong)));
    }

    #[test]
    fn rejects_reserved_bits_and_unknown_opcode() {
        let mut parser = FrameParser::new(Endpoint::Server);
        parser.push(&masked_frame(0xc2, 0, &[], &[]));
        assert!(matches!(parser.next_frame(), Err(WsError::ReservedBits)));

        let mut parser = FrameParser::new(Endpoint::Server);
        parser.push(&masked_frame(0x83, 0, &[], &[]));
        assert!(matches!(
            parser.next_frame(),
            Err(WsError::InvalidOpcode(3))
        ));
    }

    #[test]
    fn rejects_non_minimal_and_unsupported_lengths() {
        let mut parser = FrameParser::new(Endpoint::Server);
        parser.push(&masked_frame(0x82, 126, &[0, 5], &[0; 5]));
        assert!(matches!(
            parser.next_frame(),
            Err(WsError::NonMinimalLength)
        ));

        let mut parser = FrameParser::new(Endpoint::Server);
        let mut extended = [0u8; 8];
        extended[6] = 0x01;
        parser.push(&masked_frame(0x82, 127, &extended, &[0; 256]));
        assert!(matches!(
            parser.next_frame(),
            Err(WsError::NonMinimalLength)
        ));

        let mut parser = FrameParser::new(Endpoint::Server);
        let mut huge = [0u8; 8];
        huge[0] = 0x80;
        parser.push(&masked_frame(0x82, 127, &huge, &[]));
        assert!(matches!(
            parser.next_frame(),
            Err(WsError::UnsupportedLength)
        ));
    }

    #[test]
    fn rejects_payload_over_limit_before_waiting_for_data() {
        let mut parser = FrameParser::new(Endpoint::Server);
        let too_large = (MAX_FRAME_PAYLOAD as u64) + 1;
        parser.push(&masked_frame(0x82, 127, &too_large.to_be_bytes(), &[]));
        assert!(matches!(
            parser.next_frame(),
            Err(WsError::PayloadTooLarge { .. })
        ));
    }

    #[test]
    fn rejects_invalid_close_frames() {
        let mut parser = FrameParser::new(Endpoint::Server);
        parser.push(&masked_frame(0x88, 1, &[], &[0]));
        assert!(matches!(
            parser.next_frame(),
            Err(WsError::InvalidCloseFrame)
        ));

        let mut parser = FrameParser::new(Endpoint::Server);
        parser.push(&masked_frame(0x88, 2, &[], &1005u16.to_be_bytes()));
        assert!(matches!(
            parser.next_frame(),
            Err(WsError::InvalidCloseCode(1005))
        ));
    }

    #[test]
    fn close_frame_roundtrip_and_validation() {
        assert_eq!(
            CloseFrame::parse(&[]).expect("empty close"),
            CloseFrame {
                code: None,
                reason: String::new()
            }
        );

        let frame = CloseFrame::new(Some(1001), "going away").expect("valid");
        assert_eq!(
            CloseFrame::parse(&frame.encode().expect("encode")).expect("parse"),
            frame
        );

        assert!(matches!(
            CloseFrame::parse(&[0]),
            Err(WsError::InvalidCloseFrame)
        ));
        assert!(matches!(
            CloseFrame::new(None, "reason without code"),
            Err(WsError::InvalidCloseFrame)
        ));
        assert!(matches!(
            CloseFrame::new(Some(1005), ""),
            Err(WsError::InvalidCloseCode(1005))
        ));
        assert!(matches!(
            CloseFrame::new(Some(1000), &"x".repeat(MAX_CLOSE_REASON + 1)),
            Err(WsError::ControlTooLong)
        ));
        assert!(is_valid_close_code(1000));
        assert!(is_valid_close_code(3000));
        assert!(is_valid_close_code(4999));
        assert!(!is_valid_close_code(1004));
        assert!(!is_valid_close_code(1005));
        assert!(!is_valid_close_code(1015));
        assert!(!is_valid_close_code(2999));
        assert!(!is_valid_close_code(5000));
    }
}
