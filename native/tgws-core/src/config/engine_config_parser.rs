//! `EngineConfigParser.TryParse` and `CoerceDomains`, plus the pure
//! `EngineConfigFactory.FromSettings` mapping.
//!
//! Ported from `src/TgwsOne.Engine/EngineConfig.cs` (lines 98-388 in the C#
//! tree after E1.1) and `src/TgwsOne.Core/EngineConfigFactory.cs`.  The
//! command line is exactly the one tg-ws-proxy v1.11.4 accepted, because the
//! desktop `EngineCommand`, existing scripts and the process fallback pass it
//! through.

use std::collections::BTreeMap;

use crate::settings::{IpAddress, ProxySettings, is_valid_secret};

/// The default listening port.
pub const DEFAULT_PORT: i32 = 1443;
/// The default listening address.
pub const DEFAULT_HOST: &str = "127.0.0.1";
/// How often a pooled WebSocket gets a keep-alive ping in masked mode.
pub const DEFAULT_PING_INTERVAL_SECONDS: i32 = 25;
/// Random extra delay added to every ping.
pub const DEFAULT_PING_JITTER_SECONDS: i32 = 10;

const DEFAULT_DC_IP: &str = "149.154.167.220";

/// Everything the engine needs, the same values the Python tg-ws-proxy v1.11.4
/// command line carried.  Immutable: a restart with different settings is a new
/// engine instance.
#[derive(Debug, Clone, PartialEq)]
pub struct EngineConfig {
    pub host: String,
    pub port: i32,
    pub secret: String,
    pub dc_redirects: BTreeMap<i32, String>,
    pub buffer_size: i32,
    pub pool_size: i32,
    pub fallback_cf_proxy: bool,
    pub cfproxy_user_domains: Vec<String>,
    pub cfproxy_worker_domains: Vec<String>,
    pub cfproxy_h2_media: bool,
    pub disable_secure: bool,
    pub fake_tls_domain: String,
    pub proxy_protocol: bool,
    pub force_test_dc: bool,
    pub verbose: bool,
    pub ping_interval_seconds: i32,
    pub ping_jitter_seconds: i32,
    pub log_file: Option<String>,
    pub log_max_mb: f64,
    pub log_backups: i32,
}

impl Default for EngineConfig {
    fn default() -> Self {
        Self {
            host: DEFAULT_HOST.to_string(),
            port: DEFAULT_PORT,
            secret: String::new(),
            dc_redirects: BTreeMap::from([
                (2, DEFAULT_DC_IP.to_string()),
                (4, DEFAULT_DC_IP.to_string()),
            ]),
            buffer_size: 256 * 1024,
            pool_size: 4,
            fallback_cf_proxy: true,
            cfproxy_user_domains: Vec::new(),
            cfproxy_worker_domains: Vec::new(),
            cfproxy_h2_media: true,
            disable_secure: false,
            fake_tls_domain: String::new(),
            proxy_protocol: false,
            force_test_dc: false,
            verbose: false,
            ping_interval_seconds: 0,
            ping_jitter_seconds: DEFAULT_PING_JITTER_SECONDS,
            log_file: None,
            log_max_mb: 5.0,
            log_backups: 1,
        }
    }
}

impl EngineConfig {
    /// HTTP/2 for media through Cloudflare: only where the plain CF-proxy over
    /// HTTPS is usable at all.
    #[must_use]
    pub fn h2_enabled(&self) -> bool {
        self.cfproxy_h2_media
            && self.fallback_cf_proxy
            && !self.disable_secure
            && !self.force_test_dc
    }
}

