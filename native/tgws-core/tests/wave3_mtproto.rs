use serde_json::Value;
use tgws_core::proto::{AesCtr, CryptoContext, MtProtoConstants, Obfuscated2};
use tgws_core::{Random32, SplitMix64};

const FIXTURE: &str =
    include_str!("../../../tests/TgwsOne.Engine.Tests/Fixtures/obfuscated2-reference.json");

#[derive(Debug)]
struct Case {
    dc_idx: i32,
    secret: Vec<u8>,
    handshake: Vec<u8>,
    relay_random: Vec<u8>,
    relay_init: Vec<u8>,
    expect_dc: i32,
    expect_is_media: bool,
    expect_proto_tag: u32,
    up: Vec<u8>,
    up_reads: Vec<usize>,
    up_wire: Vec<u8>,
    down: Vec<u8>,
    down_reads: Vec<usize>,
    down_wire: Vec<u8>,
}

fn hex_bytes(value: &Value, field: &str) -> Vec<u8> {
    hex::decode(
        value[field]
            .as_str()
            .unwrap_or_else(|| panic!("{field} is a string")),
    )
    .unwrap_or_else(|error| panic!("{field} is hex: {error}"))
}

fn read_sizes(value: &Value) -> Vec<usize> {
    value
        .as_array()
        .expect("reads are an array")
        .iter()
        .map(|item| item.as_u64().expect("read size is an integer") as usize)
        .collect()
}

fn parse_case(value: &Value) -> Case {
    Case {
        dc_idx: value["dcIdx"].as_i64().expect("dcIdx") as i32,
        secret: hex_bytes(value, "secret"),
        handshake: hex_bytes(value, "handshake"),
        relay_random: hex_bytes(value, "relayRandom"),
        relay_init: hex_bytes(value, "relayInit"),
        expect_dc: value["expect"]["dc"].as_i64().expect("expect.dc") as i32,
        expect_is_media: value["expect"]["isMedia"]
            .as_bool()
            .expect("expect.isMedia"),
        expect_proto_tag: value["expect"]["protoTag"]
            .as_u64()
            .expect("expect.protoTag") as u32,
        up: hex_bytes(value, "up"),
        up_reads: read_sizes(&value["upReads"]),
        up_wire: hex_bytes(value, "upWire"),
        down: hex_bytes(value, "down"),
        down_reads: read_sizes(&value["downReads"]),
        down_wire: hex_bytes(value, "downWire"),
    }
}

#[test]
fn mtproto_constants_values() {
    assert_eq!(MtProtoConstants::HANDSHAKE_LENGTH, 64);
    assert_eq!(MtProtoConstants::SKIP_LENGTH, 8);
    assert_eq!(MtProtoConstants::PREKEY_LENGTH, 32);
    assert_eq!(MtProtoConstants::KEY_LENGTH, 32);
    assert_eq!(MtProtoConstants::IV_LENGTH, 16);
    assert_eq!(MtProtoConstants::PROTO_TAG_POSITION, 56);
    assert_eq!(MtProtoConstants::DC_INDEX_POSITION, 60);
    assert_eq!(MtProtoConstants::PROTO_ABRIDGED, 0xEFEF_EFEF);
    assert_eq!(MtProtoConstants::PROTO_INTERMEDIATE, 0xEEEE_EEEE);
    assert_eq!(MtProtoConstants::PROTO_SECURE, 0xDDDD_DDDD);
    assert_eq!(MtProtoConstants::RESERVED_FIRST_BYTE, 0xEF);
}

#[test]
fn mtproto_constants_tag_conversion() {
    assert_eq!(
        MtProtoConstants::proto_tag_to_int(&[0xEF, 0xEF, 0xEF, 0xEF]),
        0xEFEF_EFEF
    );
    assert_eq!(
        MtProtoConstants::proto_tag_to_int(&[0xEE, 0xEE, 0xEE, 0xEE]),
        0xEEEE_EEEE
    );
    assert_eq!(
        MtProtoConstants::proto_tag_to_int(&[0xDD, 0xDD, 0xDD, 0xDD]),
        0xDDDD_DDDD
    );
    assert_eq!(
        MtProtoConstants::proto_tag_to_int(&[0x00, 0x00, 0x00, 0x00]),
        0xDDDD_DDDD
    );
}

#[test]
fn mtproto_constants_known_tags() {
    assert!(MtProtoConstants::is_known_tag(&[0xEF, 0xEF, 0xEF, 0xEF]));
    assert!(MtProtoConstants::is_known_tag(&[0xEE, 0xEE, 0xEE, 0xEE]));
    assert!(MtProtoConstants::is_known_tag(&[0xDD, 0xDD, 0xDD, 0xDD]));
    assert!(!MtProtoConstants::is_known_tag(&[0x00, 0x00, 0x00, 0x00]));
    assert!(!MtProtoConstants::is_known_tag(&[0xEF, 0xEF, 0xEF, 0xEE]));
}

