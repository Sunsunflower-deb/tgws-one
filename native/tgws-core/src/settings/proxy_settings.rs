//! Validated proxy settings: the `[proxy]` section and the secret.
//!
//! Ported from `src/TgwsOne.Shared/ProxySettings.cs`.  The pure field parsers
//! (`TryHost`, `TryDomain`, `TryPort`, `TryInt`, `ParseDcMap`, `ParseBool`,
//! `SplitList`, `IsValidSecret`) and the two entry points that use them,
//! `ReadForm`/`Parse`/`Load`, are here.  Saving, creating a secret with a RNG
//! and logging stay in the platform shell.

use std::fmt;
use std::net::{Ipv4Addr, Ipv6Addr};

use super::ini::IniFile;
use super::to_lower_invariant;

const DEFAULT_HOST: &str = "127.0.0.1";
const DEFAULT_PORT: i32 = 1443;
const DEFAULT_DC_MAP: &str = "2:149.154.167.220,4:149.154.167.220";

/// The site the proxy pretends to be when masking is on.
pub const DEFAULT_FAKE_TLS_DOMAIN: &str = "www.microsoft.com";
const DEFAULT_BUF_KB: i32 = 256;
const DEFAULT_POOL_SIZE: i32 = 4;
const DEFAULT_LOG_MAX_MB: i32 = 5;

/// An IPv4/IPv6 address with the `.NET` textual tolerance of
/// `IPAddress.TryParse` (`"1"`, `"10.1"`, `"0x7f.1"`, a numeric scope id) and
/// the same canonical `ToString`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum IpAddress {
    V4(Ipv4Addr),
    V6 { addr: Ipv6Addr, scope: u32 },
}

impl IpAddress {
    /// `IPAddress.TryParse`: shortened and hexadecimal/octal IPv4 forms are
    /// accepted, as are brackets and a scope id on IPv6.  No surrounding space
    /// is allowed (callers trim first, as the C# code does).
    #[must_use]
    pub fn parse(text: &str) -> Option<Self> {
        if text.contains(':') {
            Self::parse_v6(text)
        } else {
            Self::parse_v4(text)
        }
    }

    /// `IPAddress.Any` (`0.0.0.0`) or `IPAddress.IPv6Any` (`::`).
    #[must_use]
    pub fn is_any(&self) -> bool {
        match self {
            Self::V4(addr) => addr.is_unspecified(),
            Self::V6 { addr, scope } => addr.is_unspecified() && *scope == 0,
        }
    }

    fn parse_v6(text: &str) -> Option<Self> {
        let text = if let Some(inner) = text.strip_prefix('[') {
            inner.strip_suffix(']')?
        } else {
            text
        };
        let (base, scope) = match text.split_once('%') {
            Some((base, scope)) => (base, scope.parse::<u32>().unwrap_or(0)),
            None => (text, 0),
        };
        let addr: Ipv6Addr = base.parse().ok()?;
        Some(Self::V6 { addr, scope })
    }

    fn parse_v4(text: &str) -> Option<Self> {
        let parts: Vec<&str> = text.split('.').collect();
        if parts.is_empty() || parts.len() > 4 || parts.iter().any(|p| p.is_empty()) {
            return None;
        }
        let mut values = Vec::with_capacity(parts.len());
        for part in &parts {
            values.push(parse_dotnet_number(part)?);
        }
        // The last part takes all remaining bytes: a.b.c.d / a.b.c / a.b / a.
        let limits = match values.len() {
            1 => [0xFFFF_FFFF, 0, 0, 0],
            2 => [0xFF, 0xFF_FFFF, 0, 0],
            3 => [0xFF, 0xFF, 0xFFFF, 0],
            4 => [0xFF, 0xFF, 0xFF, 0xFF],
            _ => return None,
        };
        let shifts = match values.len() {
            1 => [0, 0, 0, 0],
            2 => [24, 0, 0, 0],
            3 => [24, 16, 0, 0],
            4 => [24, 16, 8, 0],
            _ => return None,
        };
        let mut value: u32 = 0;
        for ((part, limit), shift) in values.iter().zip(limits).zip(shifts) {
            if *part > limit {
                return None;
            }
            value |= part << shift;
        }
        Some(Self::V4(Ipv4Addr::from(value)))
    }
}

