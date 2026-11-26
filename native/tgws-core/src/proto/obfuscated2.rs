use crate::proto::{aes_ctr::AesCtr, constants::MtProtoConstants};
use sha2::{Digest, Sha256};

const RELAY_RESERVED_STARTS: &[&[u8]] = &[
    b"HEAD",
    b"POST",
    b"GET ",
    b"\xEE\xEE\xEE\xEE",
    b"\xDD\xDD\xDD\xDD",
    b"\x16\x03\x01\x02",
    b"OPTI",
];

pub struct Obfuscated2;

impl Obfuscated2 {
    pub fn try_parse_client_handshake(
        handshake: &[u8],
        secret: &[u8],
    ) -> Option<(i32, bool, u32, Vec<u8>)> {
        if handshake.len() < MtProtoConstants::HANDSHAKE_LENGTH {
            return None;
        }

        let prekey_iv = &handshake[MtProtoConstants::SKIP_LENGTH
            ..MtProtoConstants::SKIP_LENGTH
                + MtProtoConstants::PREKEY_LENGTH
                + MtProtoConstants::IV_LENGTH];
        let mut hasher = Sha256::new();
        hasher.update(&prekey_iv[..MtProtoConstants::PREKEY_LENGTH]);
        hasher.update(secret);
        let key = hasher.finalize();

        let mut decrypted = vec![0u8; MtProtoConstants::HANDSHAKE_LENGTH];
        let mut ctr = AesCtr::new(
            key.as_slice(),
            &prekey_iv[MtProtoConstants::PREKEY_LENGTH..],
        )
        .ok()?;
        ctr.transform(
            &handshake[..MtProtoConstants::HANDSHAKE_LENGTH],
            &mut decrypted,
        );

        let tag = &decrypted
            [MtProtoConstants::PROTO_TAG_POSITION..MtProtoConstants::PROTO_TAG_POSITION + 4];
        if !MtProtoConstants::is_known_tag(tag) {
            return None;
        }

        let dc_idx = i16::from_le_bytes([
            decrypted[MtProtoConstants::DC_INDEX_POSITION],
            decrypted[MtProtoConstants::DC_INDEX_POSITION + 1],
        ]);
        let dc = (dc_idx as i32).abs();
        let is_media = dc_idx < 0;
        let proto_tag = MtProtoConstants::proto_tag_to_int(tag);

        Some((dc, is_media, proto_tag, prekey_iv.to_vec()))
    }

    pub fn generate_relay_init<R: FnMut(&mut [u8])>(
        mut rng: R,
        proto_tag: u32,
        dc_idx: i32,
    ) -> Vec<u8> {
        loop {
            let mut random = [0u8; MtProtoConstants::HANDSHAKE_LENGTH];
            rng(&mut random);
            if random[0] == MtProtoConstants::RESERVED_FIRST_BYTE {
                continue;
            }
            if MtProtoConstants::is_known_tag(&random[..4]) {
                continue;
            }
            if RELAY_RESERVED_STARTS.contains(&&random[..4]) {
                continue;
            }
            if random[4] == 0 && random[5] == 0 && random[6] == 0 && random[7] == 0 {
                continue;
            }
            return Self::seal_relay_init_exact(&random, proto_tag, dc_idx);
        }
    }

    pub fn seal_relay_init(random: &[u8], proto_tag: u32, dc_idx: i32) -> Option<Vec<u8>> {
        if random.len() < MtProtoConstants::HANDSHAKE_LENGTH {
            return None;
        }
        Some(Self::seal_relay_init_exact(random, proto_tag, dc_idx))
    }

    fn seal_relay_init_exact(random: &[u8], proto_tag: u32, dc_idx: i32) -> Vec<u8> {
        let dc_idx = dc_idx as i16;
        let mut tail_plain = [0u8; 8];
        tail_plain[..4].copy_from_slice(&proto_tag.to_le_bytes());
        tail_plain[4..6].copy_from_slice(&dc_idx.to_le_bytes());
        tail_plain[6..8].copy_from_slice(
            &random[MtProtoConstants::PROTO_TAG_POSITION + 6
                ..MtProtoConstants::PROTO_TAG_POSITION + 8],
        );

        let key = &random[MtProtoConstants::SKIP_LENGTH
            ..MtProtoConstants::SKIP_LENGTH + MtProtoConstants::PREKEY_LENGTH];
        let iv = &random[MtProtoConstants::SKIP_LENGTH + MtProtoConstants::PREKEY_LENGTH
            ..MtProtoConstants::SKIP_LENGTH
                + MtProtoConstants::PREKEY_LENGTH
                + MtProtoConstants::IV_LENGTH];

        let mut ctr = AesCtr::new(key, iv).expect("relay init always names a valid AES key and IV");
        let mut encrypted_full = random.to_vec();
        ctr.transform_in_place(&mut encrypted_full);

        let mut relay_init = random.to_vec();
        for index in 0..8 {
            relay_init[MtProtoConstants::PROTO_TAG_POSITION + index] = tail_plain[index]
                ^ encrypted_full[MtProtoConstants::PROTO_TAG_POSITION + index]
                ^ random[MtProtoConstants::PROTO_TAG_POSITION + index];
        }

        relay_init
    }

