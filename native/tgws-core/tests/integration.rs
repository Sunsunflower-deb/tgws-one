//! End-to-end checks that exercise the public API across modules.

use std::net::TcpListener;

use tgws_core::{
    CONTENT_TYPE_HANDSHAKE, Chrome120Profile, ClientHelloBuilder, DesyncPosition, ParseError,
    RawTcpStream, RecordError, SplitMix64, encode_record, parse_client_hello, split_record,
};

fn build_flight(sni: &str, seed: u64) -> Vec<u8> {
    let mut rng = SplitMix64::new(seed);
    ClientHelloBuilder::new(sni)
        .with_profile(Chrome120Profile)
        .build(&mut rng)
}

fn payload_len(record: &[u8]) -> usize {
    u16::from_be_bytes([record[3], record[4]]) as usize
}

fn reassemble(frames: &[u8]) -> Vec<u8> {
    let mut out = Vec::new();
    let mut offset = 0;
    while offset < frames.len() {
        assert!(frames.len() - offset >= 5, "truncated record header");
        assert_eq!(frames[offset], CONTENT_TYPE_HANDSHAKE);
        let len = payload_len(&frames[offset..]);
        let end = offset + 5 + len;
        assert!(end <= frames.len(), "truncated record payload");
        out.extend_from_slice(&frames[offset + 5..end]);
        offset = end;
    }
    out
}

#[test]
fn build_parse_roundtrip_resolves_sni_positions() {
    let flight = build_flight("www.example.com", 1);
    let layout = parse_client_hello(&flight).expect("built ClientHello must parse");

    assert_eq!(layout.sni(&flight), Some(b"www.example.com".as_slice()));
    assert_eq!(layout.record_end, flight.len());

    let sni_start = layout.sni_start.expect("SNI start");
    let sni_end = layout.sni_end.expect("SNI end");
    assert_eq!(
        layout.resolve(&DesyncPosition::SniStart(0), &flight),
        Some(sni_start)
    );
    assert_eq!(
        layout.resolve(&DesyncPosition::SniEnd(0), &flight),
        Some(sni_end)
    );
    assert_eq!(
        layout.resolve(&DesyncPosition::SniMid, &flight),
        Some(sni_start + (sni_end - sni_start) / 2)
    );
    assert_eq!(
        layout.resolve(&DesyncPosition::SldMid, &flight),
        Some(sni_start + 4 + 3)
    );
    assert_eq!(
        layout.resolve(&DesyncPosition::FromEnd(-1), &flight),
        Some(flight.len() - 1)
    );
}

#[test]
fn build_padded_has_exact_length_and_parses() {
    for target in [517usize, 1024, 4096] {
        let mut rng = SplitMix64::new(target as u64);
        let flight = ClientHelloBuilder::new("padded.example")
            .with_profile(Chrome120Profile)
            .build_padded(&mut rng, target);

        assert_eq!(flight.len(), target, "target {target}");
        let layout = parse_client_hello(&flight).expect("padded ClientHello must parse");
        assert_eq!(layout.sni(&flight), Some(b"padded.example".as_slice()));
    }
}

#[test]
fn split_record_preserves_plaintext_and_first_record_fails_e2_1() {
    let flight = build_flight("split.example.com", 2);
    let layout = parse_client_hello(&flight).expect("source ClientHello must parse");
    let sni_end = layout.sni_end.expect("source has SNI");

    let boundary = sni_end - 5 + 1;
    let frames = split_record(&flight, &[boundary]).expect("split must succeed");

    assert_eq!(reassemble(&frames), flight[5..]);

    let first_len = 5 + boundary;
    let first = &frames[..first_len];
    assert_eq!(
        parse_client_hello(first),
        Err(ParseError::HandshakeNotInFirstRecord)
    );
}

#[test]
fn split_record_rejects_non_handshake_input() {
    let record = encode_record(0x17, b"application data");
    assert_eq!(
        split_record(&record, &[1]),
        Err(RecordError::NotHandshakeRecord)
    );
}

#[test]
fn raw_tcp_ttl_and_oob_on_loopback() {
    let listener = TcpListener::bind(("127.0.0.1", 0)).expect("bind loopback");
    let port = listener.local_addr().expect("local addr").port();
    let stream = RawTcpStream::connect("127.0.0.1", port).expect("connect loopback");
    let _peer = listener.accept().expect("accept").0;

    let original = stream.ttl().expect("read default TTL");
    stream.set_ttl(7).expect("set TTL");
    assert_eq!(stream.ttl().expect("read TTL"), 7);
    stream.set_ttl(original).expect("restore TTL");
    assert_eq!(stream.ttl().expect("read restored TTL"), original);

    assert_eq!(stream.send_oob(b"x").expect("send OOB"), 1);
}
