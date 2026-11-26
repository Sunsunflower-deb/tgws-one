//! The INI file in the 1.x format: `[section]`, `key=value`, `;`/`#` comments.
//!
//! Ported from `src/TgwsOne.Shared/IniFile.cs`.  Only the parse and the
//! serializer are here; `Load`/`Save` (file I/O, a temp file and a rename) stay
//! in the application shell.  Section and key names are case-insensitive and
//! stored lower-case, key order and empty values survive a round trip, and
//! comments do not.

use super::to_lower_invariant;

#[derive(Debug, Clone, PartialEq, Eq)]
struct Entry {
    key: String,
    value: String,
}

#[derive(Debug, Clone, PartialEq, Eq)]
struct Section {
    name: String,
    items: Vec<Entry>,
}

/// An ordered INI document, the same shape `IniFile` keeps in memory.
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct IniFile {
    sections: Vec<Section>,
}

impl IniFile {
    /// Parses the file contents: keys before the first header belong to `app`,
    /// lines without a `key=value` pair and comments are skipped, a repeated
    /// `[section]` merges into the first one.
    #[must_use]
    pub fn parse(text: &str) -> Self {
        let mut ini = Self::default();
        let mut section = String::from("app");
        for raw in read_all_lines(text) {
            let line = raw.trim();
            if line.is_empty() || line.starts_with(';') || line.starts_with('#') {
                continue;
            }
            let bytes = line.as_bytes();
            if bytes[0] == b'[' && bytes[bytes.len() - 1] == b']' {
                section = to_lower_invariant(line[1..line.len() - 1].trim());
                continue;
            }
            let Some(eq) = line.find('=') else { continue };
            if eq == 0 {
                continue;
            }
            ini.put(
                section.clone(),
                to_lower_invariant(line[..eq].trim()),
                line[eq + 1..].trim().to_string(),
            );
        }
        ini
    }

    /// The raw value, including an empty one, or `None` when the key is absent.
    #[must_use]
    pub fn get(&self, section: &str, key: &str) -> Option<&str> {
        let section = to_lower_invariant(section);
        let key = to_lower_invariant(key);
        self.sections
            .iter()
            .find(|s| s.name == section)?
            .items
            .iter()
            .find(|e| e.key == key)
            .map(|e| e.value.as_str())
    }

    /// Sets a value: a new key goes to the end of its section, a new section to
    /// the end of the file.  A value with a line break is rejected.
    ///
    /// # Errors
    ///
    /// Returns the `ArgumentException` text of the original for a multi-line value.
    pub fn set(&mut self, section: &str, key: &str, value: &str) -> Result<(), String> {
        if value.contains('\n') || value.contains('\r') {
            return Err("INI value must be a single line".to_string());
        }
        self.put(
            to_lower_invariant(section),
            to_lower_invariant(key),
            value.to_string(),
        );
        Ok(())
    }

    /// Every `(section, key, value)` snapshot in file order.
    #[must_use]
    pub fn entries(&self) -> Vec<(&str, &str, &str)> {
        self.sections
            .iter()
            .flat_map(|section| {
                section.items.iter().map(move |entry| {
                    (
                        section.name.as_str(),
                        entry.key.as_str(),
                        entry.value.as_str(),
                    )
                })
            })
            .collect()
    }

    /// The file contents: one blank line between non-empty sections, empty
    /// sections dropped, `\n` after every line, no BOM.
    #[must_use]
    pub fn serialize(&self) -> String {
        let mut out = String::new();
        let mut first = true;
        for section in &self.sections {
            if section.items.is_empty() {
                continue;
            }
            if !first {
                out.push('\n');
            }
            first = false;
            out.push('[');
            out.push_str(&section.name);
            out.push_str("]\n");
            for entry in &section.items {
                out.push_str(&entry.key);
                out.push('=');
                out.push_str(&entry.value);
                out.push('\n');
            }
        }
        out
    }

    fn put(&mut self, section: String, key: String, value: String) {
        let index = match self.sections.iter().position(|s| s.name == section) {
            Some(index) => index,
            None => {
                self.sections.push(Section {
                    name: section,
                    items: Vec::new(),
                });
                self.sections.len() - 1
            }
        };
        let items = &mut self.sections[index].items;
        if let Some(entry) = items.iter_mut().find(|e| e.key == key) {
            entry.value = value;
        } else {
            items.push(Entry { key, value });
        }
    }
}

