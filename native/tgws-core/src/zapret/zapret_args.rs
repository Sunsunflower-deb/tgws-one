//! The nfqws2/winws2 command line for a strategy.
//!
//! Ported from `src/TgwsOne.Core/Zapret/ZapretArgs.cs`,
//! `src/TgwsOne.Core/Zapret/OsPath.cs` and
//! `src/TgwsOne.Core/Zapret/ZapretScope.cs`.  The layout is: debug, Lua
//! scripts, blobs, interception, then one or two profiles that differ only in
//! what they target (all Cloudflare addresses or the Telegram hosts) and carry
//! the same body.  Paths are absolute and use the target OS separator, so a
//! Windows layout can be built and tested on Linux.

use std::collections::HashSet;

use crate::strategy::built_in::{self, Blob};

/// The nfqws2 queue number used outside Windows.
pub const QUEUE_NUM: i32 = 3443;

/// The key of the shared `autostate` table: both profiles use it, so a host has
/// one strategy.
pub const AUTO_STATE_KEY: &str = "tgws";

/// The folder (inside the service's state dir) nfqws2 makes writable for Lua;
/// `auto` keeps its state file there.
pub const WRITABLE_FOLDER: &str = "zapret-state";

const CUSTOM_PREFIX: &str = "custom:";

/// The target OS a command line is built for.  Only the path separator differs.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum OsKind {
    /// Windows (`\` separators, `--wf-tcp-out`).
    Windows,
    /// Linux and Android (`/` separators, `--qnum`).
    Linux,
}

/// What the bypass applies to.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Default)]
pub enum ZapretScope {
    /// Only Telegram's TLS: the proxy's own traffic (the default).
    #[default]
    Telegram,
    /// Every application: Telegram's hosts plus all Cloudflare ranges.
    All,
}

/// The wire and INI spelling of [`ZapretScope`].
pub struct ZapretScopes;

impl ZapretScopes {
    /// The narrow scope.
    pub const TELEGRAM: &'static str = "telegram";
    /// The wide scope.
    pub const ALL: &'static str = "all";

    /// Anything but `all` means the narrow, default scope (an unknown value
    /// must not widen the bypass).
    #[must_use]
    pub fn parse(value: Option<&str>) -> ZapretScope {
        match value {
            Some(value) if value.trim().to_lowercase() == Self::ALL => ZapretScope::All,
            _ => ZapretScope::Telegram,
        }
    }

    /// The INI/wire form.
    #[must_use]
    pub fn to_wire(scope: ZapretScope) -> &'static str {
        scope.to_wire()
    }
}

impl ZapretScope {
    /// The INI/wire form: `all` or `telegram`.
    #[must_use]
    pub fn to_wire(self) -> &'static str {
        match self {
            Self::All => ZapretScopes::ALL,
            Self::Telegram => ZapretScopes::TELEGRAM,
        }
    }
}

/// The list files a run reads (full paths): the targets are admin-owned, the
/// exclusions service-written.  `cf_proxy_hosts` is the generated hostlist of
/// the proxy's own Cloudflare connections; an empty path means "not generated
/// yet", and the narrow scope then adds no profile for them.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ZapretLists {
    pub cloudflare_ips: String,
    pub telegram_hosts: String,
    pub exclude_hosts: String,
    pub exclude_ips: String,
    pub cf_proxy_hosts: String,
}

impl ZapretLists {
    /// The four required lists; the generated Cloudflare hostlist starts empty.
    #[must_use]
    pub fn new(
        cloudflare_ips: impl Into<String>,
        telegram_hosts: impl Into<String>,
        exclude_hosts: impl Into<String>,
        exclude_ips: impl Into<String>,
    ) -> Self {
        Self {
            cloudflare_ips: cloudflare_ips.into(),
            telegram_hosts: telegram_hosts.into(),
            exclude_hosts: exclude_hosts.into(),
            exclude_ips: exclude_ips.into(),
            cf_proxy_hosts: String::new(),
        }
    }

    /// Sets the generated Cloudflare hostlist path.
    #[must_use]
    pub fn with_cf_proxy_hosts(mut self, path: impl Into<String>) -> Self {
        self.cf_proxy_hosts = path.into();
        self
    }
}

