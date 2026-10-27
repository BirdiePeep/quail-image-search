"""Builds the calibration image set shipped with the app (src/QuailImageSearch/Calibration).

Picks one public-domain or CC0 photo per subject from Wikimedia Commons, verifies the license from
the file's metadata, stores a small JPEG (short side 400 px) and writes manifest.json with the
caption, source page, author and license of every image.

Usage: python tools/build_calibration_set.py            (needs Pillow)
       python tools/build_calibration_set.py --skip "a photo of a dog=File:Some_file.jpg"
Review the contact sheet it writes (contact_sheet.jpg next to the images, not shipped) and use
--skip to reject images that don't match their caption; the next candidate is used instead.
"""

import argparse, html, io, json, os, re, sys, time, urllib.parse, urllib.request
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
OUT_DIR = os.path.join(HERE, "..", "src", "QuailImageSearch", "Calibration")
UA = "ImageSearchCalibrationBuilder/0.1 (desktop app test data; contact via project repo)"
OK_LICENSES = ("cc0", "public domain", "pdm", "pd ")

# (search terms, caption). Varied subjects so mismatched pairs are clearly unrelated.
SUBJECTS = [
    ("dog", "a photo of a dog"), ("cat", "a photo of a cat"), ("horse", "a photo of a horse"),
    ("cow pasture", "a cow in a field"), ("parrot", "a colorful parrot"), ("butterfly flower", "a butterfly on a flower"),
    ("coral reef fish", "fish swimming on a coral reef"), ("elephant", "an elephant"), ("sheep flock", "a flock of sheep"),
    ("bicycle", "a bicycle"), ("sports car", "a car parked on a street"), ("city bus", "a bus on a city street"),
    ("steam locomotive", "a train on railway tracks"), ("airliner flight", "an airplane flying in the sky"),
    ("sailboat", "a sailboat on the water"), ("motorcycle", "a motorcycle"), ("sandy beach ocean", "a sandy beach by the ocean"),
    ("mountain peak snow", "a snowy mountain peak"), ("snowy landscape winter", "a snowy winter landscape"),
    ("forest trees path", "a path through a forest"), ("sand dunes desert", "sand dunes in a desert"),
    ("waterfall", "a waterfall"), ("city skyline night", "a city skyline at night"), ("old town street houses", "a street with old houses"),
    ("suspension bridge", "a large bridge over water"), ("church interior", "the inside of a church"),
    ("castle", "a castle"), ("lighthouse", "a lighthouse on the coast"), ("tulips", "a field of tulips"),
    ("sunflower", "a sunflower"), ("autumn tree leaves", "a tree with autumn leaves"), ("pizza", "a pizza"),
    ("soup bowl", "a bowl of soup"), ("apples fruit", "red apples"), ("bread loaf bakery", "loaves of bread"),
    ("coffee cup", "a cup of coffee"), ("cake dessert", "a slice of cake"), ("chair wooden", "a wooden chair"),
    ("laptop computer", "a laptop computer"), ("computer keyboard", "a computer keyboard"), ("open book pages", "an open book"),
    ("clock tower", "a clock tower"), ("acoustic guitar", "an acoustic guitar"), ("grand piano", "a piano"),
    ("soccer ball", "a soccer ball"), ("marathon runners", "people running a race"), ("children playing playground", "children playing outside"),
    ("crowd people square", "a crowd of people"), ("portrait woman", "a portrait of a woman"), ("elderly man portrait", "a portrait of an old man"),
    ("hands", "a pair of hands"), ("shoes", "a pair of shoes"), ("fireworks", "fireworks in the night sky"),
    ("sunset sea", "a sunset over the sea"), ("full moon", "the moon"), ("milky way night sky", "stars in the night sky"),
    ("rainbow", "a rainbow"), ("lightning storm", "lightning in a storm"), ("snowman", "a snowman"),
    ("vegetables market", "vegetables at a market"), ("statue sculpture", "a stone statue"), ("graffiti wall", "graffiti on a wall"),
    ("kitchen", "a kitchen"), ("bedroom", "a bedroom"),
]


def fetch(url):
    """GET with Wikimedia's required User-Agent, backing off when rate-limited."""
    for attempt in range(6):
        try:
            req = urllib.request.Request(url, headers={"User-Agent": UA})
            with urllib.request.urlopen(req) as r:
                return r.read()
        except urllib.error.HTTPError as e:
            if e.code != 429 or attempt == 5:
                raise
            time.sleep(15 * (attempt + 1))


def get_json(url):
    return json.loads(fetch(url))


