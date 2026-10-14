"""Export the local Soreyori installation with galtools' bundled decoders.

Usage: python scripts/extract-soreyori.py GAME_DIRECTORY OUTPUT_DIRECTORY
The .pazA/.pazB parts are read automatically through their base .paz archive.
"""
from __future__ import annotations

import pathlib
import struct
import subprocess
import sys


ROOT = pathlib.Path(__file__).resolve().parents[1]
BRIDGE = ROOT / "runtime" / "garbro-mod" / "Galtools.GarbroModBridge.exe"
INVALID = set('<>:"|?*')


def read_exact(pipe, count):
    result = bytearray()
    while len(result) < count:
        chunk = pipe.read(min(65536, count - len(result)))
        if not chunk:
            raise EOFError("Archive backend closed unexpectedly")
        result.extend(chunk)
    return bytes(result)


def text(pipe):
    length = struct.unpack("<I", read_exact(pipe, 4))[0]
    if length > 1048576:
        raise ValueError("Archive name is too long")
    return read_exact(pipe, length).decode("utf-8")


def safe_target(root, name):
    parts = name.replace("\\", "/").split("/")
    if not parts or any(p in ("", ".", "..") or any(c in INVALID for c in p) for p in parts):
        raise ValueError(f"Unsafe entry name: {name!r}")
    target = root.joinpath(*parts)
    if not target.resolve().is_relative_to(root.resolve()):
        raise ValueError(f"Entry escapes output directory: {name!r}")
    return target


def export_paz(path, output):
    with subprocess.Popen([str(BRIDGE), str(path)], stdin=subprocess.PIPE,
                          stdout=subprocess.PIPE, stderr=subprocess.PIPE, bufsize=0) as proc:
        if read_exact(proc.stdout, 4) != b"GTL1":
            raise RuntimeError(proc.stderr.read().decode("utf8", "replace"))
        description = text(proc.stdout)
        count = struct.unpack("<I", read_exact(proc.stdout, 4))[0]
        entries = []
        for _ in range(count):
            index = struct.unpack("<I", read_exact(proc.stdout, 4))[0]
            name = text(proc.stdout)
            text(proc.stdout)  # type
            read_exact(proc.stdout, 16)  # logical and packed sizes
            entries.append((index, name))
        print(f"{path.name}: {count} entries ({description})", flush=True)
        done = skipped = 0
        for index, name in entries:
            target = safe_target(output / path.stem, name)
            if target.exists():
                skipped += 1
                continue
            proc.stdin.write(f"R {index}\n".encode("ascii"))
            proc.stdin.flush()
            status = read_exact(proc.stdout, 1)[0]
            length = struct.unpack("<Q", read_exact(proc.stdout, 8))[0]
            if status:
                print(f"  failed {name}: {read_exact(proc.stdout, length).decode('utf8', 'replace')}", flush=True)
                skipped += 1
                continue
            target.parent.mkdir(parents=True, exist_ok=True)
            partial = target.with_name(target.name + ".galtools-partial")
            try:
                with partial.open("wb") as file:
                    while length:
                        chunk = read_exact(proc.stdout, min(length, 65536))
                        file.write(chunk)
                        length -= len(chunk)
                partial.replace(target)
                done += 1
            finally:
                partial.unlink(missing_ok=True)
            if done % 500 == 0:
                print(f"  {done}/{count}", flush=True)
        proc.stdin.write(b"Q\n")
        proc.stdin.flush()
        if proc.wait(timeout=30):
            raise RuntimeError(proc.stderr.read().decode("utf8", "replace"))
        print(f"  exported {done}; skipped {skipped}", flush=True)
        return done, skipped


def export_fuyin(path, output):
    with path.open("rb") as input:
        header = read_exact(input, 24)
        magic, start, reserved, index_offset, index_length = struct.unpack("<8sIIII", header)
        if magic != b"FUYINPAK" or start != 24 or reserved or index_length % 20 or index_offset + index_length != path.stat().st_size:
            raise ValueError("Invalid FUYINPAK header")
        input.seek(index_offset)
        records = [struct.unpack("<IIIII", read_exact(input, 20)) for _ in range(index_length // 20)]
        done = skipped = 0
        for name_offset, name_length, offset, size, unpacked in records:
            if size != unpacked or offset < start or offset + size > index_offset or name_offset + name_length > index_offset:
                raise ValueError("Invalid FUYINPAK entry")
            input.seek(name_offset)
            name = read_exact(input, name_length).rstrip(b"\0").decode("cp932")
            target = safe_target(output / path.stem, name)
            if target.exists():
                skipped += 1
                continue
            target.parent.mkdir(parents=True, exist_ok=True)
            input.seek(offset)
            partial = target.with_name(target.name + ".galtools-partial")
            try:
                with partial.open("wb") as file:
                    while size:
                        chunk = read_exact(input, min(size, 65536))
                        file.write(chunk)
                        size -= len(chunk)
                partial.replace(target)
                done += 1
            finally:
                partial.unlink(missing_ok=True)
    print(f"{path.name}: exported {done}; skipped {skipped}", flush=True)
    return done, skipped


def main():
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    game, output = (pathlib.Path(arg).resolve() for arg in sys.argv[1:])
    if not game.is_dir() or not BRIDGE.is_file():
        raise FileNotFoundError("Game directory or bundled bridge does not exist")
    output.mkdir(parents=True, exist_ok=True)
    total = skipped = 0
    for archive in sorted(game.glob("*.paz")):
        if archive.name.lower() == "cnbg.paz":
            result = export_fuyin(archive, output)
        else:
            result = export_paz(archive, output)
        total += result[0]
        skipped += result[1]
    print(f"Done: {total} exported, {skipped} skipped; output: {output}", flush=True)


if __name__ == "__main__":
    main()
