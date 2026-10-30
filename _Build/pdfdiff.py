# Compares a reference PDF against ours line by line and prints only the differences, so a thesis check costs
# a few hundred tokens instead of two page dumps.
# Usage: pdfdiff.py <ref.pdf> <ours.pdf> [--pages 11-14] [--tol 0.5] [--max 40] [--raw] [--strict] [--shot 27[:500-842]]
import argparse, collections, ctypes, difflib, os, sys, tempfile, unicodedata
import pypdfium2 as pdfium
import pypdfium2.raw as pdfium_c

LIGATURES = {"ﬀ": "ff", "ﬁ": "fi", "ﬂ": "fl", "ﬃ": "ffi", "ﬄ": "ffl", "­": ""}
# Extraction noise: the reference drops hyphens and minus signs, maps its dot operators differently, and our bullet
# has no text. Line-end hyphen marks (U+FFFE) and spaces only matter for display.
NOISE = str.maketrans({"-": None, "‐": None, "−": None, "•": None, " ": None, "￾": None, "⋅": "·", "∘": "◦"})
# Stacked delimiters and big operators extract as their own lines (TeX's as Z / X), so math rows never match.
PIECES = str.maketrans({c: None for c in "ZX⎡⎢⎣⎤⎥⎦⎛⎜⎝⎞⎟⎠⎧⎨⎩⎫⎬⎭∫∑∏"})


def bare(k):
	# A line key without math pieces or the invisible private-use glyphs the reference's delimiters extract as.
	return "".join(c for c in k.translate(PIECES) if not unicodedata.category(c).startswith(("C", "Z")))


def norm(s):
	s = unicodedata.normalize("NFC", "".join(LIGATURES.get(c, c) for c in s))
	return " ".join(s.split())


def lines(page):
	# Characters grouped by their baseline origin, not their boxes: a box bottom drops with descenders and
	# overlapping text rects double characters. y is measured from the top of the page, in pt.
	height = page.get_height()
	tp = page.get_textpage()
	text = tp.get_text_range()
	x, y = ctypes.c_double(), ctypes.c_double()
	chars = []
	for i in range(min(tp.count_chars(), len(text))):
		c = text[i]
		if c.isspace() or not pdfium_c.FPDFText_GetCharOrigin(tp.raw, i, x, y):
			continue
		chars.append((height - y.value, x.value, tp.get_charbox(i)[2], c))
	tp.close()
	chars.sort(key=lambda ch: ch[0])
	rows = []
	for ch in chars:
		if rows and ch[0] - rows[-1][0][0] < 2.0:
			rows[-1].append(ch)
		else:
			rows.append([ch])
	result = []
	for row in rows:
		row.sort(key=lambda ch: ch[1])
		# Spaces are rebuilt from gaps, because our export positions every glyph and writes no space characters.
		out, last = [], None
		for base, left, right, c in row:
			if last is not None and left - last > 1.5:
				out.append(" ")
			out.append(c)
			last = right
		t = norm("".join(out))
		if t:
			result.append((round(row[0][0], 1), t))
	return result


def body_bottom(page_lines):
	# Last baseline above a trailing page number.
	body = page_lines[:-1] if page_lines and page_lines[-1][1].isdigit() else page_lines
	return body[-1][0] if body else None


def page_range(spec, count):
	if not spec:
		return range(count)
	a, _, b = spec.partition("-")
	return range(int(a) - 1, min(int(b or a), count))


def clip(s, n=70):
	return s if len(s) <= n else s[:n - 1] + "…"


