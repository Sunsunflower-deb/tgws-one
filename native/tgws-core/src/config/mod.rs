//! The engine's command-line configuration.
//!
//! Ported from `src/TgwsOne.Engine/EngineConfig.cs` (`EngineConfigParser`) and,
//! for the pure mapping, `src/TgwsOne.Core/EngineConfigFactory.cs`
//! (`FromSettings`).  Secret generation, sockets and file I/O stay in the
//! platform shell; a caller that omits `--secret` supplies the generated value.

pub mod engine_config_parser;

pub use engine_config_parser::{
    DEFAULT_HOST, DEFAULT_PING_INTERVAL_SECONDS, DEFAULT_PING_JITTER_SECONDS, DEFAULT_PORT,
    EngineConfig, coerce_domains, from_settings, try_parse,
};
