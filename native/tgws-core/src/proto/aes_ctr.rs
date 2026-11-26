use aes::{Aes128, Aes192, Aes256};
use ctr::Ctr128BE;
use ctr::cipher::{KeyIvInit, StreamCipher};

const BLOCK_SIZE: usize = 16;
const MAX_BATCH_BLOCKS: usize = 256;
const MAX_BATCH_BYTES: usize = MAX_BATCH_BLOCKS * BLOCK_SIZE;

enum CtrCipher {
    Aes128(Ctr128BE<Aes128>),
    Aes192(Ctr128BE<Aes192>),
    Aes256(Ctr128BE<Aes256>),
}

impl CtrCipher {
    fn new(key: &[u8], iv: &[u8]) -> Result<Self, &'static str> {
        match key.len() {
            16 => Ctr128BE::<Aes128>::new_from_slices(key, iv)
                .map(Self::Aes128)
                .map_err(|_| "Invalid key/IV length"),
            24 => Ctr128BE::<Aes192>::new_from_slices(key, iv)
                .map(Self::Aes192)
                .map_err(|_| "Invalid key/IV length"),
            32 => Ctr128BE::<Aes256>::new_from_slices(key, iv)
                .map(Self::Aes256)
                .map_err(|_| "Invalid key/IV length"),
            _ => Err("AES key must be 16/24/32 bytes"),
        }
    }

    fn apply_keystream(&mut self, data: &mut [u8]) {
        match self {
            Self::Aes128(cipher) => cipher.apply_keystream(data),
            Self::Aes192(cipher) => cipher.apply_keystream(data),
            Self::Aes256(cipher) => cipher.apply_keystream(data),
        }
    }
}

pub struct AesCtr {
    cipher: CtrCipher,
    keystream: [u8; MAX_BATCH_BYTES],
    keystream_length: usize,
    used: usize,
}

impl AesCtr {
    pub fn new(key: &[u8], iv: &[u8]) -> Result<Self, &'static str> {
        if !matches!(key.len(), 16 | 24 | 32) {
            return Err("AES key must be 16/24/32 bytes");
        }
        if iv.len() != BLOCK_SIZE {
            return Err("CTR IV must be 16 bytes");
        }

        Ok(Self {
            cipher: CtrCipher::new(key, iv)?,
            keystream: [0u8; MAX_BATCH_BYTES],
            keystream_length: 0,
            used: 0,
        })
    }

    pub fn transform(&mut self, input: &[u8], output: &mut [u8]) {
        assert!(output.len() >= input.len(), "output is shorter than input");

        let mut offset = 0;
        while offset < input.len() {
            if self.used == self.keystream_length {
                let remaining = input.len() - offset;
                let blocks = remaining.div_ceil(BLOCK_SIZE).min(MAX_BATCH_BLOCKS);
                self.refill(blocks);
            }
            let count = (self.keystream_length - self.used).min(input.len() - offset);
            for (index, byte) in input[offset..offset + count].iter().enumerate() {
                output[offset + index] = *byte ^ self.keystream[self.used + index];
            }
            self.used += count;
            offset += count;
        }
    }

    pub fn transform_vec(&mut self, input: &[u8]) -> Vec<u8> {
        let mut output = vec![0u8; input.len()];
        self.transform(input, &mut output);
        output
    }

    pub fn transform_in_place(&mut self, data: &mut [u8]) {
        let mut offset = 0;
        while offset < data.len() {
            if self.used == self.keystream_length {
                let remaining = data.len() - offset;
                let blocks = remaining.div_ceil(BLOCK_SIZE).min(MAX_BATCH_BLOCKS);
                self.refill(blocks);
            }
            let count = (self.keystream_length - self.used).min(data.len() - offset);
            for (index, byte) in data[offset..offset + count].iter_mut().enumerate() {
                *byte ^= self.keystream[self.used + index];
            }
            self.used += count;
            offset += count;
        }
    }

    pub fn skip(&mut self, bytes: usize) {
        if bytes == 0 {
            return;
        }

        let available = self.keystream_length - self.used;
        if bytes <= available {
            self.used += bytes;
            return;
        }

        let mut remaining = bytes - available;
        self.used = 0;
        self.keystream_length = 0;

        while remaining > 0 {
            let count = remaining.min(MAX_BATCH_BYTES);
            self.keystream[..count].fill(0);
            self.cipher.apply_keystream(&mut self.keystream[..count]);
            remaining -= count;
        }
    }

    fn refill(&mut self, blocks: usize) {
        let length = blocks * BLOCK_SIZE;
        self.keystream[..length].fill(0);
        self.cipher.apply_keystream(&mut self.keystream[..length]);
        self.keystream_length = length;
        self.used = 0;
    }
}

#[cfg(test)]
mod tests {
    use super::AesCtr;

    #[test]
    fn aes_ctr_basic_encrypt_decrypt() {
        let key = [0x00u8; 16];
        let iv = [0x01u8; 16];
        let plaintext = b"Hello, World! This is a test message for AES-CTR.";

        let mut enc = AesCtr::new(&key, &iv).unwrap();
        let ciphertext = enc.transform_vec(plaintext);

        let mut dec = AesCtr::new(&key, &iv).unwrap();
        let decrypted = dec.transform_vec(&ciphertext);

        assert_eq!(decrypted, plaintext);
    }

    #[test]
    fn aes_ctr_skip_matches_transform_zeros() {
        let key = [0x42u8; 16];
        let iv = [0x24u8; 16];
        let data = vec![0xAAu8; 100];

        let mut whole = AesCtr::new(&key, &iv).unwrap();
        let full = whole.transform_vec(&data);

        let mut skipped = AesCtr::new(&key, &iv).unwrap();
        skipped.skip(50);
        let mut tail = vec![0u8; 50];
        skipped.transform(&data[50..], &mut tail);

        assert_eq!(&full[50..], &tail[..]);
    }

    #[test]
    fn aes_ctr_batching_consistency() {
        let key = [0x11u8; 16];
        let iv = [0x22u8; 16];
        let plaintext = vec![0x55u8; 5000];

        let mut one = AesCtr::new(&key, &iv).unwrap();
        let expected = one.transform_vec(&plaintext);

        let mut chunked = AesCtr::new(&key, &iv).unwrap();
        let mut actual = vec![0u8; plaintext.len()];
        let mut offset = 0;
        for chunk in plaintext.chunks(123) {
            chunked.transform(chunk, &mut actual[offset..offset + chunk.len()]);
            offset += chunk.len();
        }

        assert_eq!(expected, actual);
    }

    #[test]
    fn aes_ctr_different_key_sizes() {
        let iv = [0x00u8; 16];
        for key_size in [16, 24, 32] {
            let key = vec![0x33u8; key_size];
            let mut ctr = AesCtr::new(&key, &iv).unwrap();
            let out = ctr.transform_vec(&[0x44u8; 16]);
            assert_ne!(out, [0u8; 16]);
        }
    }

    #[test]
    fn aes_ctr_invalid_key_length() {
        let iv = [0x00u8; 16];
        assert!(AesCtr::new(&[0x00u8; 15], &iv).is_err());
        assert!(AesCtr::new(&[0x00u8; 33], &iv).is_err());
    }

    #[test]
    fn aes_ctr_invalid_iv_length() {
        let key = [0x00u8; 16];
        assert!(AesCtr::new(&key, &[0x00u8; 15]).is_err());
        assert!(AesCtr::new(&key, &[0x00u8; 17]).is_err());
    }
}
