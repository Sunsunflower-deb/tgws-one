//! A minimal blocking TCP wrapper over the platform sockets API.
//!
//! The wrapper exists so the TLS/DESYNC layer can control socket behaviour that
//! managed runtimes hide: hop limit (TTL), out-of-band data and, later, custom
//! segment injection.  Only `libc` is used; no async runtime is involved.
//!
//! Numeric constants are spelled out instead of taking them from `libc` so their
//! origin is explicit.  They come from the Linux UAPI headers:
//!
//! * `IPPROTO_IP = 0`, `IP_TTL = 2` — `include/uapi/linux/in.h`
//! * `IPPROTO_IPV6 = 41`, `IPV6_UNICAST_HOPS = 16` — `include/uapi/linux/in6.h`
//! * `MSG_OOB = 1` — `include/uapi/linux/socket.h`
//! * `IPPROTO_TCP = 6`, `TCP_NODELAY = 1` — `include/uapi/linux/tcp.h`
//!
//! TODO(desync): the next layer will use `vmsplice`/`splice` to emit forged
//! segments (our own take on ByeDPI-style desynchronisation).  That is only a
//! plan for now; this module intentionally contains no injection code.

use core::mem::size_of;
use std::io;
use std::net::{SocketAddr, ToSocketAddrs};

const IPPROTO_IP: libc::c_int = 0;
const IP_TTL: libc::c_int = 2;
const IPPROTO_IPV6: libc::c_int = 41;
const IPV6_UNICAST_HOPS: libc::c_int = 16;
const MSG_OOB: libc::c_int = 1;
const IPPROTO_TCP: libc::c_int = 6;
const TCP_NODELAY: libc::c_int = 1;

/// A blocking TCP stream owned directly through a file descriptor.
///
/// The descriptor is closed on drop.  The type is intentionally small: callers
/// that need the standard socket interface can take [`RawTcpStream::as_raw_fd`]
/// and wrap it, but ownership stays here.
#[derive(Debug)]
pub struct RawTcpStream {
    fd: i32,
    family: libc::c_int,
}

impl RawTcpStream {
    /// Resolves `host` and connects to the first reachable address.
    ///
    /// Both IPv4 and IPv6 literals and names are supported; `TCP_NODELAY` is
    /// enabled so small TLS records are not delayed by Nagle's algorithm.
    ///
    /// # Errors
    ///
    /// Returns the last resolution or connection error when no address could be
    /// used.
    pub fn connect(host: &str, port: u16) -> io::Result<Self> {
        let mut last_error = None;
        for address in (host, port).to_socket_addrs()? {
            match Self::connect_addr(address) {
                Ok(stream) => return Ok(stream),
                Err(error) => last_error = Some(error),
            }
        }

        Err(last_error.unwrap_or_else(|| {
            io::Error::new(
                io::ErrorKind::InvalidInput,
                "host did not resolve to any socket address",
            )
        }))
    }

    /// Connects to a local [`std::net::TcpListener`] address.
    ///
    /// This is mainly a convenience for tests and for callers that already did
    /// their own resolution.
    ///
    /// # Errors
    ///
    /// Propagates socket creation, option and connection errors.
    pub fn connect_to(address: SocketAddr) -> io::Result<Self> {
        Self::connect_addr(address)
    }

    /// Sets the IPv4 TTL or IPv6 unicast hop limit.
    ///
    /// # Errors
    ///
    /// Propagates `setsockopt` failures (for example an out-of-range value).
    pub fn set_ttl(&self, ttl: u8) -> io::Result<()> {
        let (level, name) = self.ttl_options();
        self.setsockopt_int(level, name, libc::c_int::from(ttl))
    }

    /// Reads the IPv4 TTL or IPv6 unicast hop limit.
    ///
    /// # Errors
    ///
    /// Propagates `getsockopt` failures and rejects values outside `u8`.
    pub fn ttl(&self) -> io::Result<u8> {
        let (level, name) = self.ttl_options();
        let value = self.getsockopt_int(level, name)?;
        u8::try_from(value)
            .map_err(|_| io::Error::new(io::ErrorKind::InvalidData, "TTL outside the u8 range"))
    }

    /// Sends one byte of TCP urgent data (`MSG_OOB`).
    ///
    /// # Errors
    ///
    /// Propagates `send` errors.
    pub fn send_oob(&self, buf: &[u8]) -> io::Result<usize> {
        self.send_with_flags(buf, MSG_OOB)
    }

    /// Sends ordinary application data.
    ///
    /// # Errors
    ///
    /// Propagates `send` errors.
    pub fn send_raw(&self, buf: &[u8]) -> io::Result<usize> {
        self.send_with_flags(buf, 0)
    }

    /// Returns the owned file descriptor.
    #[must_use]
    pub fn as_raw_fd(&self) -> i32 {
        self.fd
    }

