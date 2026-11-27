//! A small warm pool of WebSocket connections.
//!
//! This is the synchronous counterpart of `WebSocketPool` from the C# engine.
//! A bucket holds up to `pool_size` ready connections per (DC, media, test)
//! key; a client takes one, and an exhausted bucket is filled again on the next
//! request.  When no client has asked for a socket for `poolidle` seconds the
//! idle entries are closed, matching `WebSocketPool.CloseIfIdle`.
//!
//! The clock is injected through [`TimeProvider`] so the idle timeout can be
//! tested without sleeping.  The pool knows nothing about TLS or routes: the
//! connector closure makes a connection and reports which route produced it.

use std::collections::HashMap;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use super::routes::Route;

/// Which Telegram endpoint a bucket serves.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub struct PoolKey {
    /// Real DC number.
    pub dc: i32,
    /// Media (`-dc`) variant.
    pub is_media: bool,
    /// Test-DC endpoint (`/apiws_test`).
    pub is_test_dc: bool,
}

impl PoolKey {
    /// Creates a key for one endpoint flavour.
    #[must_use]
    pub fn new(dc: i32, is_media: bool, is_test_dc: bool) -> Self {
        Self {
            dc,
            is_media,
            is_test_dc,
        }
    }
}

/// The clock seam the idle timeout needs.
pub trait TimeProvider: Send + Sync {
    /// Current monotonic instant.
    fn now(&self) -> Instant;
}

/// The production clock.
#[derive(Debug, Default, Clone, Copy)]
pub struct SystemTimeProvider;

impl TimeProvider for SystemTimeProvider {
    fn now(&self) -> Instant {
        Instant::now()
    }
}

/// A clock a test moves by hand.
#[derive(Debug)]
pub struct ManualTimeProvider {
    now: Mutex<Instant>,
}

impl ManualTimeProvider {
    /// Starts at `base`.
    #[must_use]
    pub fn new(base: Instant) -> Self {
        Self {
            now: Mutex::new(base),
        }
    }

    /// Moves the clock forward.
    pub fn advance(&self, delta: Duration) {
        let mut now = self.now.lock().expect("manual clock mutex");
        *now += delta;
    }
}

impl TimeProvider for ManualTimeProvider {
    fn now(&self) -> Instant {
        *self.now.lock().expect("manual clock mutex")
    }
}

/// Builds one pooled connection and says which route made it.
pub type Connector<T> = dyn Fn(&PoolKey) -> Result<(T, Route), String> + Send + Sync;

struct Pooled<T> {
    socket: T,
    route: Route,
}

struct Inner<T> {
    buckets: HashMap<PoolKey, Vec<Pooled<T>>>,
    last_client_at: Instant,
}

/// A warm pool of connections of type `T`.
pub struct WsPool<T> {
    pool_size: usize,
    idle_timeout: Duration,
    clock: Arc<dyn TimeProvider>,
    connector: Arc<Connector<T>>,
    inner: Mutex<Inner<T>>,
}

impl<T> WsPool<T> {
    /// Creates a pool.  `idle_timeout` of zero disables the idle close, as
    /// `poolidle=0` does in the app.
    #[must_use]
    pub fn new(
        pool_size: usize,
        idle_timeout: Duration,
        connector: Arc<Connector<T>>,
        clock: Arc<dyn TimeProvider>,
    ) -> Self {
        let now = clock.now();
        Self {
            pool_size,
            idle_timeout,
            clock,
            connector,
            inner: Mutex::new(Inner {
                buckets: HashMap::new(),
                last_client_at: now,
            }),
        }
    }

    /// Maximum idle entries per bucket.
    #[must_use]
    pub fn pool_size(&self) -> usize {
        self.pool_size
    }

    /// Takes a connection, filling the bucket first when it ran dry.
    ///
    /// Returns `None` only when every connection attempt of the connector
    /// failed.
    pub fn get(&self, key: &PoolKey) -> Option<(T, Route)> {
        let now = self.clock.now();
        let mut inner = self.inner.lock().expect("pool mutex");
        self.expire_idle(&mut inner, now);
        inner.last_client_at = now;

        if let Some(entry) = inner.buckets.get_mut(key).and_then(Vec::pop) {
            return Some((entry.socket, entry.route));
        }

        self.fill(&mut inner, key);
        let entry = inner.buckets.get_mut(key).and_then(Vec::pop)?;
        Some((entry.socket, entry.route))
    }

    /// Returns an unused connection to the pool.  `false` means the bucket is
    /// full (or idle) and the caller should close the socket instead.
    pub fn checkin(&self, key: &PoolKey, socket: T, route: Route) -> bool {
        let now = self.clock.now();
        let mut inner = self.inner.lock().expect("pool mutex");
        self.expire_idle(&mut inner, now);
        inner.last_client_at = now;

        let bucket = inner.buckets.entry(*key).or_default();
        if bucket.len() >= self.pool_size {
            return false;
        }
        bucket.push(Pooled { socket, route });
        true
    }

    /// Fills a bucket to `pool_size` synchronously; returns the idle count
    /// afterwards.
    pub fn warm(&self, key: &PoolKey) -> usize {
        let now = self.clock.now();
        let mut inner = self.inner.lock().expect("pool mutex");
        self.expire_idle(&mut inner, now);
        inner.last_client_at = now;
        self.fill(&mut inner, key);
        inner.buckets.get(key).map_or(0, Vec::len)
    }

