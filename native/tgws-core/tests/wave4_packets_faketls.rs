//! Wave 4 fixtures: `MsgSplitter`, `PacketCodec` and `FakeTls`.
//!
//! The three new sections of `obfuscated2-reference.json` were recorded by
//! `tools/PortShims/Wave4Shims.cs` driving the C# originals: chunk sequences
//! through `MsgSplitter.Split`/`Flush`, encrypted frames through
//! `PacketCodec.ReadPacketAsync`/`EncodeReply`, and ClientHello
//! verification/parsing/answers through `FakeTls`. The build section records a
//! real C# answer; its RNG-dependent bytes are checked structurally and by the
//! HMAC that is the actual contract.

use std::collections::{BTreeMap, BTreeSet};

use serde_json::Value;
use sha2::{Digest, Sha256};
use tgws_core::mtproto::{
    ClientHelloInfo, FakeTls, MsgSplitter, PacketCodec, RECORD_APP_DATA, RECORD_CCS,
    RECORD_HANDSHAKE,
};
use tgws_core::proto::AesCtr;
use tgws_core::{Random32, SplitMix64};

const FIXTURE: &str =
    include_str!("../../../tests/TgwsOne.Engine.Tests/Fixtures/obfuscated2-reference.json");

fn hex_bytes(value: &Value, field: &str) -> Vec<u8> {
    hex::decode(
        value[field]
            .as_str()
            .unwrap_or_else(|| panic!("{field} is a string")),
    )
    .unwrap_or_else(|error| panic!("{field} is hex: {error}"))
}

fn optional_hex(value: &Value, field: &str) -> Option<Vec<u8>> {
    value[field]
        .as_str()
        .map(|text| hex::decode(text).unwrap_or_else(|error| panic!("{field} is hex: {error}")))
}

fn hex_list(value: &Value) -> Vec<Vec<u8>> {
    value
        .as_array()
        .expect("list is an array")
        .iter()
        .map(|item| {
            hex::decode(item.as_str().expect("hex string"))
                .unwrap_or_else(|error| panic!("list item is hex: {error}"))
        })
        .collect()
}

fn int_list(value: &Value) -> Vec<i32> {
    value
        .as_array()
        .expect("list is an array")
        .iter()
        .map(|item| item.as_i64().expect("integer") as i32)
        .collect()
}

fn hmac_sha256(key: &[u8], data: &[u8]) -> [u8; 32] {
    const BLOCK_SIZE: usize = 64;
    let mut key_block = [0u8; BLOCK_SIZE];
    if key.len() > BLOCK_SIZE {
        key_block[..32].copy_from_slice(&Sha256::digest(key));
    } else {
        key_block[..key.len()].copy_from_slice(key);
    }

    let mut inner_pad = [0u8; BLOCK_SIZE];
    let mut outer_pad = [0u8; BLOCK_SIZE];
    for ((inner, outer), byte) in inner_pad.iter_mut().zip(&mut outer_pad).zip(&key_block) {
        *inner = byte ^ 0x36;
        *outer = byte ^ 0x5C;
    }

    let mut inner = Sha256::new();
    inner.update(inner_pad);
    inner.update(data);
    let inner_digest = inner.finalize();

    let mut outer = Sha256::new();
    outer.update(outer_pad);
    outer.update(inner_digest);
    let mut result = [0u8; 32];
    result.copy_from_slice(&outer.finalize());
    result
}

#[test]
fn msg_splitter_cases_match_the_csharp_recording() {
    let document: Value = serde_json::from_str(FIXTURE).expect("valid fixture");
    let cases = document["msgSplitter"]["cases"]
        .as_array()
        .expect("msgSplitter cases");

    let mut count = 0;
    for case in cases {
        let name = case["name"].as_str().expect("case name");
        let proto = case["proto"].as_u64().expect("proto") as u32;
        let cipher_chunks = hex_list(&case["cipherChunks"]);
        let plain_chunks = hex_list(&case["plainChunks"]);
        let expected_parts = hex_list(&case["parts"]);
        let expected_flush = hex_list(&case["flush"]);

        let mut splitter = MsgSplitter::new(proto);
        let mut parts = Vec::new();
        for (cipher, plain) in cipher_chunks.iter().zip(&plain_chunks) {
            parts.extend(
                splitter
                    .split(cipher, plain)
                    .unwrap_or_else(|error| panic!("{name}: {error}")),
            );
        }

        assert_eq!(parts, expected_parts, "{name}: parts");
        assert_eq!(splitter.flush(), expected_flush, "{name}: flush");
        count += 1;
    }

    assert_eq!(count, 11, "msgSplitter cases");
    println!("msgSplitter: {count} recorded sequences");
}

