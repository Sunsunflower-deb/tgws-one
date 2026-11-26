//! Golden `ZapretArgs.Build` argv, recorded on the C# side.
//!
//! The fixture is produced by `tools/PortShims/ZapretArgsShims.cs` driven from a
//! small console program (see the RT4 report): each case is the full input and
//! the exact argv the original returns, byte for byte.  The two error cases
//! record the `ArgumentException` text; the Rust error is its prefix because
//! the C# message appends the `(Parameter '...')` suffix.

use serde_json::Value;
use tgws_core::zapret::{BuildOptions, OsKind, ZapretBundle, ZapretLists, ZapretScopes, build};

const GOLDEN: &str = include_str!("fixtures/zapret_argv_golden.json");

fn string_array(value: &Value) -> Option<Vec<String>> {
    value.as_array().map(|items| {
        items
            .iter()
            .map(|item| item.as_str().unwrap().to_string())
            .collect()
    })
}

#[test]
fn zapret_argv_matches_the_csharp_golden_cases() {
    let cases: Value = serde_json::from_str(GOLDEN).expect("valid fixture");
    let cases = cases.as_array().expect("fixture is an array");

    let mut argv_cases = 0;
    let mut error_cases = 0;
    for case in cases {
        let name = case["Name"].as_str().expect("case name");
        let os = match case["Os"].as_str().expect("os") {
            "windows" => OsKind::Windows,
            _ => OsKind::Linux,
        };
        let bundle = ZapretBundle::locate(os, case["AppDir"].as_str().unwrap(), |_| None);
        let lists = ZapretLists {
            cloudflare_ips: case["Lists"]["CloudflareIps"].as_str().unwrap().to_string(),
            telegram_hosts: case["Lists"]["TelegramHosts"].as_str().unwrap().to_string(),
            exclude_hosts: case["Lists"]["ExcludeHosts"].as_str().unwrap().to_string(),
            exclude_ips: case["Lists"]["ExcludeIps"].as_str().unwrap().to_string(),
            cf_proxy_hosts: case["Lists"]["CfProxyHosts"].as_str().unwrap().to_string(),
        };
        let custom = string_array(&case["Custom"]);
        let rotation = string_array(&case["Rotation"]);
        let options = BuildOptions {
            custom: custom.as_deref(),
            debug_file: case["Debug"].as_str(),
            scope: ZapretScopes::parse(case["Scope"].as_str()),
            rotation: rotation.as_deref(),
        };
        let result = build(
            os,
            &bundle,
            &lists,
            case["Strategy"].as_str().unwrap(),
            case["StateDir"].as_str().unwrap(),
            options,
        );

        if let Some(expected) = string_array(&case["Argv"]) {
            assert_eq!(
                result.unwrap_or_else(|err| panic!("{name}: {err}")),
                expected,
                "{name}"
            );
            argv_cases += 1;
        } else {
            let error = result.expect_err(name);
            let expected = case["Error"].as_str().expect("error text");
            assert!(
                expected.starts_with(&error) || error.starts_with(expected),
                "{name}: Rust {error:?} vs C# {expected:?}"
            );
            error_cases += 1;
        }
    }

    assert_eq!(argv_cases, 14, "argv golden sets");
    assert_eq!(error_cases, 2, "error golden sets");
}
