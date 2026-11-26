//! The five built-in strategies.
//!
//! Ported from `src/TgwsOne.Core/Zapret/BuiltInStrategies.cs`.  The four plain
//! ones are ports of the 1.x zapret1 presets; `auto` rotates them per host with
//! zapret2's `circular` orchestrator.  This module is data only: the argument
//! builder that consumes it is a later port.

/// The `auto` strategy ID.
pub const AUTO: &str = "auto";
/// The fake-TLS strategy ID.
pub const FAKE_TLS: &str = "fake-tls";
/// The split strategy ID.
pub const SPLIT: &str = "split";
/// The split-stealth strategy ID.
pub const SPLIT_STEALTH: &str = "split-stealth";
/// The aggressive strategy ID.
pub const AGGRESSIVE: &str = "aggressive";

/// A fake ClientHello (a file inside `<bundle>/files/fake`) preloaded under a
/// name Lua steps can refer to.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct Blob {
    /// The name Lua steps use for the blob.
    pub name: &'static str,
    /// The ClientHello file inside the bundle's `fake` folder.
    pub fake_file: &'static str,
}

/// The part of a strategy that differs: blobs to preload and the
/// `--lua-desync` steps (full arguments).  The target filters and the packet
/// range options around it are the same for every strategy.
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct DesyncPart {
    /// Blobs to preload before the steps run.
    pub blobs: &'static [Blob],
    /// The full `--lua-desync` arguments, in order.
    pub args: &'static [&'static str],
}

/// The built-in strategy IDs, in the order the C# `Ids` property declares them.
pub const IDS: [&str; 5] = [AUTO, FAKE_TLS, SPLIT, SPLIT_STEALTH, AGGRESSIVE];

/// What `auto` cycles through, in order: the position + 1 is the strategy
/// number tagged on each step.
pub const ROTATION: [&str; 4] = [FAKE_TLS, SPLIT, SPLIT_STEALTH, AGGRESSIVE];

/// `tls_clienthello_www_google_com.bin` preloaded as `tls_google`.
pub const GOOGLE: Blob = Blob {
    name: "tls_google",
    fake_file: "tls_clienthello_www_google_com.bin",
};

/// `tls_clienthello_vk_com.bin` preloaded as `tls_vk`.
pub const VK: Blob = Blob {
    name: "tls_vk",
    fake_file: "tls_clienthello_vk_com.bin",
};

/// The desync part of the fake-TLS strategy.
pub const FAKE_TLS_DESYNC: DesyncPart = DesyncPart {
    blobs: &[],
    args: &[
        "--lua-desync=fake:blob=0x00000000:tcp_seq=-10000:repeats=11",
        "--lua-desync=fake:blob=fake_default_tls:tcp_seq=-10000:repeats=11:tls_mod=rnd,dupsid,sni=www.google.com",
        "--lua-desync=multidisorder:pos=1,midsld",
    ],
};

/// The desync part of the split strategy.
pub const SPLIT_DESYNC: DesyncPart = DesyncPart {
    blobs: &[GOOGLE],
    args: &["--lua-desync=multisplit:pos=1:seqovl=681:seqovl_pattern=tls_google"],
};

/// The desync part of the split-stealth strategy.
pub const SPLIT_STEALTH_DESYNC: DesyncPart = DesyncPart {
    blobs: &[VK],
    args: &["--lua-desync=multisplit:pos=1:seqovl=568:seqovl_pattern=tls_vk"],
};

/// The desync part of the aggressive strategy.
pub const AGGRESSIVE_DESYNC: DesyncPart = DesyncPart {
    blobs: &[GOOGLE],
    args: &[
        "--lua-desync=fake:blob=tls_google:tcp_md5:repeats=11",
        "--lua-desync=multisplit:pos=1",
    ],
};

/// Whether `id` is one of the five built-in strategy identifiers.
#[must_use]
pub fn is_built_in(id: &str) -> bool {
    IDS.contains(&id)
}