# Artworks and museum scans (e.g. the Met's CC0 collection) aren't photos of the subject.
NOT_PHOTO = re.compile(r"\bMET\b|painting|folio|engraving|drawing|illustration|poster|stamp|coin|earring|brooch|"
                       r"\bmap\b|diagram|logo|icon|sketch|lithograph|woodcut|\d{4}–\d{2}", re.IGNORECASE)


def candidates(terms):
    """Commons photos for the terms, CC0 or public-domain only (checked again from metadata)."""
    filters = [f"incategory:Quality_images {lic}" for lic in ("hastemplate:Cc-zero", "hastemplate:PD-self")] + \
              ["hastemplate:Cc-zero", "hastemplate:PD-self", "hastemplate:PD-author", "hastemplate:PD-USGov"]
    for license_filter in filters:
        q = f"{terms} {license_filter} filetype:bitmap"
        url = ("https://commons.wikimedia.org/w/api.php?action=query&format=json&generator=search&gsrnamespace=6"
               f"&gsrlimit=15&gsrsearch={urllib.parse.quote(q)}&prop=imageinfo&iiprop=url|extmetadata|size|mime&iiurlwidth=640")
        pages = sorted(get_json(url).get("query", {}).get("pages", {}).values(), key=lambda p: p.get("index", 0))
        for p in pages:
            info = p["imageinfo"][0]
            meta = info.get("extmetadata", {})
            lic = meta.get("LicenseShortName", {}).get("value", "")
            if info.get("mime") not in ("image/jpeg", "image/png") or not lic.lower().startswith(OK_LICENSES):
                continue
            if NOT_PHOTO.search(p["title"]):
                continue
            w, h = info["width"], info["height"]
            if min(w, h) < 400 or max(w, h) / min(w, h) > 2.2:
                continue
            artist = re.sub("<[^>]+>", "", html.unescape(meta.get("Artist", {}).get("value", "unknown"))).strip()
            yield {"title": p["title"], "thumb": info["thumburl"], "page": info["descriptionurl"],
                   "license": lic, "author": artist[:120] or "unknown"}
        time.sleep(1.5)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--skip", action="append", default=[], help='"caption=File:Title" to reject a candidate')
    args = ap.parse_args()
    skip = {tuple(s.split("=", 1)) for s in args.skip}

    img_dir = os.path.join(OUT_DIR, "images")
    os.makedirs(img_dir, exist_ok=True)
    manifest, used = [], set()
    for i, (terms, caption) in enumerate(SUBJECTS):
        chosen = None
        for c in candidates(terms):
            if c["title"] in used or (caption, c["title"]) in skip:
                continue
            chosen = c
            break
        if chosen is None:
            print(f"!! no CC0/PD candidate for {caption!r}", file=sys.stderr)
            continue
        used.add(chosen["title"])
        im = Image.open(io.BytesIO(fetch(chosen["thumb"]))).convert("RGB")
        scale = 400 / min(im.size)
        if scale < 1:
            im = im.resize((round(im.width * scale), round(im.height * scale)), Image.LANCZOS)
        name = f"{i:02d}-" + re.sub(r"[^a-z0-9]+", "-", caption.lower()).strip("-") + ".jpg"
        im.save(os.path.join(img_dir, name), quality=85)
        manifest.append({"file": name, "caption": caption, "source": chosen["page"], "title": chosen["title"],
                         "author": chosen["author"], "license": chosen["license"]})
        print(f"{name:45} {chosen['license']:15} {chosen['title']}")
        time.sleep(0.3)

    with open(os.path.join(OUT_DIR, "manifest.json"), "w", encoding="utf-8", newline="\n") as f:
        json.dump({"description": "Calibration images for score rescaling. Every image is CC0 or public domain; see each entry's source page.",
                   "images": manifest}, f, indent=2, ensure_ascii=False)
        f.write("\n")

    # Contact sheet for review (not shipped).
    cols, cell = 8, 180
    rows = (len(manifest) + cols - 1) // cols
    sheet = Image.new("RGB", (cols * cell, rows * (cell + 30)), "white")
    draw = ImageDraw.Draw(sheet)
    for k, m in enumerate(manifest):
        im = Image.open(os.path.join(img_dir, m["file"]))
        im.thumbnail((cell - 6, cell - 6))
        x, y = (k % cols) * cell, (k // cols) * (cell + 30)
        sheet.paste(im, (x + 3, y + 3))
        draw.text((x + 3, y + cell), m["file"][:2] + " " + m["caption"][:26], fill="black")
    sheet.save(os.path.join(HERE, "..", "contact_sheet.jpg"), quality=80)
    print(f"{len(manifest)} images; contact sheet: contact_sheet.jpg (repo root, not committed)")


if __name__ == "__main__":
    main()
