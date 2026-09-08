"""Read ASCII DXF entities without executing embedded content."""
from pathlib import Path
from collections import Counter, defaultdict
import json

ROOT = Path(__file__).resolve().parents[1]

def entities(path):
    lines = path.read_text(encoding='cp949', errors='replace').splitlines()
    pairs = [(int(lines[i].strip()), lines[i+1].strip()) for i in range(0, len(lines)-1, 2)]
    section = None
    result = []
    current = None
    for i, (code, value) in enumerate(pairs):
        if code == 2 and i and pairs[i-1] == (0, 'SECTION'):
            section = value
        if code == 0:
            if current is not None:
                result.append(current)
                current = None
            if section == 'ENTITIES' and value != 'ENDSEC':
                current = {'type': value, 'groups': defaultdict(list)}
        elif current is not None:
            current['groups'][code].append(value)
    return result

def first(e, code, default=''):
    return e['groups'].get(code, [default])[0]

def points(e):
    g = e['groups']
    if e['type'] == 'LINE':
        return [(float(first(e,10)), float(first(e,20))), (float(first(e,11)), float(first(e,21)))]
    return list(zip(map(float,g.get(10,[])), map(float,g.get(20,[]))))

if __name__ == '__main__':
    for path in sorted((ROOT/'drawing'/'2D도면').glob('*.dxf')):
        es = entities(path)
        print('\n',path.name)
        for layer in sorted(set(first(e,8) for e in es)):
            if 'Membrane' not in layer and 'Steel Wall' not in layer:
                continue
            subset = [e for e in es if first(e,8)==layer]
            pts = [p for e in subset if e['type'] in ('LINE','LWPOLYLINE') for p in points(e)]
            print(layer, Counter(e['type'] for e in subset), 'bounds', (min(p[0] for p in pts),min(p[1] for p in pts),max(p[0] for p in pts),max(p[1] for p in pts)) if pts else None)
            horizontal, vertical = [], []
            for e in subset:
                if e['type']!='LINE': continue
                a,b=points(e)
                if abs(a[1]-b[1])<.01: horizontal.append((round(a[1],3),round(abs(a[0]-b[0]),3)))
                if abs(a[0]-b[0])<.01: vertical.append((round(a[0],3),round(abs(a[1]-b[1]),3)))
            print('H lengths',Counter(l for _,l in horizontal).most_common(8),'V lengths',Counter(l for _,l in vertical).most_common(8))
            print('H positions', sorted(set(p for p,l in horizontal))[:35], 'V positions', sorted(set(p for p,l in vertical))[:35])
