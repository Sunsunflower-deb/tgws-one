//! Runtime configuration of the Rust relay core.
//!
//! The C# engine receives an `EngineConfig` built either from the command line
//! or from `[proxy]` plus the Android `[android] poolidle`.  This module maps
//! both sources onto the same [`RelayConfig`] the sync relay runs with.
//!
//! The Cloudflare worker and proxy routes are plain configuration values, as
//! in `EngineConfig`: the routing itself lives in [`super::routes`].

use std::collections::BTreeMap;
use std::time::Duration;

use crate::config::{DEFAULT_PORT, EngineConfig};
use crate::net::telegram_network::{DC_DEFAULT_IPS, DC_TEST_IPS, WS_PATH, WS_PATH_TEST};
use crate::settings::{IniFile, ProxySettings, is_valid_secret};

/// The `[android] poolidle` default from `AndroidSettings.DefaultPoolIdle`.
pub const DEFAULT_POOL_IDLE_SECONDS: u64 = 180;
/// The default TLS port of Telegram's WebSocket hosts.
pub const DEFAULT_WS_PORT: u16 = 443;
/// The default port of the direct-TCP fallback.
pub const DEFAULT_TCP_FALLBACK_PORT: u16 = 443;
/// The default read timeout for the client handshake and upstream connect.
pub const DEFAULT_READ_TIMEOUT: Duration = Duration::from_secs(10);
/// The socket read timeout used by the relay poll loop.
pub const POLL_INTERVAL: Duration = Duration::from_millis(20);
/// The default receive/send buffer hint, matching `EngineConfig.BufferSize`.
pub const DEFAULT_BUFFER_SIZE: usize = 256 * 1024;

/// Where the engine writes its event lines.
#[derive(Debug, Clone, Copy, Default, PartialEq, Eq)]
pub enum LogLevel {
    /// No output.
    Silent,
    /// Only warnings and errors.
    Warnings,
    /// One line per connection/session event, like the C# engine log.
    #[default]
    Normal,
    /// Also protocol details.
    Verbose,
}

/// Everything the relay core needs.
///
/// `secret` is the decoded 16-byte `dd` secret.  For an `ee` link the fake-TLS
/// domain travels separately in [`RelayConfig::fake_tls_domain`].
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct RelayConfig {
    /// Listening address.
    pub host: String,
    /// Listening port.  Zero asks the OS for an ephemeral port.
    pub port: u16,
    /// Decoded obfuscated2 secret.
    pub secret: Vec<u8>,
    /// DC address overrides from `[proxy] dcmap` (`DC:IP`).
    pub dc_redirects: BTreeMap<i32, String>,
    /// Masking domain from `[proxy] faketls`; empty disables fake TLS.
    pub fake_tls_domain: String,
    /// Suffix of the `kws<dc>` WebSocket hosts.
    pub ws_domain_suffix: String,
    /// Production WebSocket path.
    pub ws_path: String,
    /// Test-DC WebSocket path.
    pub ws_path_test: String,
    /// Port the WebSocket hosts are dialled on (tests point it at loopback).
    pub ws_port: u16,
    /// Whether the WebSocket route is wrapped in TLS 1.3.
    pub ws_secure: bool,
    /// User CF-proxy base domains, in the configured order.
    pub cf_proxy_domains: Vec<String>,
    /// First CF-worker URL (`scheme://host[:port]/apiws`), if configured.
    pub worker_url: Option<String>,
    /// `--no-cfproxy` / `[proxy] cfproxy`: whether the CF-proxy route is allowed.
    pub fallback_cf_proxy: bool,
    /// `[android] poolidle`: how long an idle pool may stay warm.
    pub pool_idle_seconds: u64,
    /// `[proxy] poolsize`; zero disables the WebSocket route, as in C#.
    pub pool_size: i32,
    /// Port of the direct-TCP fallback.
    pub tcp_fallback_port: u16,
    /// Test seam for the fallback destination; `None` uses the DC table.
    pub tcp_fallback_host_override: Option<String>,
    /// `--force-test-dc`.
    pub force_test_dc: bool,
    /// Accept a PROXY protocol line before the handshake.
    pub proxy_protocol: bool,
    /// Timeout for the client handshake and upstream connect.
    pub read_timeout: Duration,
    /// Suggested socket buffer size.
    pub buffer_size: usize,
    /// Event log verbosity.
    pub log_level: LogLevel,
}