    /// Closes every idle entry after `poolidle` seconds without a client and
    /// returns how many were dropped.
    pub fn close_idle(&self) -> usize {
        let now = self.clock.now();
        let mut inner = self.inner.lock().expect("pool mutex");
        if !self.idle_expired(&inner, now) {
            return 0;
        }
        let sleeping = take_idle(&mut inner);
        let count = sleeping.len();
        drop(sleeping);
        count
    }

    /// Idle entries across all buckets.
    #[must_use]
    pub fn idle_count(&self) -> usize {
        let inner = self.inner.lock().expect("pool mutex");
        inner.buckets.values().map(Vec::len).sum()
    }

    /// Idle entries in one bucket.
    #[must_use]
    pub fn idle_for(&self, key: &PoolKey) -> usize {
        let inner = self.inner.lock().expect("pool mutex");
        inner.buckets.get(key).map_or(0, Vec::len)
    }

    fn idle_expired(&self, inner: &Inner<T>, now: Instant) -> bool {
        self.idle_timeout > Duration::ZERO
            && now.saturating_duration_since(inner.last_client_at) >= self.idle_timeout
    }

    fn expire_idle(&self, inner: &mut Inner<T>, now: Instant) {
        if self.idle_expired(inner, now) {
            let sleeping = take_idle(inner);
            drop(sleeping);
        }
    }

    fn fill(&self, inner: &mut Inner<T>, key: &PoolKey) {
        if self.pool_size == 0 {
            return;
        }
        let have = inner.buckets.get(key).map_or(0, Vec::len);
        let needed = self.pool_size.saturating_sub(have);
        if needed == 0 {
            return;
        }

        let mut created = Vec::with_capacity(needed);
        for _ in 0..needed {
            match (self.connector)(key) {
                Ok((socket, route)) => created.push(Pooled { socket, route }),
                Err(_) => break,
            }
        }
        inner.buckets.entry(*key).or_default().extend(created);
    }
}

fn take_idle<T>(inner: &mut Inner<T>) -> Vec<Pooled<T>> {
    let mut sleeping = Vec::new();
    for bucket in inner.buckets.values_mut() {
        sleeping.append(bucket);
    }
    sleeping
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::{AtomicUsize, Ordering};

    struct Tracked {
        _id: usize,
        drops: Arc<AtomicUsize>,
    }

    impl Drop for Tracked {
        fn drop(&mut self) {
            self.drops.fetch_add(1, Ordering::SeqCst);
        }
    }

    fn key() -> PoolKey {
        PoolKey::new(2, false, false)
    }

    fn pool(
        size: usize,
        idle_seconds: u64,
    ) -> (WsPool<Tracked>, Arc<ManualTimeProvider>, Arc<AtomicUsize>) {
        let base = Instant::now();
        let clock = Arc::new(ManualTimeProvider::new(base));
        let drops = Arc::new(AtomicUsize::new(0));
        let created = Arc::new(AtomicUsize::new(0));
        let connector_drops = Arc::clone(&drops);
        let connector = Arc::new(move |_key: &PoolKey| {
            let id = created.fetch_add(1, Ordering::SeqCst);
            Ok((
                Tracked {
                    _id: id,
                    drops: Arc::clone(&connector_drops),
                },
                Route::Ws,
            ))
        });
        let pool = WsPool::new(
            size,
            Duration::from_secs(idle_seconds),
            connector,
            Arc::clone(&clock) as Arc<dyn TimeProvider>,
        );
        (pool, clock, drops)
    }

    #[test]
    fn takes_and_returns_a_connection() {
        let (pool, _clock, drops) = pool(2, 30);
        let (socket, route) = pool.get(&key()).expect("warm connection");
        assert_eq!(route, Route::Ws);
        assert_eq!(pool.idle_for(&key()), 1, "one kept warm after the take");

        assert!(pool.checkin(&key(), socket, route));
        assert_eq!(pool.idle_for(&key()), 2);
        assert_eq!(drops.load(Ordering::SeqCst), 0);
    }

    #[test]
    fn idle_timeout_closes_and_the_next_take_refills() {
        let (pool, clock, drops) = pool(2, 30);
        let (socket, route) = pool.get(&key()).expect("warm connection");
        assert!(pool.checkin(&key(), socket, route));
        assert_eq!(pool.idle_count(), 2);

        clock.advance(Duration::from_secs(30));
        assert_eq!(pool.close_idle(), 2);
        assert_eq!(pool.idle_count(), 0);
        assert_eq!(drops.load(Ordering::SeqCst), 2);

        let (socket, _) = pool.get(&key()).expect("refilled connection");
        assert_eq!(pool.idle_for(&key()), 1, "get fills the bucket again");
        drop(socket);
        assert_eq!(drops.load(Ordering::SeqCst), 3);
    }

    #[test]
    fn a_failing_connector_leaves_the_bucket_empty() {
        let clock = Arc::new(ManualTimeProvider::new(Instant::now()));
        let connector = Arc::new(|_key: &PoolKey| Err("dial failed".to_string()));
        let pool = WsPool::<usize>::new(
            4,
            Duration::from_secs(30),
            connector,
            clock as Arc<dyn TimeProvider>,
        );
        assert!(pool.get(&key()).is_none());
        assert_eq!(pool.idle_count(), 0);
    }
}
