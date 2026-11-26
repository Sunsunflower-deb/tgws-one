#!/usr/bin/env python3
"""Read a classic libpcap capture (linktype EN10MB, as tcpdump -w writes it) and inspect the TCP flow that goes to
the server port. Pure standard library: this runs on the root stend where no extra packages may be installed.

Default mode prints one row per TCP segment that carries data towards the server: order, relative sequence, length,
IPv4 TTL, the URG flag and the SNI when the segment starts at the beginning of a ClientHello. --json prints the same
rows for other tools. --check runs one of the assertions tests/root/netns-desync.sh needs and exits non-zero with the
exact item that did not match, so a failure names the capture fact rather than a strategy.
"""
import argparse
import json
import struct
import sys

TCP_FLAG_URG = 0x20


def read_pcap(path):
    with open(path, "rb") as handle:
        data = handle.read()
    if len(data) < 24:
        raise ValueError(f"{path}: too short for a pcap header")

    magic = data[:4]
    if magic in (b"\xd4\xc3\xb2\xa1", b"\x4d\x3c\xb2\xa1"):
        order = "<"
    elif magic in (b"\xa1\xb2\xc3\xd4", b"\xa1\xb2\x3c\x4d"):
        order = ">"
    else:
        raise ValueError(f"{path}: not a classic pcap file (magic {magic.hex()})")

    _, major, minor, _, _, snaplen, linktype = struct.unpack(order + "IHHIIII", data[:24])
    if linktype != 1:
        raise ValueError(f"{path}: linktype {linktype}, only EN10MB (1) is supported")

    packets = []
    offset = 24
    while offset + 16 <= len(data):
        ts_sec, ts_usec, included, original = struct.unpack(order + "IIII", data[offset:offset + 16])
        offset += 16
        if offset + included > len(data):
            break
        frame = data[offset:offset + included]
        offset += included
        packets.append((ts_sec + ts_usec / 1_000_000.0, original, frame))
    return packets


def parse_tcp(frame):
    if len(frame) < 14:
        return None
    ethertype = struct.unpack("!H", frame[12:14])[0]
    l3 = 14
    while ethertype in (0x8100, 0x88A8):  # VLAN tags
        if len(frame) < l3 + 4:
            return None
        ethertype = struct.unpack("!H", frame[l3 + 2:l3 + 4])[0]
        l3 += 4
    if ethertype != 0x0800:
        return None

    ip = frame[l3:]
    if len(ip) < 20 or ip[0] >> 4 != 4:
        return None
    ihl = (ip[0] & 0x0F) * 4
    if ihl < 20 or len(ip) < ihl or ip[9] != 6:  # TCP
        return None

    total_length = struct.unpack("!H", ip[2:4])[0]
    ttl = ip[8]
    tcp = ip[ihl:]
    if len(tcp) < 20:
        return None

    source_port, dest_port, seq, _ack, offset_flags = struct.unpack("!HHIIH", tcp[:14])
    data_offset = (offset_flags >> 12) * 4
    flags = offset_flags & 0x1FF
    if data_offset < 20 or data_offset > len(tcp):
        return None
    payload = tcp[data_offset:]
    if total_length:
        payload_length = total_length - ihl - data_offset
        if 0 <= payload_length < len(payload):
            payload = payload[:payload_length]

    return {
        "source_port": source_port,
        "dest_port": dest_port,
        "seq": seq,
        "ttl": ttl,
        "urg": bool(flags & TCP_FLAG_URG),
        "payload": payload,
    }


def parse_sni(payload):
    """Return the SNI of a payload that starts at a ClientHello, tolerating a segment that carries only its start."""
    if len(payload) < 5 or payload[0] != 0x16 or payload[1] != 0x03:
        return None
    record_length = struct.unpack("!H", payload[3:5])[0]
    record = payload[5:5 + record_length]
    if len(record) < 4 or record[0] != 0x01:  # ClientHello
        return None
    handshake_length = int.from_bytes(record[1:4], "big")
    body = record[4:4 + handshake_length]
    offset = 2 + 32  # legacy_version + random
    if offset >= len(body):
        return None
    offset += 1 + body[offset]  # session id
    if offset + 2 > len(body):
        return None
    cipher_length = int.from_bytes(body[offset:offset + 2], "big")
    offset += 2 + cipher_length
    if offset >= len(body):
        return None
    offset += 1 + body[offset]  # compression methods
    if offset + 2 > len(body):
        return None
    extensions_length = int.from_bytes(body[offset:offset + 2], "big")
    offset += 2
    end = min(len(body), offset + extensions_length)
    while offset + 4 <= end:
        extension_type = int.from_bytes(body[offset:offset + 2], "big")
        extension_length = int.from_bytes(body[offset + 2:offset + 4], "big")
        offset += 4
        if extension_type == 0x0000:  # server_name
            data = body[offset:offset + extension_length]
            if len(data) < 5:
                return None
            name_length = int.from_bytes(data[3:5], "big")
            return data[5:5 + name_length].decode("ascii", "replace")
        offset += extension_length
    return None


