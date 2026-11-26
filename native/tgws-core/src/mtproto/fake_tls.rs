//! The fake-TLS handshake of MTProxy's `ee` secret.
//!
//! Ported from `src/TgwsOne.Engine/Transport/FakeTls.cs`, which follows
//! upstream `proxy/fake_tls.py` (v1.11.4). This is not TLS: a client sends an
//! ordinary TLS 1.3 ClientHello whose `random` field carries an HMAC of the
//! hello and a timestamp. The proxy answers with a ServerHello,
//! ChangeCipherSpec and one ApplicationData record whose own random field is
//! the HMAC over the client random and the whole answer. Nothing derives real
//! keys; the tunnel inside is obfuscated2, which is why the records only have
//! to look like TLS to a prober.
//!
//! `BuildResponse` in C# draws a key share and the ApplicationData filler from
//! `RandomNumberGenerator`/`Random.Shared`. Here the caller supplies an RNG
//! callback, so a test (or a platform backend) controls the bytes. The HMAC and
//! the record shape, which are the contract, do not depend on that choice.

use core::fmt;

use sha2::{Digest, Sha256};
use std::time::{SystemTime, UNIX_EPOCH};

pub const RECORD_HANDSHAKE: u8 = 0x16;
pub const RECORD_CCS: u8 = 0x14;
pub const RECORD_ALERT: u8 = 0x15;
pub const RECORD_APP_DATA: u8 = 0x17;

/// Largest payload a single TLS record may carry.
pub const MAX_RECORD_PAYLOAD: usize = 16_384;

/// How far the timestamp inside the client random may be from now, in seconds.
pub const TIMESTAMP_TOLERANCE: i64 = 120;

/// Lowest size of the first ApplicationData record the masking site sends.
pub const APP_DATA_MIN: usize = 64;

/// Without a profile stay near the smallest records such a site sends.
pub const APP_DATA_FALLBACK_LOW: usize = 64;
pub const APP_DATA_FALLBACK_HIGH: usize = 160;

pub const ALERT_HANDSHAKE_FAILURE: u8 = 0x28;
pub const ALERT_UNEXPECTED_MESSAGE: u8 = 0x0A;

const CLIENT_RANDOM_OFFSET: usize = 11;
const CLIENT_RANDOM_LEN: usize = 32;
const SESSION_ID_OFFSET: usize = 44;
const SESSION_ID_LEN: usize = 32;

const EXT_KEY_SHARE: u16 = 0x0033;

const GROUP_X25519: i32 = 0x001D;
const GROUP_X25519_MLKEM768: i32 = 0x11EC;

const X25519_KEY_LEN: usize = 32;
const MLKEM_CLIENT_SHARE_LEN: i32 = 1216;
const MLKEM_CIPHERTEXT_LEN: usize = 1088;

const SERVER_HELLO_RANDOM_OFFSET: usize = 11;

/// TLS 1.3 suites in the order a typical server picks them from the client's list.
const CIPHER_PREFERENCE: [i32; 3] = [0x1301, 0x1303, 0x1302];

const CHANGE_CIPHER_SPEC: [u8; 6] = [0x14, 0x03, 0x03, 0x00, 0x01, 0x01];

/// What a ClientHello offered: cipher suites and key_share groups with their
/// key_exchange sizes.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct ClientHelloInfo {
    pub cipher_suites: std::collections::BTreeSet<i32>,
    pub key_shares: std::collections::BTreeMap<i32, i32>,
}

impl ClientHelloInfo {
    #[must_use]
    pub fn empty() -> Self {
        Self::default()
    }
}

/// The parts of a verified ClientHello the answer is built from.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FakeTlsClientHello {
    pub client_random: Vec<u8>,
    pub session_id: Vec<u8>,
    pub timestamp: u32,
}

/// A refused or truncated record.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum FakeTlsError {
    /// The record header is shorter than five bytes.
    ShortHeader,
    /// The header version byte is not `0x03`.
    UnsupportedVersion,
}

impl fmt::Display for FakeTlsError {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::ShortHeader => f.write_str("TLS record header is shorter than five bytes"),
            Self::UnsupportedVersion => f.write_str("TLS record version is not 0x03xx"),
        }
    }
}

impl std::error::Error for FakeTlsError {}