/// The desync part of a plain strategy.
///
/// `auto` has none of its own: it is built from [`ROTATION`].
///
/// # Errors
///
/// Returns the same message as the C# `ArgumentException` for an unknown ID.
pub fn part(id: &str) -> Result<&'static DesyncPart, String> {
    match id {
        FAKE_TLS => Ok(&FAKE_TLS_DESYNC),
        SPLIT => Ok(&SPLIT_DESYNC),
        SPLIT_STEALTH => Ok(&SPLIT_STEALTH_DESYNC),
        AGGRESSIVE => Ok(&AGGRESSIVE_DESYNC),
        _ => Err(format!("no desync part for strategy '{id}'")),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ids_match_the_csharp_declaration_order() {
        assert_eq!(
            IDS,
            ["auto", "fake-tls", "split", "split-stealth", "aggressive"]
        );
    }

    #[test]
    fn rotation_matches_the_csharp_declaration_order() {
        assert_eq!(
            ROTATION,
            ["fake-tls", "split", "split-stealth", "aggressive"]
        );
    }

    #[test]
    fn is_built_in_accepts_only_the_five_ids() {
        for id in IDS {
            assert!(is_built_in(id));
        }
        assert!(!is_built_in("custom"));
        assert!(!is_built_in("Fake-TLS"));
        assert!(!is_built_in(""));
    }

    #[test]
    fn fake_tls_args_match_the_csharp_strings_byte_for_byte() {
        assert_eq!(
            FAKE_TLS_DESYNC.args,
            [
                "--lua-desync=fake:blob=0x00000000:tcp_seq=-10000:repeats=11",
                "--lua-desync=fake:blob=fake_default_tls:tcp_seq=-10000:repeats=11:tls_mod=rnd,dupsid,sni=www.google.com",
                "--lua-desync=multidisorder:pos=1,midsld",
            ]
        );
        assert!(FAKE_TLS_DESYNC.blobs.is_empty());
    }

    #[test]
    fn split_args_and_blob_match_the_csharp_strings_byte_for_byte() {
        assert_eq!(
            SPLIT_DESYNC.args,
            ["--lua-desync=multisplit:pos=1:seqovl=681:seqovl_pattern=tls_google"]
        );
        assert_eq!(
            SPLIT_DESYNC.blobs,
            [Blob {
                name: "tls_google",
                fake_file: "tls_clienthello_www_google_com.bin",
            }]
        );
    }

    #[test]
    fn split_stealth_args_and_blob_match_the_csharp_strings_byte_for_byte() {
        assert_eq!(
            SPLIT_STEALTH_DESYNC.args,
            ["--lua-desync=multisplit:pos=1:seqovl=568:seqovl_pattern=tls_vk"]
        );
        assert_eq!(
            SPLIT_STEALTH_DESYNC.blobs,
            [Blob {
                name: "tls_vk",
                fake_file: "tls_clienthello_vk_com.bin",
            }]
        );
    }

    #[test]
    fn aggressive_args_and_blob_match_the_csharp_strings_byte_for_byte() {
        assert_eq!(
            AGGRESSIVE_DESYNC.args,
            [
                "--lua-desync=fake:blob=tls_google:tcp_md5:repeats=11",
                "--lua-desync=multisplit:pos=1",
            ]
        );
        assert_eq!(AGGRESSIVE_DESYNC.blobs, [GOOGLE]);
    }

    #[test]
    fn part_resolves_every_built_in_and_rejects_auto_and_unknowns() {
        assert_eq!(part(FAKE_TLS).unwrap(), &FAKE_TLS_DESYNC);
        assert_eq!(part(SPLIT).unwrap(), &SPLIT_DESYNC);
        assert_eq!(part(SPLIT_STEALTH).unwrap(), &SPLIT_STEALTH_DESYNC);
        assert_eq!(part(AGGRESSIVE).unwrap(), &AGGRESSIVE_DESYNC);
        assert_eq!(
            part(AUTO).unwrap_err(),
            "no desync part for strategy 'auto'"
        );
        assert_eq!(
            part("custom:mine").unwrap_err(),
            "no desync part for strategy 'custom:mine'"
        );
    }

    #[test]
    fn blob_files_are_the_two_shipped_fake_clienthellos() {
        assert_eq!(GOOGLE.fake_file, "tls_clienthello_www_google_com.bin");
        assert_eq!(VK.fake_file, "tls_clienthello_vk_com.bin");
        assert_eq!(GOOGLE.name, "tls_google");
        assert_eq!(VK.name, "tls_vk");
    }
}
