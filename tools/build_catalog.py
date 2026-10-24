"""Build the bundled Endfield operator catalog (characters.json + PNG icons).

Network access is done by PowerShell (see tools/fetch_assets.ps1) because the
bundled python runtime cannot reach the internet reliably.  This script only
does offline work: parse the saved HTML, unpack the downloaded .webp icons into
PNG files, and write AxisTeller/Assets/characters.json.

Stages
------
parse     python tools/build_catalog.py parse <page.html> <workdir>
          -> <workdir>/raw.json      parsed operators (+ icon urls)
          -> <workdir>/icon_urls.txt one icon url per line
finalize  python tools/build_catalog.py finalize <workdir>
          -> AxisTeller/Assets/characters.json
          -> AxisTeller/Assets/Icons/*.png
"""
from __future__ import annotations

import html
import io
import json
import os
import re
import sys

from PIL import Image, ImageOps

BASE = "https://endfield.ooo.run"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
APP = os.path.join(ROOT, "AxisTeller")
OUT_JSON = os.path.join(APP, "Assets", "characters.json")
ICON_DIR = os.path.join(APP, "Assets", "Icons")
ICON_SIZE = 160

ELEMENT_COLOR = {
    "物理": "#d9dde3",
    "灼热": "#ff7a3c",
    "寒冷": "#63d8ff",
    "电磁": "#b98bff",
    "自然": "#7ddc6a",
}


def strip_tags(fragment: str) -> str:
    fragment = re.sub(r"<!--.*?-->", "", fragment, flags=re.S)
    fragment = re.sub(r"<[^>]+>", "", fragment)
    return html.unescape(fragment).strip()


def parse_page(page: str) -> list[dict]:
    cards = re.findall(
        r'<a class="group industrial-card[^"]*" href="/zh/operators/([^"]+)">(.*?)</a>',
        page,
        flags=re.S,
    )
    operators: list[dict] = []
    seen: set[str] = set()
    for slug, body in cards:
        if slug in seen:
            continue
        seen.add(slug)
        img = re.search(r'<img src="([^"]+)" alt="([^"]*)"', body)
        if not img:
            continue
        icon_url = img.group(1).strip()
        name = strip_tags(img.group(2)) or slug
        h3 = re.search(r"<h3[^>]*>(.*?)</h3>", body, flags=re.S)
        if h3:
            candidate = strip_tags(h3.group(1))
            if candidate:
                name = candidate

        rarity = 0
        labels: list[str] = []
        for span in re.findall(r"<span[^>]*>(.*?)</span>", body, flags=re.S):
            text = strip_tags(span)
            if not text:
                continue
            if "★" in text:
                digits = re.sub(r"[^0-9]", "", text)
                if digits:
                    rarity = int(digits)
                continue
            labels.append(text)
        profession = labels[0] if labels else ""
        element = labels[1] if len(labels) > 1 else ""

        icon_file = os.path.basename(icon_url)                    # icon_chr_0025_ardelia.webp
        op_id = re.sub(r"\.(webp|png|jpg|jpeg)$", "", icon_file)
        op_id = re.sub(r"^icon_", "", op_id)                      # chr_0025_ardelia
        operators.append(
            {
                "id": op_id,
                "slug": slug,
                "name": name,
                "rarity": rarity,
                "profession": profession,
                "element": element,
                "elementColor": ELEMENT_COLOR.get(element, "#8b93a1"),
                "icon": op_id + ".png",
                "iconUrl": icon_url if not icon_url.startswith("/") else BASE + icon_url,
            }
        )
    operators.sort(key=lambda o: (-o["rarity"], o["profession"], o["name"]))
    return operators


def cmd_parse(page_path: str, workdir: str) -> int:
    os.makedirs(workdir, exist_ok=True)
    page = open(page_path, "rb").read().decode("utf-8", "replace")
    operators = parse_page(page)
    if len(operators) < 20:
        print(f"only parsed {len(operators)} operators, aborting", file=sys.stderr)
        return 2
    with open(os.path.join(workdir, "raw.json"), "w", encoding="utf-8") as fh:
        json.dump(operators, fh, ensure_ascii=False, indent=2)
    with open(os.path.join(workdir, "icon_urls.txt"), "w", encoding="utf-8") as fh:
        for op in operators:
            fh.write(op["iconUrl"] + "\n")
    print(f"parsed {len(operators)} operators -> {workdir}")
    return 0


def cmd_finalize(workdir: str) -> int:
    operators = json.load(open(os.path.join(workdir, "raw.json"), encoding="utf-8"))
    os.makedirs(ICON_DIR, exist_ok=True)
    ok = 0
    missing: list[str] = []
    for op in operators:
        src = os.path.join(workdir, "icons", "icon_" + op["id"] + ".webp")
        if not os.path.exists(src):
            src = os.path.join(workdir, "icons", op["id"] + ".webp")
        if not os.path.exists(src):
            src = os.path.join(workdir, "icons", op["id"] + ".png")
        try:
            img = Image.open(src).convert("RGBA")
            img = ImageOps.fit(img, (ICON_SIZE, ICON_SIZE), Image.LANCZOS)
            img.save(os.path.join(ICON_DIR, op["icon"]), "PNG", optimize=True)
            ok += 1
        except Exception as exc:  # noqa: BLE001
            missing.append(f"{op['id']}: {exc}")
    print(f"icons converted: {ok}/{len(operators)}")
    for line in missing:
        print("  missing " + line, file=sys.stderr)

    clean = []
    for op in operators:
        clean.append({k: v for k, v in op.items() if k != "iconUrl"})
    payload = {
        "version": 1,
        "source": "森空岛 / 终末地 干员数据（社区镜像 endfield.ooo.run）",
        "updatedAt": "2026-10-17",
        "operators": clean,
    }
    os.makedirs(os.path.dirname(OUT_JSON), exist_ok=True)
    with open(OUT_JSON, "w", encoding="utf-8") as fh:
        json.dump(payload, fh, ensure_ascii=False, indent=2)
    print(f"wrote {OUT_JSON}")
    return 0 if not missing else 1


def main(argv: list[str]) -> int:
    if len(argv) >= 4 and argv[1] == "parse":
        return cmd_parse(argv[2], argv[3])
    if len(argv) >= 3 and argv[1] == "finalize":
        return cmd_finalize(argv[2])
    print(__doc__)
    return 1


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