impl Default for RelayConfig {
    fn default() -> Self {
        Self {
            host: "127.0.0.1".to_string(),
            port: DEFAULT_PORT as u16,
            secret: Vec::new(),
            dc_redirects: BTreeMap::new(),
            fake_tls_domain: String::new(),
            ws_domain_suffix: "web.telegram.org".to_string(),
            ws_path: WS_PATH.to_string(),
            ws_path_test: WS_PATH_TEST.to_string(),
            ws_port: DEFAULT_WS_PORT,
            ws_secure: true,
            cf_proxy_domains: Vec::new(),
            worker_url: None,
            fallback_cf_proxy: true,
            pool_idle_seconds: DEFAULT_POOL_IDLE_SECONDS,
            pool_size: 4,
            tcp_fallback_port: DEFAULT_TCP_FALLBACK_PORT,
            tcp_fallback_host_override: None,
            force_test_dc: false,
            proxy_protocol: false,
            read_timeout: DEFAULT_READ_TIMEOUT,
            buffer_size: DEFAULT_BUFFER_SIZE,
            log_level: LogLevel::Normal,
        }
    }
}

impl RelayConfig {
    /// Builds the relay configuration from the C#-compatible command-line
    /// engine configuration.
    ///
    /// `EngineConfig` does not carry `poolidle`: the Android shell appends
    /// `[android] poolidle` on its own, so callers pass it here.
    ///
    /// # Errors
    ///
    /// Rejects a secret that is not exactly 32 hexadecimal characters.
    pub fn from_engine_config(
        config: &EngineConfig,
        pool_idle_seconds: u64,
    ) -> Result<Self, String> {
        Ok(Self {
            host: config.host.clone(),
            port: u16::try_from(config.port)
                .map_err(|_| format!("порт {} вне диапазона u16", config.port))?,
            secret: decode_secret(&config.secret)?,
            dc_redirects: config.dc_redirects.clone(),
            fake_tls_domain: config.fake_tls_domain.clone(),
            ws_domain_suffix: "web.telegram.org".to_string(),
            ws_path: WS_PATH.to_string(),
            ws_path_test: WS_PATH_TEST.to_string(),
            ws_port: DEFAULT_WS_PORT,
            ws_secure: !config.disable_secure,
            cf_proxy_domains: config.cfproxy_user_domains.clone(),
            worker_url: config.cfproxy_worker_domains.first().map(|domain| {
                let scheme = if config.disable_secure {
                    "http"
                } else {
                    "https"
                };
                format!("{scheme}://{domain}{WS_PATH}")
            }),
            fallback_cf_proxy: config.fallback_cf_proxy,
            pool_idle_seconds,
            pool_size: config.pool_size,
            tcp_fallback_port: DEFAULT_TCP_FALLBACK_PORT,
            tcp_fallback_host_override: None,
            force_test_dc: config.force_test_dc,
            proxy_protocol: config.proxy_protocol,
            read_timeout: DEFAULT_READ_TIMEOUT,
            buffer_size: usize::try_from(config.buffer_size).unwrap_or(DEFAULT_BUFFER_SIZE),
            log_level: if config.verbose {
                LogLevel::Verbose
            } else {
                LogLevel::Normal
            },
        })
    }

    /// Builds the relay configuration from validated `[proxy]` settings.
    ///
    /// # Errors
    ///
    /// Rejects an invalid or empty stored secret.
    pub fn from_proxy_settings(
        settings: &ProxySettings,
        pool_idle_seconds: u64,
    ) -> Result<Self, String> {
        let mut dc_redirects = BTreeMap::new();
        for entry in &settings.dc_map {
            dc_redirects.insert(entry.dc, entry.ip.to_string());
        }
        Ok(Self {
            host: settings.host.to_string(),
            port: u16::try_from(settings.port)
                .map_err(|_| format!("порт {} вне диапазона u16", settings.port))?,
            secret: decode_secret(&settings.secret)?,
            dc_redirects,
            fake_tls_domain: settings.fake_tls_domain.clone(),
            ws_domain_suffix: "web.telegram.org".to_string(),
            ws_path: WS_PATH.to_string(),
            ws_path_test: WS_PATH_TEST.to_string(),
            ws_port: DEFAULT_WS_PORT,
            ws_secure: !settings.no_secure,
            cf_proxy_domains: settings.cf_domains.clone(),
            worker_url: settings.worker_domains.first().map(|domain| {
                let scheme = if settings.no_secure { "http" } else { "https" };
                format!("{scheme}://{domain}{WS_PATH}")
            }),
            fallback_cf_proxy: settings.cf_proxy,
            pool_idle_seconds,
            pool_size: settings.pool_size.max(0),
            tcp_fallback_port: DEFAULT_TCP_FALLBACK_PORT,
            tcp_fallback_host_override: None,
            force_test_dc: false,
            proxy_protocol: false,
            read_timeout: DEFAULT_READ_TIMEOUT,
            buffer_size: usize::try_from(settings.buf_kb.max(4).wrapping_mul(1024))
                .unwrap_or(DEFAULT_BUFFER_SIZE),
            log_level: if settings.verbose {
                LogLevel::Verbose
            } else {
                LogLevel::Normal
            },
        })
    }

