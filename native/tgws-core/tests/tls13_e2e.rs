//! Optional end-to-end check against a local `openssl s_server`.
//!
//! The test is ignored by default because it needs a running server.  Use
//! `tests/tls13-e2e.sh`, which generates a throw-away CA and a server
//! certificate signed by it, starts OpenSSL on a loopback port, and then runs:
//!
//! ```text
//! cargo test --test tls13_e2e -- --ignored --nocapture
//! ```
//!
//! Environment variables:
//!
//! * `TLS13_E2E_ADDR` — `host:port` of the server (default `127.0.0.1:9443`).
//! * `TLS13_E2E_SNI` — SNI to send (default `localhost`).
//! * `TLS13_E2E_CA` — DER file of the CA that signed the server certificate.
//!   The test verifies the real chain, name and `CertificateVerify` signature
//!   against it, so a self-signed server certificate is not trusted.

use std::env;
use std::fs;
use std::io::{ErrorKind, Read, Write};
use std::net::{TcpStream, ToSocketAddrs};
use std::thread;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

use tgws_core::SplitMix64;
use tgws_core::tls13::{
    ServerCertPolicy, SystemTimeProvider, Tls13Client, Tls13ClientConfig, WebPkiVerifier,
};

fn connect_with_retry(address: &str) -> TcpStream {
    let deadline = SystemTime::now() + Duration::from_secs(10);
    loop {
        match TcpStream::connect(address) {
            Ok(stream) => return stream,
            Err(error) => {
                if SystemTime::now() >= deadline {
                    panic!("could not connect to {address}: {error}");
                }
                thread::sleep(Duration::from_millis(100));
            }
        }
    }
}

fn seed() -> u64 {
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map_or(0, |duration| duration.as_nanos() as u64);
    nanos ^ u64::from(std::process::id())
}

#[test]
#[ignore = "needs a running openssl s_server; use tests/tls13-e2e.sh"]
fn openssl_s_server_tls13_get() {
    let address = env::var("TLS13_E2E_ADDR").unwrap_or_else(|_| "127.0.0.1:9443".into());
    let server_name = env::var("TLS13_E2E_SNI").unwrap_or_else(|_| "localhost".into());

    // Resolve once so a bad address fails with a clear message.
    let _ = address
        .to_socket_addrs()
        .unwrap_or_else(|error| panic!("invalid TLS13_E2E_ADDR {address}: {error}"))
        .next()
        .expect("address resolves to at least one socket");

    let ca_path = env::var("TLS13_E2E_CA").expect(
        "TLS13_E2E_CA must point to the DER-encoded CA certificate; use tests/tls13-e2e.sh",
    );
    let ca_der = fs::read(&ca_path)
        .unwrap_or_else(|error| panic!("could not read TLS13_E2E_CA {ca_path}: {error}"));
    let verifier = WebPkiVerifier::from_der_roots(&[ca_der], Box::new(SystemTimeProvider))
        .expect("the e2e CA is a valid trust anchor");

    let config = Tls13ClientConfig::new(server_name.clone(), Box::new(SplitMix64::new(seed())))
        .with_alpn(&[b"http/1.1"])
        .with_cert_policy(ServerCertPolicy::WebPki)
        .with_verifier(Box::new(verifier));

    let mut client = Tls13Client::new(config);
    let mut stream = connect_with_retry(&address);
    stream
        .set_read_timeout(Some(Duration::from_secs(10)))
        .expect("read timeout");
    stream
        .set_write_timeout(Some(Duration::from_secs(10)))
        .expect("write timeout");

    stream
        .write_all(&client.start().expect("build ClientHello"))
        .expect("send ClientHello");

    let mut buffer = [0u8; 16_384];
    let mut handshake_reads = 0usize;
    while !client.is_connected() {
        let read = stream.read(&mut buffer).expect("read handshake");
        assert_ne!(read, 0, "server closed the connection during the handshake");
        let outgoing = client.advance(&buffer[..read]).expect("advance handshake");
        if !outgoing.is_empty() {
            stream
                .write_all(&outgoing)
                .expect("send handshake response");
        }
        handshake_reads += 1;
        assert!(
            handshake_reads < 100,
            "handshake did not finish after {handshake_reads} reads"
        );
    }

    let alpn = client
        .negotiated_alpn()
        .map(|value| String::from_utf8_lossy(value).into_owned());
    assert_eq!(alpn.as_deref(), Some("http/1.1"));
    assert_eq!(
        client.cipher_suite().map(|suite| suite.code()),
        Some(0x1301),
        "the script forces TLS_AES_128_GCM_SHA256"
    );

    let request = b"GET / HTTP/1.0\r\nHost: localhost\r\nConnection: close\r\n\r\n".to_vec();
    let record = client
        .send_application_data(&request)
        .expect("protect HTTP request");
    stream.write_all(&record).expect("send HTTP request");

    let mut response = Vec::new();
    loop {
        match stream.read(&mut buffer) {
            Ok(0) => break,
            Ok(read) => {
                let outgoing = client
                    .advance(&buffer[..read])
                    .expect("advance application data");
                if !outgoing.is_empty() {
                    stream.write_all(&outgoing).expect("send application data");
                }
                response.extend_from_slice(&client.take_application_data());
                if response
                    .windows(15)
                    .any(|window| window == b"HTTP/1.0 200 ok")
                {
                    break;
                }
            }
            Err(error) if error.kind() == ErrorKind::WouldBlock => break,
            Err(error) if error.kind() == ErrorKind::TimedOut => break,
            Err(error) => panic!("read response: {error}"),
        }
    }

    let text = String::from_utf8_lossy(&response);
    assert!(
        text.contains("HTTP/1.0 200 ok") || text.contains("HTTP/1.0 200 OK"),
        "unexpected response from s_server: {text}"
    );
    println!("negotiated ALPN: {alpn:?}");
    println!(
        "first 240 bytes of the response:\n{}",
        &text[..text.len().min(240)]
    );
}