#[test]
fn fixture_cases_match_handshake_seal_and_streams() {
    let doc: Value = serde_json::from_str(FIXTURE).expect("valid fixture");
    let cases = doc["cases"].as_array().expect("cases array");
    let rejects = doc["rejects"].as_array().expect("rejects array");

    let mut parsed = 0;
    let mut sealed = 0;
    let mut streams = 0;
    for case_value in cases {
        let case = parse_case(case_value);

        let (dc, is_media, proto_tag, client_dec_prekey_iv) =
            Obfuscated2::try_parse_client_handshake(&case.handshake, &case.secret)
                .unwrap_or_else(|| panic!("dcIdx={}: handshake should parse", case.dc_idx));
        assert_eq!(dc, case.expect_dc, "dcIdx={}: dc", case.dc_idx);
        assert_eq!(
            is_media, case.expect_is_media,
            "dcIdx={}: isMedia",
            case.dc_idx
        );
        assert_eq!(
            proto_tag, case.expect_proto_tag,
            "dcIdx={}: protoTag",
            case.dc_idx
        );
        assert_eq!(
            client_dec_prekey_iv,
            case.handshake[8..56],
            "dcIdx={}: client prekey+iv",
            case.dc_idx
        );
        parsed += 1;

        let sealed_init =
            Obfuscated2::seal_relay_init(&case.relay_random, case.expect_proto_tag, case.dc_idx)
                .unwrap_or_else(|| panic!("dcIdx={}: relay random is long enough", case.dc_idx));
        assert_eq!(
            sealed_init, case.relay_init,
            "dcIdx={}: sealed relay init",
            case.dc_idx
        );
        sealed += 1;

        let (read_tag, read_dc) = Obfuscated2::try_read_relay_init(&case.relay_init)
            .unwrap_or_else(|| panic!("dcIdx={}: relay init should parse", case.dc_idx));
        assert_eq!(
            read_tag, case.expect_proto_tag,
            "dcIdx={}: relay protoTag",
            case.dc_idx
        );
        assert_eq!(
            read_dc as i32, case.dc_idx,
            "dcIdx={}: relay dc",
            case.dc_idx
        );

        let CryptoContext {
            mut clt_dec,
            mut clt_enc,
            mut tg_enc,
            mut tg_dec,
        } = CryptoContext::new(&client_dec_prekey_iv, &case.secret, &case.relay_init)
            .unwrap_or_else(|error| panic!("dcIdx={}: CryptoContext: {error}", case.dc_idx));

        if !case.up.is_empty() {
            let mut up_wire = vec![0u8; case.up.len()];
            let mut offset = 0;
            for read_len in &case.up_reads {
                let end = offset + read_len;
                let mut plain = vec![0u8; *read_len];
                clt_dec.transform(&case.up[offset..end], &mut plain);
                tg_enc.transform(&plain, &mut up_wire[offset..end]);
                offset = end;
            }
            assert_eq!(offset, case.up.len(), "dcIdx={}: up reads", case.dc_idx);
            assert_eq!(up_wire, case.up_wire, "dcIdx={}: upWire", case.dc_idx);
            streams += 1;
        }

        if !case.down.is_empty() {
            let mut down_wire = vec![0u8; case.down.len()];
            let mut offset = 0;
            for read_len in &case.down_reads {
                let end = offset + read_len;
                let mut plain = vec![0u8; *read_len];
                tg_dec.transform(&case.down[offset..end], &mut plain);
                clt_enc.transform(&plain, &mut down_wire[offset..end]);
                offset = end;
            }
            assert_eq!(offset, case.down.len(), "dcIdx={}: down reads", case.dc_idx);
            assert_eq!(down_wire, case.down_wire, "dcIdx={}: downWire", case.dc_idx);
            streams += 1;
        }
    }

    for reject in rejects {
        let secret = hex_bytes(reject, "secret");
        let handshake = hex_bytes(reject, "handshake");
        assert!(
            Obfuscated2::try_parse_client_handshake(&handshake, &secret).is_none(),
            "reject case should fail"
        );
    }

    assert_eq!(parsed, cases.len());
    assert_eq!(sealed, cases.len());
    assert_eq!(rejects.len(), 2);
    println!(
        "fixture: {parsed} handshakes, {sealed} relay seals, {streams} stream directions, {} rejects",
        rejects.len()
    );
}

