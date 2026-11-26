//! What the service remembers across restarts.
//!
//! Ported from `src/TgwsOne.Core/Zapret/ServiceState.cs`.  Only the pure
//! record <-> INI conversion is here: loading a file and saving it (a temp file
//! and a rename) stay in the platform shell.

use crate::settings::{IniFile, to_lower_invariant};
use crate::strategy::built_in;
use crate::strategy::providers::{UNIVERSAL, is_valid_provider};

use super::zapret_args::{ZapretScope, ZapretScopes};

const SECTION: &str = "zapret";

/// Whether the bypass is on, which strategy it uses and what it applies to.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ServiceState {
    pub enabled: bool,
    pub strategy: String,
    pub scope: ZapretScope,
    pub provider: String,
}

impl ServiceState {
    /// A missing file, key or unreadable value means the default: enabled,
    /// `auto`.  A strategy ID is kept as written (it may name a custom strategy
    /// that is gone, or one a newer version knows): the service decides what to
    /// do with it.
    #[must_use]
    pub fn load(text: &str) -> Self {
        let ini = IniFile::parse(text);
        let enabled = ini.get(SECTION, "enabled").map(str::trim);
        let enabled = !(enabled == Some("0")
            || enabled.is_some_and(|value| value.eq_ignore_ascii_case("false")));

        let strategy = ini
            .get(SECTION, "strategy")
            .map(str::trim)
            .filter(|value| !value.is_empty())
            .unwrap_or(built_in::AUTO)
            .to_string();

        // An older state file has no scope/provider key.  A missing scope means
        // the narrow one (an unknown value must not widen the bypass), and a
        // missing provider means the universal rotation those installs ran.
        let scope = ZapretScopes::parse(ini.get(SECTION, "scope"));
        let provider = ini
            .get(SECTION, "provider")
            .map(|value| to_lower_invariant(value.trim()));
        let provider = match provider.as_deref() {
            Some(name) if is_valid_provider(Some(name)) => name.to_string(),
            _ => UNIVERSAL.to_string(),
        };

        Self {
            enabled,
            strategy,
            scope,
            provider,
        }
    }

    /// Rewrites the `[zapret]` section; anything else in the file stays.  The
    /// folder is not made private (the exclusion lists next to this file must
    /// stay readable for nfqws2, which runs as an unprivileged user).
    ///
    /// # Errors
    ///
    /// Returns the INI error when a value contains a line break.
    pub fn save(&self, text: &str) -> Result<String, String> {
        let mut ini = IniFile::parse(text);
        let value = |result: Result<(), String>| {
            result.map_err(|error| format!("{error} (Parameter 'value')"))
        };
        value(ini.set(SECTION, "enabled", if self.enabled { "1" } else { "0" }))?;
        value(ini.set(SECTION, "strategy", &self.strategy))?;
        value(ini.set(SECTION, "scope", self.scope.to_wire()))?;
        value(ini.set(SECTION, "provider", &self.provider))?;
        Ok(ini.serialize())
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn state(enabled: bool, strategy: &str) -> ServiceState {
        ServiceState {
            enabled,
            strategy: strategy.to_string(),
            scope: ZapretScope::Telegram,
            provider: UNIVERSAL.to_string(),
        }
    }

    #[test]
    fn a_missing_file_gives_the_defaults() {
        assert_eq!(ServiceState::load(""), state(true, "auto"));
    }

    #[test]
    fn round_trip_writes_the_expected_file() {
        let text = state(false, "split").save("").unwrap();
        assert_eq!(
            text,
            "[zapret]\nenabled=0\nstrategy=split\nscope=telegram\nprovider=universal\n"
        );
        assert_eq!(ServiceState::load(&text), state(false, "split"));

        let mut custom = state(true, "custom:mine");
        custom.scope = ZapretScope::All;
        custom.provider = "mtn".to_string();
        let text = custom.save("").unwrap();
        assert_eq!(ServiceState::load(&text), custom);
    }

    #[test]
    fn scope_defaults_to_telegram_and_only_all_widens() {
        for (line, expected) in [
            ("scope=all", ZapretScope::All),
            ("scope=ALL", ZapretScope::All),
            ("scope=telegram", ZapretScope::Telegram),
            ("scope=junk", ZapretScope::Telegram),
            ("", ZapretScope::Telegram),
        ] {
            let text = format!("[zapret]\n{line}\n");
            assert_eq!(ServiceState::load(&text).scope, expected, "{line}");
        }
    }

    #[test]
    fn an_unknown_strategy_is_kept_as_written() {
        let text = "[zapret]\nenabled=1\nstrategy=from-the-future\n";
        assert_eq!(ServiceState::load(text).strategy, "from-the-future");
    }

    #[test]
    fn enabled_is_off_only_for_zero_or_false() {
        for (line, expected) in [
            ("enabled=0", false),
            ("enabled=1", true),
            ("enabled=false", false),
            ("enabled=TRUE", true),
            ("enabled=", true),
            ("enabled=junk", true),
            ("", true),
        ] {
            let text = format!("[zapret]\n{line}\nstrategy=split\n");
            assert_eq!(ServiceState::load(&text).enabled, expected, "{line}");
        }
    }

    #[test]
    fn a_blank_strategy_falls_back_to_auto() {
        assert_eq!(
            ServiceState::load("[zapret]\nstrategy=   \n").strategy,
            "auto"
        );
    }

    #[test]
    fn provider_is_lowercased_and_invalid_ones_fall_back_to_universal() {
        assert_eq!(
            ServiceState::load("[zapret]\nprovider=MTN\n").provider,
            "mtn"
        );
        assert_eq!(
            ServiceState::load("[zapret]\nprovider=Bad Name\n").provider,
            "universal"
        );
        assert_eq!(ServiceState::load("[zapret]\n").provider, "universal");
    }

    #[test]
    fn save_keeps_other_sections_and_updates_in_place() {
        let text = "[other]\nkeep=me\n[zapret]\nenabled=1\nstrategy=auto\n";
        let saved = state(false, "aggressive").save(text).unwrap();
        let ini = IniFile::parse(&saved);
        assert_eq!(ini.get("other", "keep"), Some("me"));
        assert_eq!(ini.get("zapret", "enabled"), Some("0"));
        assert_eq!(ini.get("zapret", "strategy"), Some("aggressive"));
        assert_eq!(ini.get("zapret", "scope"), Some("telegram"));
        assert_eq!(ini.get("zapret", "provider"), Some("universal"));
    }
}