/// `File.ReadAllLines`: `\r\n`, `\r` and `\n` end a line; a trailing terminator
/// does not add an extra empty line.
fn read_all_lines(text: &str) -> Vec<&str> {
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

#[cfg(test)]
mod tests {
    use super::*;

    fn entries(ini: &IniFile) -> Vec<(String, String, String)> {
        ini.entries()
            .into_iter()
            .map(|(s, k, v)| (s.to_string(), k.to_string(), v.to_string()))
            .collect()
    }

    #[test]
    fn comments_junk_and_orphan_keys_follow_the_csharp_rules() {
        let ini = IniFile::parse(
            "; comment\n# another\n\norphan=first\n[Proxy]\n  Port = 99  \nno equals here\n=novalue\n[broken\nhost=h\n",
        );
        assert_eq!(ini.get("app", "orphan"), Some("first"));
        assert_eq!(ini.get("proxy", "port"), Some("99"));
        assert_eq!(ini.get("proxy", "host"), Some("h"));
        assert_eq!(entries(&ini).len(), 3);
    }

    #[test]
    fn keys_are_case_insensitive_and_order_is_kept() {
        let ini = IniFile::parse("[Proxy]\nPort=99\nHost=h\n[zapret]\nStrategy=fake-tls\n");
        assert_eq!(ini.get("PROXY", "port"), Some("99"));
        assert_eq!(
            entries(&ini),
            [
                ("proxy".into(), "port".into(), "99".into()),
                ("proxy".into(), "host".into(), "h".into()),
                ("zapret".into(), "strategy".into(), "fake-tls".into()),
            ]
        );
    }

    #[test]
    fn a_repeated_section_merges_into_the_first_and_a_repeated_key_wins_last() {
        let ini = IniFile::parse("[proxy]\nport=1\n[other]\nx=1\n[proxy]\nport=2\nhost=h\n");
        assert_eq!(ini.get("proxy", "port"), Some("2"));
        assert_eq!(
            entries(&ini),
            [
                ("proxy".into(), "port".into(), "2".into()),
                ("proxy".into(), "host".into(), "h".into()),
                ("other".into(), "x".into(), "1".into()),
            ]
        );
    }

    #[test]
    fn empty_brackets_make_an_empty_section_name_and_a_lone_bracket_is_not_a_header() {
        let ini = IniFile::parse("[]\nk=v\n[\nz=1\n");
        assert_eq!(ini.get("", "k"), Some("v"));
        assert_eq!(ini.get("", "z"), Some("1"));
        assert_eq!(ini.get("app", "z"), None);
    }

    #[test]
    fn the_first_equals_splits_and_values_are_trimmed_but_empty_ones_survive() {
        let ini = IniFile::parse("[t]\na=b=c\nx=\ny= a b \n");
        assert_eq!(ini.get("t", "a"), Some("b=c"));
        assert_eq!(ini.get("t", "x"), Some(""));
        assert_eq!(ini.get("t", "y"), Some("a b"));
    }

    #[test]
    fn all_three_line_endings_are_understood() {
        let ini = IniFile::parse("[t]\r\na=1\r\n[t2]\r\nb=2\r");
        assert_eq!(ini.get("t", "a"), Some("1"));
        assert_eq!(ini.get("t2", "b"), Some("2"));
    }

    #[test]
    fn non_ascii_names_use_simple_invariant_lowercase() {
        let ini = IniFile::parse("[Прокси]\n Ключ = Значение \n[İ]\nk=v\n");
        assert_eq!(ini.get("прокси", "ключ"), Some("Значение"));
        assert_eq!(ini.get("İ", "k"), Some("v"));
    }

    #[test]
    fn serialization_drops_empty_sections_and_puts_one_blank_line_between_sections() {
        let ini = IniFile::parse("[a]\nempty=\n[empty]\n[b]\nx=1\n");
        assert_eq!(ini.serialize(), "[a]\nempty=\n\n[b]\nx=1\n");
        assert_eq!(IniFile::default().serialize(), "");
    }

    #[test]
    fn set_appends_updates_and_creates_sections_in_the_csharp_order() {
        let mut ini =
            IniFile::parse("[proxy]\nhost=127.0.0.1\nport=1443\n[zapret]\nstrategy=fake-tls\n");
        ini.set("proxy", "extra", "x").unwrap();
        ini.set("newsec", "k", "v").unwrap();
        ini.set("Proxy", "PORT", "2000").unwrap();
        assert_eq!(
            ini.serialize(),
            "[proxy]\nhost=127.0.0.1\nport=2000\nextra=x\n\n[zapret]\nstrategy=fake-tls\n\n[newsec]\nk=v\n"
        );
    }

    #[test]
    fn set_rejects_a_multiline_value() {
        let mut ini = IniFile::default();
        assert_eq!(
            ini.set("app", "secret", "a\n[proxy]\nport=1"),
            Err("INI value must be a single line".to_string())
        );
    }
}