def around(r, o, n=70):
	# Both sides cut to the same window, centred on the first character they differ at.
	i = next((k for k in range(min(len(r), len(o))) if r[k] != o[k]), min(len(r), len(o)))
	start = max(0, i - n // 3)
	pre = "…" if start else ""
	return pre + clip(r[start:], n), pre + clip(o[start:], n)


def groups(rk, ok):
	# Splits a changed hunk into runs of lines holding the same characters on both sides; the rest is one tail.
	out, i, j, gi, gj = [], 0, 0, 0, 0
	cr, co = collections.Counter(), collections.Counter()
	while i < len(rk) or j < len(ok):
		if j >= len(ok) or (i < len(rk) and sum(cr.values()) <= sum(co.values())):
			cr.update(bare(rk[i]))
			i += 1
		else:
			co.update(bare(ok[j]))
			j += 1
		if cr == co:
			out.append((gi, i, gj, j, True))
			gi, gj = i, j
			cr.clear()
			co.clear()
	if gi < len(rk) or gj < len(ok):
		out.append((gi, len(rk), gj, len(ok), False))
	return out


def reflow_break(rl, ol):
	# The first line end the two sides do not share, as "tail|head" on each side.
	def ends(side):
		at, out = 0, {}
		for k in range(len(side) - 1):
			at += len(side[k][1].translate(NOISE))
			out[at] = k
		return out
	re, oe = ends(rl), ends(ol)
	def show(side, e, other):
		k = next((e[a] for a in sorted(e) if a not in other), None)
		return "—" if k is None else f"…{side[k][1][-24:]}|{side[k + 1][1][:12]}…"
	return show(rl, re, oe), show(ol, oe, re)


def shot(ref, ours, spec):
	p, _, span = spec.partition(":")
	p = int(p) - 1
	ims = [doc[p].render(scale=1.6).to_pil() for doc in (ref, ours)]
	if span:
		y0, _, y1 = span.partition("-")
		ims = [im.crop((0, int(float(y0) * 1.6), im.width, int(float(y1) * 1.6))) for im in ims]
	from PIL import Image
	out = Image.new("RGB", (ims[0].width + ims[1].width + 10, max(im.height for im in ims)), "red")
	out.paste(ims[0], (0, 0))
	out.paste(ims[1], (ims[0].width + 10, 0))
	folder = os.path.join(tempfile.gettempdir(), "aurora-pdfdiff")
	os.makedirs(folder, exist_ok=True)
	path = os.path.join(folder, f"p{p + 1}{'_' + span.replace('-', '_') if span else ''}.png")
	out.save(path)
	print(f"shot p{p + 1} (ref | ours): {path}")


def main():
	ap = argparse.ArgumentParser()
	ap.add_argument("ref")
	ap.add_argument("ours")
	ap.add_argument("--pages")
	ap.add_argument("--tol", type=float, default=0.5)
	ap.add_argument("--max", type=int, default=40)
	ap.add_argument("--raw", action="store_true", help="no noise filtering or reflow grouping")
	ap.add_argument("--strict", action="store_true", help="exit 1 when anything differs")
	ap.add_argument("--shot", action="append", default=[], help="page[:y0-y1] side by side as a PNG")
	a = ap.parse_args()

	ref, ours = pdfium.PdfDocument(a.ref), pdfium.PdfDocument(a.ours)
	for spec in a.shot:
		shot(ref, ours, spec)
	if a.shot and not a.pages:
		return 0

	printed = 0
	counts = collections.Counter()
	first = None

	def emit(msg):
		nonlocal printed
		if printed < a.max:
			print(msg)
		printed += 1

	def real():
		return sum(counts.values()) - counts["noise"]

	ref_lines = [lines(page) for page in ref]
	bottom = max((b for b in map(body_bottom, ref_lines) if b is not None), default=None)
	key = (lambda t: t.replace(" ", "")) if a.raw else (lambda t: t.translate(NOISE))
	pages = page_range(a.pages, max(len(ref), len(ours)))
	identical = 0
	for p in pages:
		if p >= len(ref) or p >= len(ours):
			emit(f"p{p + 1} only in {'ours' if p >= len(ref) else 'ref'}")
			first = first or p + 1
			continue
		rl, ol = ref_lines[p], lines(ours[p])
		before = real()
		ob = body_bottom(ol)
		if bottom is not None and ob is not None and ob > bottom + a.tol:
			emit(f"p{p + 1} overflow: last line {ob} below the text bottom {bottom}")
			counts["overflow"] += 1
		rk, ok = [key(t) for _, t in rl], [key(t) for _, t in ol]
		rs, os_ = [bare(k) for k in rk if bare(k)], [bare(k) for k in ok if bare(k)]
		if rs and os_ and rs[0] != os_[0]:
			emit(f"p{p + 1} break: ref starts \"{clip(rl[0][1], 50)}\", ours \"{clip(ol[0][1], 50)}\"")
			counts["break"] += 1
		sm = difflib.SequenceMatcher(None, rk, ok, autojunk=False)
		prev = 0.0
		for op, i1, i2, j1, j2 in sm.get_opcodes():
			if op == "equal":
				for k in range(i2 - i1):
					d = ol[j1 + k][0] - rl[i1 + k][0]
					if abs(d) > a.tol:
						counts["pos"] += 1
					# One shifted line moves every line under it; print only where the offset changes.
					if abs(d - prev) > a.tol:
						emit(f"p{p + 1} L{i1 + k + 1} y {rl[i1 + k][0]} vs {ol[j1 + k][0]} ({d:+.1f}) \"{clip(rl[i1 + k][1], 40)}\"")
					prev = d
				continue
			hunks = [(0, i2 - i1, 0, j2 - j1, False)] if a.raw else groups(rk[i1:i2], ok[j1:j2])
			for gi1, gi2, gj1, gj2, same in hunks:
				r, o = rl[i1 + gi1:i1 + gi2], ol[j1 + gj1:j1 + gj2]
				if same:
					math = any(k != bare(k) for k in rk[i1 + gi1:i1 + gi2] + ok[j1 + gj1:j1 + gj2])
					if math or "".join(rk[i1 + gi1:i1 + gi2]) != "".join(ok[j1 + gj1:j1 + gj2]):
						counts["noise"] += 1
						continue
					rb, ob_ = reflow_break(r, o)
					emit(f"p{p + 1} L{i1 + gi1 + 1}-{i1 + gi2} reflow {len(r)}/{len(o)} lines: ref \"{rb}\" ours \"{ob_}\"")
					counts["reflow"] += 1
					continue
				for k in range(max(len(r), len(o))):
					rt = r[k][1] if k < len(r) else "∅"
					ot = o[k][1] if k < len(o) else "∅"
					rt, ot = around(rt, ot)
					emit(f"p{p + 1} L{i1 + gi1 + k + 1} text: \"{rt}\" ≠ \"{ot}\"")
					counts["text"] += 1
		if real() == before:
			identical += 1
		elif first is None:
			first = p + 1

	if printed > a.max:
		print(f"… {printed - a.max} more diff lines (raise --max or narrow --pages)")
	print(f"pages {len(ours)}/{len(ref)}, {identical}/{len(pages)} identical, reflow {counts['reflow']}, text {counts['text']}, "
		f"pos {counts['pos']} > {a.tol} pt, overflow {counts['overflow']}, noise {counts['noise']}, "
		f"first divergence {'p' + str(first) if first else 'none'}")
	return 1 if a.strict and first else 0


if __name__ == "__main__":
	sys.exit(main())