    fn connect_addr(address: SocketAddr) -> io::Result<Self> {
        match address {
            SocketAddr::V4(v4) => {
                let sin = libc::sockaddr_in {
                    sin_family: libc::AF_INET as libc::sa_family_t,
                    sin_port: v4.port().to_be(),
                    sin_addr: libc::in_addr {
                        s_addr: u32::from_ne_bytes(v4.ip().octets()),
                    },
                    sin_zero: [0; 8],
                };
                Self::connect_raw(
                    libc::AF_INET,
                    (&sin as *const libc::sockaddr_in).cast(),
                    size_of::<libc::sockaddr_in>(),
                )
            }
            SocketAddr::V6(v6) => {
                let sin6 = libc::sockaddr_in6 {
                    sin6_family: libc::AF_INET6 as libc::sa_family_t,
                    sin6_port: v6.port().to_be(),
                    sin6_flowinfo: v6.flowinfo(),
                    sin6_addr: libc::in6_addr {
                        s6_addr: v6.ip().octets(),
                    },
                    sin6_scope_id: v6.scope_id(),
                };
                Self::connect_raw(
                    libc::AF_INET6,
                    (&sin6 as *const libc::sockaddr_in6).cast(),
                    size_of::<libc::sockaddr_in6>(),
                )
            }
        }
    }

    fn connect_raw(
        domain: libc::c_int,
        address: *const libc::sockaddr,
        length: usize,
    ) -> io::Result<Self> {
        // SAFETY: `address` points at a live `sockaddr_in`/`sockaddr_in6` for the
        // duration of the call and `length` is its exact size.
        let fd = unsafe { libc::socket(domain, libc::SOCK_STREAM, 0) };
        if fd < 0 {
            return Err(io::Error::last_os_error());
        }

        let stream = Self { fd, family: domain };
        stream.set_nodelay()?;

        // SAFETY: `fd` is a fresh stream socket; `address`/`length` describe it.
        let result = unsafe { libc::connect(fd, address, length as libc::socklen_t) };
        if result < 0 {
            return Err(io::Error::last_os_error());
        }

        Ok(stream)
    }

    fn set_nodelay(&self) -> io::Result<()> {
        self.setsockopt_int(IPPROTO_TCP, TCP_NODELAY, 1)
    }

    fn ttl_options(&self) -> (libc::c_int, libc::c_int) {
        if self.family == libc::AF_INET6 {
            (IPPROTO_IPV6, IPV6_UNICAST_HOPS)
        } else {
            (IPPROTO_IP, IP_TTL)
        }
    }

    fn setsockopt_int(
        &self,
        level: libc::c_int,
        name: libc::c_int,
        value: libc::c_int,
    ) -> io::Result<()> {
        // SAFETY: the value points at a live `c_int` with the declared length.
        let result = unsafe {
            libc::setsockopt(
                self.fd,
                level,
                name,
                (&value as *const libc::c_int).cast(),
                size_of::<libc::c_int>() as libc::socklen_t,
            )
        };
        if result < 0 {
            Err(io::Error::last_os_error())
        } else {
            Ok(())
        }
    }

    fn getsockopt_int(&self, level: libc::c_int, name: libc::c_int) -> io::Result<libc::c_int> {
        let mut value: libc::c_int = 0;
        let mut length = size_of::<libc::c_int>() as libc::socklen_t;
        // SAFETY: `value` and `length` are valid for writes of the given size.
        let result = unsafe {
            libc::getsockopt(
                self.fd,
                level,
                name,
                (&mut value as *mut libc::c_int).cast(),
                &mut length,
            )
        };
        if result < 0 {
            Err(io::Error::last_os_error())
        } else {
            Ok(value)
        }
    }

    fn send_with_flags(&self, buf: &[u8], flags: libc::c_int) -> io::Result<usize> {
        // SAFETY: `buf` is a valid readable slice for its full length.
        let result = unsafe { libc::send(self.fd, buf.as_ptr().cast(), buf.len(), flags) };
        if result < 0 {
            Err(io::Error::last_os_error())
        } else {
            Ok(result as usize)
        }
    }
}

impl Drop for RawTcpStream {
    fn drop(&mut self) {
        // SAFETY: `fd` is owned by this value and closed exactly once here.
        unsafe {
            libc::close(self.fd);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Read;
    use std::net::TcpListener;

    fn loopback_pair() -> (TcpListener, RawTcpStream) {
        let listener = TcpListener::bind(("127.0.0.1", 0)).expect("bind loopback");
        let port = listener.local_addr().expect("local addr").port();
        let stream = RawTcpStream::connect("127.0.0.1", port).expect("connect loopback");
        (listener, stream)
    }

    #[test]
    fn ttl_roundtrip_on_loopback() {
        let (listener, stream) = loopback_pair();
        let _peer = listener.accept().expect("accept").0;

        let original = stream.ttl().expect("read default TTL");
        stream.set_ttl(5).expect("set TTL");
        assert_eq!(stream.ttl().expect("read TTL"), 5);

        stream.set_ttl(original).expect("restore TTL");
        assert_eq!(stream.ttl().expect("read restored TTL"), original);
    }

    #[test]
    fn send_oob_smoke_on_loopback() {
        let (listener, stream) = loopback_pair();
        let _peer = listener.accept().expect("accept").0;

        let sent = stream.send_oob(b"!").expect("send OOB");
        assert_eq!(sent, 1);
    }

    #[test]
    fn send_raw_delivers_bytes() {
        let (listener, stream) = loopback_pair();
        let (mut peer, _) = listener.accept().expect("accept");

        let sent = stream.send_raw(b"ping").expect("send");
        assert_eq!(sent, 4);

        let mut buffer = [0u8; 4];
        peer.read_exact(&mut buffer).expect("read");
        assert_eq!(&buffer, b"ping");
    }

    #[test]
    fn exposed_descriptor_is_not_negative() {
        let (_listener, stream) = loopback_pair();
        assert!(stream.as_raw_fd() >= 0);
    }
}