/// The fake-TLS handshake helpers.
pub struct FakeTls;

impl FakeTls {
    pub const RECORD_HANDSHAKE: u8 = RECORD_HANDSHAKE;
    pub const RECORD_CCS: u8 = RECORD_CCS;
    pub const RECORD_ALERT: u8 = RECORD_ALERT;
    pub const RECORD_APP_DATA: u8 = RECORD_APP_DATA;
    pub const MAX_RECORD_PAYLOAD: usize = MAX_RECORD_PAYLOAD;
    pub const TIMESTAMP_TOLERANCE: i64 = TIMESTAMP_TOLERANCE;
    pub const APP_DATA_MIN: usize = APP_DATA_MIN;
    pub const APP_DATA_FALLBACK_LOW: usize = APP_DATA_FALLBACK_LOW;
    pub const APP_DATA_FALLBACK_HIGH: usize = APP_DATA_FALLBACK_HIGH;
    pub const ALERT_HANDSHAKE_FAILURE: u8 = ALERT_HANDSHAKE_FAILURE;
    pub const ALERT_UNEXPECTED_MESSAGE: u8 = ALERT_UNEXPECTED_MESSAGE;

    /// Checks the HMAC and the timestamp of a ClientHello.
    ///
    /// The hello is hashed with the random field zeroed, so the check covers
    /// the whole message. A hello outside [`TIMESTAMP_TOLERANCE`] of
    /// `now_seconds` is refused. With `None` the current time is used.
    #[must_use]
    pub fn try_verify_client_hello(
        data: &[u8],
        secret: &[u8],
        now_seconds: Option<i64>,
    ) -> Option<FakeTlsClientHello> {
        // 5 (record) + 6 (handshake type, length, version) + 32 (random).
        if data.len() < 43 {
            return None;
        }
        if data[0] != RECORD_HANDSHAKE || data[5] != 0x01 {
            return None;
        }

        let client_random =
            data[CLIENT_RANDOM_OFFSET..CLIENT_RANDOM_OFFSET + CLIENT_RANDOM_LEN].to_vec();

        let hashed_length = data.len().min(MAX_RECORD_PAYLOAD + 5);
        let mut zeroed = data[..hashed_length].to_vec();
        zeroed[CLIENT_RANDOM_OFFSET..CLIENT_RANDOM_OFFSET + CLIENT_RANDOM_LEN].fill(0);

        let expected = hmac_sha256(secret, &zeroed);
        if !fixed_time_equals(&expected[..28], &client_random[..28]) {
            return None;
        }

        let mut timestamp_bytes = [0u8; 4];
        for ((byte, random), mac) in timestamp_bytes
            .iter_mut()
            .zip(&client_random[28..32])
            .zip(&expected[28..32])
        {
            *byte = random ^ mac;
        }
        let timestamp = u32::from_le_bytes(timestamp_bytes);

        let now = now_seconds.unwrap_or_else(current_unix_seconds);
        if (now - i64::from(timestamp)).abs() > TIMESTAMP_TOLERANCE {
            return None;
        }

        let mut session_id = vec![0u8; SESSION_ID_LEN];
        if data.len() >= SESSION_ID_OFFSET + SESSION_ID_LEN && data[43] == 0x20 {
            session_id
                .copy_from_slice(&data[SESSION_ID_OFFSET..SESSION_ID_OFFSET + SESSION_ID_LEN]);
        }

        Some(FakeTlsClientHello {
            client_random,
            session_id,
            timestamp,
        })
    }

