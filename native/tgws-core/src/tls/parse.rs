//! Span-based parser for the first TLS flight.
//!
//! The parser mirrors the E2.1 task from the Android plan but is written from
//! RFC 8446/5246 and RFC 6066.  It never allocates and never copies: every
//! interesting field is reported as an offset into the caller's buffer.  This is
//! what a later desynchronisation planner needs in order to split or fake the
//! first ClientHello without rebuilding it.
//!
//! One rule from E2.1 is enforced strictly: the ClientHello must fit completely
//! into the first TLS record.  If a record was already fragmented by a client,
//! [`parse_client_hello`] returns [`ParseError::HandshakeNotInFirstRecord`].

use core::fmt;

/// TLS record content type for handshake messages (RFC 8446, section 5.1).
pub const TLS_RECORD_HANDSHAKE: u8 = 0x16;
/// Handshake message type for ClientHello (RFC 8446, section 4).
pub const HANDSHAKE_CLIENT_HELLO: u8 = 0x01;
/// Extension type for `server_name` (RFC 6066, section 3).
pub const EXTENSION_SERVER_NAME: u16 = 0x0000;
/// Name type `host_name` inside the `server_name` extension (RFC 6066).
pub const SERVER_NAME_HOST_NAME: u8 = 0x00;

/// Reasons why a first flight cannot be interpreted as a complete ClientHello.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum ParseError {
    /// The input ends before a declared header or body field is complete.
    ShortInput,
    /// The first record is not a TLS handshake record, or its payload is not a
    /// ClientHello handshake message.
    NotHandshake,
    /// The handshake message length exceeds the payload of the first record.
    /// This is the E2.1 rule: a ClientHello spread over several records is not
    /// accepted.
    HandshakeNotInFirstRecord,
    /// A field inside the handshake body is inconsistent with the handshake
    /// length.
    MalformedHandshake,
    /// The extensions block or one of its sub-structures is malformed.
    MalformedExtensions,
}

impl fmt::Display for ParseError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let message = match self {
            Self::ShortInput => "input is shorter than the declared TLS structure",
            Self::NotHandshake => "first record is not a ClientHello",
            Self::HandshakeNotInFirstRecord => "ClientHello does not fit into the first TLS record",
            Self::MalformedHandshake => "malformed ClientHello handshake body",
            Self::MalformedExtensions => "malformed ClientHello extensions",
        };
        f.write_str(message)
    }
}

impl std::error::Error for ParseError {}

/// A position in the first flight that a desynchronisation strategy can target.
///
/// `SniStart` and `SniEnd` deltas are signed so a strategy can point a few bytes
/// before or after the host name.  `Absolute` and `FromEnd` do not depend on the
/// presence of SNI.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum DesyncPosition {
    /// Absolute byte offset from the start of the first flight.
    Absolute(usize),
    /// Offset relative to the first byte of the SNI host name.
    SniStart(isize),
    /// Offset relative to the byte just past the SNI host name.
    SniEnd(isize),
    /// Middle of the SNI host name.
    SniMid,
    /// Middle of the second-level domain inside SNI (for example `exa|mple.com`).
    SldMid,
    /// Offset relative to the end of the first flight.
    FromEnd(isize),
}

/// Offsets of the interesting parts of a parsed ClientHello.
///
/// All offsets are byte indices into the buffer that was passed to
/// [`parse_client_hello`].  `record_start` is always zero today but is kept
/// explicit so callers do not have to hard-code it.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct ClientHelloLayout {
    /// Start of the first TLS record.
    pub record_start: usize,
    /// End of the first TLS record (exclusive).
    pub record_end: usize,
    /// Start of the handshake message, including its four-byte header.
    pub handshake_start: usize,
    /// End of the handshake message (exclusive).
    pub handshake_end: usize,
    /// Start of the extensions block.
    pub extensions_start: usize,
    /// End of the extensions block (exclusive).
    pub extensions_end: usize,
    /// Start of the first `host_name` value, if SNI is present.
    pub sni_start: Option<usize>,
    /// End of the first `host_name` value (exclusive), if SNI is present.
    pub sni_end: Option<usize>,
}

