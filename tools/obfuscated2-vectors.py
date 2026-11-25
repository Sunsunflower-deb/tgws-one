#!/usr/bin/env python3
"""Reference vectors for the obfuscated2 tests: tests/TgwsOne.Engine.Tests/Fixtures/obfuscated2-reference.json.

The engine's handshake is a port of tg-ws-proxy's, which does its AES-CTR with the `cryptography` library. The
other tests of that handshake are written with the engine's own primitives, so a mistake mirrored on both sides
(a counter read in the wrong byte order, a key that is not reversed) passes them. These vectors come from the
outside and cannot.

Each case is one bridged connection as Python computes it: the client's handshake, a relay init for Telegram, and
both directions re-encrypted. The functions below are the upstream ones (_try_handshake, _generate_relay_init,
_build_crypto_ctx in proxy/tg_ws_proxy.py at v1.11.4) written out with that library.

    python3 tools/obfuscated2-vectors.py        # rewrites the fixture; needs the `cryptography` package

Everything is drawn from a fixed seed, so running this again gives byte for byte the same file.
"""
import hashlib
import json
import random
import struct
from pathlib import Path

from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes

FIXTURE = Path(__file__).resolve().parent.parent / "tests" / "TgwsOne.Engine.Tests" / "Fixtures" / "obfuscated2-reference.json"
CLIENT_TAGS = [b"\xef\xef\xef\xef", b"\xee\xee\xee\xee", b"\xdd\xdd\xdd\xdd"]     # abridged, intermediate, padded
RELAY_RESERVED_STARTS = {b"HEAD", b"POST", b"GET ", b"\xee\xee\xee\xee", b"\xdd\xdd\xdd\xdd",
                         b"\x16\x03\x01\x02", b"OPTI"}
DC_INDEXES = [1, 2, -2, 4, -4, 5, 203, -203, 10002, -10002, 32767, -32768]        # media is negative; test DCs are 10000+
STREAM_LENGTHS = [0, 1, 15, 16, 17, 31, 48, 100, 255, 1000]
SEED = 0x0BF02C7
CASES = 40


def ctr(key, iv):
    return Cipher(algorithms.AES(key), modes.CTR(iv)).encryptor()


def client_handshake(secret, tag, dc_idx, rng):
    """What a Telegram client sends: random bytes whose encrypted tail names the protocol and the DC."""
    while True:
        rnd = bytearray(rng.randbytes(64))
        if rnd[0] != 0xEF and bytes(rnd[:4]) not in CLIENT_TAGS and rnd[4:8] != b"\0" * 4:
            break
    key = hashlib.sha256(bytes(rnd[8:40]) + secret).digest()
    tail_plain = tag + struct.pack("<h", dc_idx) + rng.randbytes(2)
    encrypted = ctr(key, bytes(rnd[40:56])).update(bytes(rnd))
    for i in range(8):
        rnd[56 + i] = tail_plain[i] ^ encrypted[56 + i] ^ rnd[56 + i]
    return bytes(rnd)


def try_handshake(handshake, secret):
    dec_prekey_iv = handshake[8:56]
    dec_key = hashlib.sha256(dec_prekey_iv[:32] + secret).digest()
    dec_iv = int.from_bytes(dec_prekey_iv[32:48], "big").to_bytes(16, "big")
    decrypted = ctr(dec_key, dec_iv).update(handshake)
    proto_tag = decrypted[56:60]
    if proto_tag not in CLIENT_TAGS:
        return None
    dc_idx = int.from_bytes(decrypted[60:62], "little", signed=True)
    return abs(dc_idx), dc_idx < 0, proto_tag, dec_prekey_iv


def relay_random(rng):
    while True:
        rnd = rng.randbytes(64)
        if rnd[0] != 0xEF and rnd[:4] not in RELAY_RESERVED_STARTS and rnd[4:8] != b"\0" * 4:
            return rnd


def seal_relay_init(rnd, tag, dc_idx):
    """The 64 random bytes with the protocol tag and the signed DC number sealed into their tail.

    Upstream draws the two padding bytes after the DC separately; here they are the buffer's own last two bytes,
    which the sealed tail replaces anyway. That keeps the result a function of the buffer alone, as the engine's is.
    """
    encrypted = ctr(rnd[8:40], rnd[40:56]).update(rnd)
    tail_plain = tag + struct.pack("<h", dc_idx) + rnd[62:64]
    tail = bytes(tail_plain[i] ^ encrypted[56 + i] ^ rnd[56 + i] for i in range(8))
    return rnd[:56] + tail