#[test]
fn packet_codec_decode_matches_the_csharp_recording() {
    let document: Value = serde_json::from_str(FIXTURE).expect("valid fixture");
    let section = &document["packetCodec"];
    let key = hex_bytes(section, "key");
    let iv = hex_bytes(section, "iv");
    let cases = section["decode"].as_array().expect("decode cases");

    let mut ok_cases = 0;
    let mut error_cases = 0;
    for case in cases {
        let name = case["name"].as_str().expect("case name");
        let tag = case["tag"].as_u64().expect("tag") as u32;
        let wire = hex_bytes(case, "wire");

        let mut decrypt = AesCtr::new(&key, &iv).expect("fixture key");
        let frame = decrypt.transform_vec(&wire);
        let result = PacketCodec::decode(&frame, tag);

        match optional_hex(case, "body") {
            Some(expected_body) => {
                let (body, quick_ack) = result.unwrap_or_else(|error| panic!("{name}: {error}"));
                assert_eq!(body, expected_body, "{name}: body");
                assert_eq!(
                    quick_ack,
                    case["quickAck"].as_bool().expect("quickAck"),
                    "{name}: quick ack"
                );
                ok_cases += 1;
            }
            None => {
                let error = result.expect_err(name);
                assert_eq!(
                    error.to_string(),
                    case["error"].as_str().expect("error text"),
                    "{name}: error"
                );
                error_cases += 1;
            }
        }
    }

    assert_eq!(ok_cases, 8, "valid decode cases");
    assert_eq!(error_cases, 5, "decode error cases");
    println!("packetCodec decode: {ok_cases} ok, {error_cases} errors");
}

#[test]
fn packet_codec_encode_matches_the_csharp_recording() {
    let document: Value = serde_json::from_str(FIXTURE).expect("valid fixture");
    let section = &document["packetCodec"];
    let cases = section["encode"]
        .as_array()
        .expect("encode cases")
        .iter()
        .chain(section["encodeErrors"].as_array().expect("encode errors"));

    let mut ok_cases = 0;
    let mut error_cases = 0;
    for case in cases {
        let name = case["name"].as_str().expect("case name");
        let tag = case["tag"].as_u64().expect("tag") as u32;
        let body = hex_bytes(case, "body");

        match optional_hex(case, "wire") {
            Some(expected_wire) => {
                let wire = PacketCodec::encode_reply(&body, tag)
                    .unwrap_or_else(|error| panic!("{name}: {error}"));
                assert_eq!(wire, expected_wire, "{name}: wire");
                ok_cases += 1;
            }
            None => {
                let error = PacketCodec::encode_reply(&body, tag).expect_err(name);
                assert_eq!(
                    error.to_string(),
                    case["error"].as_str().expect("error text"),
                    "{name}: error"
                );
                error_cases += 1;
            }
        }
    }

    assert_eq!(ok_cases, 4, "encode cases");
    assert_eq!(error_cases, 2, "encode error cases");
    println!("packetCodec encode: {ok_cases} ok, {error_cases} errors");
}