    /// Extracts the offered cipher suites and key_share groups.
    ///
    /// Malformed input yields empty info, exactly like the C# `catch`, so the
    /// caller falls back to the plain X25519 / TLS_AES_128_GCM_SHA256 answer.
    #[must_use]
    pub fn parse_client_hello(data: &[u8]) -> ClientHelloInfo {
        let mut position = 5 + 4 + 2 + CLIENT_RANDOM_LEN;
        if position >= data.len() {
            return ClientHelloInfo::empty();
        }
        position += 1 + usize::from(data[position]); // session id

        let Some(suites_length) = read_u16_be(data, position) else {
            return ClientHelloInfo::empty();
        };
        let suites_start = position + 2;
        position = suites_start + usize::from(suites_length);
        if position > data.len() {
            return ClientHelloInfo::empty();
        }

        let mut cipher_suites = std::collections::BTreeSet::new();
        let mut offset = suites_start;
        while offset + 1 < position {
            let Some(suite) = read_u16_be(data, offset) else {
                return ClientHelloInfo::empty();
            };
            cipher_suites.insert(i32::from(suite));
            offset += 2;
        }

        if position >= data.len() {
            return ClientHelloInfo::empty();
        }
        position += 1 + usize::from(data[position]); // compression methods

        let mut key_shares = std::collections::BTreeMap::new();
        let Some(extensions_length) = read_u16_be(data, position) else {
            return ClientHelloInfo {
                cipher_suites,
                key_shares,
            };
        };
        position += 2;
        let extensions_end = (position + usize::from(extensions_length)).min(data.len());

        while position + 4 <= extensions_end {
            let Some(extension_type) = read_u16_be(data, position) else {
                return ClientHelloInfo::empty();
            };
            let Some(extension_length) = read_u16_be(data, position + 2) else {
                return ClientHelloInfo::empty();
            };
            position += 4;
            if extension_type == EXT_KEY_SHARE && position + 2 <= extensions_end {
                let Some(shares_length) = read_u16_be(data, position) else {
                    return ClientHelloInfo::empty();
                };
                let shares_end = (position + 2 + usize::from(shares_length)).min(extensions_end);
                let mut offset = position + 2;
                while offset + 4 <= shares_end {
                    let Some(group) = read_u16_be(data, offset) else {
                        return ClientHelloInfo::empty();
                    };
                    let Some(key_length) = read_u16_be(data, offset + 2) else {
                        return ClientHelloInfo::empty();
                    };
                    offset += 4 + usize::from(key_length);
                    if offset <= shares_end {
                        key_shares
                            .entry(i32::from(group))
                            .or_insert(i32::from(key_length));
                    }
                }
            }
            position += usize::from(extension_length);
        }

        ClientHelloInfo {
            cipher_suites,
            key_shares,
        }
    }

    /// Builds the answer a client expects: ServerHello, ChangeCipherSpec and
    /// one ApplicationData record.
    ///
    /// The ServerHello random is replaced by the HMAC over the client random
    /// and the rest of the answer. Telegram parses exactly these three records
    /// and verifies that HMAC, so the shape is fixed while the record size may
    /// vary. `rng` fills the key share and the ApplicationData filler;
    /// `app_data_length` is clamped to [`APP_DATA_MIN`]..=[`MAX_RECORD_PAYLOAD`]
    /// and, when absent, drawn from `rng` in
    /// [`APP_DATA_FALLBACK_LOW`]..=[`APP_DATA_FALLBACK_HIGH`].
    #[must_use]
    pub fn build_response<R: FnMut(&mut [u8])>(
        secret: &[u8],
        client_random: &[u8],
        session_id: &[u8],
        info: &ClientHelloInfo,
        app_data_length: Option<usize>,
        mut rng: R,
    ) -> Vec<u8> {
        let (group, key_exchange) = server_key_share(info, &mut rng);
        let mut extensions = vec![0x00, 0x2B, 0x00, 0x02, 0x03, 0x04];
        extensions.extend_from_slice(&key_share_extension(group, &key_exchange));

        let cipher_suite = select_cipher_suite(info);

        // ServerHello body: version, 32-byte random (filled below), session id,
        // suite, compression, extensions.
        let body_length = 2 + 32 + 1 + session_id.len() + 2 + 1 + 2 + extensions.len();
        let message_length = 1 + 3 + body_length;
        let mut response = Vec::with_capacity(
            5 + message_length + CHANGE_CIPHER_SPEC.len() + 5 + MAX_RECORD_PAYLOAD,
        );

        response.extend_from_slice(&[
            RECORD_HANDSHAKE,
            0x03,
            0x03,
            (message_length >> 8) as u8,
            message_length as u8,
        ]);
        response.push(0x02); // ServerHello
        response.extend_from_slice(&[
            (body_length >> 16) as u8,
            (body_length >> 8) as u8,
            body_length as u8,
        ]);
        response.extend_from_slice(&[0x03, 0x03]); // negotiated TLS 1.2 legacy version
        response.extend_from_slice(&[0u8; 32]); // server random, replaced at the end
        response.push(session_id.len() as u8);
        response.extend_from_slice(session_id);
        response.extend_from_slice(&[(cipher_suite >> 8) as u8, cipher_suite as u8]);
        response.push(0x00); // no compression
        response.extend_from_slice(&[(extensions.len() >> 8) as u8, extensions.len() as u8]);
        response.extend_from_slice(&extensions);

        response.extend_from_slice(&CHANGE_CIPHER_SPEC);

        let app_data_size = app_data_length_value(app_data_length, &mut rng);
        response.extend_from_slice(&[
            RECORD_APP_DATA,
            0x03,
            0x03,
            (app_data_size >> 8) as u8,
            app_data_size as u8,
        ]);
        let mut filler = vec![0u8; app_data_size];
        rng(&mut filler);
        response.extend_from_slice(&filler);

        let mut hmac_input = Vec::with_capacity(client_random.len() + response.len());
        hmac_input.extend_from_slice(client_random);
        hmac_input.extend_from_slice(&response);
        let server_random = hmac_sha256(secret, &hmac_input);
        response[SERVER_HELLO_RANDOM_OFFSET..SERVER_HELLO_RANDOM_OFFSET + 32]
            .copy_from_slice(&server_random);

        response
    }

