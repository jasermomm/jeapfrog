"""Windows adapter for the upstream Tadpole tools."""

import argparse
import ctypes
from ctypes import wintypes
import hashlib
import msvcrt
import os
from pathlib import Path, PurePosixPath
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import time
import zipfile


CREATE_NO_WINDOW = getattr(subprocess, "CREATE_NO_WINDOW", 0)
MAX_ARCHIVE = 768 * 1024 * 1024
APP_ID = re.compile(r"[A-Za-z0-9][A-Za-z0-9_-]{0,79}\Z")
RELEASE_VIEWER = "b906f5cb5362de54a42ffdb30cf43ae9af3ff302ad48f8e81db2b883055d112c"
RELEASE_ENGINE = "91417af05c84e5d56882f0607360c6c2b2c576fef4fa12778d861303985f8ecb"


def field(text, key):
    match = re.search(r'^' + re.escape(key) + r'\s*=\s*"?([^"\r\n]+)', text, re.M)
    return match.group(1).strip() if match else ""


def member_path(name):
    parts = PurePosixPath(name).parts
    if (not parts or name.startswith(("/", "\\")) or
            any(part in ("..", ".") or ":" in part or "\\" in part or
                part.endswith((" ", ".")) or
                re.match(r"(?i)^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:[.]|$)", part)
                for part in parts)):
        raise ValueError("Unsupported path in cartridge archive.")
    return parts


def check_packages(entries):
    packages = []
    size = 0
    seen = set()
    for name, count, kind, read in entries:
        parts = member_path(name)
        unique_name = "/".join(parts).casefold()
        if unique_name in seen or kind not in ("file", "dir"):
            raise ValueError("Unsupported entry in cartridge archive.")
        seen.add(unique_name)
        size += count
        if size > MAX_ARCHIVE:
            raise ValueError("Cartridge archive is too large.")
        if kind == "file" and parts[-1].lower() == "meta.inf":
            if count > 65536:
                raise ValueError("Cartridge metadata is too large.")
            meta = read().decode("utf-8", "replace")
            pkg = field(meta, "PackageID")
            if pkg and not APP_ID.fullmatch(pkg):
                raise ValueError("Invalid package ID.")
            if pkg and field(meta, "Type") == "Application":
                entry = field(meta, "AppSo")
                if entry:
                    member_path(entry)
                packages.append((pkg, field(meta, "Device")))
    if not packages:
        raise ValueError("No LeapFrog application found in this archive.")
    return packages


def verify_archive(path):
    if zipfile.is_zipfile(path):
        with zipfile.ZipFile(path) as z:
            return check_packages(
                (i.filename, i.file_size, "dir" if i.is_dir() else
                 "link" if (i.external_attr >> 16) & 0o170000 == 0o120000 else "file",
                 lambda i=i: z.read(i)) for i in z.infolist())
    with tarfile.open(path) as t:
        return check_packages(
            (i.name, i.size, "file" if i.isfile() else "dir" if i.isdir() else "link",
             lambda i=i: t.extractfile(i).read()) for i in t)


def run_tool(root, name, *args, env=None):
    subprocess.run([sys.executable, str(root / "tools" / name)] + list(map(str, args)),
                   cwd=str(root), env=env, check=True, creationflags=CREATE_NO_WINDOW)


