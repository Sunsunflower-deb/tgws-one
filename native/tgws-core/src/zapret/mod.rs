//! The zapret2 command line and the service's remembered state.
//!
//! Ported from `TgwsOne.Core.Zapret` (`ZapretArgs`, `OsPath`, `ZapretScope`,
//! `ServiceState`).  Only the pure parts are here: an argv is assembled as
//! strings, and the state is a conversion between a record and INI text.  The
//! file reads/writes, the service process and the nftables calls stay in the
//! platform shell.

pub mod service_state;
pub mod zapret_args;

pub use service_state::ServiceState;
pub use zapret_args::{
    AUTO_STATE_KEY, BuildOptions, OsKind, QUEUE_NUM, WRITABLE_FOLDER, ZapretBundle, ZapretLists,
    ZapretScope, ZapretScopes, build, os_path_join, os_path_separator,
};