impl fmt::Display for IpAddress {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::V4(addr) => write!(f, "{addr}"),
            Self::V6 { addr, scope } => {
                if *scope == 0 {
                    write!(f, "{addr}")
                } else {
                    write!(f, "{addr}%{scope}")
                }
            }
        }
    }
}

/// `.NET` number syntax for an IPv4 part: `0x`/`0X` hex, a leading `0` octal,
/// otherwise decimal; an invalid digit or an overflow rejects the address.
fn parse_dotnet_number(part: &str) -> Option<u32> {
    let (radix, digits) =
        if let Some(hex) = part.strip_prefix("0x").or_else(|| part.strip_prefix("0X")) {
            (16, hex)
        } else if part.len() > 1 && part.starts_with('0') {
            (8, &part[1..])
        } else {
            (10, part)
        };
    if digits.is_empty() {
        return None;
    }
    let mut value: u32 = 0;
    for byte in digits.bytes() {
        let digit = char::from(byte).to_digit(radix)?;
        value = value.checked_mul(radix)?.checked_add(digit)?;
    }
    Some(value)
}

/// One data-centre override.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DcEntry {
    pub dc: i32,
    pub ip: IpAddress,
}

/// Raw values as a settings window holds them, before validation.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProxyForm {
    pub host: String,
    pub port: String,
    pub dc_map: String,
    pub cf_proxy: bool,
    pub h2: bool,
    pub no_secure: bool,
    pub verbose: bool,
    pub cf_domains: String,
    pub worker_domains: String,
    pub buf_kb: String,
    pub pool_size: String,
    pub log_max_mb: String,
    pub fake_tls_domain: String,
}

/// Validated settings: what the engine is started with.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ProxySettings {
    pub host: IpAddress,
    pub port: i32,
    pub dc_map: Vec<DcEntry>,
    pub cf_proxy: bool,
    pub h2: bool,
    pub no_secure: bool,
    pub verbose: bool,
    pub cf_domains: Vec<String>,
    pub worker_domains: Vec<String>,
    pub buf_kb: i32,
    pub pool_size: i32,
    pub log_max_mb: i32,
    pub secret: String,
    pub fake_tls_domain: String,
}

/// A validation problem; `field` is the `ProxyForm` name.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct FieldError {
    pub field: &'static str,
    pub message: String,
}

/// INI strings with the 1.x defaults where absent.  The DC map is one entry per
/// line.
#[must_use]
pub fn read_form(ini: &IniFile) -> ProxyForm {
    let text = |key: &str, default: &str| match ini.get("proxy", key) {
        Some(value) if !value.is_empty() => value.to_string(),
        _ => default.to_string(),
    };
    let number =
        |key: &str, default: i32| parse_integer(&text(key, "")).unwrap_or(default).to_string();
    let flag = |key: &str, default: bool| parse_bool(ini.get("proxy", key), default);

    ProxyForm {
        host: text("host", DEFAULT_HOST),
        port: number("port", DEFAULT_PORT),
        dc_map: ini
            .get("proxy", "dcmap")
            .unwrap_or(DEFAULT_DC_MAP)
            .replace(',', "\n"),
        cf_proxy: flag("cfproxy", true),
        h2: flag("h2", true),
        no_secure: flag("nosecure", false),
        verbose: flag("verbose", true),
        cf_domains: text("cfdomains", ""),
        worker_domains: text("workerdomains", ""),
        buf_kb: number("bufkb", DEFAULT_BUF_KB),
        pool_size: number("poolsize", DEFAULT_POOL_SIZE),
        log_max_mb: number("logmaxmb", DEFAULT_LOG_MAX_MB),
        fake_tls_domain: ini
            .get("proxy", "faketls")
            .unwrap_or(DEFAULT_FAKE_TLS_DOMAIN)
            .to_string(),
    }
}