def import_game(root, source):
    if source.stat().st_size > MAX_ARCHIVE:
        raise ValueError("Cartridge archive is too large.")
    sysroot = root / "runtime/sysroot"
    with tempfile.TemporaryDirectory(prefix="jeap-cart-", dir=str(root.parent)) as scratch:
        stage = Path(scratch)
        archive = source
        if source.suffix.lower() in (".7z", ".bin"):
            rom = source
            if source.suffix.lower() == ".7z":
                listing = subprocess.check_output(["tar.exe", "-tf", str(source)],
                                                  creationflags=CREATE_NO_WINDOW)
                names = listing.decode("utf-8", "replace").splitlines()
                carts = [name for name in names if name.lower().endswith(".bin")]
                if len(carts) != 1:
                    raise ValueError("Expected one cartridge image in the archive.")
                member_path(carts[0])
                rom = stage / "cartridge.bin"
                with rom.open("wb") as output:
                    with subprocess.Popen(["tar.exe", "-xOf", str(source), "--", carts[0]],
                                          stdout=subprocess.PIPE,
                                          creationflags=CREATE_NO_WINDOW) as proc:
                        total = 0
                        for block in iter(lambda: proc.stdout.read(1024 * 1024), b""):
                            total += len(block)
                            if total > MAX_ARCHIVE:
                                proc.kill()
                                raise ValueError("Cartridge image is too large.")
                            output.write(block)
                        if proc.wait():
                            raise ValueError("Could not extract the cartridge image.")
            run_tool(root, "cart2tar.py", "-q", "-o", stage, rom)
            archive = stage / (rom.stem + ".tar")
        packages = verify_archive(archive)
        bulk = sysroot / "LF/Bulk"
        base = sysroot / "LF/Base"
        env = dict(os.environ, TADPOLE_BULK=str(bulk), TADPOLE_BASE=str(base))
        run_tool(root, "install-game.py", archive, env=env)
        for package, _ in packages:
            if not (bulk / "ProgramFiles" / package / "meta.inf").is_file():
                raise ValueError("The engine could not install the cartridge.")
            print("installed " + package, flush=True)


def patched_copy(original, name, release_hash, changes):
    content = original.read_bytes()
    if hashlib.sha256(content).hexdigest() != release_hash:
        return original
    patched = bytearray(content)
    for offset, old, new in changes:
        if patched[offset:offset + len(old)] != old:
            raise RuntimeError("The runtime build has changed.")
        patched[offset:offset + len(old)] = new
    output = original.with_name(name)
    if not output.is_file() or output.read_bytes() != patched:
        temp = output.with_suffix(".tmp")
        temp.write_bytes(patched)
        temp.replace(output)
    return output


def direct_viewer(root):
    # Skip the viewer's startup wake-key pulses for direct game launches.
    calls = ((0xa88c, "e88f72ffff"), (0xa89e, "e87d72ffff"),
             (0xa8b6, "e86572ffff"), (0xa8c8, "e85372ffff"))
    return patched_copy(root / "tadpole/viewer/build/tadpole-view.exe",
                        "jeapfrog-view.exe", RELEASE_VIEWER,
                        [(offset, bytes.fromhex(call), b"\x90" * 5) for offset, call in calls])


def direct_engine(root):
    # gp_poll_readable must wait when PeekNamedPipe fails on a listening pipe.
    # Reporting it ready before a writer connects causes EAGAIN in AppManager.
    return patched_copy(root / "glasspole/build/glasspole.exe",
                        "jeapfrog-core.exe", RELEASE_ENGINE,
                        [(0x173b3, bytes.fromhex("74ab"), bytes.fromhex("746b"))])


