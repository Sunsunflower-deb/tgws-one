//! Settings and link formats ported from `TgwsOne.Shared` and `TgwsOne.Core`.
//!
//! These modules only parse and produce text: the INI file, the proxy form and
//! its field parsers, the exclusions lists, and the `tg://proxy` link.  Reading
//! and writing files, generating secrets and resolving a wildcard listen address
//! through a socket stay in the platform shell.

pub mod exclusions;
pub mod ini;
pub mod proxy_settings;
pub mod telegram_link;

pub use exclusions::{MAX_ENTRIES, normalize_hosts, normalize_ips};
pub use ini::IniFile;
pub use proxy_settings::{
    DcEntry, FieldError, IpAddress, ProxyForm, ProxySettings, is_valid_secret, load, parse,
    read_form, split_list,
};
pub use telegram_link::{build_dd, build_ee, connect_address, escape_data_string};

/// `string.ToLowerInvariant`: per-character simple lowercase.
///
/// Unlike Rust's `str::to_lowercase` this keeps a character whose full
/// lowercase mapping expands (for example `İ`) as it was, and never applies
/// the context-sensitive Greek final sigma.
pub(crate) fn to_lower_invariant(s: &str) -> String {
    s.chars()
        .map(|c| {
            let mut lower = c.to_lowercase();
            match (lower.next(), lower.next()) {
                (Some(one), None) => one,
                _ => c,
            }
        })
        .collect()
}