/// Validates a form.  All problems are reported in field order; the settings
/// are `None` when there is any.
#[must_use]
pub fn parse(form: &ProxyForm, secret: &str) -> (Option<ProxySettings>, Vec<FieldError>) {
    let mut errors = Vec::new();

    let port = try_port(&form.port);
    if port.is_none() {
        errors.push(FieldError {
            field: "Port",
            message: "Порт должен быть числом от 1 до 65535.".to_string(),
        });
    }
    let host = try_host(&form.host);
    if host.is_none() {
        errors.push(FieldError {
            field: "Host",
            message: "Хост должен быть IP-адресом, например 127.0.0.1.".to_string(),
        });
    }
    let dc_map = parse_dc_map(&form.dc_map);
    if let Err(error) = &dc_map {
        errors.push(error.clone());
    }
    let buf_kb = try_int(&form.buf_kb, 4);
    if buf_kb.is_none() {
        errors.push(FieldError {
            field: "BufKb",
            message: "Буфер — не меньше 4 КБ.".to_string(),
        });
    }
    let pool_size = try_int(&form.pool_size, 0);
    if pool_size.is_none() {
        errors.push(FieldError {
            field: "PoolSize",
            message: "Пул WS — не меньше 0.".to_string(),
        });
    }
    let log_max_mb = try_int(&form.log_max_mb, 1);
    if log_max_mb.is_none() {
        errors.push(FieldError {
            field: "LogMaxMb",
            message: "Размер лога — не меньше 1 МБ.".to_string(),
        });
    }
    let has_domain = !form.fake_tls_domain.trim().is_empty();
    let fake_tls = try_domain(&form.fake_tls_domain);
    if has_domain && fake_tls.is_none() {
        errors.push(FieldError {
            field: "FakeTlsDomain",
            message: "Домен для маскировки — имя вида www.example.com, без схемы и пути."
                .to_string(),
        });
    }

    if !errors.is_empty() {
        return (None, errors);
    }
    (
        Some(ProxySettings {
            host: host.expect("checked above"),
            port: port.expect("checked above"),
            dc_map: dc_map.expect("checked above"),
            cf_proxy: form.cf_proxy,
            h2: form.h2,
            no_secure: form.no_secure,
            verbose: form.verbose,
            cf_domains: split_list(Some(&form.cf_domains)),
            worker_domains: split_list(Some(&form.worker_domains)),
            buf_kb: buf_kb.expect("checked above"),
            pool_size: pool_size.expect("checked above"),
            log_max_mb: log_max_mb.expect("checked above"),
            secret: secret.to_string(),
            fake_tls_domain: if has_domain {
                normalize_domain(&form.fake_tls_domain)
            } else {
                String::new()
            },
        }),
        errors,
    )
}

/// Settings from the INI; each missing or invalid field falls back to its
/// default.  Never fails.  The secret is the stored one if valid, otherwise
/// empty: the shell calls `EnsureSecret`.
#[must_use]
pub fn load(ini: &IniFile) -> ProxySettings {
    let form = read_form(ini);
    let dc_map = parse_dc_map(&form.dc_map)
        .or_else(|_| parse_dc_map(DEFAULT_DC_MAP))
        .unwrap_or_default();
    let secret = ini.get("app", "secret");

    ProxySettings {
        host: try_host(&form.host).unwrap_or(IpAddress::V4(Ipv4Addr::LOCALHOST)),
        port: try_port(&form.port).unwrap_or(DEFAULT_PORT),
        dc_map,
        cf_proxy: form.cf_proxy,
        h2: form.h2,
        no_secure: form.no_secure,
        verbose: form.verbose,
        cf_domains: split_list(Some(&form.cf_domains)),
        worker_domains: split_list(Some(&form.worker_domains)),
        buf_kb: try_int(&form.buf_kb, 4).unwrap_or(DEFAULT_BUF_KB),
        pool_size: try_int(&form.pool_size, 0).unwrap_or(DEFAULT_POOL_SIZE),
        log_max_mb: try_int(&form.log_max_mb, 1).unwrap_or(DEFAULT_LOG_MAX_MB),
        secret: if is_valid_secret(secret) {
            secret.unwrap_or_default().to_string()
        } else {
            String::new()
        },
        fake_tls_domain: match try_domain(&form.fake_tls_domain) {
            Some(domain) => normalize_domain(&domain),
            None => String::new(),
        },
    }
}