/// Parses the engine command line.
///
/// `new_secret` is called only when `--secret` is absent: the original
/// generates 16 random bytes there, and randomness stays behind this seam.
///
/// # Errors
///
/// Returns `"help"` for `-h`/`--help`, otherwise the exact stderr message of
/// the original parser.
pub fn try_parse<F>(args: &[String], new_secret: F) -> Result<EngineConfig, String>
where
    F: FnOnce() -> String,
{
    let mut host = DEFAULT_HOST.to_string();
    let mut port = DEFAULT_PORT;
    let mut secret: Option<String> = None;
    let mut dc_ip: Vec<Option<String>> = Vec::new();
    let mut dc_ip_given = false;
    let mut verbose = false;
    let mut log_file: Option<String> = None;
    let mut log_max_mb = 5.0_f64;
    let mut log_backups = 1;
    let mut buf_kb = 256;
    let mut pool_size = 4;
    let mut cf_domains: Vec<String> = Vec::new();
    let mut worker_domains: Vec<String> = Vec::new();
    let mut no_h2 = false;
    let mut no_cfproxy = false;
    let mut no_secure = false;
    let mut force_test_dc = false;
    let mut proxy_protocol = false;
    let mut fake_tls_domain = String::new();
    let mut ping_interval = 0;
    let mut ping_jitter = DEFAULT_PING_JITTER_SECONDS;

    let mut i = 0;
    while i < args.len() {
        let flag = args[i].clone();
        match flag.as_str() {
            "-h" | "--help" => return Err("help".to_string()),
            "--host" => {
                i += 1;
                let Some(value) = args.get(i) else {
                    return Err("после --host нужен адрес".to_string());
                };
                if IpAddress::parse(value).is_none() {
                    return Err(format!(
                        "--host ждёт IP-адрес, например 127.0.0.1 или 0.0.0.0 (получилось «{value}»)"
                    ));
                }
                host = value.clone();
            }
            "--port" => {
                i += 1;
                let Some(value) = args.get(i) else {
                    return Err("после --port нужно число".to_string());
                };
                let Some(parsed) = parse_i32(value) else {
                    return Err("после --port нужно число".to_string());
                };
                if !(1..=65535).contains(&parsed) {
                    return Err(format!(
                        "--port должен быть от 1 до 65535 (получилось {parsed})"
                    ));
                }
                port = parsed;
            }
            "--secret" => {
                i += 1;
                let Some(value) = args.get(i) else {
                    return Err("после --secret нужен секрет".to_string());
                };
                secret = Some(value.clone());
            }
            "--dc-ip" => {
                dc_ip_given = true;
                let value = if args.get(i + 1).is_some_and(|next| !next.starts_with('-')) {
                    i += 1;
                    Some(args[i].clone())
                } else {
                    None
                };
                dc_ip.push(value);
            }
            "-v" | "--verbose" => verbose = true,
            "--log-file" => {
                i += 1;
                let Some(value) = args.get(i) else {
                    return Err("после --log-file нужен путь".to_string());
                };
                log_file = Some(value.clone());
            }
            "--log-max-mb" => {
                i += 1;
                let Some(value) = args.get(i) else {
                    return Err("после --log-max-mb нужно число".to_string());
                };
                let Some(parsed) = parse_double(value) else {
                    return Err("после --log-max-mb нужно число".to_string());
                };
                log_max_mb = parsed;
            }
            "--log-backups" => {
                i += 1;
                let Some(value) = args.get(i) else {
                    return Err("после --log-backups нужно число".to_string());
                };
                let Some(parsed) = parse_i32(value) else {
                    return Err("после --log-backups нужно число".to_string());
                };
                log_backups = parsed;
            }
            "--buf-kb" => {
                i += 1;
                let Some(value) = args.get(i) else {
                    return Err("после --buf-kb нужно число".to_string());
                };
                let Some(parsed) = parse_i32(value) else {
                    return Err("после --buf-kb нужно число".to_string());
                };
                buf_kb = parsed;
            }
            "--pool-size" => {
                i += 1;
                let Some(value) = args.get(i) else {
                    return Err("после --pool-size нужно число".to_string());
                };
                let Some(parsed) = parse_i32(value) else {
                    return Err("после --pool-size нужно число".to_string());
                };
                pool_size = parsed;
            }
            "--cfproxy-domain" => {
                i += 1;
                let Some(value) = args.get(i) else {
                    return Err("после --cfproxy-domain нужен домен".to_string());
                };
                cf_domains.push(value.clone());
            }
            "--cfproxy-worker-domain" => {
                i += 1;
                let Some(value) = args.get(i) else {
                    return Err("после --cfproxy-worker-domain нужен домен".to_string());
                };
                worker_domains.push(value.clone());
            }
            "--no-h2" | "--cfproxy-h2-media" => no_h2 = flag == "--no-h2",
            "--no-cfproxy" => no_cfproxy = true,
            "--no-secure" => no_secure = true,
            "--fake-tls-domain" => {
                i += 1;
                let Some(value) = args.get(i) else {
                    return Err("после --fake-tls-domain нужен домен".to_string());
                };
                fake_tls_domain = value.trim().to_string();
            }
            "--ping-interval" => {
                i += 1;
                let Some(value) = args.get(i) else {
                    return Err("после --ping-interval нужно число".to_string());
                };
                let Some(parsed) = parse_i32(value) else {
                    return Err("после --ping-interval нужно число".to_string());
                };
                if parsed < 0 {
                    return Err("--ping-interval не может быть отрицательным".to_string());
                }
                ping_interval = parsed;
            }
            "--ping-jitter" => {
                i += 1;
                let Some(value) = args.get(i) else {
                    return Err("после --ping-jitter нужно число".to_string());
                };
                let Some(parsed) = parse_i32(value) else {
                    return Err("после --ping-jitter нужно число".to_string());
                };
                if parsed < 0 {
                    return Err("--ping-jitter не может быть отрицательным".to_string());
                }
                ping_jitter = parsed;
            }
            "--force-test-dc" => force_test_dc = true,
            "--proxy-protocol" => proxy_protocol = true,
            other => return Err(format!("неизвестный ключ: {other}")),
        }
        i += 1;
    }

    let secret = match secret {
        None => new_secret(),
        Some(secret) => {
            let secret = secret.trim().to_string();
            if !is_valid_secret(Some(&secret)) {
                return Err(
                    "секрет должен состоять ровно из 32 шестнадцатеричных символов".to_string(),
                );
            }
            secret
        }
    };

    let dc_redirects = if !dc_ip_given {
        BTreeMap::from([
            (2, DEFAULT_DC_IP.to_string()),
            (4, DEFAULT_DC_IP.to_string()),
        ])
    } else if dc_ip.iter().any(Option::is_none) {
        // A bare --dc-ip means "the user cleared the list", not "use the defaults".
        BTreeMap::new()
    } else {
        let mut redirects = BTreeMap::new();
        for entry in dc_ip.iter().flatten() {
            let Some((dc, ip)) = parse_dc_entry(entry) else {
                return Err(format!("неверный --dc-ip {entry}, ожидается DC:IP"));
            };
            redirects.insert(dc, ip);
        }
        redirects
    };

    Ok(EngineConfig {
        host,
        port,
        secret,
        dc_redirects,
        buffer_size: buf_kb.max(4).wrapping_mul(1024),
        pool_size: pool_size.max(0),
        fallback_cf_proxy: !no_cfproxy,
        cfproxy_user_domains: coerce_domains(&cf_domains),
        cfproxy_worker_domains: coerce_domains(&worker_domains),
        cfproxy_h2_media: !no_h2,
        disable_secure: no_secure,
        fake_tls_domain,
        proxy_protocol,
        force_test_dc,
        verbose,
        ping_interval_seconds: ping_interval,
        ping_jitter_seconds: ping_jitter,
        log_file,
        log_max_mb,
        log_backups,
    })
}

