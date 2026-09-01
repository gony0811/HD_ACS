#!/usr/bin/env python3
"""
Markdown 사양서 → PDF 생성기 (mermaid 다이어그램 렌더링 포함).

용도: docs/VDA5050_INTERFACE_SPEC.md 등 사양서를 배포용 PDF로 변환한다.
      사양서를 개정할 때마다 PDF도 함께 재생성할 것.

사용법:
    python3 tools/build_spec_pdf.py docs/VDA5050_INTERFACE_SPEC.md
    python3 tools/build_spec_pdf.py docs/VDA5050_INTERFACE_SPEC.md -o /tmp/out.pdf

사전 준비 (한 번만):
    pip install markdown playwright --break-system-packages
    python3 -m playwright install chromium
    npm install mermaid@10.9.1          # 스크립트와 같은 디렉터리 또는 리포 루트에서
    # 한글 폰트 필요: Noto Sans CJK KR (Linux: fonts-noto-cjk / macOS: 기본 탑재)

mermaid.min.js 탐색 순서:
    1) --mermaid 로 지정한 경로
    2) <리포>/node_modules/mermaid/dist/mermaid.min.js
    3) <스크립트 디렉터리>/node_modules/mermaid/dist/mermaid.min.js
  못 찾으면 다이어그램은 코드블록 그대로 출력된다(문서는 정상 생성).
"""
import argparse, html as _html, pathlib, re, sys

CSS = """
@page { size: A4; margin: 18mm 16mm 20mm 16mm; }
* { box-sizing: border-box; }
body { font-family: "Noto Sans CJK KR","Noto Sans KR","Apple SD Gothic Neo",-apple-system,sans-serif;
       font-size: 10pt; line-height: 1.62; color: #1a1d21; margin: 0; }
h1 { font-size: 19pt; border-bottom: 2.5px solid #1a4d8f; padding-bottom: 8px; margin: 0 0 16px; color:#0f2f5c; }
h2 { font-size: 14pt; margin: 26px 0 10px; padding: 6px 0 6px 10px; border-left: 4px solid #1a4d8f;
     background: #f2f5fa; color:#0f2f5c; page-break-after: avoid; }
h3 { font-size: 11.5pt; margin: 18px 0 7px; color: #23405f; page-break-after: avoid; }
h4 { font-size: 10.5pt; margin: 14px 0 6px; color:#23405f; page-break-after: avoid; }
p, li { orphans: 2; widows: 2; }
table { border-collapse: collapse; width: 100%; margin: 10px 0 14px; font-size: 8.6pt; page-break-inside: avoid; }
th { background: #e8eef7; border: 1px solid #b9c6d8; padding: 5px 7px; text-align: left; color:#123; }
td { border: 1px solid #ccd5e2; padding: 5px 7px; vertical-align: top; }
tr:nth-child(even) td { background: #fafbfd; }
code { font-family: "DejaVu Sans Mono","SFMono-Regular",monospace; font-size: 8.4pt;
       background: #f0f2f5; padding: 1px 4px; border-radius: 3px; color:#8a2d2d; }
pre { background: #f7f8fa; border: 1px solid #dfe3e9; border-left: 3px solid #7d8ea3;
      padding: 9px 11px; border-radius: 3px; overflow-x: hidden; page-break-inside: avoid; }
pre code { background: none; padding: 0; font-size: 8pt; color:#24292e; white-space: pre-wrap; word-break: break-all; }
blockquote { margin: 10px 0; padding: 8px 12px; background: #fffdf3; border-left: 3px solid #d9a441;
             color: #4a4335; font-size: 9.2pt; page-break-inside: avoid; }
blockquote p { margin: 4px 0; }
hr { border: none; border-top: 1px solid #dde2e8; margin: 22px 0; }
ul, ol { padding-left: 20px; margin: 8px 0; }
li { margin: 3px 0; }
.mermaid { background: #fff; text-align: center; margin: 14px 0; page-break-inside: avoid; }
a { color: #1a4d8f; text-decoration: none; }
strong { color: #0d1b2a; }
"""


def find_mermaid(explicit, md_path):
    if explicit:
        return pathlib.Path(explicit)
    rel = "node_modules/mermaid/dist/mermaid.min.js"
    for base in (md_path.resolve().parent.parent, pathlib.Path(__file__).resolve().parent):
        p = base / rel
        if p.exists():
            return p
    return None