/// Exactly 32 hex characters; the engine refuses anything else.
#[must_use]
pub fn is_valid_secret(secret: Option<&str>) -> bool {
    secret.is_some_and(|s| s.len() == 32 && s.bytes().all(|b| b.is_ascii_hexdigit()))
}

/// Splits on `, ; space tab` and line breaks, trims, drops empties and
/// duplicates (ordinal), keeping the first occurrence order.
#[must_use]
pub fn split_list(raw: Option<&str>) -> Vec<String> {
    let Some(raw) = raw else {
        return Vec::new();
    };
    let mut seen = std::collections::HashSet::new();
    let mut items = Vec::new();
    for item in raw.split([',', ';', ' ', '\t', '\r', '\n']) {
        let item = item.trim();
        if item.is_empty() {
            continue;
        }
        if seen.insert(item.to_string()) {
            items.push(item.to_string());
        }
    }
    items
}

// ---------- field parsers shared by parse and load ----------

fn parse_bool(value: Option<&str>, default: bool) -> bool {
    match value {
        None | Some("") => default,
        Some(value) => {
            value == "1" || value.eq_ignore_ascii_case("true") || value.eq_ignore_ascii_case("yes")
        }
    }
}

fn try_host(text: &str) -> Option<IpAddress> {
    IpAddress::parse(text.trim())
}

fn try_domain(raw: &str) -> Option<String> {
    let domain = normalize_domain(raw);
    if domain.is_empty() || domain.len() > 253 {
        return None;
    }
    let labels: Vec<&str> = domain.split('.').collect();
    if labels.len() < 2 {
        return None;
    }
    for label in &labels {
        if label.is_empty() || label.len() > 63 {
            return None;
        }
        if label.starts_with('-') || label.ends_with('-') {
            return None;
        }
        if !label.chars().all(|c| c.is_ascii_alphanumeric() || c == '-') {
            return None;
        }
    }
    if !labels
        .last()
        .is_some_and(|label| label.chars().any(|c| c.is_ascii_alphabetic()))
    {
        return None;
    }
    Some(domain)
}

fn normalize_domain(raw: &str) -> String {
    to_lower_invariant(raw.trim().trim_end_matches('.'))
}

fn try_port(text: &str) -> Option<i32> {
    parse_integer(text.trim()).filter(|port| (1..=65535).contains(port))
}

fn try_int(text: &str, min: i32) -> Option<i32> {
    parse_integer(text.trim()).filter(|value| *value >= min)
}

/// `int.TryParse(..., NumberStyles.Integer, InvariantCulture)`: optional sign,
/// ASCII digits only, must fit in `i32`.
fn parse_integer(text: &str) -> Option<i32> {
    let bytes = text.as_bytes();
    let (negative, digits) = match bytes.first()? {
        b'-' => (true, &bytes[1..]),
        b'+' => (false, &bytes[1..]),
        _ => (false, bytes),
    };
    if digits.is_empty() || !digits.iter().all(u8::is_ascii_digit) {
        return None;
    }
    let mut value: i64 = 0;
    for digit in digits {
        value = value
            .checked_mul(10)?
            .checked_add(i64::from(digit - b'0'))?;
    }
    let value = if negative { -value } else { value };
    i32::try_from(value).ok()
}