    /// A TLS alert record, so a refused connection still looks like a TLS
    /// endpoint that said no.
    #[must_use]
    pub fn alert(description: u8) -> Vec<u8> {
        vec![RECORD_ALERT, 0x03, 0x03, 0x00, 0x02, 0x02, description]
    }

    /// Reads a TLS record header: type, version and a two-byte payload length.
    ///
    /// # Errors
    ///
    /// Returns [`FakeTlsError::ShortHeader`] for fewer than five bytes and
    /// [`FakeTlsError::UnsupportedVersion`] when `header[1] != 0x03`. Like the
    /// C# original, the content type and `header[2]` are not checked.
    pub fn try_read_record_length(header: &[u8]) -> Result<u16, FakeTlsError> {
        if header.len() < 5 {
            return Err(FakeTlsError::ShortHeader);
        }
        if header[1] != 0x03 {
            return Err(FakeTlsError::UnsupportedVersion);
        }
        Ok(u16::from_be_bytes([header[3], header[4]]))
    }
}

fn read_u16_be(data: &[u8], offset: usize) -> Option<u16> {
    let bytes = data.get(offset..offset + 2)?;
    Some(u16::from_be_bytes([bytes[0], bytes[1]]))
}

fn select_cipher_suite(info: &ClientHelloInfo) -> i32 {
    for suite in CIPHER_PREFERENCE {
        if info.cipher_suites.contains(&suite) {
            return suite;
        }
    }
    CIPHER_PREFERENCE[0]
}

fn server_key_share<R: FnMut(&mut [u8])>(info: &ClientHelloInfo, rng: &mut R) -> (i32, Vec<u8>) {
    if info.key_shares.get(&GROUP_X25519_MLKEM768) == Some(&MLKEM_CLIENT_SHARE_LEN) {
        let mut exchange = vec![0u8; MLKEM_CIPHERTEXT_LEN];
        rng(&mut exchange);
        exchange.extend_from_slice(&x25519_public_key(rng));
        (GROUP_X25519_MLKEM768, exchange)
    } else {
        (GROUP_X25519, x25519_public_key(rng))
    }
}

fn x25519_public_key<R: FnMut(&mut [u8])>(rng: &mut R) -> Vec<u8> {
    let mut key = vec![0u8; X25519_KEY_LEN];
    rng(&mut key);
    if let Some(last) = key.last_mut() {
        *last &= 0x7F;
    }
    key
}

fn key_share_extension(group: i32, key_exchange: &[u8]) -> Vec<u8> {
    let data_length = 4 + key_exchange.len();
    let mut out = vec![
        (EXT_KEY_SHARE >> 8) as u8,
        EXT_KEY_SHARE as u8,
        (data_length >> 8) as u8,
        data_length as u8,
        (group >> 8) as u8,
        group as u8,
        (key_exchange.len() >> 8) as u8,
        key_exchange.len() as u8,
    ];
    out.extend_from_slice(key_exchange);
    out
}