    /// Reads the whole engine configuration from the INI file the application
    /// stores: `ProxySettings::load` plus `[android] poolidle`.
    ///
    /// A missing or malformed `poolidle` falls back to
    /// [`DEFAULT_POOL_IDLE_SECONDS`], exactly like `AndroidSettingsStore`.
    ///
    /// # Errors
    ///
    /// Rejects an invalid or empty stored secret.
    pub fn from_ini(ini: &IniFile) -> Result<Self, String> {
        let settings = crate::settings::proxy_settings::load(ini);
        let pool_idle = read_pool_idle(ini);
        Self::from_proxy_settings(&settings, pool_idle)
    }

    /// The WebSocket hosts for a DC, in the C# order.
    #[must_use]
    pub fn ws_domains(&self, dc: i32, is_media: bool) -> Vec<String> {
        let dc = if dc == 203 { 2 } else { dc };
        if is_media {
            vec![
                format!("kws{dc}-1.{}", self.ws_domain_suffix),
                format!("kws{dc}.{}", self.ws_domain_suffix),
            ]
        } else {
            vec![format!("kws{dc}.{}", self.ws_domain_suffix)]
        }
    }

    /// The WebSocket request target for production or test DCs.
    #[must_use]
    pub fn ws_path_for(&self, is_test_dc: bool) -> &str {
        if is_test_dc {
            &self.ws_path_test
        } else {
            &self.ws_path
        }
    }

    /// The direct-TCP fallback destination: the override, otherwise the C#
    /// production/test table for the DC.
    #[must_use]
    pub fn fallback_host(&self, dc: i32, is_test_dc: bool) -> Option<String> {
        if let Some(host) = &self.tcp_fallback_host_override {
            return Some(host.clone());
        }
        let table = if is_test_dc {
            DC_TEST_IPS
        } else {
            DC_DEFAULT_IPS
        };
        table
            .iter()
            .find(|(entry, _)| *entry == dc)
            .map(|(_, ip)| (*ip).to_string())
    }
}

/// Decodes the 32-character `dd` secret (or the first part of an `ee` link).
///
/// # Errors
///
/// Returns a human-readable message for a missing, odd-length or non-hex value.
pub fn decode_secret(text: &str) -> Result<Vec<u8>, String> {
    let trimmed = text.trim();
    let hex = trimmed
        .strip_prefix("dd")
        .or_else(|| trimmed.strip_prefix("ee"))
        .unwrap_or(trimmed);
    if !is_valid_secret(Some(hex)) {
        return Err(format!(
            "секрет должен состоять ровно из 32 шестнадцатеричных символов (получилось «{trimmed}»)"
        ));
    }
    let mut bytes = Vec::with_capacity(16);
    for pair in hex.as_bytes().chunks_exact(2) {
        let high = hex_digit(pair[0])?;
        let low = hex_digit(pair[1])?;
        bytes.push((high << 4) | low);
    }
    Ok(bytes)
}

fn hex_digit(byte: u8) -> Result<u8, String> {
    match byte {
        b'0'..=b'9' => Ok(byte - b'0'),
        b'a'..=b'f' => Ok(byte - b'a' + 10),
        b'A'..=b'F' => Ok(byte - b'A' + 10),
        _ => Err(format!("не шестнадцатеричный символ: {}", char::from(byte))),
    }
}

