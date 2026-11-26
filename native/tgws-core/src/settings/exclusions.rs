//! The user-editable exclusions: hosts and IPs the bypass must never touch.
//!
//! Ported from `src/TgwsOne.Core/Zapret/Exclusions.cs`.  Every entry is checked
//! before it reaches a file nfqws2/winws2 parses; blank entries are ignored, a
//! repeat is dropped and anything past [`MAX_ENTRIES`] distinct valid entries is
//! reported as invalid.  Reading and writing the list files stays in the shell.

use std::collections::HashSet;
use std::net::{IpAddr, Ipv6Addr};

use super::to_lower_invariant;

/// How many distinct valid entries each list keeps.
pub const MAX_ENTRIES: usize = 1000;

const MAX_HOST_LENGTH: usize = 253;

/// Trim, lower-case, IDN to punycode; blank entries are ignored.  `invalid`
/// holds the rejected entries as typed (trimmed) plus valid ones beyond the
/// limit, in input order.
#[must_use]
pub fn normalize_hosts<I, S>(raw: I) -> (Vec<String>, Vec<String>)
where
    I: IntoIterator<Item = S>,
    S: AsRef<str>,
{
    normalize(raw, normalize_host)
}

/// A single IPv4/IPv6 address or a CIDR network (host bits cleared), in
/// canonical text.  A repeat of an already accepted entry is not an error.
#[must_use]
pub fn normalize_ips<I, S>(raw: I) -> (Vec<String>, Vec<String>)
where
    I: IntoIterator<Item = S>,
    S: AsRef<str>,
{
    normalize(raw, normalize_ip)
}

fn normalize<I, S, F>(raw: I, normalize: F) -> (Vec<String>, Vec<String>)
where
    I: IntoIterator<Item = S>,
    S: AsRef<str>,
    F: Fn(&str) -> Option<String>,
{
    let mut valid = Vec::new();
    let mut invalid = Vec::new();
    let mut seen = HashSet::new();
    for entry in raw {
        let text = entry.as_ref().trim();
        if text.is_empty() {
            continue;
        }
        match normalize(text) {
            None => invalid.push(text.to_string()),
            Some(norm) => {
                if !seen.insert(norm.clone()) {
                    continue;
                }
                if valid.len() >= MAX_ENTRIES {
                    invalid.push(text.to_string());
                } else {
                    valid.push(norm);
                }
            }
        }
    }
    (valid, invalid)
}

fn normalize_host(text: &str) -> Option<String> {
    let ascii = idn_to_ascii(&to_lower_invariant(text))?;
    let ascii = to_lower_invariant(&ascii);

    if ascii.len() > MAX_HOST_LENGTH {
        return None;
    }
    let labels: Vec<&str> = ascii.split('.').collect();
    if labels.len() < 2 {
        return None;
    }
    if !labels.iter().all(|label| valid_label(label)) {
        return None;
    }
    // No top-level domain is all digits: such an entry is an IP typed into the
    // wrong list.
    if labels
        .last()
        .is_some_and(|label| label.bytes().all(|b| b.is_ascii_digit()))
    {
        return None;
    }
    Some(ascii)
}

fn normalize_ip(text: &str) -> Option<String> {
    let (addr_text, prefix_text) = match text.split_once('/') {
        Some((addr, prefix)) => (addr, Some(prefix)),
        None => (text, None),
    };
    let addr = try_parse_address(addr_text)?;

    let max_prefix = match addr {
        IpAddr::V4(_) => 32,
        IpAddr::V6(_) => 128,
    };
    let Some(prefix_text) = prefix_text else {
        return Some(addr.to_string());
    };
    if !valid_prefix(prefix_text) {
        return None;
    }
    let prefix: i32 = prefix_text.parse().ok()?;
    if prefix > max_prefix {
        return None;
    }
    Some(format!("{}/{}", mask(addr, prefix), prefix))
}