#[test]
fn fake_tls_verify_parse_and_alerts_match_the_csharp_recording() {
    let document: Value = serde_json::from_str(FIXTURE).expect("valid fixture");
    let section = &document["fakeTls"];
    let root_secret = hex_bytes(section, "secret");

    let mut verified = 0;
    let mut refused = 0;
    for case in section["verify"].as_array().expect("verify cases") {
        let name = case["name"].as_str().expect("case name");
        let hello = hex_bytes(case, "hello");
        let secret = hex_bytes(case, "secret");
        let now = case["now"].as_i64().expect("now");
        let expected_ok = case["ok"].as_bool().expect("ok");

        let parsed = FakeTls::try_verify_client_hello(&hello, &secret, Some(now));
        if expected_ok {
            let parsed = parsed.unwrap_or_else(|| panic!("{name}: hello must verify"));
            assert_eq!(
                hex::encode(&parsed.client_random),
                case["clientRandom"].as_str().expect("clientRandom"),
                "{name}: client random"
            );
            assert_eq!(
                hex::encode(&parsed.session_id),
                case["sessionId"].as_str().expect("sessionId"),
                "{name}: session id"
            );
            assert_eq!(
                i64::from(parsed.timestamp),
                case["timestamp"].as_i64().expect("timestamp"),
                "{name}: timestamp"
            );
            verified += 1;
        } else {
            assert!(parsed.is_none(), "{name}: hello must be refused");
            refused += 1;
        }
    }

    let mut parsed_cases = 0;
    for case in section["parse"].as_array().expect("parse cases") {
        let name = case["name"].as_str().expect("case name");
        let hello = hex_bytes(case, "hello");
        let info = FakeTls::parse_client_hello(&hello);

        let expected_suites: BTreeSet<i32> = int_list(&case["cipherSuites"]).into_iter().collect();
        assert_eq!(info.cipher_suites, expected_suites, "{name}: suites");

        let groups = int_list(&case["groups"]);
        let lengths = int_list(&case["keyLengths"]);
        let expected_shares: BTreeMap<i32, i32> = groups.into_iter().zip(lengths).collect();
        assert_eq!(info.key_shares, expected_shares, "{name}: key shares");
        parsed_cases += 1;
    }

    let mut alerts = 0;
    for case in section["alerts"].as_array().expect("alerts") {
        let description = case["description"].as_u64().expect("description") as u8;
        let expected = hex_bytes(case, "wire");
        assert_eq!(FakeTls::alert(description), expected);
        alerts += 1;
    }

    let mut record_lengths = 0;
    for case in section["recordLengths"].as_array().expect("record lengths") {
        let header = hex_bytes(case, "header");
        match case["length"].as_u64() {
            Some(expected) => assert_eq!(
                FakeTls::try_read_record_length(&header).expect("valid header") as u64,
                expected
            ),
            None => assert!(FakeTls::try_read_record_length(&header).is_err()),
        }
        record_lengths += 1;
    }

    assert_eq!(verified, 2, "verified hellos");
    assert_eq!(refused, 5, "refused hellos");
    assert_eq!(parsed_cases, 2, "parsed hellos");
    assert_eq!(alerts, 2, "alerts");
    assert_eq!(record_lengths, 3, "record lengths");
    assert!(!root_secret.is_empty());
    println!(
        "fakeTls: {verified} verified, {refused} refused, {parsed_cases} parsed, {alerts} alerts, {record_lengths} record lengths"
    );
}

#[test]
fn fake_tls_build_response_matches_the_answer_contract() {
    let document: Value = serde_json::from_str(FIXTURE).expect("valid fixture");
    let section = &document["fakeTls"];
    let secret = hex_bytes(section, "secret");

    let mut count = 0;
    for case in section["build"].as_array().expect("build cases") {
        let name = case["name"].as_str().expect("case name");
        let client_random = hex_bytes(case, "clientRandom");
        let session_id = hex_bytes(case, "sessionId");
        let suites = int_list(&case["cipherSuites"]);
        let groups = int_list(&case["groups"]);
        let lengths = int_list(&case["keyLengths"]);
        let requested = case["appDataLength"].as_u64().map(|value| value as usize);

        let recorded_answer = hex_bytes(case, "answer");
        check_answer(
            &recorded_answer,
            &secret,
            &client_random,
            &session_id,
            &suites,
            &groups,
            &lengths,
            requested,
            name,
        );

        let info = ClientHelloInfo {
            cipher_suites: suites.iter().copied().collect(),
            key_shares: groups
                .iter()
                .copied()
                .zip(lengths.iter().copied())
                .collect(),
        };
        let mut rng = SplitMix64::new(count as u64 + 1);
        let answer = FakeTls::build_response(
            &secret,
            &client_random,
            &session_id,
            &info,
            requested,
            |buffer| rng.fill_bytes(buffer),
        );
        check_answer(
            &answer,
            &secret,
            &client_random,
            &session_id,
            &suites,
            &groups,
            &lengths,
            requested,
            name,
        );
        count += 1;
    }

    assert_eq!(count, 5, "build cases");
    println!("fakeTls build: {count} answers checked structurally and by HMAC");
}