/// Where the pinned zapret2 release lives: the executable, the Lua scripts and
/// the fake ClientHello files.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ZapretBundle {
    pub dir: String,
    pub exe: String,
    pub lua_dir: String,
    pub fake_dir: String,
}

impl ZapretBundle {
    /// `<app_dir>/zapret2` unless `TGWS_ZAPRET_DIR` says otherwise;
    /// `TGWS_ZAPRET_EXE` replaces just the executable.
    #[must_use]
    pub fn locate<F>(os: OsKind, app_dir: &str, env: F) -> Self
    where
        F: Fn(&str) -> Option<String>,
    {
        let dir = match env("TGWS_ZAPRET_DIR") {
            Some(dir) if !dir.is_empty() => dir,
            _ => os_path_join(os, app_dir, &["zapret2"]),
        };
        let exe_name = if os == OsKind::Windows {
            "winws2.exe"
        } else {
            "nfqws2"
        };
        let exe = match env("TGWS_ZAPRET_EXE") {
            Some(exe) if !exe.is_empty() => exe,
            _ => os_path_join(os, &dir, &[exe_name]),
        };
        Self {
            lua_dir: os_path_join(os, &dir, &["lua"]),
            fake_dir: os_path_join(os, &dir, &["files", "fake"]),
            dir,
            exe,
        }
    }
}

/// Optional inputs of [`build`], mirroring the optional parameters of the C#
/// `ZapretArgs.Build`.
#[derive(Debug, Clone, Copy, Default)]
pub struct BuildOptions<'a> {
    /// The loaded desync part when the strategy is `custom:<name>`.
    pub custom: Option<&'a [String]>,
    /// A debug log file; when set, `--debug=@<file>` is the first argument.
    pub debug_file: Option<&'a str>,
    /// What the bypass applies to.
    pub scope: ZapretScope,
    /// What `auto` walks; `None` or an empty list means the universal rotation.
    pub rotation: Option<&'a [String]>,
}

/// The target OS separator.
#[must_use]
pub fn os_path_separator(os: OsKind) -> char {
    if os == OsKind::Windows { '\\' } else { '/' }
}

/// Joins path parts with the target OS separator.
#[must_use]
pub fn os_path_join(os: OsKind, first: &str, rest: &[&str]) -> String {
    let sep = os_path_separator(os);
    let mut joined = first.to_string();
    for part in rest {
        if !joined.ends_with(sep) {
            joined.push(sep);
        }
        joined.push_str(part);
    }
    joined
}