/// `^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\z`.
fn valid_label(label: &str) -> bool {
    let bytes = label.as_bytes();
    if bytes.is_empty() || bytes.len() > 63 {
        return false;
    }
    if !(bytes[0].is_ascii_lowercase() || bytes[0].is_ascii_digit()) {
        return false;
    }
    if bytes.len() == 1 {
        return true;
    }
    if !(bytes[bytes.len() - 1].is_ascii_lowercase() || bytes[bytes.len() - 1].is_ascii_digit()) {
        return false;
    }
    bytes[1..bytes.len() - 1]
        .iter()
        .all(|b| b.is_ascii_lowercase() || b.is_ascii_digit() || *b == b'-')
}

/// `IPAddress.TryParse` also takes `"1"`, `"10.1"` and scope ids; here only the
/// plain textual forms are accepted, as the C# code does.
fn try_parse_address(text: &str) -> Option<IpAddr> {
    let plain = if text.contains(':') {
        text.bytes()
            .all(|b| b.is_ascii_hexdigit() || matches!(b, b':' | b'.'))
    } else {
        is_dotted_quad(text)
    };
    if !plain {
        return None;
    }
    text.parse().ok()
}

/// `^\d{1,3}(\.\d{1,3}){3}\z`.
fn is_dotted_quad(text: &str) -> bool {
    let parts: Vec<&str> = text.split('.').collect();
    parts.len() == 4
        && parts
            .iter()
            .all(|p| (1..=3).contains(&p.len()) && p.bytes().all(|b| b.is_ascii_digit()))
}

/// `^\d{1,3}\z`.
fn valid_prefix(text: &str) -> bool {
    (1..=3).contains(&text.len()) && text.bytes().all(|b| b.is_ascii_digit())
}

fn mask(addr: IpAddr, prefix: i32) -> IpAddr {
    match addr {
        IpAddr::V4(addr) => {
            let mut bytes = addr.octets();
            for (i, byte) in bytes.iter_mut().enumerate() {
                let keep = (prefix - i as i32 * 8).clamp(0, 8);
                *byte &= (0xFF_u32 << (8 - keep)) as u8;
            }
            IpAddr::V4(bytes.into())
        }
        IpAddr::V6(addr) => {
            let mut bytes = addr.octets();
            for (i, byte) in bytes.iter_mut().enumerate() {
                let keep = (prefix - i as i32 * 8).clamp(0, 8);
                *byte &= (0xFF_u32 << (8 - keep)) as u8;
            }
            IpAddr::V6(Ipv6Addr::from(bytes))
        }
    }
}

// ---------- IDNA2003-ish: lowercase + fullwidth mapping + RFC 3492 ----------

fn idn_to_ascii(text: &str) -> Option<String> {
    let mut labels = Vec::new();
    for label in text.split('.') {
        if label.is_empty() {
            return None;
        }
        labels.push(idn_label(label)?);
    }
    Some(labels.join("."))
}

fn idn_label(label: &str) -> Option<String> {
    let mapped: String = label
        .chars()
        .map(|c| match c {
            '\u{FF01}'..='\u{FF5E}' => char::from_u32(c as u32 - 0xFEE0).unwrap_or(c),
            _ => c,
        })
        .collect();
    if mapped.is_ascii() {
        return Some(mapped);
    }
    let chars: Vec<char> = mapped.chars().collect();
    Some(format!("xn--{}", punycode_encode(&chars)?))
}