def load_segments(path, dest_port):
    packets = read_pcap(path)
    parsed = []
    for timestamp, _original, frame in packets:
        tcp = parse_tcp(frame)
        if tcp is None or tcp["dest_port"] != dest_port or not tcp["payload"]:
            continue
        parsed.append((timestamp, tcp))

    if not parsed:
        return []

    base = min(tcp["seq"] for _timestamp, tcp in parsed)
    segments = []
    for index, (timestamp, tcp) in enumerate(parsed):
        payload = tcp["payload"]
        segments.append({
            "order": index,
            "time": round(timestamp, 6),
            "seq": tcp["seq"],
            "rel": (tcp["seq"] - base) & 0xFFFFFFFF,
            "len": len(payload),
            "ttl": tcp["ttl"],
            "urg": tcp["urg"],
            "sni": parse_sni(payload),
            "record_len": (struct.unpack("!H", payload[3:5])[0]
                           if len(payload) >= 5 and payload[0] == 0x16 else None),
        })
    return segments


def describe(segments):
    if not segments:
        return "в захвате нет сегментов с данными к серверу"
    return "; ".join(
        f"#{s['order']} rel={s['rel']} len={s['len']} ttl={s['ttl']}"
        f"{' URG' if s['urg'] else ''}"
        f"{' sni=' + s['sni'] if s['sni'] else ''}"
        for s in segments)


def check_split(segments, args):
    first = next((s for s in segments if s["rel"] == 0), None)
    if first is None:
        return False, "нет сегмента, начинающего полёт (rel=0)"
    if first["len"] != args.cut:
        return False, f"первый сегмент длиной {first['len']}, ожидалась позиция разреза {args.cut} ({describe(segments)})"
    second = next((s for s in segments if s["rel"] == args.cut and s["order"] > first["order"]), None)
    if second is None:
        return False, f"нет второго сегмента со смещением {args.cut} ({describe(segments)})"
    return True, f"первый сегмент {first['len']} байт, второй начинается со смещения {args.cut}"


def check_disorder_ttl(segments, args):
    low = next((s for s in segments if s["rel"] == 0 and s["ttl"] == 1), None)
    if low is None:
        return False, f"нет первого куска (rel=0) с TTL=1 ({describe(segments)})"
    if low["len"] != args.cut:
        return False, f"первый кусок длиной {low['len']}, ожидалась позиция {args.cut} ({describe(segments)})"
    rest = next((s for s in segments
                 if s["rel"] == args.cut and s["ttl"] == args.system_ttl), None)
    if rest is None:
        return False, f"нет второго куска (rel={args.cut}) с системным TTL={args.system_ttl} ({describe(segments)})"
    if low["order"] > rest["order"]:
        return False, f"второй кусок ушёл раньше первого ({describe(segments)})"
    return True, f"первый кусок TTL=1, второй TTL={args.system_ttl}"


def check_disorder_order(segments, args):
    if not segments:
        return False, "нет сегментов с данными к серверу"
    first = segments[0]
    if first["rel"] != args.cut:
        return False, f"первым на сервер пришёл сегмент со смещением {first['rel']}, ожидался {args.cut} ({describe(segments)})"
    retransmission = next((s for s in segments[1:] if s["rel"] == 0), None)
    if retransmission is None:
        return False, f"после второго куска не пришла ретрансмиссия первого (rel=0) ({describe(segments)})"
    return True, f"порядок на сервере: сначала rel={first['rel']}, затем ретрансмиссия rel=0"


def check_fake_dead(segments, args):
    fake = next((s for s in segments
                 if s["rel"] == 0 and s["len"] == args.cut and s["ttl"] == 1), None)
    if fake is None:
        return False, f"нет фейкового сегмента rel=0 длиной {args.cut} с TTL=1 ({describe(segments)})"
    if fake["sni"] != args.fake_sni:
        return False, f"SNI погибшего сегмента {fake['sni']!r}, ожидался {args.fake_sni!r} ({describe(segments)})"
    return True, f"фейк {fake['len']} байт, TTL=1, SNI={fake['sni']}"


