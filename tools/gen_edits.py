"""Regenerate corpus edits.json files with >20 operations each.

Ops are derived from each sample's document.json so every target satisfies
EditEngine validation (mcid>=0, non-artifact, bbox, whitelisted tags, donor
siblings for adds). setText/add content reuses the donor node's own characters
so the embedded-font-subset check always passes.
"""
import json, sys, io
from pathlib import Path

ROOT = Path(r"C:\projects\pdf\apex\SamplePDFs-ExtracredJSON")

PARA_PARENT_WL = {"Document", "Sect", "Div", "Art", "BlockQuote", "TOC", "TOCI", "Caption", "Note"}
# No H1: deleting a document's top heading skips a level and fails PDF/UA-1 verify.
DELETE_WL = {"P", "H2", "H3", "H4", "H5", "H6", "Lbl", "Span"}

def rotate_words(text):
    words = text.split()
    if len(words) >= 3:
        return " ".join(words[1:] + words[:1])
    if len(words) == 2:
        return " ".join([words[1], words[0]])
    return text[::-1] if len(text) > 1 else text

def load(folder):
    name = folder.name
    doc_path = folder / f"{name}-document.json"
    edits_path = folder / f"{name}-edits.json"
    if not doc_path.exists() or not edits_path.exists():
        return None
    doc = json.loads(doc_path.read_text(encoding="utf-8-sig"))
    edits = json.loads(edits_path.read_text(encoding="utf-8-sig"))
    geom_path = folder / f"{name}-geometry.json"
    geom = json.loads(geom_path.read_text(encoding="utf-8-sig")) if geom_path.exists() else {}
    return doc, edits, edits_path, geom