#[allow(clippy::too_many_arguments)]
fn check_answer(
    answer: &[u8],
    secret: &[u8],
    client_random: &[u8],
    session_id: &[u8],
    suites: &[i32],
    groups: &[i32],
    lengths: &[i32],
    requested: Option<usize>,
    name: &str,
) {
    assert_eq!(answer[0], RECORD_HANDSHAKE, "{name}: ServerHello record");
    let server_hello_length = 5 + usize::from(u16::from_be_bytes([answer[3], answer[4]]));
    assert_eq!(
        answer[server_hello_length], RECORD_CCS,
        "{name}: CCS record"
    );
    assert_eq!(
        &answer[server_hello_length..server_hello_length + 6],
        &[0x14, 0x03, 0x03, 0x00, 0x01, 0x01],
        "{name}: CCS body"
    );

    let app_data_offset = server_hello_length + 6;
    assert_eq!(answer[app_data_offset], RECORD_APP_DATA, "{name}: app data");
    let app_data_length = usize::from(u16::from_be_bytes([
        answer[app_data_offset + 3],
        answer[app_data_offset + 4],
    ]));
    assert_eq!(
        answer.len(),
        app_data_offset + 5 + app_data_length,
        "{name}: answer length"
    );
    if let Some(requested) = requested {
        assert_eq!(
            app_data_length,
            requested.clamp(64, 16_384),
            "{name}: clamped app data length"
        );
    } else {
        assert!(
            (64..=160).contains(&app_data_length),
            "{name}: fallback app data length {app_data_length}"
        );
    }

    let body_start = 9; // record(5) + handshake type(1) + length(3)
    let sid_length = usize::from(answer[body_start + 34]);
    assert_eq!(sid_length, session_id.len(), "{name}: session id length");
    assert_eq!(
        &answer[body_start + 35..body_start + 35 + sid_length],
        session_id,
        "{name}: echoed session id"
    );

    let suite_offset = body_start + 35 + sid_length;
    let suite = i32::from(u16::from_be_bytes([
        answer[suite_offset],
        answer[suite_offset + 1],
    ]));
    let expected_suite = [0x1301, 0x1303, 0x1302]
        .into_iter()
        .find(|candidate| suites.contains(candidate))
        .unwrap_or(0x1301);
    assert_eq!(suite, expected_suite, "{name}: cipher suite");

    let expected_group = groups
        .iter()
        .position(|&group| group == 0x11EC)
        .filter(|&index| lengths[index] == 1216)
        .map_or(0x001D, |_| 0x11EC);
    assert_eq!(
        server_key_share_group(answer, sid_length),
        expected_group,
        "{name}: key share group"
    );

    let mut zeroed = answer.to_vec();
    zeroed[11..43].fill(0);
    let mut hmac_input = client_random.to_vec();
    hmac_input.extend_from_slice(&zeroed);
    assert_eq!(
        &answer[11..43],
        &hmac_sha256(secret, &hmac_input),
        "{name}: server random HMAC"
    );
}

fn server_key_share_group(answer: &[u8], sid_length: usize) -> i32 {
    let body_start = 9;
    let extensions_length_offset = body_start + 2 + 32 + 1 + sid_length + 2 + 1;
    let extensions_length = usize::from(u16::from_be_bytes([
        answer[extensions_length_offset],
        answer[extensions_length_offset + 1],
    ]));
    let mut offset = extensions_length_offset + 2;
    let end = offset + extensions_length;
    while offset + 4 <= end {
        let extension_type = u16::from_be_bytes([answer[offset], answer[offset + 1]]);
        let extension_length =
            usize::from(u16::from_be_bytes([answer[offset + 2], answer[offset + 3]]));
        if extension_type == 0x0033 {
            return i32::from(u16::from_be_bytes([answer[offset + 4], answer[offset + 5]]));
        }
        offset += 4 + extension_length;
    }
    0
}