def crypto_ctx(client_dec_prekey_iv, secret, relay_init):
    clt_dec = ctr(hashlib.sha256(client_dec_prekey_iv[:32] + secret).digest(), client_dec_prekey_iv[32:48])
    clt_dec.update(b"\0" * 64)                          # past the client's 64-byte handshake
    clt_enc_prekey_iv = client_dec_prekey_iv[::-1]
    clt_enc = ctr(hashlib.sha256(clt_enc_prekey_iv[:32] + secret).digest(), clt_enc_prekey_iv[32:48])
    relay_dec_prekey_iv = relay_init[8:56][::-1]
    tg_dec = ctr(relay_dec_prekey_iv[:32], relay_dec_prekey_iv[32:48])
    tg_enc = ctr(relay_init[8:40], relay_init[40:56])
    tg_enc.update(b"\0" * 64)                           # past the relay init already sent
    return clt_dec, clt_enc, tg_enc, tg_dec


def read_sizes(total, rng):
    """How many bytes each read delivers; sums to total."""
    if total == 0:
        return []
    cuts = sorted(rng.sample(range(1, total), rng.randint(0, min(8, total - 1))))
    starts, prev = [], 0
    for cut in cuts:
        starts.append(cut - prev)
        prev = cut
    starts.append(total - prev)
    return starts


def re_encrypt(stream, first, second, reads):
    wire, offset = b"", 0
    for count in reads:
        wire += second.update(first.update(stream[offset:offset + count]))
        offset += count
    return wire


def main():
    rng = random.Random(SEED)
    cases = []
    for index in range(CASES):
        tag = CLIENT_TAGS[index % len(CLIENT_TAGS)]
        dc_idx = DC_INDEXES[index % len(DC_INDEXES)]
        secret = rng.randbytes(32 if index % 4 == 3 else 16)
        handshake = client_handshake(secret, tag, dc_idx, rng)
        rnd = relay_random(rng)
        relay_init = seal_relay_init(rnd, tag, dc_idx)

        parsed = try_handshake(handshake, secret)
        assert parsed is not None and parsed[:3] == (abs(dc_idx), dc_idx < 0, tag), index
        clt_dec, clt_enc, tg_enc, tg_dec = crypto_ctx(parsed[3], secret, relay_init)
        up = rng.randbytes(STREAM_LENGTHS[index % len(STREAM_LENGTHS)])
        down = rng.randbytes(STREAM_LENGTHS[(index * 7) % len(STREAM_LENGTHS)])
        up_reads, down_reads = read_sizes(len(up), rng), read_sizes(len(down), rng)

        cases.append({
            "dcIdx": dc_idx,
            "secret": secret.hex(),
            "handshake": handshake.hex(),
            "relayRandom": rnd.hex(),
            "relayInit": relay_init.hex(),
            "expect": {"dc": parsed[0], "isMedia": parsed[1], "protoTag": struct.unpack("<I", parsed[2])[0]},
            "up": up.hex(), "upReads": up_reads, "upWire": re_encrypt(up, clt_dec, tg_enc, up_reads).hex(),
            "down": down.hex(), "downReads": down_reads, "downWire": re_encrypt(down, tg_dec, clt_enc, down_reads).hex(),
        })

    wrong_key = rng.randbytes(16)
    wrong_secret = client_handshake(rng.randbytes(16), CLIENT_TAGS[1], 2, rng)
    zeros = b"\0" * 64
    zeros_key = rng.randbytes(16)
    assert try_handshake(wrong_secret, wrong_key) is None
    assert try_handshake(zeros, zeros_key) is None

    document = {
        "source": "tools/obfuscated2-vectors.py",
        "cases": cases,
        "rejects": [{"secret": wrong_key.hex(), "handshake": wrong_secret.hex()},
                    {"secret": zeros_key.hex(), "handshake": zeros.hex()}],
    }
    FIXTURE.parent.mkdir(parents=True, exist_ok=True)
    FIXTURE.write_text(json.dumps(document, indent=1) + "\n")
    print(f"{FIXTURE}: {len(cases)} cases, {len(document['rejects'])} rejects, {FIXTURE.stat().st_size // 1024} KB")


if __name__ == "__main__":
    main()