def build_html(md_path, mermaid_js, title):
    import markdown
    src = md_path.read_text(encoding="utf-8")
    blocks = []

    def stash(m):
        blocks.append(m.group(1))
        return f"\n@@MERMAID{len(blocks)-1}@@\n"

    src = re.sub(r"```mermaid\n(.*?)```", stash, src, flags=re.S)
    body = markdown.markdown(src, extensions=["tables", "fenced_code", "toc", "sane_lists", "attr_list"])
    for i, b in enumerate(blocks):
        body = body.replace(f"<p>@@MERMAID{i}@@</p>", f'<pre class="mermaid">{_html.escape(b)}</pre>')

    if mermaid_js:
        script = (f"<script>{mermaid_js.read_text(encoding='utf-8')}</script>"
                  "<script>"
                  "mermaid.initialize({startOnLoad:false,theme:'neutral',securityLevel:'loose',"
                  "fontFamily:'Noto Sans CJK KR, sans-serif',flowchart:{useMaxWidth:true},"
                  "sequence:{useMaxWidth:true}});"
                  "window.__done=false;"
                  "mermaid.run({querySelector:'.mermaid'})"
                  ".then(()=>{window.__done=true}).catch(e=>{console.error(e);window.__done=true});"
                  "</script>")
    else:
        script = "<script>window.__done=true;</script>"
        if blocks:
            print(f"  ! mermaid.min.js 없음 — 다이어그램 {len(blocks)}개는 텍스트로 출력", file=sys.stderr)

    return (f'<!doctype html><html lang="ko"><head><meta charset="utf-8">'
            f"<title>{_html.escape(title)}</title><style>{CSS}</style></head><body>"
            f"{body}{script}</body></html>")


def render_pdf(html_path, out_path, header):
    from playwright.sync_api import sync_playwright
    with sync_playwright() as p:
        b = p.chromium.launch()
        pg = b.new_page()
        pg.goto("file://" + str(html_path.resolve()), wait_until="load")
        pg.wait_for_function("window.__done === true", timeout=90000)
        pg.wait_for_timeout(1200)
        n = pg.eval_on_selector_all(".mermaid svg", "els=>els.length")
        pg.pdf(path=str(out_path), format="A4", print_background=True,
               margin={"top": "18mm", "bottom": "20mm", "left": "16mm", "right": "16mm"},
               display_header_footer=True,
               header_template=('<div style="font-size:7pt;color:#8a94a3;width:100%;padding:0 16mm;">'
                                f"{_html.escape(header)}</div>"),
               footer_template=('<div style="font-size:7pt;color:#8a94a3;width:100%;padding:0 16mm;'
                                'text-align:right;"><span class="pageNumber"></span> / '
                                '<span class="totalPages"></span></div>'))
        b.close()
    return n


def main():
    ap = argparse.ArgumentParser(description="Markdown 사양서 → PDF (mermaid 렌더링 포함)")
    ap.add_argument("markdown", help="입력 .md 경로")
    ap.add_argument("-o", "--out", help="출력 .pdf 경로 (기본: 같은 위치·같은 이름)")
    ap.add_argument("--mermaid", help="mermaid.min.js 경로 (미지정 시 자동 탐색)")
    ap.add_argument("--header", help="페이지 머리글 (기본: 문서 첫 H1)")
    a = ap.parse_args()

    md = pathlib.Path(a.markdown)
    if not md.exists():
        sys.exit(f"입력 파일 없음: {md}")
    out = pathlib.Path(a.out) if a.out else md.with_suffix(".pdf")

    first_h1 = next((l[2:].strip() for l in md.read_text(encoding="utf-8").splitlines()
                     if l.startswith("# ")), md.stem)
    header = a.header or first_h1

    mm = find_mermaid(a.mermaid, md)
    html = build_html(md, mm, first_h1)
    tmp = out.with_suffix(".build.html")
    tmp.write_text(html, encoding="utf-8")
    try:
        n = render_pdf(tmp, out, header)
    finally:
        tmp.unlink(missing_ok=True)
    print(f"생성: {out}  (mermaid {n}개 렌더링)")


if __name__ == "__main__":
    main()