impl ClientHelloLayout {
    /// Returns the SNI host name bytes, or `None` when the ClientHello has no
    /// `host_name` entry.
    #[must_use]
    pub fn sni<'a>(&self, buf: &'a [u8]) -> Option<&'a [u8]> {
        let start = self.sni_start?;
        let end = self.sni_end?;
        buf.get(start..end)
    }

    /// Resolves a desynchronisation position to an absolute offset.
    ///
    /// Returns `None` when the position is not applicable (no SNI for an
    /// SNI-relative position) or when the resulting offset is outside the
    /// buffer.  An offset exactly at `buf.len()` is considered valid because it
    /// describes an append/split point.
    #[must_use]
    pub fn resolve(&self, pos: &DesyncPosition, buf: &[u8]) -> Option<usize> {
        match *pos {
            DesyncPosition::Absolute(offset) => (offset <= buf.len()).then_some(offset),
            DesyncPosition::SniStart(delta) => {
                let start = self.sni_start?;
                shift(start, delta, buf.len())
            }
            DesyncPosition::SniEnd(delta) => {
                let end = self.sni_end?;
                shift(end, delta, buf.len())
            }
            DesyncPosition::SniMid => {
                let start = self.sni_start?;
                let end = self.sni_end?;
                (end >= start).then(|| start + (end - start) / 2)
            }
            DesyncPosition::SldMid => {
                let sni = self.sni(buf)?;
                let middle = second_level_mid(sni)?;
                self.sni_start.map(|start| start + middle)
            }
            DesyncPosition::FromEnd(delta) => shift(buf.len(), delta, buf.len()),
        }
    }
}

/// Parses the first flight and returns the offsets of its ClientHello.
///
/// # Errors
///
/// Returns [`ParseError`] when the buffer is truncated, the first record is not
/// a handshake record, the handshake is not a ClientHello, or the ClientHello
/// does not fit into the first record.
pub fn parse_client_hello(first_flight: &[u8]) -> Result<ClientHelloLayout, ParseError> {
    if first_flight.len() < 5 {
        return Err(ParseError::ShortInput);
    }
    if first_flight[0] != TLS_RECORD_HANDSHAKE {
        return Err(ParseError::NotHandshake);
    }

    let record_len = u16::from_be_bytes([first_flight[3], first_flight[4]]) as usize;
    let record_end = 5usize
        .checked_add(record_len)
        .ok_or(ParseError::ShortInput)?;
    if first_flight.len() < record_end {
        return Err(ParseError::ShortInput);
    }
    if record_len < 4 {
        return Err(ParseError::ShortInput);
    }

    let handshake_start = 5;
    if first_flight[handshake_start] != HANDSHAKE_CLIENT_HELLO {
        return Err(ParseError::NotHandshake);
    }

    let handshake_len = read_u24(&first_flight[handshake_start + 1..handshake_start + 4]);
    let handshake_end = handshake_start
        .checked_add(4)
        .and_then(|pos| pos.checked_add(handshake_len))
        .ok_or(ParseError::ShortInput)?;
    if handshake_end > record_end {
        return Err(ParseError::HandshakeNotInFirstRecord);
    }

    let mut cursor = Cursor::new(first_flight, handshake_start + 4, handshake_end);
    cursor.skip(2, ParseError::MalformedHandshake)?; // legacy_version
    cursor.skip(32, ParseError::MalformedHandshake)?; // random

    let session_id_len = cursor.u8(ParseError::MalformedHandshake)? as usize;
    cursor.skip(session_id_len, ParseError::MalformedHandshake)?;

    let cipher_suites_len = cursor.u16(ParseError::MalformedHandshake)? as usize;
    if cipher_suites_len % 2 != 0 {
        return Err(ParseError::MalformedHandshake);
    }
    cursor.skip(cipher_suites_len, ParseError::MalformedHandshake)?;

    let compression_len = cursor.u8(ParseError::MalformedHandshake)? as usize;
    cursor.skip(compression_len, ParseError::MalformedHandshake)?;

    let extensions_len = cursor.u16(ParseError::MalformedExtensions)? as usize;
    let extensions_start = cursor.pos;
    let extensions = cursor.take(extensions_len, ParseError::MalformedExtensions)?;
    let extensions_end = cursor.pos;
    if cursor.remaining() != 0 {
        return Err(ParseError::MalformedHandshake);
    }

    let sni = parse_extensions(extensions, extensions_start)?;
    let (sni_start, sni_end) = match sni {
        Some((start, end)) => (Some(start), Some(end)),
        None => (None, None),
    };

    Ok(ClientHelloLayout {
        record_start: 0,
        record_end,
        handshake_start,
        handshake_end,
        extensions_start,
        extensions_end,
        sni_start,
        sni_end,
    })
}