/// Accepts lines and/or commas, one `N:IP` each.  An empty result is valid.
fn parse_dc_map(raw: &str) -> Result<Vec<DcEntry>, FieldError> {
    let mut map = Vec::new();
    for line in raw
        .split([',', '\r', '\n'])
        .map(str::trim)
        .filter(|line| !line.is_empty())
    {
        let Some(colon) = line.find(':').filter(|colon| *colon > 0) else {
            return Err(FieldError {
                field: "DcMap",
                message: format!(
                    "Строка DC → IP должна быть в формате номер:IP, например 4:149.154.167.220. Проблемная строка: {line}"
                ),
            });
        };
        let dc = parse_integer(line[..colon].trim());
        let ip = IpAddress::parse(line[colon + 1..].trim());
        let (Some(dc), Some(ip)) = (dc, ip) else {
            return Err(FieldError {
                field: "DcMap",
                message: format!("Не удалось разобрать строку DC → IP: {line}"),
            });
        };
        map.push(DcEntry { dc, ip });
    }
    Ok(map)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn default_form() -> ProxyForm {
        ProxyForm {
            host: "127.0.0.1".into(),
            port: "1443".into(),
            dc_map: "2:149.154.167.220\n4:149.154.167.220".into(),
            cf_proxy: true,
            h2: true,
            no_secure: false,
            verbose: true,
            cf_domains: String::new(),
            worker_domains: String::new(),
            buf_kb: "256".into(),
            pool_size: "4".into(),
            log_max_mb: "5".into(),
            fake_tls_domain: DEFAULT_FAKE_TLS_DOMAIN.into(),
        }
    }

    fn parse_ok(form: &ProxyForm) -> ProxySettings {
        let (settings, errors) = parse(form, "0123456789abcdef0123456789abcdef");
        assert!(errors.is_empty(), "{errors:?}");
        settings.expect("valid form")
    }

    #[test]
    fn empty_ini_gives_the_one_x_defaults() {
        let settings = load(&IniFile::parse(""));
        assert_eq!(settings.host, IpAddress::V4(Ipv4Addr::LOCALHOST));
        assert_eq!(settings.port, 1443);
        assert_eq!(
            settings.dc_map,
            [
                DcEntry {
                    dc: 2,
                    ip: IpAddress::parse("149.154.167.220").unwrap(),
                },
                DcEntry {
                    dc: 4,
                    ip: IpAddress::parse("149.154.167.220").unwrap(),
                },
            ]
        );
        assert!(settings.cf_proxy && settings.h2 && !settings.no_secure && settings.verbose);
        assert_eq!(
            (settings.buf_kb, settings.pool_size, settings.log_max_mb),
            (256, 4, 5)
        );
        assert_eq!(settings.secret, "");
        // `ReadForm` defaults the masking domain, so `Load` keeps it here.
        assert_eq!(settings.fake_tls_domain, DEFAULT_FAKE_TLS_DOMAIN);
    }

    #[test]
    fn read_form_defaults_and_numbers_are_normalised() {
        let form = read_form(&IniFile::parse(""));
        assert_eq!(form.host, "127.0.0.1");
        assert_eq!(form.port, "1443");
        assert_eq!(form.dc_map, "2:149.154.167.220\n4:149.154.167.220");
        assert_eq!(form.fake_tls_domain, DEFAULT_FAKE_TLS_DOMAIN);

        let form = read_form(&IniFile::parse("[proxy]\nport=003\nbufkb=abc\n"));
        assert_eq!(form.port, "3");
        assert_eq!(form.buf_kb, "256");
    }

    #[test]
    fn boolean_spellings_match_the_csharp_rules() {
        let ini = IniFile::parse("[proxy]\ncfproxy=YES\nh2=off\nnosecure=True\nverbose=\n");
        let settings = load(&ini);
        assert!(settings.cf_proxy);
        assert!(!settings.h2);
        assert!(settings.no_secure);
        assert!(settings.verbose); // empty means "use the default", which is true
    }

    #[test]
    fn parse_reports_all_field_errors_in_order() {
        let mut form = default_form();
        form.port = "0".into();
        form.host = "localhost".into();
        form.buf_kb = "3".into();
        form.pool_size = "-1".into();
        form.log_max_mb = "0".into();
        let (settings, errors) = parse(&form, "");
        assert!(settings.is_none());
        let fields: Vec<_> = errors.iter().map(|e| e.field).collect();
        assert_eq!(fields, ["Port", "Host", "BufKb", "PoolSize", "LogMaxMb"]);
    }

    #[test]
    fn parse_dc_map_accepts_commas_lines_and_reports_bad_lines() {
        let form = default_form();
        assert_eq!(parse_ok(&form).dc_map.len(), 2);

        let mut bad = default_form();
        bad.dc_map = "2:1.1.1.1\n:149.154.167.220".into();
        let (_, errors) = parse(&bad, "");
        assert_eq!(errors[0].field, "DcMap");
        assert!(
            errors[0]
                .message
                .ends_with("Проблемная строка: :149.154.167.220")
        );

        bad.dc_map = "x:1.2.3.4".into();
        let (_, errors) = parse(&bad, "");
        assert_eq!(
            errors[0].message,
            "Не удалось разобрать строку DC → IP: x:1.2.3.4"
        );

        let mut empty = default_form();
        empty.dc_map = "  \r\n ".into();
        assert!(parse_ok(&empty).dc_map.is_empty());
    }

    #[test]
    fn the_host_parser_keeps_the_dotnet_tolerance() {
        for (raw, shown) in [
            ("1", "0.0.0.1"),
            ("10.1", "10.0.0.1"),
            ("10.1.2", "10.1.0.2"),
            ("0x7f.1", "127.0.0.1"),
            ("010", "0.0.0.8"),
            ("::1", "::1"),
            ("[2001:db8::1]", "2001:db8::1"),
            ("::ffff:127.0.0.1", "::ffff:127.0.0.1"),
            ("fe80::1%12", "fe80::1%12"),
            ("fe80::1%eth0", "fe80::1"),
        ] {
            assert_eq!(
                IpAddress::parse(raw).map(|ip| ip.to_string()),
                Some(shown.into()),
                "{raw}"
            );
        }
        for raw in [
            "08",
            "1.2.3.256",
            "1.2.3.4.5",
            "1.2.3.4 ",
            "1..2.3",
            "",
            "localhost",
        ] {
            assert_eq!(IpAddress::parse(raw), None, "{raw}");
        }
    }

    #[test]
    fn fake_tls_domain_is_validated_and_normalised() {
        let mut form = default_form();
        form.fake_tls_domain = "  WWW.Example.COM.  ".into();
        assert_eq!(parse_ok(&form).fake_tls_domain, "www.example.com");

        form.fake_tls_domain = "  ".into();
        assert_eq!(parse_ok(&form).fake_tls_domain, "");

        for bad in [
            "localhost",
            "example.123",
            "http://x.com",
            "a_b.com",
            "-a.com",
        ] {
            form.fake_tls_domain = bad.into();
            let (settings, errors) = parse(&form, "");
            assert!(settings.is_none(), "{bad}");
            assert_eq!(errors[0].field, "FakeTlsDomain", "{bad}");
        }
    }

    #[test]
    fn split_list_and_secret_match_the_csharp_contract() {
        assert_eq!(
            split_list(Some("a.com, b.com;a.com\tc.com")),
            ["a.com", "b.com", "c.com"]
        );
        assert_eq!(split_list(Some("\r\nx\n x ")), ["x"]);
        assert!(split_list(None).is_empty());
        assert!(split_list(Some(" ,; ")).is_empty());

        assert!(is_valid_secret(Some("0123456789abcdef0123456789abcdef")));
        assert!(is_valid_secret(Some("0123456789ABCDEF0123456789ABCDEF")));
        assert!(!is_valid_secret(Some("abc")));
        assert!(!is_valid_secret(Some("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")));
        assert!(!is_valid_secret(None));
    }

    #[test]
    fn load_falls_back_on_garbage_but_keeps_a_valid_secret() {
        let ini = IniFile::parse(
            "[proxy]\nhost=localhost\nport=abc\nbufkb=1\ndcmap=\n[app]\nsecret=0123456789abcdef0123456789abcdef\n",
        );
        let settings = load(&ini);
        assert_eq!(settings.host, IpAddress::V4(Ipv4Addr::LOCALHOST));
        assert_eq!(settings.port, 1443);
        assert_eq!(settings.buf_kb, 256);
        assert!(settings.dc_map.is_empty()); // an empty map is a real value
        assert_eq!(settings.secret, "0123456789abcdef0123456789abcdef");
    }
}
