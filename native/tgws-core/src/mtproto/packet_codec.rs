//! Whole-packet framing for the HTTP/2 media route.
//!
//! Ported from `src/TgwsOne.Engine/MtProto/PacketCodec.cs`. The C# version reads
//! from a `Stream` and decrypts through `AesCtr`; here the caller supplies the
//! already-decrypted frame and the transport keeps the stream. `EncodeReply`
//! takes the padded-transport padding from the caller instead of drawing it
//! from `RandomNumberGenerator`, so no entropy source is hidden inside the
//! core.
//!
//! Two `int` expressions in the original wrap silently (RT plan, section 6):
//! `20 + ReadInt32LittleEndian(body[16..20])` and `length - packetLength`. This
//! port uses `wrapping_add`/`wrapping_sub` and keeps the same range checks and
//! error messages, so the observable result is identical to C#.

use core::fmt;

use crate::proto::MtProtoConstants;

/// A packet that the codec refuses or cannot read.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct PacketError {
    message: String,
}

impl PacketError {
    fn new(message: impl Into<String>) -> Self {
        Self {
            message: message.into(),
        }
    }

    #[must_use]
    pub fn message(&self) -> &str {
        &self.message
    }
}

impl fmt::Display for PacketError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(&self.message)
    }
}

impl std::error::Error for PacketError {}

/// Reads and writes whole MTProto transport packets.
pub struct PacketCodec;

impl PacketCodec {
    pub const MAX_PACKET: usize = 4 * 1024 * 1024;
    const MAX_PLAIN_PADDING: i32 = 256;

    /// Decodes one packet from its decrypted header and body.
    ///
    /// Returns the body and the abridged "quick ack" bit. The bit is carried
    /// but not acted on, as upstream. When the frame ends before the declared
    /// length, the error replaces the `EndOfStreamException` the C# stream
    /// read would throw.
    ///
    /// # Errors
    ///
    /// Returns [`PacketError`] for a length outside the supported range, a
    /// malformed padded frame, or a body that is not 4-byte aligned.
    pub fn decode(frame: &[u8], tag: u32) -> Result<(Vec<u8>, bool), PacketError> {
        let mut position = 0usize;
        let length: i32;
        let quick_ack: bool;

        if tag == MtProtoConstants::PROTO_ABRIDGED {
            let first = *frame
                .get(position)
                .ok_or_else(|| PacketError::new("unexpected end of stream"))?;
            position += 1;
            quick_ack = (first & 0x80) != 0;
            let mut words = i32::from(first & 0x7F);
            if words == 0x7F {
                let extended = frame
                    .get(position..position + 3)
                    .ok_or_else(|| PacketError::new("unexpected end of stream"))?;
                position += 3;
                words = i32::from(extended[0])
                    | (i32::from(extended[1]) << 8)
                    | (i32::from(extended[2]) << 16);
            }
            length = words * 4;
        } else {
            let value = frame
                .get(position..position + 4)
                .ok_or_else(|| PacketError::new("unexpected end of stream"))?;
            position += 4;
            let packed = u32::from_le_bytes([value[0], value[1], value[2], value[3]]);
            quick_ack = (packed & 0x8000_0000) != 0;
            length = (packed & 0x7FFF_FFFF) as i32;
        }

        let max_length = Self::MAX_PACKET as i64
            + if tag == MtProtoConstants::PROTO_SECURE {
                Self::MAX_PLAIN_PADDING as i64
            } else {
                0
            };
        if length < 24 || i64::from(length) > max_length {
            return Err(PacketError::new(format!(
                "native packet length outside supported range: {length}"
            )));
        }

        let body_slice = frame
            .get(position..position + length as usize)
            .ok_or_else(|| PacketError::new("unexpected end of stream"))?;
        let mut body = body_slice.to_vec();

        if tag == MtProtoConstants::PROTO_SECURE {
            let packet_length: i32;
            let max_padding: i32;
            if body[..8].iter().all(|&byte| byte == 0) {
                let value = i32::from_le_bytes([body[16], body[17], body[18], body[19]]);
                packet_length = 20i32.wrapping_add(value);
                max_padding = Self::MAX_PLAIN_PADDING;
            } else {
                packet_length = 24 + ((length - 24) / 16) * 16;
                max_padding = 15;
            }
            let padding = length.wrapping_sub(packet_length);
            if packet_length < 24 || packet_length > length || padding > max_padding {
                return Err(PacketError::new(format!(
                    "invalid padded native packet: frame={length} packet={packet_length} padding={padding} limit={max_padding}"
                )));
            }
            body.truncate(packet_length as usize);
        }

        if body.len() % 4 != 0 || body.len() > Self::MAX_PACKET {
            return Err(PacketError::new("invalid native packet alignment or size"));
        }

        Ok((body, quick_ack))
    }

    /// Encodes a response without padded-transport padding.
    ///
    /// # Errors
    ///
    /// Returns [`PacketError`] when the body is empty, unaligned, or larger
    /// than [`Self::MAX_PACKET`].
    pub fn encode_reply(body: &[u8], tag: u32) -> Result<Vec<u8>, PacketError> {
        Self::encode_reply_with_padding(body, tag, &[])
    }

