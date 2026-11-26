//! The host names the proxy itself opens over TLS when it falls back to
//! Cloudflare, as a hostlist nfqws2/winws2 can match.
//!
//! Ported from `src/TgwsOne.Core/Zapret/CfProxyHosts.cs`.  The narrow bypass
//! scope targets `telegram.txt`, but the proxy's fallback dials
//! `kws<dc>.<domain>` on Cloudflare, so the CF proxy's domain pool has to be
//! covered explicitly.

use std::collections::HashSet;

use super::to_lower_invariant;

/// Admin-owned list of the base domains to expand; shipped with the
/// application, seeded once by the service.
pub const DOMAINS_FILE: &str = "cfproxy-domains.txt";

/// Generated hostlist the narrow scope bypasses; rewritten from
/// [`DOMAINS_FILE`] on every run.
pub const HOSTS_FILE: &str = "cfproxy-hosts.txt";

/// Telegram's WebSocket hosts behind Cloudflare are `kws1`…`kws5` (DC 203 uses
/// DC 2's hosts).
pub const DCS: [u8; 5] = [1, 2, 3, 4, 5];

/// The comment block written at the top of the generated hostlist.
pub const HEADER: &str = "Сгенерировано службой TgwsOne: узлы Cloudflare, через которые прокси ходит в Telegram.\nФайл перезаписывается при каждом запуске обхода — правьте cfproxy-domains.txt.";

/// Every host name to bypass for the given base domains: the domain itself and
/// `kws<dc>.<domain>` for each data centre.
///
/// Entries are compared with the ClientHello name as written, so no wildcard is
/// relied on.  Repeat entries are dropped and the input order is kept.
#[must_use]
pub fn expand<I, S>(domains: I) -> Vec<String>
where
    I: IntoIterator<Item = S>,
    S: AsRef<str>,
{
    let mut seen = HashSet::new();
    let mut hosts = Vec::new();
    for raw in domains {
        let domain = to_lower_invariant(raw.as_ref().trim().trim_end_matches('.'));
        if domain.is_empty() || domain.starts_with('#') {
            continue;
        }
        if seen.insert(domain.clone()) {
            hosts.push(domain.clone());
        }
        for dc in DCS {
            let host = format!("kws{dc}.{domain}");
            if seen.insert(host.clone()) {
                hosts.push(host);
            }
        }
    }
    hosts
}

#[cfg(test)]
mod tests {
    use super::*;

    fn kws(domain: &str) -> Vec<String> {
        let mut hosts = vec![domain.to_string()];
        hosts.extend((1..=5).map(|dc| format!("kws{dc}.{domain}")));
        hosts
    }

    #[test]
    fn lowercases_and_expands_all_five_data_centres() {
        assert_eq!(expand(["EXAMPLE.com"]), kws("example.com"));
    }

    #[test]
    fn trailing_dot_is_trimmed() {
        assert_eq!(expand(["example.com."]), kws("example.com"));
    }

    #[test]
    fn duplicates_are_dropped_case_insensitively() {
        assert_eq!(expand(["example.com", "EXAMPLE.COM"]), kws("example.com"));
        assert_eq!(expand(["a.b", "a.b.", "A.B.."]), kws("a.b"));
    }

    #[test]
    fn comments_and_empty_entries_are_skipped() {
        assert_eq!(expand(["#comment", " real.org "]), kws("real.org"));
        assert_eq!(expand(["", "   ", "example.net"]), kws("example.net"));
        assert_eq!(expand(Vec::<String>::new()), Vec::<String>::new());
    }

    #[test]
    fn an_input_that_already_has_a_kws_prefix_is_expanded_again() {
        let mut expected = vec!["kws1.example.com".to_string()];
        expected.extend((1..=5).map(|dc| format!("kws{dc}.kws1.example.com")));
        assert_eq!(expand(["kws1.example.com"]), expected);
    }

    #[test]
    fn unicode_domains_keep_their_letters() {
        assert_eq!(expand(["пример.рф"]), kws("пример.рф"));
    }

    #[test]
    fn lowercasing_is_simple_not_full_case_folding() {
        assert_eq!(expand(["Straße.de"]), kws("straße.de"));
        assert_eq!(expand(["ẞ.example"]), kws("ß.example"));
        assert_eq!(expand(["İstanbul"]), kws("İstanbul"));
    }

    #[test]
    fn the_generated_header_matches_the_csharp_string() {
        assert_eq!(
            HEADER,
            "Сгенерировано службой TgwsOne: узлы Cloudflare, через которые прокси ходит в Telegram.\nФайл перезаписывается при каждом запуске обхода — правьте cfproxy-domains.txt."
        );
    }

    #[test]
    fn data_centres_are_one_to_five() {
        assert_eq!(DCS, [1, 2, 3, 4, 5]);
        assert_eq!(DOMAINS_FILE, "cfproxy-domains.txt");
        assert_eq!(HOSTS_FILE, "cfproxy-hosts.txt");
    }
}
