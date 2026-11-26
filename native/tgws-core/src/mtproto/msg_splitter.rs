//! Packet splitting for the WebSocket route.
//!
//! Ported from `src/TgwsOne.Engine/MtProto/MsgSplitter.cs`. Telegram Desktop
//! may coalesce several MTProto packets into one TLS/TCP write, while the
//! WebSocket endpoint expects one packet per frame. The ciphertext is queued
//! next to the plaintext because packet lengths are visible only after
//! decryption, yet the bytes sent on must be the original ciphertext.
//!
//! The C# instance keeps its state between calls; this port does the same. The
//! C# `int packetLength = 4 + payloadLength` in the intermediate header wraps
//! silently (see the RT plan, section 6), so [`MsgSplitter`] uses
//! `wrapping_add` and treats the resulting non-positive length exactly like the
//! original: the rest is passed through and splitting is disabled.

use core::fmt;

use crate::proto::MtProtoConstants;

/// Why a chunk cannot be split.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum SplitError {
    /// The plaintext chunk is shorter than the ciphertext chunk, so a header
    /// byte is not available. The C# original throws `IndexOutOfRangeException`
    /// (or `ArgumentOutOfRangeException` from `RemoveRange`) in this case.
    ShortPlain,
}

impl fmt::Display for SplitError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::ShortPlain => f.write_str("plain chunk is shorter than cipher chunk"),
        }
    }
}

impl std::error::Error for SplitError {}

/// Splits an obfuscated2 stream into individual transport packets.
pub struct MsgSplitter {
    proto: u32,
    cipher: Vec<u8>,
    plain: Vec<u8>,
    disabled: bool,
}

impl MsgSplitter {
    #[must_use]
    pub fn new(proto_tag: u32) -> Self {
        Self {
            proto: proto_tag,
            cipher: Vec::new(),
            plain: Vec::new(),
            disabled: false,
        }
    }

    /// Whether the splitter has latched into pass-through mode.
    #[must_use]
    pub fn is_disabled(&self) -> bool {
        self.disabled
    }

    /// Feeds one ciphertext/plaintext chunk and returns the packets that became
    /// complete. An empty ciphertext chunk is a no-op, exactly like C#.
    ///
    /// # Errors
    ///
    /// Returns [`SplitError::ShortPlain`] when a packet header or the bytes to
    /// drop after a packet are not present in the plaintext chunk.
    pub fn split(
        &mut self,
        cipher_chunk: &[u8],
        plain_chunk: &[u8],
    ) -> Result<Vec<Vec<u8>>, SplitError> {
        if cipher_chunk.is_empty() {
            return Ok(Vec::new());
        }
        if self.disabled {
            return Ok(vec![cipher_chunk.to_vec()]);
        }

        self.cipher.extend_from_slice(cipher_chunk);
        self.plain.extend_from_slice(plain_chunk);

        let mut parts = Vec::new();
        let mut offset = 0usize;
        let buffer_length = self.cipher.len();
        while offset < buffer_length {
            let Some(packet_length) = self.next_packet_length(offset, buffer_length - offset)?
            else {
                break;
            };
            if packet_length <= 0 {
                parts.push(self.cipher[offset..].to_vec());
                offset = buffer_length;
                self.disabled = true;
                break;
            }
            let packet_length = packet_length as usize;
            if offset == 0 && packet_length == buffer_length {
                parts.push(self.cipher.clone());
            } else {
                parts.push(self.cipher[offset..offset + packet_length].to_vec());
            }
            offset += packet_length;
        }

        if offset > 0 {
            if offset > self.plain.len() {
                return Err(SplitError::ShortPlain);
            }
            self.cipher.drain(..offset);
            self.plain.drain(..offset);
        }
        Ok(parts)
    }

    /// Whatever is left when the client is done writing: sent as one last
    /// frame, then the buffer is cleared.
    pub fn flush(&mut self) -> Vec<Vec<u8>> {
        if self.cipher.is_empty() {
            return Vec::new();
        }
        let tail = core::mem::take(&mut self.cipher);
        self.plain.clear();
        vec![tail]
    }

    fn next_packet_length(
        &self,
        offset: usize,
        available: usize,
    ) -> Result<Option<i32>, SplitError> {
        if available == 0 {
            return Ok(None);
        }
        if self.proto == MtProtoConstants::PROTO_ABRIDGED {
            return self.next_abridged_length(offset, available);
        }
        if self.proto == MtProtoConstants::PROTO_INTERMEDIATE
            || self.proto == MtProtoConstants::PROTO_SECURE
        {
            return self.next_intermediate_length(offset, available);
        }
        Ok(Some(0))
    }

