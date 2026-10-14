"""Inspect PAZ index and a few decoded entries through galtools' shipped bridge."""
import struct
import subprocess
import sys

from smoke_bridge import exact, string


def main():
    bridge, archive = sys.argv[1:3]
    with subprocess.Popen([bridge, archive], stdin=subprocess.PIPE,
                          stdout=subprocess.PIPE, stderr=subprocess.PIPE, bufsize=0) as proc:
        assert exact(proc.stdout, 4) == b"GTL1"
        print(string(proc.stdout))
        count = struct.unpack("<I", exact(proc.stdout, 4))[0]
        entries = []
        for _ in range(count):
            index = struct.unpack("<I", exact(proc.stdout, 4))[0]
            name, kind = string(proc.stdout), string(proc.stdout)
            size, packed = struct.unpack("<QQ", exact(proc.stdout, 16))
            entries.append((index, name, kind, size, packed))
        print(count, "entries", entries[:3])
        candidates = [e for e in entries if e[2] == "image" and e[3] > 10000][:8]
        for index, name, _, _, _ in candidates:
            for command in ("R", "P"):
                proc.stdin.write(f"{command} {index}\n".encode("ascii"))
                proc.stdin.flush()
                status = exact(proc.stdout, 1)[0]
                length = struct.unpack("<Q", exact(proc.stdout, 8))[0]
                data = exact(proc.stdout, length)
                print(index, name, command, "status", status, "length", length,
                      "head", data[:32].hex() if status == 0 else data[:100].decode("utf8", "replace"))
        proc.stdin.write(b"Q\n")
        proc.stdin.flush()
        assert proc.wait(timeout=10) == 0, proc.stderr.read().decode("utf8", "replace")


if __name__ == "__main__":
    main()