/// `AndroidSettingsStore.Load` for `poolidle`: a non-negative integer, or the
/// default when the key is absent or malformed.
#[must_use]
pub fn read_pool_idle(ini: &IniFile) -> u64 {
    ini.get("android", "poolidle")
        .and_then(|raw| raw.trim().parse::<i64>().ok())
        .filter(|value| *value >= 0)
        .map_or(DEFAULT_POOL_IDLE_SECONDS, |value| value as u64)
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::settings::{IniFile, proxy_settings};

    const SECRET: &str = "0123456789abcdef0123456789abcdef";

    fn settings() -> ProxySettings {
        let ini = IniFile::parse(&format!(
            "[app]\nsecret={SECRET}\n[proxy]\nhost=127.0.0.1\nport=1555\n\
             dcmap=2:149.154.167.220,4:149.154.167.221\nfaketls=www.example.com\n\
             cfdomains=proxy.example\nworkerdomains=worker.example\npoolsize=3\nbufkb=64\n"
        ));
        proxy_settings::load(&ini)
    }

    #[test]
    fn secret_decoding_accepts_dd_ee_and_plain_hex() {
        assert_eq!(decode_secret(SECRET).unwrap().len(), 16);
        assert_eq!(decode_secret(&format!("dd{SECRET}")).unwrap().len(), 16);
        assert_eq!(decode_secret(&format!("ee{SECRET}")).unwrap().len(), 16);
        assert_eq!(
            decode_secret("00ff102030405060708090a0b0c0d0e0").unwrap(),
            [
                0x00, 0xff, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x80, 0x90, 0xa0, 0xb0, 0xc0,
                0xd0, 0xe0
            ]
        );
    }

    #[test]
    fn secret_decoding_rejects_bad_values() {
        for value in ["", "dd", "xyz", "0123", &format!("{SECRET}0")] {
            assert!(decode_secret(value).is_err(), "{value:?}");
        }
    }

    #[test]
    fn proxy_settings_map_onto_the_relay_config() {
        let config = RelayConfig::from_proxy_settings(&settings(), 60).unwrap();
        assert_eq!(config.host, "127.0.0.1");
        assert_eq!(config.port, 1555);
        assert_eq!(config.secret.len(), 16);
        assert_eq!(config.dc_redirects[&2], "149.154.167.220");
        assert_eq!(config.fake_tls_domain, "www.example.com");
        assert_eq!(config.cf_proxy_domains, ["proxy.example"]);
        assert_eq!(
            config.worker_url.as_deref(),
            Some("https://worker.example/apiws")
        );
        assert!(config.fallback_cf_proxy);
        assert_eq!(config.pool_idle_seconds, 60);
        assert_eq!(config.pool_size, 3);
        assert_eq!(config.buffer_size, 64 * 1024);
    }

    #[test]
    fn ini_pool_idle_follows_android_settings() {
        let make = |idle: &str| {
            IniFile::parse(&format!(
                "[app]\nsecret={SECRET}\n[android]\npoolidle={idle}\n"
            ))
        };
        assert_eq!(read_pool_idle(&make("45")), 45);
        assert_eq!(read_pool_idle(&make("0")), 0);
        assert_eq!(read_pool_idle(&make("-3")), DEFAULT_POOL_IDLE_SECONDS);
        assert_eq!(read_pool_idle(&make("nope")), DEFAULT_POOL_IDLE_SECONDS);
        assert_eq!(
            read_pool_idle(&IniFile::parse("")),
            DEFAULT_POOL_IDLE_SECONDS
        );
    }

    #[test]
    fn ini_requires_a_valid_secret() {
        let ini = IniFile::parse("[app]\nsecret=short\n");
        assert!(RelayConfig::from_ini(&ini).is_err());
    }

    #[test]
    fn ws_hosts_match_the_csharp_network_table() {
        let config = RelayConfig::default();
        assert_eq!(config.ws_domains(1, false), ["kws1.web.telegram.org"]);
        assert_eq!(
            config.ws_domains(2, true),
            ["kws2-1.web.telegram.org", "kws2.web.telegram.org"]
        );
        assert_eq!(config.ws_domains(203, false), ["kws2.web.telegram.org"]);
        assert_eq!(config.ws_path_for(false), "/apiws");
        assert_eq!(config.ws_path_for(true), "/apiws_test");
    }

    #[test]
    fn fallback_host_prefers_the_override_then_the_tables() {
        let mut config = RelayConfig::default();
        assert_eq!(
            config.fallback_host(2, false).as_deref(),
            Some("149.154.167.51")
        );
        assert_eq!(
            config.fallback_host(2, true).as_deref(),
            Some("149.154.167.40")
        );
        assert_eq!(config.fallback_host(99, false), None);
        config.tcp_fallback_host_override = Some("127.0.0.1".to_string());
        assert_eq!(config.fallback_host(2, false).as_deref(), Some("127.0.0.1"));
    }
}
