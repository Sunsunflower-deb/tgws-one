//! The `tg://proxy` link a client adds.
//!
//! Ported from `src/TgwsOne.Shared/TelegramLink.cs` and
//! `src/TgwsOne.Engine/EngineConfig.cs` (`TelegramLink`).  A wildcard listen
//! address becomes loopback for the `dd` link; with a masking domain the link
//! carries an `ee` secret followed by that domain in lower-case hex.  Resolving
//! `0.0.0.0` to the machine's LAN address needs a UDP socket and stays in the
//! shell, which passes the resolved host to [`build_ee`].

use std::fmt::Write as _;
use std::net::{Ipv4Addr, Ipv6Addr};

use super::proxy_settings::IpAddress;

/// The address a client connects to for a given listen address: wildcards
/// become loopback.
#[must_use]
pub fn connect_address(listen: &IpAddress) -> IpAddress {
    match listen {
        IpAddress::V4(addr) if addr.is_unspecified() => IpAddress::V4(Ipv4Addr::LOCALHOST),
        IpAddress::V6 { addr, scope } if addr.is_unspecified() && *scope == 0 => IpAddress::V6 {
            addr: Ipv6Addr::LOCALHOST,
            scope: 0,
        },
        other => other.clone(),
    }
}

/// `tg://proxy?server=…&port=…&secret=dd…`.  `server` is a query parameter, so
/// an IPv6 address goes in URL-encoded and without brackets.
#[must_use]
pub fn build_dd(host: &IpAddress, port: i32, secret: &str) -> String {
    format!(
        "tg://proxy?server={}&port={port}&secret=dd{secret}",
        escape_data_string(&connect_address(host).to_string())
    )
}

/// The masked `ee` link: `secret` is followed by the fake-TLS domain in hex.
/// `link_host` is already resolved (a wildcard listen address is not handled
/// here).  An empty domain keeps the bare `dd` secret.
#[must_use]
pub fn build_ee(link_host: &str, port: i32, secret: &str, fake_tls_domain: &str) -> String {
    let secret = if fake_tls_domain.is_empty() {
        format!("dd{secret}")
    } else {
        // `Encoding.ASCII.GetBytes`: every non-ASCII code unit becomes '?'.
        let ascii: Vec<u8> = fake_tls_domain
            .chars()
            .map(|c| if (c as u32) < 128 { c as u8 } else { b'?' })
            .collect();
        format!("ee{secret}{}", hex_lower(&ascii))
    };
    format!(
        "tg://proxy?server={}&port={port}&secret={secret}",
        escape_data_string(link_host)
    )
}

/// `Uri.EscapeDataString`: `A-Za-z0-9-._~` stay, every other UTF-8 byte becomes
/// `%XX` with upper-case hex.
#[must_use]
pub fn escape_data_string(text: &str) -> String {
    let mut out = String::with_capacity(text.len());
    for &byte in text.as_bytes() {
        if byte.is_ascii_alphanumeric() || matches!(byte, b'-' | b'.' | b'_' | b'~') {
            out.push(char::from(byte));
        } else {
            let _ = write!(out, "%{byte:02X}");
        }
    }
    out
}

fn hex_lower(bytes: &[u8]) -> String {
    let mut out = String::with_capacity(bytes.len() * 2);
    for &byte in bytes {
        let _ = write!(out, "{byte:02x}");
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    const SECRET: &str = "0123456789abcdef0123456789abcdef";

    fn ip(text: &str) -> IpAddress {
        IpAddress::parse(text).expect("test address")
    }

    #[test]
    fn loopback_and_wildcards_build_the_dd_link() {
        assert_eq!(
            build_dd(&ip("127.0.0.1"), 1443, SECRET),
            format!("tg://proxy?server=127.0.0.1&port=1443&secret=dd{SECRET}")
        );
        assert_eq!(
            build_dd(&ip("0.0.0.0"), 2080, SECRET),
            format!("tg://proxy?server=127.0.0.1&port=2080&secret=dd{SECRET}")
        );
        assert_eq!(
            build_dd(&ip("::"), 1443, SECRET),
            format!("tg://proxy?server=%3A%3A1&port=1443&secret=dd{SECRET}")
        );
    }

    #[test]
    fn an_ipv6_host_is_escaped_without_brackets() {
        let link = build_dd(&ip("2001:db8::1"), 1443, SECRET);
        assert_eq!(
            link,
            format!("tg://proxy?server=2001%3Adb8%3A%3A1&port=1443&secret=dd{SECRET}")
        );
        assert!(!link.contains('['));
    }

    #[test]
    fn connect_address_maps_wildcards_and_keeps_the_rest() {
        assert_eq!(connect_address(&ip("0.0.0.0")).to_string(), "127.0.0.1");
        assert_eq!(connect_address(&ip("::")).to_string(), "::1");
        assert_eq!(
            connect_address(&ip("192.168.1.5")).to_string(),
            "192.168.1.5"
        );
        assert_eq!(connect_address(&ip("::1")).to_string(), "::1");
    }

    #[test]
    fn the_ee_link_carries_the_domain_in_lower_case_hex() {
        assert_eq!(
            build_ee("127.0.0.1", 1443, SECRET, "www.microsoft.com"),
            format!(
                "tg://proxy?server=127.0.0.1&port=1443&secret=ee{SECRET}7777772e6d6963726f736f66742e636f6d"
            )
        );
    }

    #[test]
    fn non_ascii_masking_domain_uses_the_ascii_encoder() {
        assert!(build_ee("127.0.0.1", 1443, SECRET, "пример.рф").ends_with("3f3f3f3f3f3f2e3f3f"));
    }

    #[test]
    fn an_empty_masking_domain_keeps_the_dd_secret() {
        assert_eq!(
            build_ee("127.0.0.1", 1443, SECRET, ""),
            format!("tg://proxy?server=127.0.0.1&port=1443&secret=dd{SECRET}")
        );
    }

    #[test]
    fn escaping_follows_uri_escape_data_string() {
        assert_eq!(escape_data_string("127.0.0.1"), "127.0.0.1");
        assert_eq!(escape_data_string("::1"), "%3A%3A1");
        assert_eq!(escape_data_string("::"), "%3A%3A");
        assert_eq!(escape_data_string("a b"), "a%20b");
        assert_eq!(escape_data_string("-._~"), "-._~");
        assert_eq!(escape_data_string("%&= "), "%25%26%3D%20");
        assert_eq!(escape_data_string("рус"), "%D1%80%D1%83%D1%81");
    }
}