def build(folder):
    loaded = load(folder)
    if loaded is None:
        print(f"SKIP {folder.name}: missing document/edits json")
        return
    doc, edits, edits_path, geom = loaded
    tree = doc["tree"]
    by_id = {n["id"]: n for n in tree}
    children = {}
    for n in tree:
        children.setdefault(n.get("parent"), []).append(n)

    def kids(nid):
        return children.get(nid, [])

    def has_bbox(n):
        return n["width"] > 0 and n["height"] > 0

    def is_leaf_text(n):
        return (n["mcid"] >= 0 and not n["isArtifact"] and has_bbox(n)
                and n.get("content", "").strip())

    leaves_by_page = {}
    for n in tree:
        if n["mcid"] >= 0 and not n["isArtifact"] and has_bbox(n):
            leaves_by_page.setdefault(n["page"], []).append(n)

    def overlaps_other_leaf(n):
        for m in leaves_by_page.get(n["page"], []):
            if m["id"] == n["id"]:
                continue
            ox = min(n["x"] + n["width"], m["x"] + m["width"]) - max(n["x"], m["x"])
            oy = min(n["y"] + n["height"], m["y"] + m["height"]) - max(n["y"], m["y"])
            if ox > 1 and oy > 1:
                return True
        return False

    def fits_bbox(n):
        # Fragment nodes carry the whole paragraph in `content` while their mcid draws
        # only the first words (CARE p1 "El Programa" — bbox 46x10, content 400 chars).
        h, w = n["height"], n["width"]
        font = min(12.0, h) if h <= 16 else 10.0
        lines = max(1, round(h / (font * 1.2)))
        capacity = (w / (0.5 * font)) * lines * 1.35
        return len(n.get("content", "")) <= capacity

    def inline_continuation(n):
        # A leaf edge-adjacent to another leaf on the same baseline is an inline
        # fragment (e.g. a Span mid-sentence). Replacing it breaks the glyph-advance
        # cursor for the rest of the line — the writer restores Tm+Td state only.
        for m in leaves_by_page.get(n["page"], []):
            if m["id"] == n["id"]:
                continue
            if abs(m["y"] - n["y"]) > 2:
                continue
            gap1 = n["x"] - (m["x"] + m["width"])
            gap2 = m["x"] - (n["x"] + n["width"])
            if -1 < gap1 < 4 or -1 < gap2 < 4:
                return True
        return False

    # Marked-content ids per page that no tree node owns. Their words are drawn text the
    # engine cannot redact (PLATO p1: node 19's second line tail is orphan mcid 20 —
    # setText left the old tail behind next to the replacement).
    tree_mcids = {}
    for n in tree:
        if n["mcid"] >= 0:
            tree_mcids.setdefault(n["page"], set()).add(n["mcid"])
    orphan_words = {}
    for page_str, mcids in (geom.get("pageMcidWords") or {}).items():
        page = int(page_str)
        for mcid_str, words in mcids.items():
            if int(mcid_str) in tree_mcids.get(page, set()):
                continue
            orphan_words.setdefault(page, []).extend(words)

    def contains_orphan_words(n):
        for w in orphan_words.get(n["page"], []):
            cx = w["X"] + w["Width"] / 2
            cy = w["Y"] + w["Height"] / 2
            if n["x"] < cx < n["x"] + n["width"] and n["y"] < cy < n["y"] + n["height"]:
                return True
        return False

    # Form-field widgets embedded mid-paragraph (fill-in blanks). The writer re-wraps
    # replacement text from the bbox left edge, running it underneath the widgets.
    form_nodes = {}
    for m in tree:
        if m.get("text") == "Form" and has_bbox(m):
            form_nodes.setdefault(m["page"], []).append(m)

    def contains_form_widget(n):
        for m in form_nodes.get(n["page"], []):
            cx = m["x"] + m["width"] / 2
            cy = m["y"] + m["height"] / 2
            if n["x"] < cx < n["x"] + n["width"] and n["y"] < cy < n["y"] + n["height"]:
                return True
        return False

    def safe_target(n):
        return (n.get("text") != "Link" and not overlaps_other_leaf(n)
                and fits_bbox(n) and not inline_continuation(n)
                and not contains_orphan_words(n)
                and not contains_form_widget(n))

    existing_ops = [o for o in edits.get("operations", []) if not o["id"].startswith("gen-")]
    used_targets = set()
    for op in existing_ops:
        for key in ("target", "parent"):
            if key in op:
                used_targets.add(op[key])

    ops = list(existing_ops)
    seq = 0

    def next_id(kind):
        nonlocal seq
        seq += 1
        return f"gen-{kind}-{seq:02d}"

    # --- setText candidates: leaf text nodes, content long enough to visibly change
    set_candidates = [n for n in tree if is_leaf_text(n)
                      and len(n["content"].split()) >= 2
                      and n["id"] not in used_targets
                      and safe_target(n)]
    set_candidates.sort(key=lambda n: (n["page"], -(n["y"])))
    # spread across pages: take at most 2 per page
    per_page = {}
    picked_set = []
    for n in set_candidates:
        if per_page.get(n["page"], 0) >= 2:
            continue
        per_page[n["page"]] = per_page.get(n["page"], 0) + 1
        picked_set.append(n)
        if len(picked_set) >= 14:
            break
    for n in picked_set:
        used_targets.add(n["id"])
        ops.append({
            "id": next_id("setText"),
            "type": "setText",
            "page": n["page"],
            "target": n["id"],
            "newContent": rotate_words(n["content"]),
        })

    # --- deleteNode: leaf (no non-artifact descendants), whitelisted tag
    def non_artifact_desc(n):
        stack = list(kids(n["id"]))
        while stack:
            c = stack.pop()
            if not c["isArtifact"]:
                return True
            stack.extend(kids(c["id"]))
        return False

    del_candidates = [n for n in tree if n["mcid"] >= 0 and not n["isArtifact"]
                      and n.get("text") in DELETE_WL
                      and n["id"] not in used_targets
                      and not non_artifact_desc(n)
                      and has_bbox(n) and safe_target(n)]
    del_candidates.sort(key=lambda n: (n["page"], n["y"]))
    picked_del = []
    seen_pages = set()
    for n in del_candidates:
        if n["page"] in seen_pages:
            continue
        seen_pages.add(n["page"])
        picked_del.append(n)
        if len(picked_del) >= 4:
            break
    for n in picked_del:
        used_targets.add(n["id"])
        ops.append({
            "id": next_id("delete"),
            "type": "deleteNode",
            "page": n["page"],
            "target": n["id"],
        })

    # --- addParagraph: parent in whitelist with a P child that has bbox+mcid, high on page
    para_parents = []
    for n in tree:
        if n.get("text") not in PARA_PARENT_WL:
            continue
        ch = kids(n["id"])
        donors = [c for c in ch if c.get("text") == "P" and has_bbox(c)
                  and c.get("content", "").strip()
                  and c["id"] not in used_targets]
        mcid_ok = any(c["mcid"] >= 0 for c in ch) or any(
            any(g["mcid"] >= 0 for g in kids(c["id"])) for c in ch)
        if not donors or not mcid_ok:
            continue
        bbox_ch = [c for c in ch if has_bbox(c)]
        last = min(bbox_ch, key=lambda c: c["y"])
        if last["y"] < 150:
            continue
        # Mirror EditEngine's placement (below the parent's lowest child) and skip
        # donors whose appended copy would collide with an existing leaf.
        donor = donors[-1]
        # EditEngine re-wraps the content at the donor width, so the real box can be
        # taller than the donor bbox — pad generously before checking for collisions.
        est_h = 2 * donor["height"] + 20
        new_y = last["y"] - 12 - est_h
        collides = False
        for m in leaves_by_page.get(donor["page"], []):
            ox = min(donor["x"] + donor["width"], m["x"] + m["width"]) - max(donor["x"], m["x"])
            oy = min(new_y + est_h, m["y"] + m["height"]) - max(new_y, m["y"])
            if ox > 1 and oy > 1:
                collides = True
                break
        if collides:
            continue
        para_parents.append((n, donor))
    para_parents.sort(key=lambda t: t[1]["page"])
    seen_pages = set()
    added_para = 0
    for parent, donor in para_parents:
        if donor["page"] in seen_pages:
            continue
        seen_pages.add(donor["page"])
        ops.append({
            "id": next_id("addPara"),
            "type": "addParagraph",
            "parent": parent["id"],
            "index": -1,
            "tag": "P",
            "content": rotate_words(donor["content"]),
            "style": {"inheritFrom": donor["id"]},
        })
        added_para += 1
        if added_para >= 3:
            break

    # --- addListItem: L container with LI children each having Lbl+LBody, room below
    added_li = 0
    for n in tree:
        if added_li >= 2:
            break
        if n.get("text") not in ("L", "List"):
            continue
        lis = [c for c in kids(n["id"]) if c.get("text") == "LI"]
        if not lis:
            continue
        donor = lis[-1]
        dk = kids(donor["id"])
        lbl = next((k for k in dk if k.get("text") == "Lbl"), None)
        lbody = next((k for k in dk if k.get("text") == "LBody"), None)
        if lbl is None or lbody is None:
            continue
        if {donor["id"], lbl["id"], lbody["id"]} & used_targets:
            continue
        if not (has_bbox(lbl) and has_bbox(lbody)):
            continue
        if min(lbl["y"], lbody["y"]) < 150:
            continue
        # Placement pre-check (mirrors the addPara guard): the engine lands the new item
        # below the list's lowest child and can refuse (or push into) close neighbours.
        row_left = min(lbl["x"], lbody["x"])
        row_right = max(lbl["x"] + lbl["width"], lbody["x"] + lbody["width"])
        est_h = 2 * max(lbl["height"], lbody["height"]) + 20
        new_y = min(lbl["y"], lbody["y"]) - 6 - est_h
        li_ids = set()
        stack = list(kids(n["id"]))
        while stack:
            c = stack.pop()
            li_ids.add(c["id"])
            stack.extend(kids(c["id"]))
        collides = False
        for m in leaves_by_page.get(lbody["page"], []):
            if m["id"] in li_ids:
                continue
            ox = min(row_right, m["x"] + m["width"]) - max(row_left, m["x"])
            oy = min(new_y + est_h, m["y"] + m["height"]) - max(new_y, m["y"])
            if ox > 1 and oy > 1:
                collides = True
                break
        if collides:
            continue
        # Bullet-list Lbl glyphs are often absent from extracted content; "•" survives
        # the donor-font-subset check while "-" renders as a visibly wrong label.
        lbl_text = lbl.get("content", "").strip() or "\u2022"
        body_src = lbody.get("content", "").strip()
        if not body_src:
            leaf = next((k for k in kids(lbody["id"]) if k.get("content", "").strip()), None)
            if leaf is None:
                continue
            body_src = leaf["content"]
        ops.append({
            "id": next_id("addLI"),
            "type": "addListItem",
            "parent": n["id"],
            "index": -1,
            "labelText": lbl_text,
            "bodyText": rotate_words(body_src),
            "style": {"inheritFrom": donor["id"]},
        })
        added_li += 1

    # top up with single-per-page setText if still <= 20
    if len(ops) <= 20:
        extra = [n for n in tree if is_leaf_text(n) and n["id"] not in used_targets
                 and safe_target(n)]
        extra.sort(key=lambda n: (n["page"], -(n["y"])))
        for n in extra:
            if len(ops) > 22:
                break
            used_targets.add(n["id"])
            ops.append({
                "id": next_id("setText"),
                "type": "setText",
                "page": n["page"],
                "target": n["id"],
                "newContent": rotate_words(n["content"]),
            })

    edits["operations"] = ops
    edits_path.write_text(json.dumps(edits, ensure_ascii=False, indent=2) + "\n",
                          encoding="utf-8")
    print(f"{folder.name}: {len(ops)} ops "
          f"(set={sum(1 for o in ops if o['type']=='setText')}, "
          f"del={sum(1 for o in ops if o['type']=='deleteNode')}, "
          f"addP={sum(1 for o in ops if o['type']=='addParagraph')}, "
          f"addLI={sum(1 for o in ops if o['type']=='addListItem')})")

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
for folder in sorted(ROOT.iterdir()):
    if folder.is_dir():
        build(folder)
