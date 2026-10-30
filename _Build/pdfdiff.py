# Compares a reference PDF against ours line by line and prints only the differences, so a thesis check costs
# a few hundred tokens instead of two page dumps. Usage: pdfdiff.py <ref.pdf> <ours.pdf> [--pages 11-14] [--tol 0.5] [--max 40]
import argparse, ctypes, difflib, sys, unicodedata
import pypdfium2 as pdfium
import pypdfium2.raw as pdfium_c

LIGATURES = {"ﬀ": "ff", "ﬁ": "fi", "ﬂ": "fl", "ﬃ": "ffi", "ﬄ": "ffl", "­": ""}


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


def main():
	ap = argparse.ArgumentParser()
	ap.add_argument("ref")
	ap.add_argument("ours")
	ap.add_argument("--pages")
	ap.add_argument("--tol", type=float, default=0.5)
	ap.add_argument("--max", type=int, default=40)
	a = ap.parse_args()

	ref, ours = pdfium.PdfDocument(a.ref), pdfium.PdfDocument(a.ours)
	printed, text_diffs, pos_diffs, identical, first = 0, 0, 0, 0, None

	def emit(msg):
		nonlocal printed
		if printed < a.max:
			print(msg)
		printed += 1

	pages = page_range(a.pages, max(len(ref), len(ours)))
	for p in pages:
		if p >= len(ref) or p >= len(ours):
			emit(f"p{p + 1} only in {'ours' if p >= len(ref) else 'ref'}")
			first = first or p + 1
			continue
		rl, ol = lines(ref[p]), lines(ours[p])
		page_text, page_pos = 0, 0
		# Lines match on their characters alone; word spacing is rebuilt from gaps and not worth a diff.
		rk, ok = [t.replace(" ", "") for _, t in rl], [t.replace(" ", "") for _, t in ol]
		if rk and ok and rk[0] != ok[0]:
			emit(f"p{p + 1} break: ref starts \"{clip(rl[0][1], 50)}\", ours \"{clip(ol[0][1], 50)}\"")
		if len(rl) != len(ol):
			emit(f"p{p + 1} lines {len(rl)} vs {len(ol)}")
		sm = difflib.SequenceMatcher(None, rk, ok, autojunk=False)
		prev = 0.0
		for op, i1, i2, j1, j2 in sm.get_opcodes():
			if op == "equal":
				for k in range(i2 - i1):
					d = ol[j1 + k][0] - rl[i1 + k][0]
					if abs(d) > a.tol:
						page_pos += 1
					# One shifted line moves every line under it; print only where the offset changes.
					if abs(d - prev) > a.tol:
						emit(f"p{p + 1} L{i1 + k + 1} y {rl[i1 + k][0]} vs {ol[j1 + k][0]} ({d:+.1f}) \"{clip(rl[i1 + k][1], 40)}\"")
					prev = d
				continue
			page_text += max(i2 - i1, j2 - j1)
			for k in range(max(i2 - i1, j2 - j1)):
				r = rl[i1 + k][1] if i1 + k < i2 else "∅"
				o = ol[j1 + k][1] if j1 + k < j2 else "∅"
				r, o = around(r, o)
				emit(f"p{p + 1} L{i1 + k + 1} text: \"{r}\" ≠ \"{o}\"")
		text_diffs += page_text
		pos_diffs += page_pos
		if page_text == 0 and page_pos == 0 and len(rl) == len(ol):
			identical += 1
		elif first is None:
			first = p + 1

	if printed > a.max:
		print(f"… {printed - a.max} more diff lines (raise --max or narrow --pages)")
	print(f"pages {len(ours)}/{len(ref)}, {identical}/{len(pages)} pages identical, {text_diffs} text diffs, "
		f"{pos_diffs} position diffs > {a.tol} pt, first divergence {'p' + str(first) if first else 'none'}")
	return 1 if first else 0


if __name__ == "__main__":
	sys.exit(main())