    pub fn try_read_relay_init(relay_init: &[u8]) -> Option<(u32, i16)> {
        if relay_init.len() < MtProtoConstants::HANDSHAKE_LENGTH {
            return None;
        }
        let key = &relay_init[MtProtoConstants::SKIP_LENGTH
            ..MtProtoConstants::SKIP_LENGTH + MtProtoConstants::PREKEY_LENGTH];
        let iv = &relay_init[MtProtoConstants::SKIP_LENGTH + MtProtoConstants::PREKEY_LENGTH
            ..MtProtoConstants::SKIP_LENGTH
                + MtProtoConstants::PREKEY_LENGTH
                + MtProtoConstants::IV_LENGTH];

        let mut ctr = AesCtr::new(key, iv).ok()?;
        let mut decrypted = [0u8; MtProtoConstants::HANDSHAKE_LENGTH];
        ctr.transform(
            &relay_init[..MtProtoConstants::HANDSHAKE_LENGTH],
            &mut decrypted,
        );

        let tag = &decrypted
            [MtProtoConstants::PROTO_TAG_POSITION..MtProtoConstants::PROTO_TAG_POSITION + 4];
        if !MtProtoConstants::is_known_tag(tag) {
            return None;
        }

        let proto_tag = u32::from_le_bytes([tag[0], tag[1], tag[2], tag[3]]);
        let dc_idx = i16::from_le_bytes([
            decrypted[MtProtoConstants::PROTO_TAG_POSITION + 4],
            decrypted[MtProtoConstants::PROTO_TAG_POSITION + 5],
        ]);

        Some((proto_tag, dc_idx))
    }
}

pub struct CryptoContext {
    pub clt_dec: AesCtr,
    pub clt_enc: AesCtr,
    pub tg_enc: AesCtr,
    pub tg_dec: AesCtr,
}

impl CryptoContext {
    pub fn new(
        client_dec_prekey_iv: &[u8],
        secret: &[u8],
        relay_init: &[u8],
    ) -> Result<Self, &'static str> {
        if client_dec_prekey_iv.len()
            < MtProtoConstants::PREKEY_LENGTH + MtProtoConstants::IV_LENGTH
        {
            return Err("client_dec_prekey_iv too short");
        }
        if relay_init.len()
            < MtProtoConstants::SKIP_LENGTH
                + MtProtoConstants::KEY_LENGTH
                + MtProtoConstants::IV_LENGTH
        {
            return Err("relay_init too short");
        }

        let clt_dec_prekey = &client_dec_prekey_iv[..MtProtoConstants::PREKEY_LENGTH];
        let clt_dec_iv = &client_dec_prekey_iv[MtProtoConstants::PREKEY_LENGTH
            ..MtProtoConstants::PREKEY_LENGTH + MtProtoConstants::IV_LENGTH];

        let mut hasher = Sha256::new();
        hasher.update(clt_dec_prekey);
        hasher.update(secret);
        let clt_dec_key = hasher.finalize();

        let mut reversed = client_dec_prekey_iv
            [..MtProtoConstants::PREKEY_LENGTH + MtProtoConstants::IV_LENGTH]
            .to_vec();
        reversed.reverse();
        let clt_enc_prekey = &reversed[..MtProtoConstants::PREKEY_LENGTH];
        let clt_enc_iv = &reversed[MtProtoConstants::PREKEY_LENGTH
            ..MtProtoConstants::PREKEY_LENGTH + MtProtoConstants::IV_LENGTH];

        let mut hasher = Sha256::new();
        hasher.update(clt_enc_prekey);
        hasher.update(secret);
        let clt_enc_key = hasher.finalize();

        let mut clt_dec = AesCtr::new(clt_dec_key.as_slice(), clt_dec_iv)?;
        let clt_enc = AesCtr::new(clt_enc_key.as_slice(), clt_enc_iv)?;
        clt_dec.skip(MtProtoConstants::HANDSHAKE_LENGTH);

        let relay_enc_key = &relay_init[MtProtoConstants::SKIP_LENGTH
            ..MtProtoConstants::SKIP_LENGTH + MtProtoConstants::KEY_LENGTH];
        let relay_enc_iv = &relay_init[MtProtoConstants::SKIP_LENGTH
            + MtProtoConstants::PREKEY_LENGTH
            ..MtProtoConstants::SKIP_LENGTH
                + MtProtoConstants::PREKEY_LENGTH
                + MtProtoConstants::IV_LENGTH];

