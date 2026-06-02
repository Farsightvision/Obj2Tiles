#!/usr/bin/env python3
"""Deterministic atlas-quality metric — measures texture detail directly from
baked GLBs, with NO render-pipeline noise. The bake is deterministic, GLB chunk
parsing is deterministic, and the metrics are deterministic, so small A/B deltas
are reliable (unlike render var-of-Laplacian, which has ~4% run-to-run noise).

For each GLB in a tileset: extract the embedded atlas JPEG (parse GLB chunks ->
glTF JSON -> image bufferView -> BIN slice -> decode), then measure:
  - sharpness  : variance of the Laplacian (higher = more high-freq detail)
  - edge_energy: mean |gradient| (higher = crisper edges)
  - mpix       : atlas megapixels (detail *capacity*)
  - kb         : atlas JPEG bytes (information content at the encode quality)

Two modes:
  single   : python atlas_quality.py <tileset_dir>
  compare  : python atlas_quality.py <A_dir> <B_dir>   (A=reference/bar, B=candidate)

Compare matches tiles by content URI (geometry is md5-identical across configs,
so the same tile path covers the same surface) and reports per-tile + aggregate
sharpness deltas — the deterministic regression/win signal.
"""
import sys, json, struct, io
from pathlib import Path
import numpy as np
from PIL import Image


def extract_atlas(glb_path):
    """Return the first embedded image of a GLB as a PIL Image, or None."""
    data = Path(glb_path).read_bytes()
    if len(data) < 12 or data[:4] != b"glTF":
        return None
    total = struct.unpack("<I", data[8:12])[0]
    pos = 12
    json_obj = None
    bin_chunk = None
    while pos + 8 <= total:
        clen, ctype = struct.unpack("<I", data[pos:pos+4])[0], data[pos+4:pos+8]
        body = data[pos+8:pos+8+clen]
        if ctype == b"JSON":
            json_obj = json.loads(body.decode("utf-8"))
        elif ctype == b"BIN\x00":
            bin_chunk = body
        pos += 8 + clen
    if json_obj is None or not json_obj.get("images"):
        return None
    img = json_obj["images"][0]
    if "bufferView" not in img or bin_chunk is None:
        return None
    bv = json_obj["bufferViews"][img["bufferView"]]
    off, ln = bv.get("byteOffset", 0), bv["byteLength"]
    return Image.open(io.BytesIO(bin_chunk[off:off+ln])).convert("RGB")


def metrics(im):
    g = np.asarray(im.convert("L"), dtype=np.float64)
    lap = (np.gradient(np.gradient(g, axis=0), axis=0)
           + np.gradient(np.gradient(g, axis=1), axis=1))
    gy, gx = np.gradient(g)
    # Chroma-edge energy: gradient magnitude of the a*,b* opponent-ish channels (Cb,Cr proxy via
    # simple RGB->YCbCr). Captures color-edge detail that luma-only var-of-Laplacian is BLIND to —
    # needed to evaluate 4:4:4 chroma + linear-light (Compand) which act on color, not luma.
    rgb = np.asarray(im, dtype=np.float64)
    R, G, B = rgb[..., 0], rgb[..., 1], rgb[..., 2]
    Cb = -0.168736 * R - 0.331264 * G + 0.5 * B
    Cr = 0.5 * R - 0.418688 * G - 0.081312 * B
    cby, cbx = np.gradient(Cb); cry, crx = np.gradient(Cr)
    chroma_edge = float((np.hypot(cbx, cby) + np.hypot(crx, cry)).mean())
    return {
        "sharpness": float(lap.var()),
        "edge_energy": float(np.hypot(gx, gy).mean()),
        "chroma_edge": chroma_edge,
        "mpix": im.width * im.height / 1e6,
    }


def scan(tileset_dir):
    """tile content-uri -> (metrics, kb)."""
    root = Path(tileset_dir)
    out = {}
    for glb in sorted(root.rglob("*.glb")):
        im = extract_atlas(glb)
        uri = str(glb.relative_to(root))
        if im is None:
            out[uri] = (None, glb.stat().st_size / 1024)
        else:
            m = metrics(im)
            out[uri] = (m, glb.stat().st_size / 1024)
    return out


def agg(scanned):
    sh = [m["sharpness"] for m, _ in scanned.values() if m]
    ed = [m["edge_energy"] for m, _ in scanned.values() if m]
    mp = [m["mpix"] for m, _ in scanned.values() if m]
    kb = [k for _, k in scanned.values()]
    n = len(sh)
    return {
        "tiles": len(scanned), "textured": n,
        "sharp_mean": np.mean(sh) if n else 0,
        "edge_mean": np.mean(ed) if n else 0,
        "mpix_total": np.sum(mp) if n else 0,
        "kb_total": np.sum(kb), "kb_p95": np.percentile(kb, 95) if kb else 0,
    }


if __name__ == "__main__":
    if len(sys.argv) == 2:
        a = agg(scan(sys.argv[1]))
        print(f"tiles={a['tiles']} textured={a['textured']} "
              f"sharp_mean={a['sharp_mean']:.2f} edge_mean={a['edge_mean']:.3f} "
              f"mpix_total={a['mpix_total']:.2f} kb_total={a['kb_total']:.0f} kb_p95={a['kb_p95']:.0f}")
    elif len(sys.argv) == 3:
        A, B = scan(sys.argv[1]), scan(sys.argv[2])
        aa, ab = agg(A), agg(B)
        print(f"{'METRIC':14} {'A(ref)':>12} {'B(cand)':>12} {'B-vs-A':>10}")
        for key in ("sharp_mean", "edge_mean", "mpix_total", "kb_total", "kb_p95"):
            va, vb = aa[key], ab[key]
            d = (vb/va-1)*100 if va else 0.0
            print(f"{key:14} {va:>12.3f} {vb:>12.3f} {d:>+9.2f}%")
        # per-tile deltas on matched textured tiles (geometry identical -> same surface)
        common = [u for u in A if u in B and A[u][0] and B[u][0]]
        def pt(metric):
            ds = [(B[u][0][metric]/A[u][0][metric]-1)*100 for u in common if A[u][0][metric] > 0]
            if not ds: return
            worse = sum(1 for d in ds if d < -1); better = sum(1 for d in ds if d > 1)
            label = "MORE-DETAIL/sharper" if metric != "mpix" else "MORE texels"
            print(f"per-tile {metric:10} ({len(ds)}): {better} {label.split('/')[0]} / "
                  f"{worse} less / {len(ds)-better-worse} equal | "
                  f"median {np.median(ds):+.2f}% min {min(ds):+.1f}% max {max(ds):+.1f}%")
        print()
        pt("mpix")       # RESOLUTION = the primary render-softness predictor (texels/surface)
        pt("sharpness")  # per-pixel detail (note: JPEG noise can inflate this)
        pt("edge_energy")
    else:
        print(__doc__); sys.exit(1)
