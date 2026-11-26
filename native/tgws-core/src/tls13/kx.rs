//! X25519 key agreement for the TLS 1.3 `key_share` extension.
//!
//! The ephemeral key pair is generated from a 32-byte seed supplied by a
//! [`Random32`] source, mirroring the injection style used by
//! [`crate::tls::client_hello`].  The trait implementations in that module are
//! deterministic test generators; a production caller must supply a
//! cryptographically secure source of 32 random bytes.

use core::fmt;

use x25519_dalek::{PublicKey, StaticSecret};

use super::TlsError;
use crate::tls::client_hello::Random32;

/// An X25519 ephemeral key pair.
pub struct X25519KeyPair {
    secret: StaticSecret,
    public: [u8; 32],
}

impl fmt::Debug for X25519KeyPair {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.debug_struct("X25519KeyPair")
            .field("public", &self.public)
            .finish_non_exhaustive()
    }
}

impl X25519KeyPair {
    /// Creates a key pair by injecting a 32-byte private seed.
    ///
    /// X25519 clamps the scalar during multiplication, so the seed does not
    /// need to be pre-clamped.
    #[must_use]
    pub fn from_seed(seed: [u8; 32]) -> Self {
        let secret = StaticSecret::from(seed);
        let public = PublicKey::from(&secret).to_bytes();
        Self { secret, public }
    }

    /// Creates a key pair from `rng`, as the ClientHello builder does.
    ///
    /// The RNG must be cryptographically secure in production; the deterministic
    /// generators in [`crate::tls::client_hello`] are for tests only.
    #[must_use]
    pub fn generate(rng: &mut dyn Random32) -> Self {
        let mut seed = [0u8; 32];
        rng.fill_bytes(&mut seed);
        Self::from_seed(seed)
    }

    /// Returns the public key that goes into a `KeyShareEntry`.
    #[must_use]
    pub fn public_key(&self) -> [u8; 32] {
        self.public
    }

    /// Computes the X25519 shared secret with a peer public key.
    ///
    /// # Errors
    ///
    /// Returns [`TlsError::NonContributoryKeyShare`] when the peer key is a
    /// low-order point and the shared secret is all zero (RFC 8446,
    /// section 7.4.1).
    pub fn diffie_hellman(&self, peer_public: &[u8; 32]) -> Result<[u8; 32], TlsError> {
        let peer = PublicKey::from(*peer_public);
        let shared = self.secret.diffie_hellman(&peer);
        if !shared.was_contributory() {
            return Err(TlsError::NonContributoryKeyShare);
        }
        Ok(shared.to_bytes())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::tls::client_hello::{ClientHelloBuilder, DefaultProfile};
    use crate::tls::parse::parse_client_hello;

    fn bytes(hex_string: &str) -> Vec<u8> {
        hex::decode(hex_string).expect("test vector is valid hex")
    }

    fn array32(hex_string: &str) -> [u8; 32] {
        let decoded = bytes(hex_string);
        let mut out = [0u8; 32];
        out.copy_from_slice(&decoded);
        out
    }

    #[test]
    fn rfc7748_x25519_agreement() {
        // RFC 7748, section 6.1.
        let alice_private =
            array32("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
        let alice_public =
            array32("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a");
        let bob_private =
            array32("5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb");
        let bob_public =
            array32("de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f");
        let expected_shared =
            array32("4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742");

        let alice = X25519KeyPair::from_seed(alice_private);
        let bob = X25519KeyPair::from_seed(bob_private);

        assert_eq!(alice.public_key(), alice_public);
        assert_eq!(bob.public_key(), bob_public);

        let alice_shared = alice
            .diffie_hellman(&bob_public)
            .expect("agreement succeeds");
        let bob_shared = bob
            .diffie_hellman(&alice_public)
            .expect("agreement succeeds");
        assert_eq!(alice_shared, expected_shared);
        assert_eq!(alice_shared, bob_shared);
    }

    #[test]
    fn rejects_non_contributory_peer_key() {
        let local = X25519KeyPair::from_seed([0x42; 32]);
        assert_eq!(
            local.diffie_hellman(&[0u8; 32]),
            Err(TlsError::NonContributoryKeyShare)
        );
    }

    #[test]
    fn generate_is_deterministic_for_a_seeded_rng() {
        use crate::tls::client_hello::SplitMix64;

        let mut first_rng = SplitMix64::new(99);
        let mut second_rng = SplitMix64::new(99);
        let first = X25519KeyPair::generate(&mut first_rng);
        let second = X25519KeyPair::generate(&mut second_rng);
        assert_eq!(first.public_key(), second.public_key());
    }

    /// RNG stub that injects `key` on the third `fill_bytes` call, which is the
    /// key_share draw inside `ClientHelloBuilder::build_inner`.
    struct KeyShareRng {
        key: [u8; 32],
        fills: usize,
    }

    impl Random32 for KeyShareRng {
        fn next_u32(&mut self) -> u32 {
            0x9e37_79b9
        }

        fn fill_bytes(&mut self, dst: &mut [u8]) {
            self.fills += 1;
            if self.fills == 3 && dst.len() == 32 {
                dst.copy_from_slice(&self.key);
            } else {
                for (index, byte) in dst.iter_mut().enumerate() {
                    *byte = (index as u8).wrapping_add(self.fills as u8);
                }
            }
        }
    }

    #[test]
    fn client_hello_key_share_matches_generated_key_pair() {
        let client = X25519KeyPair::from_seed([0x11; 32]);
        let client_public = client.public_key();
        let mut rng = KeyShareRng {
            key: client_public,
            fills: 0,
        };
        let flight = ClientHelloBuilder::new("example.com")
            .with_profile(DefaultProfile)
            .build(&mut rng);

        let wire_public = find_client_key_share(&flight);
        assert_eq!(wire_public, client_public);

        let server = X25519KeyPair::from_seed([0x22; 32]);
        let server_public = server.public_key();

        let client_shared = client
            .diffie_hellman(&server_public)
            .expect("client agreement");
        let server_shared = server
            .diffie_hellman(&wire_public)
            .expect("server agreement");
        assert_eq!(client_shared, server_shared);
    }

    fn find_client_key_share(flight: &[u8]) -> [u8; 32] {
        let layout = parse_client_hello(flight).expect("ClientHello parses");
        let extensions = &flight[layout.extensions_start..layout.extensions_end];
        let mut pos = 0;
        while pos + 4 <= extensions.len() {
            let extension_type = u16::from_be_bytes([extensions[pos], extensions[pos + 1]]);
            let length = u16::from_be_bytes([extensions[pos + 2], extensions[pos + 3]]) as usize;
            let data = &extensions[pos + 4..pos + 4 + length];
            if extension_type == 0x0033 {
                let key_len = u16::from_be_bytes([data[4], data[5]]) as usize;
                let mut key = [0u8; 32];
                key.copy_from_slice(&data[6..6 + key_len]);
                return key;
            }
            pos += 4 + length;
        }
        panic!("ClientHello has no key_share extension");
    }
}
