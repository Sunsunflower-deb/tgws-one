//! Which built-in strategies the `auto` rotation walks, per operator or relay.
//!
//! Ported from `src/TgwsOne.Core/Zapret/ProviderStrategies.cs`.  The set is
//! administrator data: `<provider>.rotation` in the service's strategies
//! folder, one strategy ID per line, `#` starting a comment.  The universal set
//! is used whenever the provider is unknown, its file is unreadable, or it
//! names no usable strategy: a mistyped provider must never leave the machine
//! without a working bypass.

use std::collections::HashSet;
use std::path::Path;

use super::{built_in, read_all_lines};

/// The default provider: the universal rotation every other name falls back to.
pub const UNIVERSAL: &str = "universal";

/// The suffix of a provider's rotation file.
pub const EXTENSION: &str = ".rotation";

/// Lower-case letters, digits, `.`, `_`, `-`; at most 32 characters.
#[must_use]
pub fn is_valid_provider(name: Option<&str>) -> bool {
    name.is_some_and(valid_provider)
}

/// The rotation for `provider`: its own file when it has a usable one, the
/// universal set otherwise.
#[must_use]
pub fn rotation(strategies_dir: &Path, provider: Option<&str>) -> Vec<String> {
    match provider {
        Some(name) if valid_provider(name) && name != UNIVERSAL => {
            let path = strategies_dir.join(format!("{name}{EXTENSION}"));
            match std::fs::read_to_string(&path) {
                Ok(text) => expand(read_all_lines(&text)),
                Err(_) => built_in_rotation(),
            }
        }
        _ => built_in_rotation(),
    }
}

/// Keeps the built-in strategy IDs in file order.  `auto` is dropped (it cannot
/// rotate through itself), unknown names are dropped, repeats are collapsed.
/// An empty result means "use the universal set".
#[must_use]
pub fn expand<I, S>(lines: I) -> Vec<String>
where
    I: IntoIterator<Item = S>,
    S: AsRef<str>,
{
    let mut seen: HashSet<String> = HashSet::new();
    let mut rotation = Vec::new();
    for raw in lines {
        let line = raw.as_ref().trim();
        if line.is_empty() || line.starts_with('#') {
            continue;
        }
        if line == built_in::AUTO || !built_in::is_built_in(line) {
            continue;
        }
        if seen.insert(line.to_string()) {
            rotation.push(line.to_string());
        }
    }
    if rotation.is_empty() {
        built_in_rotation()
    } else {
        rotation
    }
}

/// Providers that have their own rotation file, by name, so the application can
/// offer them.  The universal set is always first: it is what any other name
/// resolves to.
#[must_use]
pub fn providers(strategies_dir: &Path) -> Vec<String> {
    let mut names = Vec::new();
    if let Ok(entries) = std::fs::read_dir(strategies_dir) {
        for entry in entries.flatten() {
            let path = entry.path();
            if !path.is_file() {
                continue;
            }
            let Some(file) = entry.file_name().to_str().map(str::to_string) else {
                continue;
            };
            let Some(name) = file.strip_suffix(EXTENSION) else {
                continue;
            };
            if valid_provider(name) && name != UNIVERSAL {
                names.push(name.to_string());
            }
        }
    }
    // `StringComparer.Ordinal` is byte order, the same as `String`'s `Ord`.
    names.sort_unstable();
    names.insert(0, UNIVERSAL.to_string());
    names
}

fn built_in_rotation() -> Vec<String> {
    built_in::ROTATION
        .iter()
        .map(|s| (*s).to_string())
        .collect()
}

/// `^[a-z0-9][a-z0-9._-]{0,31}\z`: 1..32 ASCII chars, no upper case, no leading
/// punctuation.
fn valid_provider(name: &str) -> bool {
    let bytes = name.as_bytes();
    if bytes.is_empty() || bytes.len() > 32 {
        return false;
    }
    if !(bytes[0].is_ascii_lowercase() || bytes[0].is_ascii_digit()) {
        return false;
    }
    bytes[1..].iter().all(|byte| {
        byte.is_ascii_lowercase() || byte.is_ascii_digit() || matches!(byte, b'.' | b'_' | b'-')
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn valid_provider_bounds_and_charset() {
        assert!(is_valid_provider(Some("universal")));
        assert!(is_valid_provider(Some("a.b_c-d")));
        assert!(is_valid_provider(Some("a")));
        assert!(is_valid_provider(Some("0123456789")));
        assert!(!is_valid_provider(None));
        assert!(!is_valid_provider(Some("MTN")));
        assert!(!is_valid_provider(Some("-bad")));
        assert!(!is_valid_provider(Some(".lead")));
        assert!(!is_valid_provider(Some("bad name")));
    }

    #[test]
    fn valid_provider_length_is_one_to_thirty_two() {
        let thirty_two = "a".repeat(32);
        let thirty_three = "a".repeat(33);
        assert!(is_valid_provider(Some(&thirty_two)));
        assert!(!is_valid_provider(Some(&thirty_three)));
        assert!(!is_valid_provider(Some("")));
    }

    #[test]
    fn expand_drops_auto_comments_unknowns_and_repeats() {
        let lines = [
            "split",
            "auto",
            "bogus",
            "split",
            "# comment",
            "",
            " aggressive ",
        ];
        assert_eq!(expand(lines), ["split", "aggressive"]);
    }

    #[test]
    fn expand_falls_back_to_the_universal_rotation() {
        let universal: Vec<String> = built_in::ROTATION
            .iter()
            .map(|s| (*s).to_string())
            .collect();
        assert_eq!(expand(["auto"]), universal);
        assert_eq!(expand(["#only", "comments"]), universal);
        assert_eq!(expand(Vec::<String>::new()), universal);
    }

    #[test]
    fn expand_keeps_all_four_built_ins_in_file_order() {
        assert_eq!(
            expand(["aggressive", "split-stealth", "fake-tls", "split"]),
            ["aggressive", "split-stealth", "fake-tls", "split"]
        );
    }

    #[test]
    fn rotation_without_a_usable_provider_is_the_universal_set() {
        let dir = Path::new("/nonexistent/strategies");
        let universal: Vec<String> = built_in::ROTATION
            .iter()
            .map(|s| (*s).to_string())
            .collect();
        assert_eq!(rotation(dir, None), universal);
        assert_eq!(rotation(dir, Some(UNIVERSAL)), universal);
        assert_eq!(rotation(dir, Some("Bad")), universal);
        assert_eq!(rotation(dir, Some("mtn")), universal);
    }

    #[test]
    fn providers_without_a_folder_is_just_universal() {
        assert_eq!(
            providers(Path::new("/nonexistent/strategies")),
            ["universal"]
        );
    }
}