/// Builds the command line for a strategy.
///
/// # Errors
///
/// Returns the `ArgumentException` text of the original for an unknown
/// strategy, or for a `custom:<name>` strategy without its loaded desync part.
pub fn build(
    os: OsKind,
    bundle: &ZapretBundle,
    lists: &ZapretLists,
    strategy_id: &str,
    state_dir: &str,
    options: BuildOptions<'_>,
) -> Result<Vec<String>, String> {
    let auto = strategy_id == built_in::AUTO;
    let universal: Vec<String> = built_in::ROTATION
        .iter()
        .map(|step| (*step).to_string())
        .collect();
    let steps: &[String] = match options.rotation {
        Some(rotation) if !rotation.is_empty() => rotation,
        _ => &universal,
    };

    let mut blobs: Vec<Blob> = Vec::new();
    let mut extra_inits: Vec<String> = Vec::new();
    let mut extra_blobs: Vec<String> = Vec::new();
    let mut body: Vec<String> = Vec::new();

    if auto {
        body.extend([
            "--in-range=-s5556".to_string(),
            "--out-range=-d10".to_string(),
            "--payload=all".to_string(),
            format!("--lua-desync=circular:fails=3:time=60:key={AUTO_STATE_KEY}"),
            "--in-range=x".to_string(),
            "--payload=tls_client_hello".to_string(),
        ]);
        for (index, step) in steps.iter().enumerate() {
            let part = built_in::part(step)?;
            blobs.extend_from_slice(part.blobs);
            for arg in part.args {
                body.push(format!("{arg}:strategy={}", index + 1));
            }
        }
    } else {
        let part_args: Vec<String> = if strategy_id.starts_with(CUSTOM_PREFIX) {
            let custom = options
                .custom
                .ok_or_else(|| format!("strategy '{strategy_id}' needs its loaded desync part"))?;
            custom.to_vec()
        } else {
            let part = built_in::part(strategy_id)?;
            blobs.extend_from_slice(part.blobs);
            part.args.iter().map(|arg| (*arg).to_string()).collect()
        };

        body.extend([
            "--out-range=-d10".to_string(),
            "--payload=tls_client_hello".to_string(),
        ]);
        // The body is repeated in both profiles, but blobs and Lua scripts are
        // global (nfqws2 refuses a blob name defined twice): lines of a custom
        // strategy that declare them go to the head, once.
        for arg in part_args {
            if arg.starts_with("--lua-init=") {
                extra_inits.push(arg);
            } else if arg.starts_with("--blob=") {
                extra_blobs.push(arg);
            } else {
                body.push(arg);
            }
        }
    }

    let lua = |file: &str| format!("--lua-init=@{}", os_path_join(os, &bundle.lua_dir, &[file]));

    let mut argv = Vec::new();
    if let Some(debug_file) = options.debug_file {
        argv.push(format!("--debug=@{debug_file}"));
    }
    argv.push(lua("zapret-lib.lua"));
    argv.push(lua("zapret-antidpi.lua"));
    if auto {
        argv.push(lua("zapret-auto.lua"));
        argv.push(format!(
            "--writable={}",
            os_path_join(os, state_dir, &[WRITABLE_FOLDER])
        ));
        argv.push(lua("tgws-state.lua"));
    }
    argv.extend(distinct(&extra_inits));

    let mut seen_blobs = HashSet::new();
    for blob in &blobs {
        if seen_blobs.insert(blob.name) {
            argv.push(format!(
                "--blob={}:@{}",
                blob.name,
                os_path_join(os, &bundle.fake_dir, &[blob.fake_file])
            ));
        }
    }
    argv.extend(distinct(&extra_blobs));

    if os == OsKind::Windows {
        argv.push("--wf-tcp-out=443".to_string());
    } else {
        argv.push(format!("--qnum={QUEUE_NUM}"));
    }

    // The narrow scope must still cover the proxy's own Cloudflare connections:
    // the fallback dials kws<dc>.<cf domain>, names that are not in
    // telegram.txt at all.  Without this profile the proxy has no protected
    // route out when the direct route is blocked.
    if options.scope == ZapretScope::Telegram && !lists.cf_proxy_hosts.is_empty() {
        argv.extend([
            "--filter-tcp=443".to_string(),
            "--filter-l7=tls".to_string(),
            format!("--hostlist={}", lists.cf_proxy_hosts),
            format!("--hostlist-exclude={}", lists.exclude_hosts),
        ]);
        argv.extend(body.iter().cloned());
        argv.push("--new".to_string());
    }

    // The wide scope (opt-in) starts with every Cloudflare address: other
    // applications, browsers first of all, are affected by it too.
    if options.scope == ZapretScope::All {
        argv.extend([
            "--filter-tcp=443".to_string(),
            "--filter-l7=tls".to_string(),
            format!("--ipset={}", lists.cloudflare_ips),
            format!("--ipset-exclude={}", lists.exclude_ips),
            format!("--hostlist-exclude={}", lists.exclude_hosts),
        ]);
        argv.extend(body.iter().cloned());
        argv.push("--new".to_string());
    }

    argv.extend([
        "--filter-tcp=443".to_string(),
        "--filter-l7=tls".to_string(),
        format!("--hostlist={}", lists.telegram_hosts),
        format!("--hostlist-exclude={}", lists.exclude_hosts),
    ]);
    argv.extend(body);
    Ok(argv)
}