fn app_data_length_value<R: FnMut(&mut [u8])>(requested: Option<usize>, rng: &mut R) -> usize {
    match requested {
        Some(size) => size.clamp(APP_DATA_MIN, MAX_RECORD_PAYLOAD),
        None => {
            let mut bytes = [0u8; 4];
            rng(&mut bytes);
            let range = APP_DATA_FALLBACK_HIGH - APP_DATA_FALLBACK_LOW + 1;
            APP_DATA_FALLBACK_LOW + (u32::from_le_bytes(bytes) as usize % range)
        }
    }
}

fn current_unix_seconds() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |duration| duration.as_secs() as i64)
}

fn fixed_time_equals(left: &[u8], right: &[u8]) -> bool {
    if left.len() != right.len() {
        return false;
    }
    let mut difference = 0u8;
    for (a, b) in left.iter().zip(right) {
        difference |= a ^ b;
    }
    difference == 0
}

fn hmac_sha256(key: &[u8], data: &[u8]) -> [u8; 32] {
    const BLOCK_SIZE: usize = 64;
    let mut key_block = [0u8; BLOCK_SIZE];
    if key.len() > BLOCK_SIZE {
        let digest = Sha256::digest(key);
        key_block[..32].copy_from_slice(&digest);
    } else {
        key_block[..key.len()].copy_from_slice(key);
    }

    let mut inner_pad = [0u8; BLOCK_SIZE];
    let mut outer_pad = [0u8; BLOCK_SIZE];
    for ((inner, outer), key) in inner_pad.iter_mut().zip(&mut outer_pad).zip(&key_block) {
        *inner = key ^ 0x36;
        *outer = key ^ 0x5C;
    }

    let mut inner = Sha256::new();
    inner.update(inner_pad);
    inner.update(data);
    let inner_digest = inner.finalize();

    let mut outer = Sha256::new();
    outer.update(outer_pad);
    outer.update(inner_digest);
    let result = outer.finalize();

    let mut output = [0u8; 32];
    output.copy_from_slice(&result);
    output
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tls::client_hello::{Random32, SplitMix64};

    const SECRET: &[u8] = &[
        0x00, 0xB4, 0x2B, 0xE1, 0xAE, 0x20, 0xF9, 0xE3, 0xB5, 0xD6, 0xE5, 0x43, 0xCC, 0x4B, 0xCE,
        0x98,
    ];
    const NOW: i64 = 1_700_000_000;

    fn hello(random: &[u8; 32], key_share: &[u8], hybrid: bool) -> Vec<u8> {
        let group: i32 = if hybrid {
            GROUP_X25519_MLKEM768
        } else {
            GROUP_X25519
        };
        let mut body = vec![0x03, 0x03];
        body.extend_from_slice(random);
        body.push(0x20);
        body.extend_from_slice(&[0x11u8; 32]);
        body.extend_from_slice(&[0x00, 0x06, 0x13, 0x01, 0x13, 0x02, 0x13, 0x03]);
        body.extend_from_slice(&[0x01, 0x00]);

        let mut share = Vec::new();
        share.extend_from_slice(&[(group >> 8) as u8, group as u8]);
        share.extend_from_slice(&[(key_share.len() >> 8) as u8, key_share.len() as u8]);
        share.extend_from_slice(key_share);
        let shares_length = share.len();
        let mut extension = vec![
            0x00,
            0x33,
            ((shares_length + 2) >> 8) as u8,
            (shares_length + 2) as u8,
            (shares_length >> 8) as u8,
            shares_length as u8,
        ];
        extension.extend_from_slice(&share);

        body.extend_from_slice(&[(extension.len() >> 8) as u8, extension.len() as u8]);
        body.extend_from_slice(&extension);

        let mut message = vec![0x01];
        message.extend_from_slice(&[
            (body.len() >> 16) as u8,
            (body.len() >> 8) as u8,
            body.len() as u8,
        ]);
        message.extend_from_slice(&body);

        let mut record = vec![
            RECORD_HANDSHAKE,
            0x03,
            0x01,
            (message.len() >> 8) as u8,
            message.len() as u8,
        ];
        record.extend_from_slice(&message);
        record
    }

    fn signed_hello(timestamp: u32, hybrid: bool) -> Vec<u8> {
        let key_share = vec![0x22u8; if hybrid { 1216 } else { 32 }];
        let zeroed = hello(&[0u8; 32], &key_share, hybrid);
        let expected = hmac_sha256(SECRET, &zeroed);
        let mut random = [0u8; 32];
        random[..28].copy_from_slice(&expected[..28]);
        for index in 0..4 {
            random[28 + index] = expected[28 + index] ^ ((timestamp >> (8 * index)) & 0xFF) as u8;
        }
        hello(&random, &key_share, hybrid)
    }

    #[test]
    fn verifies_a_hello_signed_with_the_secret() {
        let hello = signed_hello(NOW as u32, false);
        let parsed = FakeTls::try_verify_client_hello(&hello, SECRET, Some(NOW)).unwrap();
        assert_eq!(parsed.timestamp, NOW as u32);
        assert_eq!(parsed.session_id.len(), 32);
        assert_eq!(parsed.client_random, hello[11..43]);
    }

    #[test]
    fn refuses_another_secret_and_a_stale_timestamp() {
        let hello = signed_hello(NOW as u32, false);
        assert!(FakeTls::try_verify_client_hello(&hello, &[0u8; 16], Some(NOW)).is_none());
        assert!(
            FakeTls::try_verify_client_hello(&hello, SECRET, Some(NOW + TIMESTAMP_TOLERANCE))
                .is_some()
        );
        assert!(
            FakeTls::try_verify_client_hello(&hello, SECRET, Some(NOW + TIMESTAMP_TOLERANCE + 1))
                .is_none()
        );
    }

    #[test]
    fn parsed_hello_reports_suites_and_key_shares() {
        let info = FakeTls::parse_client_hello(&signed_hello(NOW as u32, true));
        assert!(info.cipher_suites.contains(&0x1301));
        assert_eq!(info.key_shares.get(&GROUP_X25519_MLKEM768), Some(&1216));
    }

    #[test]
    fn build_response_is_verifiable_the_way_the_client_verifies_it() {
        let hello = signed_hello(NOW as u32, false);
        let parsed = FakeTls::try_verify_client_hello(&hello, SECRET, Some(NOW)).unwrap();
        let info = FakeTls::parse_client_hello(&hello);

        let mut rng = SplitMix64::new(7);
        let answer = FakeTls::build_response(
            SECRET,
            &parsed.client_random,
            &parsed.session_id,
            &info,
            Some(200),
            |buffer| rng.fill_bytes(buffer),
        );

        let mut zeroed = answer.clone();
        zeroed[11..43].fill(0);
        let mut hmac_input = parsed.client_random.clone();
        hmac_input.extend_from_slice(&zeroed);
        assert_eq!(&answer[11..43], &hmac_sha256(SECRET, &hmac_input));

        assert_eq!(answer[0], RECORD_HANDSHAKE);
        let server_hello_length = 5 + usize::from(u16::from_be_bytes([answer[3], answer[4]]));
        assert_eq!(answer[server_hello_length], RECORD_CCS);
        let app_data_offset = server_hello_length + 6;
        assert_eq!(answer[app_data_offset], RECORD_APP_DATA);
        assert_eq!(
            u16::from_be_bytes([answer[app_data_offset + 3], answer[app_data_offset + 4]]),
            200
        );
    }

    #[test]
    fn alert_has_the_fixed_record_shape() {
        assert_eq!(
            FakeTls::alert(ALERT_HANDSHAKE_FAILURE),
            vec![0x15, 0x03, 0x03, 0x00, 0x02, 0x02, 0x28]
        );
    }

    #[test]
    fn record_length_rejects_short_and_wrong_version() {
        assert_eq!(
            FakeTls::try_read_record_length(&[0x16, 0x03, 0x03, 0x00]),
            Err(FakeTlsError::ShortHeader)
        );
        assert_eq!(
            FakeTls::try_read_record_length(&[0x16, 0x02, 0x03, 0x00, 0x02]),
            Err(FakeTlsError::UnsupportedVersion)
        );
        assert_eq!(
            FakeTls::try_read_record_length(&[0x16, 0x03, 0x03, 0x00, 0x02]),
            Ok(2)
        );
    }
}