fn punycode_encode(input: &[char]) -> Option<String> {
    const BASE: u32 = 36;
    const TMIN: u32 = 1;
    const TMAX: u32 = 26;
    const INITIAL_BIAS: u32 = 72;
    const INITIAL_N: u32 = 128;

    let code_points: Vec<u32> = input.iter().map(|c| *c as u32).collect();
    let mut output = String::new();
    let basic: Vec<u32> = code_points.iter().copied().filter(|c| *c < 128).collect();
    for code in &basic {
        output.push(char::from_u32(*code)?);
    }
    let mut handled = basic.len() as u32;
    if handled > 0 {
        output.push('-');
    }

    let mut n = INITIAL_N;
    let mut delta: u32 = 0;
    let mut bias = INITIAL_BIAS;
    let total = code_points.len() as u32;

    while handled < total {
        let m = code_points.iter().copied().filter(|c| *c >= n).min()?;
        delta = delta.checked_add((m - n).checked_mul(handled + 1)?)?;
        n = m;
        for code in &code_points {
            if *code < n {
                delta = delta.checked_add(1)?;
            }
            if *code == n {
                let mut q = delta;
                let mut k = BASE;
                loop {
                    let t = if k <= bias {
                        TMIN
                    } else if k >= bias + TMAX {
                        TMAX
                    } else {
                        k - bias
                    };
                    if q < t {
                        break;
                    }
                    let digit = t + (q - t) % (BASE - t);
                    output.push(encode_digit(digit)?);
                    q = (q - t) / (BASE - t);
                    k += BASE;
                }
                output.push(encode_digit(q)?);
                bias = adapt(delta, handled + 1, handled == basic.len() as u32);
                delta = 0;
                handled += 1;
            }
        }
        delta = delta.checked_add(1)?;
        n = n.checked_add(1)?;
    }
    Some(output)
}

fn adapt(mut delta: u32, num_points: u32, first: bool) -> u32 {
    const BASE: u32 = 36;
    const TMIN: u32 = 1;
    const TMAX: u32 = 26;
    const SKEW: u32 = 38;
    const DAMP: u32 = 700;
    delta = if first { delta / DAMP } else { delta / 2 };
    delta += delta / num_points;
    let mut k = 0;
    while delta > ((BASE - TMIN) * TMAX) / 2 {
        delta /= BASE - TMIN;
        k += BASE;
    }
    k + (((BASE - TMIN + 1) * delta) / (delta + SKEW))
}

