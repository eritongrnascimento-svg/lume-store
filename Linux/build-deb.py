"""Create the Debian binary package using only the Python standard library."""
import gzip
import io
import tarfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent
VERSION = "0.2.0"
APP_ID = "io.github.eritongrnascimento_svg.LumeStore"


def tar_payload(files):
    stream = io.BytesIO()
    with tarfile.open(fileobj=stream, mode="w", format=tarfile.USTAR_FORMAT) as archive:
        for name, content, mode in files:
            info = tarfile.TarInfo("./" + name)
            info.size = len(content)
            info.mode = mode
            archive.addfile(info, io.BytesIO(content))
    return gzip.compress(stream.getvalue(), mtime=0)


def ar_member(name, content):
    header = f"{name + '/':<16}{0:<12}{0:<6}{0:<6}{'100644':<8}{len(content):<10}`\n"
    return header.encode("ascii") + content + (b"\n" if len(content) % 2 else b"")


def build(out_dir):
    control = f"""Package: lume-store
Version: {VERSION}
Section: utils
Priority: optional
Architecture: all
Maintainer: Lume Store Team <eritongrnascimento-svg@users.noreply.github.com>
Depends: python3 (>= 3.9), python3-tk, apt, packagekit-tools, flatpak
Homepage: https://github.com/eritongrnascimento-svg/lume-store
Description: Lume Store application catalog
 Search and install packages from APT and configured Flatpak remotes.
""".encode()
    copyright_text = """Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/
Upstream-Name: Lume Store
Source: https://github.com/eritongrnascimento-svg/lume-store

Files: *
Copyright: 2026 Lume Store contributors
License: GPL-3.0-only
 The full license text is in /usr/share/doc/lume-store/LICENSE.
""".encode()
    data = [
        ("usr/bin/lume-store", (ROOT / "lume-store").read_bytes(), 0o755),
        ("usr/share/applications/lume-store.desktop", (ROOT / "lume-store.desktop").read_bytes(), 0o644),
        (f"usr/share/icons/hicolor/256x256/apps/{APP_ID}.png", (ROOT / "logo.png").read_bytes(), 0o644),
        ("usr/share/doc/lume-store/copyright", copyright_text, 0o644),
        ("usr/share/doc/lume-store/LICENSE", (ROOT.parent / "LICENSE").read_bytes(), 0o644),
    ]
    out_dir.mkdir(parents=True, exist_ok=True)
    output = out_dir / f"lume-store_{VERSION}_all.deb"
    output.write_bytes(
        b"!<arch>\n" +
        ar_member("debian-binary", b"2.0\n") +
        ar_member("control.tar.gz", tar_payload([("control", control, 0o644)])) +
        ar_member("data.tar.gz", tar_payload(data))
    )
    print(output)


if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, default=Path("dist"))
    build(parser.parse_args().output)