/// Maps the settings the application stores onto the engine configuration.
///
/// # Errors
///
/// Returns the `ToDictionary` error when two DC map entries repeat a DC number.
pub fn from_settings(
    settings: &ProxySettings,
    log_file: Option<&str>,
) -> Result<EngineConfig, String> {
    let mut dc_redirects = BTreeMap::new();
    for entry in &settings.dc_map {
        if dc_redirects
            .insert(entry.dc, entry.ip.to_string())
            .is_some()
        {
            return Err(format!(
                "An item with the same key has already been added. Key: {}",
                entry.dc
            ));
        }
    }

    Ok(EngineConfig {
        host: settings.host.to_string(),
        port: settings.port,
        secret: settings.secret.clone(),
        dc_redirects,
        buffer_size: settings.buf_kb.max(4).wrapping_mul(1024),
        pool_size: settings.pool_size.max(0),
        fallback_cf_proxy: settings.cf_proxy,
        cfproxy_user_domains: settings.cf_domains.clone(),
        cfproxy_worker_domains: settings.worker_domains.clone(),
        cfproxy_h2_media: settings.h2,
        disable_secure: settings.no_secure,
        fake_tls_domain: settings.fake_tls_domain.clone(),
        // Masked mode already hides the handshake; an idle pooled socket must
        // not give it away by staying silent.
        ping_interval_seconds: if settings.fake_tls_domain.is_empty() {
            0
        } else {
            DEFAULT_PING_INTERVAL_SECONDS
        },
        verbose: settings.verbose,
        log_file: log_file.map(str::to_string),
        log_max_mb: f64::from(settings.log_max_mb),
        log_backups: 1,
        ..EngineConfig::default()
    })
}

/// Splits each entry on `, ; space tab CR LF`, trims, drops empties and
/// case-insensitive duplicates, keeping the first spelling and order.
#[must_use]
pub fn coerce_domains<I, S>(raw: I) -> Vec<String>
where
    I: IntoIterator<Item = S>,
    S: AsRef<str>,
{
    let mut seen = std::collections::HashSet::new();
    let mut result = Vec::new();
    for entry in raw {
        for item in entry
            .as_ref()
            .split([',', ';', ' ', '\t', '\r', '\n'])
            .map(str::trim)
            .filter(|item| !item.is_empty())
        {
            if seen.insert(item.to_lowercase()) {
                result.push(item.to_string());
            }
        }
    }
    result
}