    fn next_abridged_length(
        &self,
        offset: usize,
        available: usize,
    ) -> Result<Option<i32>, SplitError> {
        let first = i32::from(*self.plain.get(offset).ok_or(SplitError::ShortPlain)?);
        let (payload_length, header_length) = if first == 0x7F || first == 0xFF {
            if available < 4 {
                return Ok(None);
            }
            let extended = self
                .plain
                .get(offset + 1..offset + 4)
                .ok_or(SplitError::ShortPlain)?;
            let words = i32::from(extended[0])
                | (i32::from(extended[1]) << 8)
                | (i32::from(extended[2]) << 16);
            (words * 4, 4i32)
        } else {
            ((first & 0x7F) * 4, 1i32)
        };
        if payload_length <= 0 {
            return Ok(Some(0));
        }
        let packet_length = header_length + payload_length;
        if available < packet_length as usize {
            Ok(None)
        } else {
            Ok(Some(packet_length))
        }
    }

    fn next_intermediate_length(
        &self,
        offset: usize,
        available: usize,
    ) -> Result<Option<i32>, SplitError> {
        if available < 4 {
            return Ok(None);
        }
        let header = self
            .plain
            .get(offset..offset + 4)
            .ok_or(SplitError::ShortPlain)?;
        let value = u32::from_le_bytes([header[0], header[1], header[2], header[3]]);
        let payload_length = (value & 0x7FFF_FFFF) as i32;
        if payload_length <= 0 {
            return Ok(Some(0));
        }
        // C# is unchecked: `4 + 0x7FFFFFFF` is negative, and the negative value
        // is then caught by the `packetLength <= 0` branch in `Split`.
        let packet_length = 4i32.wrapping_add(payload_length);
        if packet_length > 0 && available < packet_length as usize {
            Ok(None)
        } else {
            Ok(Some(packet_length))
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn abridged_payload(payload: &[u8]) -> Vec<u8> {
        let mut packet = vec![(payload.len() / 4) as u8];
        packet.extend_from_slice(payload);
        packet
    }

    #[test]
    fn abridged_splits_two_packets_from_one_chunk() {
        let a = abridged_payload(&[1, 2, 3, 4]);
        let b = abridged_payload(&[5, 6, 7, 8, 9, 10, 11, 12]);
        let chunk: Vec<u8> = [a.clone(), b.clone()].concat();

        let mut splitter = MsgSplitter::new(MtProtoConstants::PROTO_ABRIDGED);
        let parts = splitter.split(&chunk, &chunk).unwrap();

        assert_eq!(parts, vec![a, b]);
    }

    #[test]
    fn intermediate_length_wraps_like_csharp_and_disables_splitting() {
        // payloadLength = 0x7FFFFFFF; 4 + payloadLength wraps to a negative
        // number, which the C# code treats as "send the rest, then stop".
        let chunk = [0xFF, 0xFF, 0xFF, 0x7F, 0xAA, 0xBB];
        let mut splitter = MsgSplitter::new(MtProtoConstants::PROTO_INTERMEDIATE);

        let parts = splitter.split(&chunk, &chunk).unwrap();
        assert_eq!(parts, vec![chunk.to_vec()]);
        assert!(splitter.is_disabled());

        let later = [9u8, 8, 7, 6];
        assert_eq!(
            splitter.split(&later, &later).unwrap(),
            vec![later.to_vec()]
        );
    }

    #[test]
    fn zero_length_packet_disables_splitting() {
        let bad = [0u8; 4];
        let chunk: Vec<u8> = [bad.to_vec(), vec![1, 2, 3, 4]].concat();
        let mut splitter = MsgSplitter::new(MtProtoConstants::PROTO_SECURE);

        assert_eq!(splitter.split(&chunk, &chunk).unwrap(), vec![chunk.clone()]);
        assert!(splitter.is_disabled());
    }

    #[test]
    fn short_plain_chunk_is_an_error() {
        let cipher = abridged_payload(&[1, 2, 3, 4]);
        let mut splitter = MsgSplitter::new(MtProtoConstants::PROTO_ABRIDGED);
        assert_eq!(splitter.split(&cipher, &[]), Err(SplitError::ShortPlain));
    }

    #[test]
    fn flush_returns_the_incomplete_tail() {
        let packet = abridged_payload(&[1, 2, 3, 4, 5, 6, 7, 8]);
        let mut splitter = MsgSplitter::new(MtProtoConstants::PROTO_ABRIDGED);
        assert!(
            splitter
                .split(&packet[..6], &packet[..6])
                .unwrap()
                .is_empty()
        );
        assert_eq!(splitter.flush(), vec![packet[..6].to_vec()]);
        assert!(splitter.flush().is_empty());
    }
}