/// `Enumerable.Distinct`: keeps the first occurrence, ordinal equality.
fn distinct(items: &[String]) -> Vec<String> {
    let mut seen = HashSet::new();
    let mut out = Vec::new();
    for item in items {
        if seen.insert(item.as_str()) {
            out.push(item.clone());
        }
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    fn lists() -> ZapretLists {
        ZapretLists::new(
            "/etc/tgwsone/lists/cloudflare-ips.txt",
            "/etc/tgwsone/lists/telegram.txt",
            "/var/lib/tgwsone/exclude-hosts.txt",
            "/var/lib/tgwsone/exclude-ips.txt",
        )
        .with_cf_proxy_hosts("/etc/tgwsone/lists/cfproxy-hosts.txt")
    }

    fn bundle() -> ZapretBundle {
        ZapretBundle::locate(OsKind::Linux, "/opt/tgwsone", |_| None)
    }

    fn linux(strategy: &str, options: BuildOptions<'_>) -> Vec<String> {
        build(
            OsKind::Linux,
            &bundle(),
            &lists(),
            strategy,
            "/var/lib/tgwsone",
            options,
        )
        .expect("valid strategy")
    }

    #[test]
    fn separators_and_join_follow_the_target_os() {
        assert_eq!(os_path_separator(OsKind::Windows), '\\');
        assert_eq!(os_path_separator(OsKind::Linux), '/');
        assert_eq!(
            os_path_join(OsKind::Linux, "/opt/tgwsone", &["zapret2", "lua"]),
            "/opt/tgwsone/zapret2/lua"
        );
        assert_eq!(
            os_path_join(
                OsKind::Windows,
                r"C:\Program Files\TgwsOne",
                &["zapret2", "lua"]
            ),
            r"C:\Program Files\TgwsOne\zapret2\lua"
        );
        // A trailing separator is not duplicated.
        assert_eq!(
            os_path_join(OsKind::Linux, "/opt/tgwsone/", &["zapret2"]),
            "/opt/tgwsone/zapret2"
        );
    }

    #[test]
    fn scopes_parse_narrow_by_default_and_round_trip() {
        for value in [None, Some("telegram"), Some("ALL?"), Some("junk"), Some("")] {
            assert_eq!(
                ZapretScopes::parse(value),
                ZapretScope::Telegram,
                "{value:?}"
            );
        }
        assert_eq!(ZapretScopes::parse(Some("all")), ZapretScope::All);
        assert_eq!(ZapretScopes::parse(Some(" ALL ")), ZapretScope::All);
        assert_eq!(ZapretScope::All.to_wire(), "all");
        assert_eq!(ZapretScope::Telegram.to_wire(), "telegram");
        assert_eq!(ZapretScopes::to_wire(ZapretScope::All), "all");
    }

    #[test]
    fn bundle_layout_and_env_overrides() {
        let linux = ZapretBundle::locate(OsKind::Linux, "/opt/tgwsone", |_| None);
        assert_eq!(linux.dir, "/opt/tgwsone/zapret2");
        assert_eq!(linux.exe, "/opt/tgwsone/zapret2/nfqws2");
        assert_eq!(linux.lua_dir, "/opt/tgwsone/zapret2/lua");
        assert_eq!(linux.fake_dir, "/opt/tgwsone/zapret2/files/fake");

        let windows = ZapretBundle::locate(OsKind::Windows, r"C:\Program Files\TgwsOne", |_| None);
        assert_eq!(windows.exe, r"C:\Program Files\TgwsOne\zapret2\winws2.exe");

        let overridden = ZapretBundle::locate(OsKind::Linux, "/opt/tgwsone", |name| match name {
            "TGWS_ZAPRET_DIR" => Some("/x/z".to_string()),
            "TGWS_ZAPRET_EXE" => Some("/x/fake".to_string()),
            _ => None,
        });
        assert_eq!(overridden.dir, "/x/z");
        assert_eq!(overridden.exe, "/x/fake");
        assert_eq!(overridden.lua_dir, "/x/z/lua");
    }

    #[test]
    fn plain_strategy_narrow_scope_with_hostlist_has_one_first_profile() {
        let argv = linux("split", BuildOptions::default());
        assert_eq!(
            &argv[..5],
            [
                "--lua-init=@/opt/tgwsone/zapret2/lua/zapret-lib.lua",
                "--lua-init=@/opt/tgwsone/zapret2/lua/zapret-antidpi.lua",
                "--blob=tls_google:@/opt/tgwsone/zapret2/files/fake/tls_clienthello_www_google_com.bin",
                "--qnum=3443",
                "--filter-tcp=443",
            ]
        );
        assert!(argv.contains(&"--hostlist=/etc/tgwsone/lists/cfproxy-hosts.txt".to_string()));
        assert!(!argv.iter().any(|a| a.starts_with("--ipset=")));
        assert_eq!(argv.iter().filter(|a| *a == "--new").count(), 1);
    }

    #[test]
    fn plain_strategy_without_generated_hostlist_skips_the_extra_profile() {
        let lists = ZapretLists::new("/l/cf.txt", "/l/telegram.txt", "/l/eh.txt", "/l/ei.txt");
        let argv = build(
            OsKind::Linux,
            &bundle(),
            &lists,
            "split",
            "/var/lib/tgwsone",
            BuildOptions::default(),
        )
        .unwrap();
        assert!(!argv.iter().any(|a| a == "--new"));
        assert!(argv.contains(&"--hostlist=/l/telegram.txt".to_string()));
    }

    #[test]
    fn auto_wide_has_state_scripts_and_two_deduplicated_blobs() {
        let argv = linux(
            "auto",
            BuildOptions {
                scope: ZapretScope::All,
                ..BuildOptions::default()
            },
        );
        assert!(argv.contains(&"--lua-init=@/opt/tgwsone/zapret2/lua/zapret-auto.lua".to_string()));
        assert!(argv.contains(&"--writable=/var/lib/tgwsone/zapret-state".to_string()));
        let blobs: Vec<&String> = argv.iter().filter(|a| a.starts_with("--blob=")).collect();
        assert_eq!(
            blobs,
            [
                "--blob=tls_google:@/opt/tgwsone/zapret2/files/fake/tls_clienthello_www_google_com.bin",
                "--blob=tls_vk:@/opt/tgwsone/zapret2/files/fake/tls_clienthello_vk_com.bin",
            ]
        );
        assert!(
            argv.contains(
                &"--lua-desync=multisplit:pos=1:seqovl=568:seqovl_pattern=tls_vk:strategy=3"
                    .to_string()
            )
        );
    }

    #[test]
    fn custom_blob_and_lua_init_lines_are_hoisted_once() {
        let custom = vec![
            "--blob=mine:@/etc/tgwsone/strategies/mine.bin".to_string(),
            "--lua-init=@/etc/tgwsone/strategies/mine.lua".to_string(),
            "--lua-desync=multisplit:pos=2".to_string(),
        ];
        let argv = linux(
            "custom:mine",
            BuildOptions {
                custom: Some(&custom),
                ..BuildOptions::default()
            },
        );
        assert_eq!(
            argv.iter()
                .filter(|a| *a == "--blob=mine:@/etc/tgwsone/strategies/mine.bin")
                .count(),
            1
        );
        assert_eq!(
            argv.iter()
                .filter(|a| *a == "--lua-init=@/etc/tgwsone/strategies/mine.lua")
                .count(),
            1
        );
        assert_eq!(
            argv.iter()
                .filter(|a| *a == "--lua-desync=multisplit:pos=2")
                .count(),
            2
        );
    }

    #[test]
    fn custom_without_part_and_unknown_strategy_are_errors() {
        assert_eq!(
            build(
                OsKind::Linux,
                &bundle(),
                &lists(),
                "custom:mine",
                "/var/lib/tgwsone",
                BuildOptions::default(),
            )
            .unwrap_err(),
            "strategy 'custom:mine' needs its loaded desync part"
        );
        assert_eq!(
            build(
                OsKind::Linux,
                &bundle(),
                &lists(),
                "nonsense",
                "/var/lib/tgwsone",
                BuildOptions::default(),
            )
            .unwrap_err(),
            "no desync part for strategy 'nonsense'"
        );
    }

    #[test]
    fn debug_file_is_first_and_windows_uses_wf_tcp_out() {
        let argv = build(
            OsKind::Windows,
            &ZapretBundle::locate(OsKind::Windows, r"C:\Program Files\TgwsOne", |_| None),
            &lists(),
            "split",
            r"C:\ProgramData\TgwsOne\state",
            BuildOptions {
                debug_file: Some("/tmp/debug.log"),
                scope: ZapretScope::All,
                ..BuildOptions::default()
            },
        )
        .unwrap();
        assert_eq!(argv[0], "--debug=@/tmp/debug.log");
        assert!(argv.contains(&"--wf-tcp-out=443".to_string()));
        assert!(!argv.iter().any(|a| a.starts_with("--qnum=")));
    }
}
