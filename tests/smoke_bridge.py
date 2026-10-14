"""Developer smoke test for a local GARbro-Mod archive; no game assets are saved."""
import struct
import subprocess
import sys
import time


def exact(pipe, count):
    data = bytearray()
    while len(data) < count:
        chunk = pipe.read(count - len(data))
        if not chunk:
            raise EOFError("bridge closed early")
        data.extend(chunk)
    return bytes(data)


def string(pipe):
    length = struct.unpack("<I", exact(pipe, 4))[0]
    assert length < 1048576
    return exact(pipe, length).decode("utf-8", "replace")


def main():
    bridge, archive, scheme = sys.argv[1:4]
    audio = len(sys.argv) > 4 and sys.argv[4] == "--audio"
    with subprocess.Popen(
        [bridge, archive, scheme],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
        bufsize=0,
    ) as proc:
        assert exact(proc.stdout, 4) == b"GTL1"
        description = string(proc.stdout)
        count = struct.unpack("<I", exact(proc.stdout, 4))[0]
        entries = []
        for _ in range(count):
            index = struct.unpack("<I", exact(proc.stdout, 4))[0]
            name = string(proc.stdout)
            kind = string(proc.stdout)
            size, packed = struct.unpack("<QQ", exact(proc.stdout, 16))
            entries.append((index, name, kind, size, packed))
        print(description, count, "entries")
        print("first:", entries[:3])
        candidates = [i for i, _, _, size, _ in entries if size > (5000 if audio else 100000)]
        index = candidates[0]
        started = time.perf_counter()
        if audio:
            failures = []
            for index in candidates[:2000]:
                proc.stdin.write("A {}\n".format(index).encode("ascii"))
                proc.stdin.flush()
                status = exact(proc.stdout, 1)[0]
                length = struct.unpack("<Q", exact(proc.stdout, 8))[0]
                assert length < 200000000
                content = exact(proc.stdout, length)
                if status == 0 and content.startswith(b"RIFF"):
                    print("WAV", length, "bytes", round(time.perf_counter() - started, 3), "seconds")
                    break
                if len(failures) < 3:
                    failures.append(content[:90])
            else:
                raise AssertionError("No audio entry among first 2000 candidates: " + repr(failures))
            proc.stdin.write("M {}\n".format(index).encode("ascii"))
            proc.stdin.flush()
            status = exact(proc.stdout, 1)[0]
            length = struct.unpack("<Q", exact(proc.stdout, 8))[0]
            assert length < 200000000
            mp3 = exact(proc.stdout, length)
            assert status == 0, mp3.decode("utf-8", "replace")
            assert mp3.startswith(b"ID3") or mp3.startswith(b"\xff\xfb")
            print("MP3", length, "bytes")
        else:
            proc.stdin.write("P {}\n".format(index).encode("ascii"))
            proc.stdin.flush()
            preview_status = exact(proc.stdout, 1)[0]
            preview_length = struct.unpack("<Q", exact(proc.stdout, 8))[0]
            preview = exact(proc.stdout, preview_length)
            assert preview_status == 0, preview.decode("utf-8", "replace")
            assert preview.startswith(b"\x89PNG\r\n\x1a\n")
            preview_width, preview_height = struct.unpack(">II", preview[16:24])
            assert preview_height <= 480
            print("Preview", preview_width, preview_height, preview_length, "bytes")
            proc.stdin.write("I {}\n".format(index).encode("ascii"))
            proc.stdin.flush()
            status = exact(proc.stdout, 1)[0]
            length = struct.unpack("<Q", exact(proc.stdout, 8))[0]
            assert length < 200000000
            content = exact(proc.stdout, length)
            assert status == 0, content.decode("utf-8", "replace")
            assert content.startswith(b"\x89PNG\r\n\x1a\n")
            width, height = struct.unpack(">II", content[16:24])
            print("PNG", width, height, length, "bytes", round(time.perf_counter() - started, 3), "seconds")
        proc.stdin.write("R {}\n".format(index).encode("ascii"))
        proc.stdin.flush()
        status = exact(proc.stdout, 1)[0]
        length = struct.unpack("<Q", exact(proc.stdout, 8))[0]
        assert length < 200000000
        content = exact(proc.stdout, length)
        assert status == 0, content.decode("utf-8", "replace")
        print("Original", length, "bytes", content[:16])
        proc.stdin.write(b"Q\n")
        proc.stdin.flush()
        assert proc.wait(timeout=10) == 0


if __name__ == "__main__":
    main()
