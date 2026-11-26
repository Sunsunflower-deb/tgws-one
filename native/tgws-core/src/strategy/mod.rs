//! Pure strategy data and helpers ported from `TgwsOne.Core.Zapret`.
//!
//! The modules here only compute bytes, strings and tables: no sockets, no
//! async and no file writes.  File reads are limited to the small strategy
//! folders the C# application keeps; they are pure enough to test offline.

pub mod auto_state;
pub mod built_in;
pub mod cf_proxy_hosts;
pub mod custom;
pub mod providers;

pub use auto_state::{AutoHost, FILE_NAME as AUTO_STATE_FILE_NAME, read as read_auto_state};
pub use built_in::{Blob, DesyncPart};
pub use cf_proxy_hosts::{DCS as CF_PROXY_DCS, expand as expand_cf_proxy_hosts};
pub use custom::is_valid_name as is_valid_custom_name;
pub use providers::{
    EXTENSION as PROVIDER_EXTENSION, UNIVERSAL as UNIVERSAL_PROVIDER, expand as expand_rotation,
    is_valid_provider, providers as list_providers, rotation as provider_rotation,
};

/// `File.ReadAllLines`: a line ends at `\r\n`, `\r` or `\n`, and a trailing
/// terminator does not add an extra empty line.
pub(crate) fn read_all_lines(text: &str) -> Vec<&str> {
    let bytes = text.as_bytes();
    let mut lines = Vec::new();
    let mut start = 0;
    let mut i = 0;
    while i < bytes.len() {
        match bytes[i] {
            b'\r' => {
                lines.push(&text[start..i]);
                i += if bytes.get(i + 1) == Some(&b'\n') {
                    2
                } else {
                    1
                };
                start = i;
            }
            b'\n' => {
                lines.push(&text[start..i]);
                i += 1;
                start = i;
            }
            _ => i += 1,
        }
    }
    if start < bytes.len() {
        lines.push(&text[start..]);
    }
    lines
}

/// `string.ToLowerInvariant`: per-character simple lowercase.
///
/// Unlike Rust's `str::to_lowercase` this keeps a character whose full
/// lowercase mapping expands (for example `İ` -> `i` + combining dot) and
/// never applies the context-sensitive Greek final sigma.
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
