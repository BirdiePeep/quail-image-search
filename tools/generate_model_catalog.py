# Regenerates src/QuailImageSearch/Services/Models/models.json (the built-in model list) with exact file sizes,
# SHA-256 hashes and revisions pinned to each repo's current commit, from the Hugging Face API.
# Usage: python tools/generate_model_catalog.py               (standard library only)
#        python tools/generate_model_catalog.py --add-hashes  (only fills in missing hashes, keeping the
#                                                               pinned revisions and everything else)
import hashlib, json, os, sys, urllib.request

def api(url):
    with urllib.request.urlopen(url) as r:
        return json.load(r)

def sha256(repo, revision, path):
    """The file's SHA-256: from the API for Git LFS files (the ONNX models), else by downloading it."""
    folder = os.path.dirname(path)
    for f in api(f"https://huggingface.co/api/models/{repo}/tree/{revision}" + (f"/{folder}" if folder else "")):
        if f["path"] == path and f.get("lfs"):
            return f["lfs"]["oid"]
    h = hashlib.sha256()
    with urllib.request.urlopen(f"https://huggingface.co/{repo}/resolve/{revision}/{path}") as r:
        for chunk in iter(lambda: r.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()

out_path = os.path.join(os.path.dirname(__file__), "..", "src", "QuailImageSearch", "Services", "Models", "models.json")

def write(catalog):
    with open(out_path, "w", encoding="utf-8", newline="\n") as f:
        json.dump(catalog, f, indent=2)
        f.write("\n")

if "--add-hashes" in sys.argv:
    with open(out_path, encoding="utf-8") as f:
        catalog = json.load(f)
    for m in catalog["models"]:
        for file in m["files"].values():
            if "sha256" not in file:
                file["sha256"] = sha256(m["repo"], m["revision"], file["path"])
        print(f"{m['id']:20} {m['repo']}@{m['revision'][:7]}")
    write(catalog)
    sys.exit()

CLIP_IMG = dict(resize="shortest_edge", resample="bicubic", mean=[0.48145466, 0.4578275, 0.40821073], std=[0.26862954, 0.26130258, 0.27577711])
MOBILE_IMG = dict(resize="shortest_edge", resample="bilinear", mean=[0, 0, 0], std=[1, 1, 1])
SIGLIP_IMG = dict(resize="exact", resample="bicubic", mean=[0.5, 0.5, 0.5], std=[0.5, 0.5, 0.5])
MIT = dict(license="MIT", licenseUrl="https://github.com/openai/CLIP/blob/main/LICENSE")
APACHE = dict(license="Apache-2.0", licenseUrl="https://huggingface.co/google/siglip-base-patch16-224")
APPLE = dict(license="Apple AMLR (research use only)", licenseUrl="https://huggingface.co/apple/MobileCLIP-S0/blob/main/LICENSE",
             licenseNote="Apple's model license limits use to non-commercial research.")

specs = [
    ("clip-vit-b32", "CLIP ViT-B/32", "Xenova/clip-vit-base-patch32", "", "clip", 224, CLIP_IMG, MIT,
     "OpenAI's original CLIP. A solid all-rounder."),
    ("clip-vit-b32-q8", "CLIP ViT-B/32 (8-bit)", "Xenova/clip-vit-base-patch32", "_quantized", "clip", 224, CLIP_IMG, MIT,
     "The same model quantized to 8 bits: about 4x smaller and faster on CPU, slightly less accurate."),
    ("clip-vit-b16", "CLIP ViT-B/16", "Xenova/clip-vit-base-patch16", "", "clip", 224, CLIP_IMG, MIT,
     "OpenAI CLIP with finer image patches: more accurate than B/32, about 2.5x slower to index."),
    ("mobileclip-s0", "MobileCLIP-S0", "Xenova/mobileclip_s0", "", "clip", 256, MOBILE_IMG, APPLE,
     "Apple's MobileCLIP, smallest variant: indexes as fast as 8-bit CLIP ViT-B/32, with accuracy close to CLIP ViT-B/16."),
    ("mobileclip-s2", "MobileCLIP-S2", "Xenova/mobileclip_s2", "", "clip", 256, MOBILE_IMG, APPLE,
     "Apple's MobileCLIP, mid-size: more accurate than CLIP ViT-B/16, about 1.5x slower to index than CLIP ViT-B/32."),
    ("mobileclip-b", "MobileCLIP-B", "Xenova/mobileclip_b", "", "clip", 224, MOBILE_IMG, APPLE,
     "Apple's MobileCLIP, largest variant: the most accurate MobileCLIP, speed similar to CLIP ViT-B/16."),
    ("siglip-b16-224", "SigLIP B/16 (224)", "Xenova/siglip-base-patch16-224", "", "siglip", 224, SIGLIP_IMG, APACHE,
     "Google's SigLIP: typically more accurate than CLIP of the same size, about 3.5x slower to index than CLIP ViT-B/32. Scores use a different scale: good matches are often only 0.05 to 0.15."),
    ("siglip-b16-224-q8", "SigLIP B/16 (224, 8-bit)", "Xenova/siglip-base-patch16-224", "_quantized", "siglip", 224, SIGLIP_IMG, APACHE,
     "SigLIP B/16 quantized to 8 bits: about 4x smaller and faster, slightly less accurate."),
    ("siglip-b16-384", "SigLIP B/16 (384)", "Xenova/siglip-base-patch16-384", "", "siglip", 384, SIGLIP_IMG, APACHE,
     "SigLIP at 384x384 resolution: better on small details, about 3x slower to index than the 224 version."),
]

models = []
for (id_, name, repo, suffix, family, size, img, lic, desc) in specs:
    info = api(f"https://huggingface.co/api/models/{repo}")
    sha = info["sha"]
    sizes = {f["path"]: f["size"] for f in api(f"https://huggingface.co/api/models/{repo}/tree/{sha}/onnx")}
    root = {f["path"]: f.get("size") for f in api(f"https://huggingface.co/api/models/{repo}/tree/{sha}")}
    vision, text = f"onnx/vision_model{suffix}.onnx", f"onnx/text_model{suffix}.onnx"
    entry = dict(
        id=id_, name=name, family=family, description=desc, **lic,
        repo=repo, revision=sha,
        files=dict(
            vision=dict(path=vision, size=sizes[vision], sha256=sha256(repo, sha, vision)),
            text=dict(path=text, size=sizes[text], sha256=sha256(repo, sha, text)),
            tokenizer=dict(path="tokenizer.json", size=root["tokenizer.json"], sha256=sha256(repo, sha, "tokenizer.json")),
        ),
        image=dict(size=size, **img),
        text=(dict(maxLength=64, padTo=64, padId=1) if family == "siglip" else dict(maxLength=77, padTo=77, padId=0) if img is MOBILE_IMG else dict(maxLength=77, padTo=None, padId=0)),
    )
    models.append(entry)
    total = sum(f["size"] for f in entry["files"].values())
    print(f"{id_:20} {total/1e6:7.0f} MB  {repo}@{sha[:7]}")

write(dict(schemaVersion=1, models=models))

