"""Export XP3 archives using the bundled bridge and Formats.dat.

Usage: python scripts/extract-xp3.py GAME_DIRECTORY OUTPUT_DIRECTORY
Requires Python only for this optional batch entry point, not for the desktop app.
Existing output files are never overwritten.
"""
import json
import pathlib
import struct
import subprocess
import sys
import tempfile

BRIDGE = pathlib.Path(__file__).resolve().parents[1] / "runtime/garbro-mod/Galtools.GarbroModBridge.exe"


def exact(pipe, size):
    data = bytearray()
    while len(data) < size:
        chunk = pipe.read(min(65536, size - len(data)))
        if not chunk:
            raise EOFError("Archive backend closed unexpectedly")
        data.extend(chunk)
    return bytes(data)


def text(pipe):
    size = struct.unpack("<I", exact(pipe, 4))[0]
    if size > 1048576:
        raise ValueError("Invalid protocol string length")
    return exact(pipe, size).decode("utf8")


def target_path(root, name):
    parts = name.replace("\\", "/").split("/")
    reserved = {"CON", "PRN", "AUX", "NUL"} | {f"{p}{i}" for p in ("COM", "LPT") for i in range(1, 10)}
    if any(not p or p in (".", "..") or p.endswith((" ", "."))
           or p.split(".")[0].upper() in reserved
           or any(c in '<>:"|?*' or ord(c) < 32 for c in p) for p in parts):
        raise ValueError(f"Unsafe entry name: {name!r}")
    result = root.joinpath(*parts)
    if not result.resolve().is_relative_to(root.resolve()):
        raise ValueError("Output path escapes export directory")
    return result


def export(path, output):
    with tempfile.TemporaryFile() as errors, subprocess.Popen(
        [str(BRIDGE), str(path)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=errors, bufsize=0,
    ) as proc:
        try:
            if exact(proc.stdout, 4) != b"GTL1":
                raise ValueError("Invalid bridge protocol")
            description = text(proc.stdout)
            count = struct.unpack("<I", exact(proc.stdout, 4))[0]
            entries = []
            for _ in range(count):
                index = struct.unpack("<I", exact(proc.stdout, 4))[0]
                name, kind = text(proc.stdout), text(proc.stdout)
                exact(proc.stdout, 16)
                entries.append((index, name, kind))
            print(f"{path.name}: {count} entries ({description})", flush=True)
            result = dict(archive=path.name, entries=count, exported=0, skipped=0, bytes=0)
            for index, name, kind in entries:
                target = target_path(output / path.stem, name)
                if target.exists():
                    result["skipped"] += 1
                    continue
                proc.stdin.write(f"R {index}\n".encode("ascii"))
                proc.stdin.flush()
                status = exact(proc.stdout, 1)[0]
                length = struct.unpack("<Q", exact(proc.stdout, 8))[0]
                if status:
                    raise ValueError(f"{name}: {exact(proc.stdout, length).decode('utf8', 'replace')}")
                target.parent.mkdir(parents=True, exist_ok=True)
                partial = target.with_name(target.name + ".galtools-partial")
                # Exclusive creation protects previous interrupted output as well.
                with partial.open("xb") as file:
                    remaining = length
                    while remaining:
                        chunk = exact(proc.stdout, min(remaining, 65536))
                        file.write(chunk)
                        remaining -= len(chunk)
                partial.rename(target)
                result["exported"] += 1
                result["bytes"] += length
                if result["exported"] % 500 == 0:
                    print(f"  {result['exported']}/{count}", flush=True)
            proc.stdin.write(b"Q\n")
            proc.stdin.flush()
            if proc.wait(timeout=30):
                raise RuntimeError("Archive backend failed")
            print(result, flush=True)
            return result
        except Exception:
            if proc.poll() is None:
                proc.terminate()
                proc.wait(timeout=10)
            errors.seek(0)
            print(errors.read().decode("utf8", "replace"), file=sys.stderr)
            raise


def main():
    if len(sys.argv) != 3:
        raise SystemExit(__doc__)
    game, output = (pathlib.Path(p).resolve() for p in sys.argv[1:])
    archives = sorted(game.glob("*.xp3"))
    if not archives or not BRIDGE.is_file():
        raise FileNotFoundError("Missing XP3 archives or bundled bridge")
    output.mkdir(parents=True, exist_ok=True)
    summary = [export(path, output) for path in archives]
    with (output / "extraction-summary.json").open("x", encoding="utf8") as file:
        json.dump(summary, file, ensure_ascii=False, indent=2)
    print(f"Done: {sum(s['exported'] for s in summary)} exported; output: {output}", flush=True)


if __name__ == "__main__":
    main()
