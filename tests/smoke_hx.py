"""Regression check for Hxv4 synthetic names, content types, and TLGref layers."""
import struct
import subprocess
import sys
import time


def exact(stream, count):
    result = bytearray()
    while len(result) < count:
        part = stream.read(count - len(result))
        if not part:
            raise EOFError("bridge closed unexpectedly")
        result.extend(part)
    return bytes(result)


def string(stream):
    length = struct.unpack("<I", exact(stream, 4))[0]
    assert length < 1048576
    return exact(stream, length).decode("utf-8")


def request(proc, command, index):
    proc.stdin.write(f"{command} {index}\n".encode("ascii"))
    proc.stdin.flush()
    status = exact(proc.stdout, 1)[0]
    length = struct.unpack("<Q", exact(proc.stdout, 8))[0]
    assert length < 200000000
    data = exact(proc.stdout, length)
    assert status == 0, data.decode("utf-8", "replace")
    return data


def main():
    bridge, archive = sys.argv[1:3]
    with subprocess.Popen(
        [bridge, archive, "Magalumina"], stdin=subprocess.PIPE,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE, bufsize=0,
    ) as proc:
        assert exact(proc.stdout, 4) == b"GTL1"
        string(proc.stdout)
        count = struct.unpack("<I", exact(proc.stdout, 4))[0]
        entries = []
        for _ in range(count):
            index = struct.unpack("<I", exact(proc.stdout, 4))[0]
            name, kind = string(proc.stdout), string(proc.stdout)
            size, packed = struct.unpack("<QQ", exact(proc.stdout, 16))
            entries.append((index, name, kind, size, packed))
        assert count == 718, count
        assert entries[0][1].startswith("#00000_")
        assert entries[1][1].endswith(".png") and entries[1][2] == "image"
        assert entries[6][1] == "ev001__nox6m4.tlg" and entries[6][2] == "image"
        assert "ev001__nox6m4_layer" in entries[95][1] and entries[95][2] == "image"
        start = time.perf_counter()
        for command in ("P", "I"):
            data = request(proc, command, 95)
            assert data.startswith(b"\x89PNG\r\n\x1a\n")
            width, height = struct.unpack(">II", data[16:24])
            assert (width, height) == ((853, 480) if command == "P" else (1920, 1080))
            print(command, entries[95][1], width, height, len(data), round(time.perf_counter() - start, 3))
        for token in ("evm001__66zpib_1_layer", "evmh006__bp2vkr_layer"):
            matching = [entry for entry in entries if token in entry[1]]
            assert matching, token
            data = request(proc, "P", matching[0][0])
            assert data.startswith(b"\x89PNG\r\n\x1a\n")
            width, height = struct.unpack(">II", data[16:24])
            assert height == 480
            print("P", matching[0][1], width, height, len(data))
        proc.stdin.write(b"Q\n")
        proc.stdin.flush()
        assert proc.wait(timeout=10) == 0


if __name__ == "__main__":
    main()
