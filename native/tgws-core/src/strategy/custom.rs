//! Names of administrator-written strategies.
//!
//! Ported from `src/TgwsOne.Core/Zapret/CustomStrategies.cs`.  A custom
//! strategy is `<dir>/<name>.args`, one argument per line.  Only the name check
//! is ported in this wave; `Parse` that validates the options is a later port.

/// `^[a-z0-9-]{1,32}\z`: 1..32 ASCII lower-case letters, digits or `-`.
#[must_use]
pub fn is_valid_name(name: &str) -> bool {
    let bytes = name.as_bytes();
    (1..=32).contains(&bytes.len())
        && bytes
            .iter()
            .all(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit() || *byte == b'-')
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn accepts_ascii_lowercase_digits_and_hyphens() {
        assert!(is_valid_name("split-stealth"));
        assert!(is_valid_name("a"));
        assert!(is_valid_name("a-b-c"));
        assert!(is_valid_name("123"));
        assert!(is_valid_name("-lead"));
        assert!(is_valid_name("trail-"));
        assert!(is_valid_name("-"));
    }

    #[test]
    fn rejects_empty_names() {
        assert!(!is_valid_name(""));
    }

    #[test]
    fn rejects_uppercase_and_other_punctuation() {
        assert!(!is_valid_name("UPPER"));
        assert!(!is_valid_name("under_score"));
        assert!(!is_valid_name("dot.name"));
        assert!(!is_valid_name("has space"));
        assert!(!is_valid_name("slash/name"));
    }

    #[test]
    fn rejects_non_ascii_names() {
        assert!(!is_valid_name("кириллица"));
        assert!(!is_valid_name("café"));
        assert!(!is_valid_name("ſ"));
    }

    #[test]
    fn length_is_one_to_thirty_two() {
        assert!(is_valid_name(&"a".repeat(32)));
        assert!(!is_valid_name(&"a".repeat(33)));
    }

    #[test]
    fn regex_anchors_reject_a_trailing_newline() {
        assert!(!is_valid_name("split\n"));
        assert!(!is_valid_name("\nsplit"));
    }
}