def check_fake_alive(segments, args):
    if not segments:
        return False, "нет сегментов с данными к серверу"
    first = segments[0]
    if first["rel"] != args.cut:
        return False, f"первым на сервер пришёл сегмент со смещением {first['rel']}, ожидался {args.cut} ({describe(segments)})"
    fake = next((s for s in segments if s["sni"] == args.fake_sni), None)
    if fake is not None:
        return False, f"фейковый SNI {args.fake_sni!r} дошёл до сервера ({describe(segments)})"
    real = next((s for s in segments if s["rel"] == 0 and s["sni"] == args.real_sni), None)
    if real is None:
        return False, f"ретрансляция первого куска (rel=0) не несёт настоящий SNI {args.real_sni!r} ({describe(segments)})"
    return True, f"фейк не дошёл, сервер получил оригинал с SNI={real['sni']}"


def check_oob(segments, args):
    urgent = next((s for s in segments if s["urg"]), None)
    if urgent is None:
        return False, f"ни у одного сегмента с данными нет флага URG ({describe(segments)})"
    return True, f"сегмент #{urgent['order']} с флагом URG, len={urgent['len']}"


def check_none(segments, args):
    first = next((s for s in segments if s["rel"] == 0), None)
    if first is None:
        return False, "нет сегмента, начинающего полёт (rel=0)"
    if first["ttl"] != args.system_ttl:
        return False, f"TTL первого сегмента {first['ttl']}, ожидался системный {args.system_ttl}"
    if first["urg"]:
        return False, "у первого сегмента стоит флаг URG, хотя обхода нет"
    if first["record_len"] is None:
        return False, "первый сегмент не начинается с TLS-записи"
    if first["len"] < 5 + first["record_len"]:
        return False, (f"ClientHello разрезан: первый сегмент {first['len']} байт, "
                       f"а запись объявляет {first['record_len']} байт данных")
    duplicates = [s for s in segments if s["rel"] == 0]
    if len(duplicates) > 1:
        return False, f"первый кусок пришёл {len(duplicates)} раза (ретрансмиссия без обхода)"
    if any(s["urg"] or s["ttl"] != args.system_ttl for s in segments):
        return False, f"есть сегменты с низким TTL или URG ({describe(segments)})"
    return True, f"один сегмент {first['len']} байт с полным ClientHello, TTL={first['ttl']}, без URG"


CHECKS = {
    "split": check_split,
    "disorder-ttl": check_disorder_ttl,
    "disorder-order": check_disorder_order,
    "fake-dead": check_fake_dead,
    "fake-alive": check_fake_alive,
    "oob": check_oob,
    "none": check_none,
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--pcap", required=True)
    parser.add_argument("--dst-port", type=int, default=443)
    parser.add_argument("--json", action="store_true")
    parser.add_argument("--quiet", action="store_true")
    parser.add_argument("--check", choices=sorted(CHECKS))
    parser.add_argument("--cut", type=int)
    parser.add_argument("--system-ttl", type=int)
    parser.add_argument("--fake-sni")
    parser.add_argument("--real-sni")
    args = parser.parse_args()

    try:
        segments = load_segments(args.pcap, args.dst_port)
    except (OSError, ValueError) as error:
        print(f"pcap: {error}", file=sys.stderr)
        return 3

    if args.json:
        print(json.dumps(segments, ensure_ascii=False))
        return 0

    if not args.quiet:
        print(f"pcap {args.pcap}: сегментов с данными к порту {args.dst_port} — {len(segments)}")
        for s in segments:
            print(f"  #{s['order']} rel={s['rel']:<5} len={s['len']:<5} ttl={s['ttl']:<3}"
                  f"{' URG' if s['urg'] else '    '}"
                  f"{' sni=' + s['sni'] if s['sni'] else ''}")

    if args.check:
        required = {
            "split": ["cut"],
            "disorder-ttl": ["cut", "system_ttl"],
            "disorder-order": ["cut"],
            "fake-dead": ["cut", "fake_sni"],
            "fake-alive": ["cut", "real_sni", "fake_sni"],
            "oob": [],
            "none": ["system_ttl"],
        }[args.check]
        missing = [name for name in required if getattr(args, name) is None]
        if missing:
            print(f"pcap: для проверки {args.check} нужны --{', --'.join(missing)}", file=sys.stderr)
            return 3
        ok, reason = CHECKS[args.check](segments, args)
        if not ok:
            print(f"не сошлось: {reason}", file=sys.stderr)
            return 1
        print(f"ok: {reason}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