fn parse_extensions(extensions: &[u8], base: usize) -> Result<Option<(usize, usize)>, ParseError> {
    let mut pos = 0;
    let mut sni = None;

    while pos < extensions.len() {
        if extensions.len() - pos < 4 {
            return Err(ParseError::MalformedExtensions);
        }
        let extension_type = u16::from_be_bytes([extensions[pos], extensions[pos + 1]]);
        let extension_len = u16::from_be_bytes([extensions[pos + 2], extensions[pos + 3]]) as usize;
        pos += 4;

        let end = pos
            .checked_add(extension_len)
            .ok_or(ParseError::MalformedExtensions)?;
        if end > extensions.len() {
            return Err(ParseError::MalformedExtensions);
        }

        if extension_type == EXTENSION_SERVER_NAME && sni.is_none() {
            sni = parse_server_name(&extensions[pos..end], base + pos)?;
        }

        pos = end;
    }

    Ok(sni)
}

fn parse_server_name(data: &[u8], base: usize) -> Result<Option<(usize, usize)>, ParseError> {
    if data.len() < 2 {
        return Err(ParseError::MalformedExtensions);
    }

    let list_len = u16::from_be_bytes([data[0], data[1]]) as usize;
    if 2 + list_len > data.len() {
        return Err(ParseError::MalformedExtensions);
    }
    let list_end = 2 + list_len;

    let mut pos = 2;
    while pos < list_end {
        if list_end - pos < 3 {
            return Err(ParseError::MalformedExtensions);
        }
        let name_type = data[pos];
        let name_len = u16::from_be_bytes([data[pos + 1], data[pos + 2]]) as usize;
        pos += 3;

        let end = pos
            .checked_add(name_len)
            .ok_or(ParseError::MalformedExtensions)?;
        if end > list_end {
            return Err(ParseError::MalformedExtensions);
        }

        if name_type == SERVER_NAME_HOST_NAME {
            return Ok(Some((base + pos, base + end)));
        }
        pos = end;
    }

    Ok(None)
}

fn read_u24(bytes: &[u8]) -> usize {
    ((bytes[0] as usize) << 16) | ((bytes[1] as usize) << 8) | bytes[2] as usize
}

fn shift(base: usize, delta: isize, len: usize) -> Option<usize> {
    let shifted = isize::try_from(base).ok()?.checked_add(delta)?;
    if shifted < 0 {
        return None;
    }
    let shifted = usize::try_from(shifted).ok()?;
    (shifted <= len).then_some(shifted)
}

fn second_level_mid(host: &[u8]) -> Option<usize> {
    let end = host.iter().rposition(|byte| *byte != b'.')? + 1;
    let host = &host[..end];

    let tld_dot = host.iter().rposition(|byte| *byte == b'.')?;
    let sld_start = host[..tld_dot]
        .iter()
        .rposition(|byte| *byte == b'.')
        .map_or(0, |dot| dot + 1);
    let sld_len = tld_dot - sld_start;
    if sld_len == 0 {
        return None;
    }

    Some(sld_start + sld_len / 2)
}

struct Cursor<'a> {
    buf: &'a [u8],
    pos: usize,
    end: usize,
}

impl<'a> Cursor<'a> {
    fn new(buf: &'a [u8], pos: usize, end: usize) -> Self {
        Self { buf, pos, end }
    }

    fn remaining(&self) -> usize {
        self.end.saturating_sub(self.pos)
    }