        let mut relay_reversed = relay_init[MtProtoConstants::SKIP_LENGTH
            ..MtProtoConstants::SKIP_LENGTH
                + MtProtoConstants::PREKEY_LENGTH
                + MtProtoConstants::IV_LENGTH]
            .to_vec();
        relay_reversed.reverse();
        let relay_dec_key = &relay_reversed[..MtProtoConstants::KEY_LENGTH];
        let relay_dec_iv = &relay_reversed[MtProtoConstants::KEY_LENGTH
            ..MtProtoConstants::KEY_LENGTH + MtProtoConstants::IV_LENGTH];

        let mut tg_enc = AesCtr::new(relay_enc_key, relay_enc_iv)?;
        let tg_dec = AesCtr::new(relay_dec_key, relay_dec_iv)?;
        tg_enc.skip(MtProtoConstants::HANDSHAKE_LENGTH);

        Ok(Self {
            clt_dec,
            clt_enc,
            tg_enc,
            tg_dec,
        })
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn try_parse_client_handshake_basic() {
        let handshake = hex::decode("a39e7caaeafd6e5ed385524f40802c3a3b65258fc2bab0d8174a27c06b9ee8590e5fe75ef5a61d4b359ac8ed98de31891e1cf45ff4b69e40ad44ec6f05f7ce8c").unwrap();
        let secret = hex::decode("ed62e95a7f448fdafe7dcbe5670e0922").unwrap();
        let result = Obfuscated2::try_parse_client_handshake(&handshake, &secret);
        assert!(result.is_some());
        let (dc, is_media, proto_tag, _) = result.unwrap();
        assert_eq!(dc, 1);
        assert!(!is_media);
        assert_eq!(proto_tag, MtProtoConstants::PROTO_ABRIDGED);
    }

    #[test]
    fn try_parse_client_handshake_short_input() {
        let handshake = vec![0u8; 32];
        let secret = vec![0u8; 16];
        assert!(Obfuscated2::try_parse_client_handshake(&handshake, &secret).is_none());
    }

    #[test]
    fn try_parse_client_handshake_wrong_key() {
        let handshake = hex::decode("a39e7caaeafd6e5ed385524f40802c3a3b65258fc2bab0d8174a27c06b9ee8590e5fe75ef5a61d4b359ac8ed98de31891e1cf45ff4b69e40ad44ec6f05f7ce8c").unwrap();
        let secret = hex::decode("00000000000000000000000000000000").unwrap();
        assert!(Obfuscated2::try_parse_client_handshake(&handshake, &secret).is_none());
    }

    #[test]
    fn try_parse_client_handshake_zeros() {
        let handshake = vec![0u8; 64];
        let secret = vec![0u8; 16];
        assert!(Obfuscated2::try_parse_client_handshake(&handshake, &secret).is_none());
    }

    #[test]
    fn generate_relay_init_roundtrip() {
        let proto_tag = MtProtoConstants::PROTO_ABRIDGED;
        let dc_idx = 2i32;
        let relay_init = Obfuscated2::generate_relay_init(|buf| buf.fill(0xAA), proto_tag, dc_idx);
        assert_eq!(relay_init.len(), 64);
        let (read_tag, read_dc) = Obfuscated2::try_read_relay_init(&relay_init).unwrap();
        assert_eq!(read_tag, proto_tag);
        assert_eq!(read_dc as i32, dc_idx);
    }

    #[test]
    fn try_read_relay_init_invalid() {
        let relay_init = vec![0u8; 64];
        assert!(Obfuscated2::try_read_relay_init(&relay_init).is_none());
    }

    #[test]
    fn seal_relay_init_rejects_short_input() {
        assert!(
            Obfuscated2::seal_relay_init(&[0u8; 63], MtProtoConstants::PROTO_ABRIDGED, 1).is_none()
        );
    }

    #[test]
    fn crypto_context_creation() {
        let client_dec_prekey_iv = hex::decode("aaeafd6e5ed385524f40802c3a3b65258fc2bab0d8174a27c06b9ee8590e5fe75ef5a61d4b359ac8ed98de31891e1cf45ff4b69e40ad44ec6f05f7ce8c").unwrap();
        let secret = hex::decode("ed62e95a7f448fdafe7dcbe5670e0922").unwrap();
        let relay_init = hex::decode("493546c9ff7631581dadbc82a86f0de240a523c9aa687f716f061fde62849155af063175072fee988bd4387209b1e66b9d578054e3c08ad037f6b3501306024a").unwrap();

        assert!(CryptoContext::new(&client_dec_prekey_iv, &secret, &relay_init).is_ok());
    }
}
