//! Reads `autostate.tsv`, the file `zapret2/tgws-state.lua` keeps up to date
//! inside nfqws2's writable folder.
//!
//! Ported from `src/TgwsOne.Core/Zapret/AutoState.cs`.  The file is the only
//! truth: a missing or unreadable one simply means there is nothing to show.

use std::collections::BTreeMap;
use std::path::Path;

use super::built_in;
use super::read_all_lines;

/// The same name the Lua script writes.
pub const FILE_NAME: &str = "autostate.tsv";

/// What `auto` currently does for one host: the built-in strategy it settled on
/// and how many failures it has counted.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct AutoHost {
    /// The host name, as written in the first column.
    pub host: String,
    /// The built-in strategy ID the rotation number resolved to.
    pub strategy: String,
    /// The failure counter from the third column.
    pub failures: i32,
}

/// Lines of `host TAB strategy number TAB failures`; the number (1..4) becomes
/// the ID in [`built_in::ROTATION`].  Lines that don't fit are skipped.  Sorted
/// by host; a host listed twice keeps its first line.
///
/// # Errors
///
/// Returns an error only where the C# code throws instead of returning empty:
/// an empty path, or a path with an embedded NUL.  A missing or unreadable file
/// yields an empty list.
pub fn read(tsv_file: impl AsRef<Path>) -> Result<Vec<AutoHost>, String> {
    let path = tsv_file.as_ref();
    if path.as_os_str().is_empty() {
        return Err("The value cannot be an empty string. (Parameter 'path')".to_string());
    }
    if path.to_string_lossy().contains('\0') {
        return Err("The path contains a NUL character.".to_string());
    }

    let Ok(bytes) = std::fs::read(path) else {
        return Ok(Vec::new());
    };
    let text = String::from_utf8_lossy(&bytes);
    Ok(parse_lines(&text))
}

fn parse_lines(text: &str) -> Vec<AutoHost> {
    let mut hosts: BTreeMap<String, AutoHost> = BTreeMap::new();
    for line in read_all_lines(text) {
        let line = line.trim_end_matches('\r');
        let parts: Vec<&str> = line.split('\t').collect();
        if parts.len() != 3 || parts[0].is_empty() {
            continue;
        }
        let Some(number) = parse_digits(parts[1]) else {
            continue;
        };
        let Ok(index) = usize::try_from(number) else {
            continue;
        };
        if !(1..=built_in::ROTATION.len()).contains(&index) {
            continue;
        }
        let Some(failures) = parse_digits(parts[2]) else {
            continue;
        };
        hosts
            .entry(parts[0].to_string())
            .or_insert_with(|| AutoHost {
                host: parts[0].to_string(),
                strategy: built_in::ROTATION[index - 1].to_string(),
                failures,
            });
    }
    hosts.into_values().collect()
}

/// `int.TryParse(s, NumberStyles.None, InvariantCulture)`: ASCII digits only,
/// with no sign or surrounding space.
fn parse_digits(s: &str) -> Option<i32> {
    if s.is_empty() || !s.bytes().all(|byte| byte.is_ascii_digit()) {
        return None;
    }
    let mut value: i32 = 0;
    for byte in s.bytes() {
        value = value.checked_mul(10)?.checked_add(i32::from(byte - b'0'))?;
    }
    Some(value)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn sorted_by_host_and_first_duplicate_wins() {
        let text = "a.example\t1\t2\na.example\t2\t5\nz.example\t4\t0\nb.example\t2\t3\n";
        assert_eq!(
            parse_lines(text),
            [
                AutoHost {
                    host: "a.example".into(),
                    strategy: "fake-tls".into(),
                    failures: 2,
                },
                AutoHost {
                    host: "b.example".into(),
                    strategy: "split".into(),
                    failures: 3,
                },
                AutoHost {
                    host: "z.example".into(),
                    strategy: "aggressive".into(),
                    failures: 0,
                },
            ]
        );
    }

    #[test]
    fn number_styles_none_rejects_signs_and_spaces() {
        let text = "b\t0\t1\nb\t5\t1\nb\t+1\t1\nb\t 1\t1\nb\t-1\t1\nb\t01\t3\nok\t2\t4\n";
        assert_eq!(
            parse_lines(text),
            [
                AutoHost {
                    host: "b".into(),
                    strategy: "fake-tls".into(),
                    failures: 3,
                },
                AutoHost {
                    host: "ok".into(),
                    strategy: "split".into(),
                    failures: 4,
                },
            ]
        );
    }

    #[test]
    fn malformed_rows_and_overflows_are_skipped() {
        let text = "\t1\t0\nc\t1\t+2\nd\t1\t-3\ne\t1\nf\t1\t2\textra\ng\t3\t9\nh\t99999999999\t0\ni\t1\t99999999999\n";
        assert_eq!(
            parse_lines(text),
            [AutoHost {
                host: "g".into(),
                strategy: "split-stealth".into(),
                failures: 9,
            }]
        );
    }

    #[test]
    fn crlf_and_lone_cr_are_both_line_breaks() {
        assert_eq!(
            parse_lines("crlf\t1\t1\r\nsecond\t2\t2\r\n"),
            [
                AutoHost {
                    host: "crlf".into(),
                    strategy: "fake-tls".into(),
                    failures: 1,
                },
                AutoHost {
                    host: "second".into(),
                    strategy: "split".into(),
                    failures: 2,
                },
            ]
        );
        assert_eq!(
            parse_lines("lone\rcr\t3\t3\rlast\t4\t4\r"),
            [
                AutoHost {
                    host: "cr".into(),
                    strategy: "split-stealth".into(),
                    failures: 3,
                },
                AutoHost {
                    host: "last".into(),
                    strategy: "aggressive".into(),
                    failures: 4,
                },
            ]
        );
    }

    #[test]
    fn empty_input_has_no_hosts() {
        assert_eq!(parse_lines(""), []);
    }

    #[test]
    fn unicode_hosts_sort_by_ordinal_bytes() {
        assert_eq!(
            parse_lines("юникод\t2\t1\nÜnïcode\t1\t0\n"),
            [
                AutoHost {
                    host: "Ünïcode".into(),
                    strategy: "fake-tls".into(),
                    failures: 0,
                },
                AutoHost {
                    host: "юникод".into(),
                    strategy: "split".into(),
                    failures: 1,
                },
            ]
        );
    }

    #[test]
    fn missing_file_reads_as_empty() {
        assert_eq!(read("/nonexistent/autostate.tsv"), Ok(Vec::new()));
    }

    #[test]
    fn empty_path_is_an_error_like_the_csharp_file_api() {
        assert_eq!(
            read(""),
            Err("The value cannot be an empty string. (Parameter 'path')".to_string())
        );
    }

    #[test]
    fn read_decodes_a_utf8_file() {
        let path = std::env::temp_dir().join(format!(
            "tgws-core-autostate-{}-read.tsv",
            std::process::id()
        ));
        std::fs::write(&path, "хост\t1\t7\n").expect("write fixture");
        let hosts = read(&path).expect("read fixture");
        let _ = std::fs::remove_file(&path);
        assert_eq!(
            hosts,
            [AutoHost {
                host: "хост".into(),
                strategy: "fake-tls".into(),
                failures: 7,
            }]
        );
    }
}