    fn u8(&mut self, error: ParseError) -> Result<u8, ParseError> {
        if self.remaining() < 1 {
            return Err(error);
        }
        let value = self.buf[self.pos];
        self.pos += 1;
        Ok(value)
    }

    fn u16(&mut self, error: ParseError) -> Result<u16, ParseError> {
        if self.remaining() < 2 {
            return Err(error);
        }
        let value = u16::from_be_bytes([self.buf[self.pos], self.buf[self.pos + 1]]);
        self.pos += 2;
        Ok(value)
    }

    fn skip(&mut self, len: usize, error: ParseError) -> Result<(), ParseError> {
        self.take(len, error).map(|_| ())
    }

    fn take(&mut self, len: usize, error: ParseError) -> Result<&'a [u8], ParseError> {
        let end = self.pos.checked_add(len).ok_or(error)?;
        if end > self.end {
            return Err(error);
        }
        let slice = &self.buf[self.pos..end];
        self.pos = end;
        Ok(slice)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn push_extension(out: &mut Vec<u8>, extension_type: u16, data: &[u8]) {
        out.extend_from_slice(&extension_type.to_be_bytes());
        out.extend_from_slice(&(data.len() as u16).to_be_bytes());
        out.extend_from_slice(data);
    }

    fn server_name_extension(sni: &str) -> Vec<u8> {
        let mut data = Vec::new();
        data.extend_from_slice(&((3 + sni.len()) as u16).to_be_bytes());
        data.push(SERVER_NAME_HOST_NAME);
        data.extend_from_slice(&(sni.len() as u16).to_be_bytes());
        data.extend_from_slice(sni.as_bytes());
        data
    }

    fn build_client_hello(
        sni: Option<&str>,
        session_id: &[u8],
        grease: bool,
        legacy_version: [u8; 2],
    ) -> Vec<u8> {
        let mut extensions = Vec::new();
        if grease {
            push_extension(&mut extensions, 0x0a0a, &[]);
        }
        if let Some(sni) = sni {
            push_extension(
                &mut extensions,
                EXTENSION_SERVER_NAME,
                &server_name_extension(sni),
            );
        }
        push_extension(&mut extensions, 0x002b, &[0x02, 0x03, 0x04, 0x03, 0x03]);

        let mut body = Vec::new();
        body.extend_from_slice(&legacy_version);
        body.extend_from_slice(&[0x5a; 32]);
        body.push(session_id.len() as u8);
        body.extend_from_slice(session_id);

        let mut ciphers = Vec::new();
        if grease {
            ciphers.extend_from_slice(&0x1a1a_u16.to_be_bytes());
        }
        ciphers.extend_from_slice(&0x1301_u16.to_be_bytes());
        ciphers.extend_from_slice(&0x1302_u16.to_be_bytes());
        ciphers.extend_from_slice(&0xc02b_u16.to_be_bytes());
        body.extend_from_slice(&(ciphers.len() as u16).to_be_bytes());
        body.extend_from_slice(&ciphers);

        body.push(1);
        body.push(0);
        body.extend_from_slice(&(extensions.len() as u16).to_be_bytes());
        body.extend_from_slice(&extensions);

        let mut record = Vec::new();
        record.push(TLS_RECORD_HANDSHAKE);
        record.extend_from_slice(&[0x03, 0x01]);
        record.extend_from_slice(&((4 + body.len()) as u16).to_be_bytes());
        record.push(HANDSHAKE_CLIENT_HELLO);
        record.extend_from_slice(&[
            ((body.len() >> 16) & 0xff) as u8,
            ((body.len() >> 8) & 0xff) as u8,
            (body.len() & 0xff) as u8,
        ]);
        record.extend_from_slice(&body);
        record
    }