fn encode_digit(digit: u32) -> Option<char> {
    match digit {
        0..=25 => char::from_u32(u32::from(b'a') + digit),
        26..=35 => char::from_u32(u32::from(b'0') + (digit - 26)),
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn hosts(raw: &[&str]) -> (Vec<String>, Vec<String>) {
        normalize_hosts(raw)
    }

    fn ips(raw: &[&str]) -> (Vec<String>, Vec<String>) {
        normalize_ips(raw)
    }

    #[test]
    fn valid_hosts_are_lowercased_and_idn_is_punycoded() {
        for (raw, expected) in [
            ("sberbank.ru", "sberbank.ru"),
            ("Sub.Example.COM", "sub.example.com"),
            ("  spaced.example.com \t", "spaced.example.com"),
            ("пример.рф", "xn--e1afmkfd.xn--p1ai"),
            ("ПРИМЕР.РФ", "xn--e1afmkfd.xn--p1ai"),
            ("xn--e1afmkfd.xn--p1ai", "xn--e1afmkfd.xn--p1ai"),
            ("straße.de", "xn--strae-oqa.de"),
            ("ẞ.de", "xn--zca.de"),
            ("ＡＢＣ.example", "abc.example"),
            ("中文.tw", "xn--fiq228c.tw"),
            ("a.b", "a.b"),
            ("my-site.example.com", "my-site.example.com"),
            ("123.example.com", "123.example.com"),
        ] {
            assert_eq!(hosts(&[raw]), (vec![expected.to_string()], vec![]), "{raw}");
        }
    }

    #[test]
    fn invalid_hosts_are_reported_as_trimmed_input() {
        for raw in [
            "localhost",
            "-a.com",
            "a-.com",
            "a..b",
            ".example.com",
            "example.com.",
            "exa mple.com",
            "http://x.com",
            "x.com/path",
            "user@x.com",
            "*.example.com",
            "under_score.example.com",
            "203.0.113.5",
            "example.123",
        ] {
            assert_eq!(
                hosts(&[raw]),
                (vec![], vec![raw.trim().to_string()]),
                "{raw}"
            );
        }
    }

    #[test]
    fn host_label_and_total_length_limits() {
        let ok_label = format!("{}.com", "a".repeat(63));
        let too_long_label = format!("{}.com", "a".repeat(64));
        assert_eq!(
            hosts(&[&ok_label, &too_long_label]),
            (vec![ok_label], vec![too_long_label])
        );

        let label = |n: usize| "a".repeat(n);
        let ok = format!("{}.{}.{}.{}", label(63), label(63), label(63), label(61));
        let too_long = format!("{}.{}.{}.{}", label(63), label(63), label(63), label(62));
        assert_eq!(ok.len(), 253);
        assert_eq!(hosts(&[&ok, &too_long]), (vec![ok], vec![too_long]));
    }

    #[test]
    fn valid_ips_and_cidrs_are_canonical() {
        for (raw, expected) in [
            ("203.0.113.5", "203.0.113.5"),
            ("203.0.113.0/24", "203.0.113.0/24"),
            ("2001:db8::/32", "2001:db8::/32"),
            ("2001:db8::1", "2001:db8::1"),
            ("2001:DB8::1", "2001:db8::1"),
            ("2001:0db8:0:0:0:0:0:0/32", "2001:db8::/32"),
            ("203.0.113.77/24", "203.0.113.0/24"),
            ("10.0.0.0/8", "10.0.0.0/8"),
            ("10.0.0.1/32", "10.0.0.1/32"),
            ("  192.168.0.1  ", "192.168.0.1"),
        ] {
            assert_eq!(ips(&[raw]), (vec![expected.to_string()], vec![]), "{raw}");
        }
    }

    #[test]
    fn invalid_ips_follow_the_strict_contract() {
        for raw in [
            "300.1.1.1",
            "10.0.0.0/33",
            "2001:db8::/129",
            "abc",
            "1",
            "10.1",
            "1.2.3.4/",
            "1.2.3.4/-1",
            "1.2.3.4/x",
            "1.2.3.4/24/8",
            "1.2.3.4 /24",
            "fe80::1%eth0",
            "[2001:db8::1]",
            "example.com",
        ] {
            assert_eq!(ips(&[raw]), (vec![], vec![raw.trim().to_string()]), "{raw}");
        }
    }

    #[test]
    fn dedup_keeps_the_first_and_ignores_blank_entries() {
        let (valid, _) = hosts(&[
            "a.com",
            "B.com",
            "A.COM",
            " a.com ",
            "пример.рф",
            "xn--e1afmkfd.xn--p1ai",
            "b.com",
            "",
            "   ",
            "\t",
        ]);
        assert_eq!(valid, ["a.com", "b.com", "xn--e1afmkfd.xn--p1ai"]);

        let (valid, _) = ips(&["10.0.0.1", "10.0.0.0/24", "10.0.0.7/24", "10.0.0.1"]);
        assert_eq!(valid, ["10.0.0.1", "10.0.0.0/24"]);
    }

    #[test]
    fn the_limit_is_checked_after_dedup() {
        let mut raw = vec!["bad host".to_string(), "dup.example.com".to_string()];
        for i in 0..1001 {
            raw.push(format!("h{i}.example.com"));
        }
        raw.push("dup.example.com".to_string());

        let (valid, invalid) = normalize_hosts(&raw);
        assert_eq!(valid.len(), MAX_ENTRIES);
        assert_eq!(valid[0], "dup.example.com");
        assert_eq!(valid[valid.len() - 1], "h998.example.com");
        assert_eq!(
            invalid,
            ["bad host", "h999.example.com", "h1000.example.com"]
        );
    }

    #[test]
    fn the_ip_limit_is_enforced() {
        let raw: Vec<String> = (0..1002)
            .map(|i| format!("10.{}.{}.1", i / 256, i % 256))
            .collect();
        let (valid, invalid) = normalize_ips(&raw);
        assert_eq!(valid.len(), 1000);
        assert_eq!(invalid.len(), 2);
    }
}
