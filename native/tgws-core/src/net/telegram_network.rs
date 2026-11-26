//! Where Telegram's data centres are and what their WebSocket endpoints are
//! called.
//!
//! Ported from `src/TgwsOne.Engine/Transport/TelegramNetwork.cs`.  The tables
//! keep the C# declaration order: it is part of the routing contract.

/// The WebSocket path Telegram serves.
pub const WS_PATH: &str = "/apiws";
/// The WebSocket path of the test data centres.
pub const WS_PATH_TEST: &str = "/apiws_test";

/// Default production data-centre addresses, in C# declaration order.
pub const DC_DEFAULT_IPS: &[(i32, &str)] = &[
    (1, "149.154.175.50"),
    (2, "149.154.167.51"),
    (3, "149.154.175.100"),
    (4, "149.154.167.91"),
    (5, "149.154.171.5"),
    (203, "91.105.192.100"),
];

/// Test data-centre addresses, in C# declaration order.
pub const DC_TEST_IPS: &[(i32, &str)] = &[
    (1, "149.154.175.10"),
    (2, "149.154.167.40"),
    (3, "149.154.175.117"),
];

/// DC 203 (the Amsterdam "other" DC) uses DC 2's WebSocket hosts.
///
/// Media connections try the `kws<dc>-1` host first, then the plain one.
#[must_use]
pub fn ws_domains(dc: i32, is_media: bool) -> Vec<String> {
    let dc = if dc == 203 { 2 } else { dc };
    if is_media {
        vec![
            format!("kws{dc}-1.web.telegram.org"),
            format!("kws{dc}.web.telegram.org"),
        ]
    } else {
        vec![format!("kws{dc}.web.telegram.org")]
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn paths_match_the_csharp_constants() {
        assert_eq!(WS_PATH, "/apiws");
        assert_eq!(WS_PATH_TEST, "/apiws_test");
    }

    #[test]
    fn default_ips_match_the_csharp_table_and_order() {
        assert_eq!(
            DC_DEFAULT_IPS,
            [
                (1, "149.154.175.50"),
                (2, "149.154.167.51"),
                (3, "149.154.175.100"),
                (4, "149.154.167.91"),
                (5, "149.154.171.5"),
                (203, "91.105.192.100"),
            ]
        );
    }

    #[test]
    fn test_ips_match_the_csharp_table_and_order() {
        assert_eq!(
            DC_TEST_IPS,
            [
                (1, "149.154.175.10"),
                (2, "149.154.167.40"),
                (3, "149.154.175.117"),
            ]
        );
    }

    #[test]
    fn plain_connections_use_one_host() {
        assert_eq!(ws_domains(1, false), ["kws1.web.telegram.org"]);
        assert_eq!(ws_domains(5, false), ["kws5.web.telegram.org"]);
    }

    #[test]
    fn media_connections_try_the_dash_one_host_first() {
        assert_eq!(
            ws_domains(2, true),
            ["kws2-1.web.telegram.org", "kws2.web.telegram.org",]
        );
        assert_eq!(
            ws_domains(5, true),
            ["kws5-1.web.telegram.org", "kws5.web.telegram.org",]
        );
    }

    #[test]
    fn dc_203_uses_dc_2_hosts_for_both_modes() {
        assert_eq!(ws_domains(203, false), ["kws2.web.telegram.org"]);
        assert_eq!(
            ws_domains(203, true),
            ["kws2-1.web.telegram.org", "kws2.web.telegram.org",]
        );
    }

    #[test]
    fn other_numbers_are_rendered_as_is() {
        assert_eq!(ws_domains(0, false), ["kws0.web.telegram.org"]);
        assert_eq!(
            ws_domains(-1, true),
            ["kws-1-1.web.telegram.org", "kws-1.web.telegram.org",]
        );
        assert_eq!(
            ws_domains(i32::MAX, false),
            ["kws2147483647.web.telegram.org"]
        );
    }
}