def open_trace_log(path):
    # FILE_APPEND_DATA keeps inherited Win32 writes appending after a log trim.
    kernel = ctypes.WinDLL("kernel32", use_last_error=True)
    create = kernel.CreateFileW
    create.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                       ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    create.restype = wintypes.HANDLE
    handle = create(str(path), 0x100004, 7, None, 2, 0x80, None)
    if handle == ctypes.c_void_p(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    fd = msvcrt.open_osfhandle(handle, os.O_WRONLY | os.O_BINARY)
    return os.fdopen(fd, "wb", buffering=0)


def play(root, package, software):
    if not APP_ID.fullmatch(package):
        raise ValueError("Invalid package ID.")
    sysroot = root / "runtime/sysroot"
    guest = direct_engine(root)
    working = root / "run"
    working.mkdir(exist_ok=True)
    sentinel = working / "stop.req"
    if sentinel.exists():
        sentinel.unlink()
    meta = (sysroot / "LF/Bulk/ProgramFiles" / package / "meta.inf").read_text(
        encoding="utf-8", errors="replace")
    entry = field(meta, "AppSo")
    member_path(entry)
    app = "/LF/Bulk/ProgramFiles/{}/{}".format(package, entry)
    if not (sysroot / app.lstrip("/")).is_file():
        raise ValueError("The game's executable is missing.")
    profiles = sysroot / "LF/Bulk/Data/Local"
    player = next((slot for slot in ("0", "1", "2", "3")
                   if (profiles / slot / "profile.dsc").is_file()), None)
    if player is None:
        run_tool(root, "make-profile.py", "--name", "Player", "--grade", "4")
        player = "0"
    (profiles / player / package).mkdir(exist_ok=True)
    state = root / "state"
    state.mkdir(exist_ok=True)
    env = dict(os.environ, TADPOLE_PROJECT=root.as_posix(),
               XDG_STATE_HOME=state.as_posix(),
               XDG_CONFIG_HOME=(root / "config").as_posix(),
               XDG_CACHE_HOME=(root / "cache").as_posix())
    drive_relative = root.as_posix()[len(root.drive):]
    libs = ("shimlibs-gl", "shimlibs-z", "shimlibs", "libs")
    guest_env = {
        "LD_LIBRARY_PATH": ":".join(drive_relative + "/runtime/" + name for name in libs),
        "TSLIB_CONFFILE": "/nonexistent-ts.conf",
        "TADPOLE_SYSROOT": drive_relative + "/runtime/sysroot",
        "TADPOLE_GL": "1",
        "TADPOLE_GL_SOFTWARE" if software else "TADPOLE_GL_HLE": "1",
    }
    viewer = [str(direct_viewer(root)), "--rotate", "0"]
    game_log_path = working / "game.log"
    for attempt in range(4):
        started = time.monotonic()
        children = []
        output = open_trace_log(game_log_path) if attempt else game_log_path.open("wb")
        with tempfile.TemporaryDirectory(prefix="session-", dir=str(working)) as session, \
                output as game_log, \
                (working / "viewer.log").open("wb") as viewer_log:
            share = Path(session).as_posix()
            env["TADPOLE_DIR"] = share
            guest_env["TADPOLE_DIR"] = share
            command = [str(guest), "--sysroot", str(sysroot)]
            if attempt:
                # Tracing changes scheduling around an intermittent startup assert.
                command.append("--trace")
            for key, value in guest_env.items():
                command += ["-E", key + "=" + value]
            command += ["/LF/Base/bin/AppManager", app, player]
            try:
                front = subprocess.Popen(viewer, cwd=str(root), env=env, stdout=viewer_log,
                                         stderr=subprocess.STDOUT, creationflags=CREATE_NO_WINDOW)
                children.append(front)
                # The guest checks for an active GPU command ring on its first frame.
                ring = Path(session) / "glcmd.bin"
                deadline = time.monotonic() + 3
                while not ring.exists() and front.poll() is None and time.monotonic() < deadline:
                    time.sleep(0.05)
                if front.poll() is not None:
                    raise RuntimeError("Game window could not open. See viewer.log.")
                if sentinel.exists():
                    return
                emu = subprocess.Popen(command, cwd=str(root), env=env,
                                       stdout=game_log,
                                       stderr=subprocess.STDOUT, creationflags=CREATE_NO_WINDOW)
                children.append(emu)
                while not sentinel.exists() and front.poll() is None:
                    result = emu.poll()
                    if result is not None:
                        if attempt < 3 and result == 143 and time.monotonic() - started < 8:
                            break
                        if result == 0:
                            return
                        raise RuntimeError("Game stopped. See game.log in the data folder.")
                    if attempt and game_log_path.stat().st_size > 16 * 1024 * 1024:
                        with game_log_path.open("r+b") as rotate:
                            rotate.truncate()
                    time.sleep(0.15)
                else:
                    return
            finally:
                for child in reversed(children):
                    if child.poll() is None:
                        child.terminate()
                        child.wait()


def main():
    p = argparse.ArgumentParser()
    p.add_argument("root", type=Path)
    p.add_argument("action", choices=("import", "play"))
    p.add_argument("value")
    p.add_argument("--software", action="store_true")
    args = p.parse_args()
    try:
        if args.action == "import":
            import_game(args.root.resolve(), Path(args.value).resolve())
        else:
            play(args.root.resolve(), args.value, args.software)
        return 0
    except (OSError, ValueError, RuntimeError, subprocess.CalledProcessError,
            tarfile.TarError, zipfile.BadZipFile) as exc:
        print(str(exc), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