    #[test]
    fn parses_tls13_client_hello_with_sni() {
        let flight = build_client_hello(Some("www.example.com"), &[0x11; 32], false, [0x03, 0x03]);
        let layout = parse_client_hello(&flight).expect("synthetic ClientHello must parse");

        assert_eq!(layout.record_start, 0);
        assert_eq!(layout.record_end, flight.len());
        assert_eq!(layout.handshake_start, 5);
        assert_eq!(layout.sni(&flight), Some(b"www.example.com".as_slice()));

        let start = layout.sni_start.expect("SNI start");
        let end = layout.sni_end.expect("SNI end");
        assert_eq!(&flight[start..end], b"www.example.com");
        assert_eq!(
            layout.resolve(&DesyncPosition::SniStart(0), &flight),
            Some(start)
        );
        assert_eq!(
            layout.resolve(&DesyncPosition::SniEnd(0), &flight),
            Some(end)
        );
        assert_eq!(
            layout.resolve(&DesyncPosition::SniMid, &flight),
            Some(start + 15 / 2)
        );
        assert_eq!(
            layout.resolve(&DesyncPosition::SldMid, &flight),
            Some(start + 4 + 7 / 2)
        );
        assert_eq!(
            layout.resolve(&DesyncPosition::FromEnd(0), &flight),
            Some(flight.len())
        );
        assert_eq!(
            layout.resolve(&DesyncPosition::Absolute(10), &flight),
            Some(10)
        );
    }

    #[test]
    fn parses_tls12_client_hello_without_session_id() {
        let flight = build_client_hello(Some("example.com"), &[], false, [0x03, 0x03]);
        let layout = parse_client_hello(&flight).expect("TLS 1.2 ClientHello must parse");
        assert_eq!(layout.sni(&flight), Some(b"example.com".as_slice()));
        assert_eq!(
            layout.resolve(&DesyncPosition::SldMid, &flight),
            layout.sni_start.map(|start| start + 3)
        );
    }

    #[test]
    fn tolerates_grease_values() {
        let flight = build_client_hello(Some("grease.example"), &[0x22; 32], true, [0x03, 0x03]);
        let layout = parse_client_hello(&flight).expect("GREASE must be ignored");
        assert_eq!(layout.sni(&flight), Some(b"grease.example".as_slice()));
    }

    #[test]
    fn missing_sni_makes_sni_positions_inapplicable() {
        let flight = build_client_hello(None, &[0x33; 32], false, [0x03, 0x03]);
        let layout = parse_client_hello(&flight).expect("ClientHello without SNI must parse");

        assert_eq!(layout.sni(&flight), None);
        assert_eq!(layout.resolve(&DesyncPosition::SniStart(0), &flight), None);
        assert_eq!(layout.resolve(&DesyncPosition::SniEnd(0), &flight), None);
        assert_eq!(layout.resolve(&DesyncPosition::SniMid, &flight), None);
        assert_eq!(layout.resolve(&DesyncPosition::SldMid, &flight), None);
        assert_eq!(
            layout.resolve(&DesyncPosition::Absolute(7), &flight),
            Some(7)
        );
    }

    #[test]
    fn rejects_truncated_input() {
        let flight = build_client_hello(Some("example.com"), &[0x44; 32], false, [0x03, 0x03]);
        for cut in 0..flight.len() {
            assert!(parse_client_hello(&flight[..cut]).is_err(), "cut at {cut}");
        }
    }

    #[test]
    fn rejects_non_handshake_record() {
        let mut flight = build_client_hello(Some("example.com"), &[0x55; 32], false, [0x03, 0x03]);
        flight[0] = 0x17;
        assert_eq!(parse_client_hello(&flight), Err(ParseError::NotHandshake));
    }

    #[test]
    fn rejects_client_hello_split_across_records() {
        let flight = build_client_hello(Some("example.com"), &[0x66; 32], false, [0x03, 0x03]);
        let mut split = flight.clone();
        let shorter = split.len() - 10;
        split[3..5].copy_from_slice(&((shorter - 5) as u16).to_be_bytes());
        split.truncate(shorter);

        assert_eq!(
            parse_client_hello(&split),
            Err(ParseError::HandshakeNotInFirstRecord)
        );
    }

    #[test]
    fn sld_mid_handles_short_hosts() {
        assert_eq!(second_level_mid(b"localhost"), None);
        assert_eq!(second_level_mid(b"example.com."), Some(3));
        assert_eq!(second_level_mid(b"www.example.com"), Some(7));
    }
}
