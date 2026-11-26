//! C ABI surface for the native core.
//!
//! The crate is built as both `lib` and `cdylib`, so a future Android/.NET host
//! can load it without a Rust toolchain.  Only the version probe exists today.
//! Future entry points will follow the same style: create and destroy an egress
//! stream configured with a desynchronisation strategy, plus small functions for
//! feeding and draining buffers.  No C# binding is written yet.

use core::ffi::c_char;

const VERSION: &str = concat!(env!("CARGO_PKG_VERSION"), "\0");

/// Returns a NUL-terminated static version string.
///
/// The pointer is valid for the lifetime of the library and must not be freed.
/// This is the only C ABI symbol in the skeleton.
#[unsafe(no_mangle)]
pub extern "C" fn tgws_core_version() -> *const c_char {
    VERSION.as_ptr().cast()
}