#[test]
fn fixture_aes_ctr_vectors_match() {
    let doc: Value = serde_json::from_str(FIXTURE).expect("valid fixture");
    let vectors = doc["aesCtr"].as_array().expect("aesCtr array");

    let mut count = 0;
    for vector in vectors {
        let name = vector["name"].as_str().expect("vector name");
        let key = hex_bytes(vector, "key");
        let iv = hex_bytes(vector, "iv");
        let skip = vector["skip"].as_u64().expect("skip") as usize;
        let plain = hex_bytes(vector, "plain");
        let expected = hex_bytes(vector, "cipher");
        let reads = read_sizes(&vector["reads"]);
        assert_eq!(reads.iter().sum::<usize>(), plain.len(), "{name}: reads");

        let mut chunked = AesCtr::new(&key, &iv).unwrap();
        chunked.skip(skip);
        let mut actual = vec![0u8; plain.len()];
        let mut offset = 0;
        for read_len in &reads {
            let end = offset + read_len;
            chunked.transform(&plain[offset..end], &mut actual[offset..end]);
            offset = end;
        }
        assert_eq!(offset, plain.len(), "{name}: chunk offset");
        assert_eq!(actual, expected, "{name}: chunked cipher");

        let mut in_place = plain.clone();
        let mut whole = AesCtr::new(&key, &iv).unwrap();
        whole.skip(skip);
        whole.transform_in_place(&mut in_place);
        assert_eq!(in_place, expected, "{name}: in-place cipher");

        let mut zeros = AesCtr::new(&key, &iv).unwrap();
        let skipped_zeros = vec![0u8; skip];
        let mut discarded = vec![0u8; skip];
        zeros.transform(&skipped_zeros, &mut discarded);
        assert_eq!(
            zeros.transform_vec(&plain),
            expected,
            "{name}: skip matches zeros"
        );

        let mut decrypt = AesCtr::new(&key, &iv).unwrap();
        decrypt.skip(skip);
        assert_eq!(decrypt.transform_vec(&expected), plain, "{name}: decrypt");

        count += 1;
    }

    assert_eq!(count, 12, "AES-CTR vectors");
    println!("aesCtr: {count} vectors");
}

#[test]
fn generate_relay_init_injects_rng_and_avoids_reserved_starts() {
    let cases = [
        (MtProtoConstants::PROTO_ABRIDGED, 1i32),
        (MtProtoConstants::PROTO_INTERMEDIATE, -2),
        (MtProtoConstants::PROTO_SECURE, 10_002),
        (MtProtoConstants::PROTO_ABRIDGED, -32_768),
    ];

    for (index, (proto_tag, dc_idx)) in cases.into_iter().enumerate() {
        let mut rng = SplitMix64::new(index as u64 + 1);
        let relay_init =
            Obfuscated2::generate_relay_init(|buffer| rng.fill_bytes(buffer), proto_tag, dc_idx);

        assert_eq!(relay_init.len(), 64);
        assert_ne!(relay_init[0], 0xEF);
        assert!(!MtProtoConstants::is_known_tag(&relay_init[..4]));
        assert!(!is_reserved_start(&relay_init[..4]));
        assert!(
            !(relay_init[4] == 0 && relay_init[5] == 0 && relay_init[6] == 0 && relay_init[7] == 0)
        );

        let (read_tag, read_dc) = Obfuscated2::try_read_relay_init(&relay_init)
            .expect("generated relay init should parse");
        assert_eq!(read_tag, proto_tag);
        assert_eq!(read_dc as i32, dc_idx as i16 as i32);
    }
}

#[test]
fn rejects_short_and_corrupt_inputs() {
    let secret = [0u8; 16];
    assert!(Obfuscated2::try_parse_client_handshake(&[0u8; 63], &secret).is_none());
    assert!(Obfuscated2::try_parse_client_handshake(&[0u8; 64], &secret).is_none());
    assert!(Obfuscated2::try_read_relay_init(&[0u8; 63]).is_none());
    assert!(Obfuscated2::try_read_relay_init(&[0u8; 64]).is_none());
    assert!(
        Obfuscated2::seal_relay_init(&[0u8; 63], MtProtoConstants::PROTO_ABRIDGED, 1).is_none()
    );
    assert!(CryptoContext::new(&[0u8; 47], &secret, &[0u8; 64]).is_err());
    assert!(CryptoContext::new(&[0u8; 48], &secret, &[0u8; 55]).is_err());
}

fn is_reserved_start(prefix: &[u8]) -> bool {
    const RESERVED: &[&[u8]] = &[
        b"HEAD",
        b"POST",
        b"GET ",
        b"OPTI",
        &[0x16, 0x03, 0x01, 0x02],
        &[0xEE, 0xEE, 0xEE, 0xEE],
        &[0xDD, 0xDD, 0xDD, 0xDD],
    ];
    RESERVED.contains(&prefix)
}