    /// Encodes a response, using `padding` verbatim for the padded transport.
    ///
    /// The C# original draws between zero and three padding bytes from
    /// `RandomNumberGenerator`; the caller here supplies them. For every other
    /// transport the argument is ignored.
    ///
    /// # Errors
    ///
    /// Returns [`PacketError`] when the body is empty, unaligned, or larger
    /// than [`Self::MAX_PACKET`].
    pub fn encode_reply_with_padding(
        body: &[u8],
        tag: u32,
        padding: &[u8],
    ) -> Result<Vec<u8>, PacketError> {
        if body.is_empty() || body.len() % 4 != 0 || body.len() > Self::MAX_PACKET {
            return Err(PacketError::new(format!(
                "invalid HTTP MTProto response length: {}",
                body.len()
            )));
        }

        if tag == MtProtoConstants::PROTO_ABRIDGED {
            let words = body.len() / 4;
            let prefix: Vec<u8> = if words < 0x7F {
                vec![words as u8]
            } else {
                vec![
                    0x7F,
                    (words & 0xFF) as u8,
                    ((words >> 8) & 0xFF) as u8,
                    ((words >> 16) & 0xFF) as u8,
                ]
            };
            let mut result = prefix;
            result.extend_from_slice(body);
            return Ok(result);
        }

        let padding = if tag == MtProtoConstants::PROTO_SECURE {
            padding
        } else {
            &[]
        };
        let total = body.len() + padding.len();
        let mut output = Vec::with_capacity(4 + total);
        output.extend_from_slice(&(total as i32).to_le_bytes());
        output.extend_from_slice(body);
        output.extend_from_slice(padding);
        Ok(output)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn intermediate_round_trip() {
        let body = [0u8; 64];
        let wire = PacketCodec::encode_reply(&body, MtProtoConstants::PROTO_INTERMEDIATE).unwrap();
        let (decoded, quick) =
            PacketCodec::decode(&wire, MtProtoConstants::PROTO_INTERMEDIATE).unwrap();
        assert_eq!(decoded, body);
        assert!(!quick);
    }

    #[test]
    fn abridged_quick_ack_bit_is_reported() {
        let mut frame = vec![0x80 | 6];
        frame.extend_from_slice(&[0u8; 24]);
        let (body, quick) = PacketCodec::decode(&frame, MtProtoConstants::PROTO_ABRIDGED).unwrap();
        assert_eq!(body, [0u8; 24]);
        assert!(quick);
    }

    #[test]
    fn padded_branch_wraps_like_csharp() {
        // First eight body bytes are zero, so packetLength = 20 + int32 at
        // body offset 16. 20 + 8 = 28; frame length 40 leaves padding of 12.
        let mut frame = 40i32.to_le_bytes().to_vec();
        frame.extend_from_slice(&[0u8; 40]);
        frame[4 + 16..4 + 20].copy_from_slice(&8i32.to_le_bytes());
        let (body, _) = PacketCodec::decode(&frame, MtProtoConstants::PROTO_SECURE).unwrap();
        assert_eq!(body.len(), 28);
    }

    #[test]
    fn padded_frame_rejects_excess_padding() {
        let mut frame = 300i32.to_le_bytes().to_vec();
        frame.extend_from_slice(&[0u8; 300]);
        frame[4 + 16..4 + 20].copy_from_slice(&4i32.to_le_bytes()); // packetLength = 24, padding = 276
        let error = PacketCodec::decode(&frame, MtProtoConstants::PROTO_SECURE).unwrap_err();
        assert!(error.message().contains("padding=276"), "{error}");
    }

    #[test]
    fn rejects_length_outside_the_range() {
        let frame = 20i32.to_le_bytes();
        let error = PacketCodec::decode(&frame, MtProtoConstants::PROTO_INTERMEDIATE).unwrap_err();
        assert_eq!(
            error.message(),
            "native packet length outside supported range: 20"
        );
    }

    #[test]
    fn rejects_unaligned_body() {
        let mut frame = 25i32.to_le_bytes().to_vec();
        frame.extend_from_slice(&[0u8; 25]);
        let error = PacketCodec::decode(&frame, MtProtoConstants::PROTO_INTERMEDIATE).unwrap_err();
        assert_eq!(error.message(), "invalid native packet alignment or size");
    }

    #[test]
    fn encode_rejects_empty_and_unaligned_bodies() {
        assert!(PacketCodec::encode_reply(&[], MtProtoConstants::PROTO_INTERMEDIATE).is_err());
        assert!(
            PacketCodec::encode_reply(&[0u8; 7], MtProtoConstants::PROTO_INTERMEDIATE).is_err()
        );
    }

    #[test]
    fn padded_encode_uses_injected_padding() {
        let wire = PacketCodec::encode_reply_with_padding(
            &[0u8; 32],
            MtProtoConstants::PROTO_SECURE,
            &[9, 8, 7],
        )
        .unwrap();
        assert_eq!(wire.len(), 4 + 32 + 3);
        assert_eq!(i32::from_le_bytes(wire[..4].try_into().unwrap()), 35);
        assert_eq!(&wire[36..], &[9, 8, 7]);
    }
}