/// `int.TryParse(..., NumberStyles.Integer, InvariantCulture)`: optional sign,
/// ASCII digits only, must fit in `i32`.
fn parse_i32(text: &str) -> Option<i32> {
    let bytes = text.trim().as_bytes();
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

/// `double.TryParse(..., NumberStyles.Float, InvariantCulture)`.
fn parse_double(text: &str) -> Option<f64> {
    text.trim().parse::<f64>().ok()
}

/// One `DC:IP` entry; only IPv4 is accepted by the upstream parser.
fn parse_dc_entry(entry: &str) -> Option<(i32, String)> {
    let colon = entry.find(':').filter(|colon| *colon > 0)?;
    let dc = parse_i32(&entry[..colon])?;
    let ip = IpAddress::parse(&entry[colon + 1..])?;
    if !matches!(ip, IpAddress::V4(_)) {
        return None;
    }
    Some((dc, ip.to_string()))
}

#[cfg(test)]
mod tests {
    use super::*;

    const SECRET: &str = "0123456789abcdef0123456789abcdef";

    fn parse(args: &[&str]) -> EngineConfig {
        let owned: Vec<String> = args.iter().map(|arg| (*arg).to_string()).collect();
        try_parse(&owned, || panic!("a valid secret was supplied")).expect("valid arguments")
    }

    fn with_secret(args: &[&str]) -> Vec<String> {
        let mut all = vec!["--secret".to_string(), SECRET.to_string()];
        all.extend(args.iter().map(|arg| (*arg).to_string()));
        all
    }

    #[test]
    fn defaults_match_the_historical_engine() {
        let config = parse(&["--secret", SECRET]);
        assert_eq!(config.host, "127.0.0.1");
        assert_eq!(config.port, 1443);
        assert_eq!(config.secret, SECRET);
        assert_eq!(config.dc_redirects.len(), 2);
        assert_eq!(config.dc_redirects[&2], "149.154.167.220");
        assert_eq!(config.dc_redirects[&4], "149.154.167.220");
        assert!(config.fallback_cf_proxy);
        assert!(config.cfproxy_h2_media);
        assert!(config.h2_enabled());
        assert!(!config.disable_secure);
        assert_eq!(config.buffer_size, 256 * 1024);
        assert_eq!(config.pool_size, 4);
        assert_eq!(config.log_file, None);
        assert_eq!(config.log_max_mb, 5.0);
        assert_eq!(config.log_backups, 1);
    }

    #[test]
    fn a_missing_secret_comes_from_the_injected_generator() {
        let config = try_parse(&[], || SECRET.to_string()).expect("valid");
        assert_eq!(config.secret, SECRET);
    }

    #[test]
    fn bare_dc_ip_means_an_empty_list_not_the_defaults() {
        let config = parse(&["--secret", SECRET, "--dc-ip"]);
        assert!(config.dc_redirects.is_empty());
    }

    #[test]
    fn dc_ip_repeats_and_parses() {
        let config = parse(&[
            "--secret",
            SECRET,
            "--dc-ip",
            "2:149.154.167.1",
            "--dc-ip",
            "4:149.154.167.2",
        ]);
        assert_eq!(config.dc_redirects[&2], "149.154.167.1");
        assert_eq!(config.dc_redirects[&4], "149.154.167.2");
    }

    #[test]
    fn bad_dc_ip_is_an_error() {
        for value in ["2:not-an-ip", "x:149.154.167.1", "2:2001:db8::1"] {
            let mut args = with_secret(&["--dc-ip", value]);
            assert_eq!(
                try_parse(&args, String::new).unwrap_err(),
                format!("неверный --dc-ip {value}, ожидается DC:IP")
            );
            args.clear();
        }
    }

    #[test]
    fn secret_must_be_thirty_two_hex_characters() {
        assert!(try_parse(&["--secret".into(), "abc".into()], String::new).is_err());
        assert!(try_parse(&["--secret".into(), "z".repeat(32)], String::new).is_err());
        assert!(try_parse(&with_secret(&[]), String::new).is_ok());
        assert!(try_parse(&["--secret".into(), SECRET.to_uppercase()], String::new).is_ok());
    }

    #[test]
    fn flags_map_to_the_same_booleans_as_upstream() {
        let config = parse(&[
            "--secret",
            SECRET,
            "--no-cfproxy",
            "--no-h2",
            "--no-secure",
            "--fake-tls-domain",
            "example.test",
            "--force-test-dc",
            "--proxy-protocol",
            "-v",
        ]);
        assert!(!config.fallback_cf_proxy);
        assert!(!config.cfproxy_h2_media);
        assert!(!config.h2_enabled());
        assert!(config.disable_secure);
        assert_eq!(config.fake_tls_domain, "example.test");
        assert!(config.force_test_dc);
        assert!(config.proxy_protocol);
        assert!(config.verbose);
    }

    #[test]
    fn h2_is_off_where_the_cf_route_itself_is_unusable() {
        assert!(!parse(&["--secret", SECRET, "--no-secure"]).h2_enabled());
        assert!(!parse(&["--secret", SECRET, "--force-test-dc"]).h2_enabled());
        assert!(!parse(&["--secret", SECRET, "--no-cfproxy"]).h2_enabled());
    }

    #[test]
    fn domains_are_split_and_deduplicated_case_insensitively() {
        let config = parse(&[
            "--secret",
            SECRET,
            "--cfproxy-domain",
            "a.com,b.com",
            "--cfproxy-domain",
            "A.COM c.com",
            "--cfproxy-worker-domain",
            "w.dev;x.dev",
        ]);
        assert_eq!(config.cfproxy_user_domains, ["a.com", "b.com", "c.com"]);
        assert_eq!(config.cfproxy_worker_domains, ["w.dev", "x.dev"]);
    }

    #[test]
    fn numbers_and_log_flags_are_read() {
        let config = parse(&[
            "--secret",
            SECRET,
            "--host",
            "0.0.0.0",
            "--port",
            "2080",
            "--buf-kb",
            "64",
            "--pool-size",
            "0",
            "--log-file",
            "/tmp/p.log",
            "--log-max-mb",
            "9.5",
            "--log-backups",
            "3",
        ]);
        assert_eq!(config.host, "0.0.0.0");
        assert_eq!(config.port, 2080);
        assert_eq!(config.buffer_size, 64 * 1024);
        assert_eq!(config.pool_size, 0);
        assert_eq!(config.log_file.as_deref(), Some("/tmp/p.log"));
        assert_eq!(config.log_max_mb, 9.5);
        assert_eq!(config.log_backups, 3);
    }

    #[test]
    fn bad_arguments_are_errors() {
        for arg in ["--port", "--unknown", "stray"] {
            assert!(
                try_parse(&with_secret(&[arg]), String::new).is_err(),
                "{arg}"
            );
        }
    }

    #[test]
    fn a_host_that_is_not_an_address_and_a_bad_port_are_rejected() {
        for (flag, value) in [
            ("--host", "localhost"),
            ("--host", "127.0.0.1:1443"),
            ("--host", ""),
            ("--port", "0"),
            ("--port", "65536"),
            ("--port", "-1"),
        ] {
            assert!(
                try_parse(&with_secret(&[flag, value]), String::new).is_err(),
                "{flag} {value}"
            );
        }
    }

    #[test]
    fn any_ip_address_is_a_host_and_both_port_ends_are_accepted() {
        for host in ["127.0.0.1", "0.0.0.0", "::1"] {
            assert_eq!(parse(&["--secret", SECRET, "--host", host]).host, host);
        }
        assert_eq!(parse(&["--secret", SECRET, "--port", "1"]).port, 1);
        assert_eq!(parse(&["--secret", SECRET, "--port", "65535"]).port, 65535);
    }

    #[test]
    fn help_is_recognised() {
        assert_eq!(
            try_parse(&["--help".into()], String::new).unwrap_err(),
            "help"
        );
    }

    #[test]
    fn buffer_size_keeps_the_wrapping_multiplication() {
        let config = parse(&["--secret", SECRET, "--buf-kb", "2097152"]);
        assert_eq!(config.buffer_size, 2_097_152_i32.wrapping_mul(1024));
        let config = parse(&["--secret", SECRET, "--buf-kb", "1"]);
        assert_eq!(config.buffer_size, 4 * 1024);
    }

    #[test]
    fn coerce_domains_matches_the_csharp_split_and_dedup() {
        assert_eq!(
            coerce_domains(["a.com,b.com", "A.COM c.com;semi.com"]),
            ["a.com", "b.com", "c.com", "semi.com"]
        );
        assert_eq!(coerce_domains(["  ", "", "x\ty"]), ["x", "y"]);
        assert!(coerce_domains(Vec::<String>::new()).is_empty());
    }
}
