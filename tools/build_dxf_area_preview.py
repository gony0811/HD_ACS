"""Build a review-only area/task coordinate list from DXF line geometry.

Coordinates are drawing-local millimetres, not calibrated ACS wall coordinates.
"""
from pathlib import Path
from collections import defaultdict, Counter
import heapq
import json
from analyze_dxf_regions import ROOT, entities, first, points

OUT = ROOT / 'drawing' / 'area-preview'
EPS = .03
AREA_WIDTH = 1440
AREA_HEIGHT = 1440

def segment_records(es, layers):
    result=[]
    for e in es:
        if first(e,8) not in layers: continue
        if e['type'] not in ('LINE','LWPOLYLINE'): continue
        if e['type']=='LWPOLYLINE' and (any(abs(float(v))>1e-8 for v in e['groups'].get(42,[])) or abs(float(first(e,230,'1'))-1)>1e-8):
            continue
        pts=points(e)
        edges=list(zip(pts,pts[1:]))
        if e['type']=='LWPOLYLINE' and int(first(e,70,'0')) & 1 and pts[-1]!=pts[0]:
            edges.append((pts[-1],pts[0]))
        for a,b in edges:
            if abs(a[1]-b[1])<EPS:
                axis,fixed,lo,hi='H',a[1],min(a[0],b[0]),max(a[0],b[0])
            elif abs(a[0]-b[0])<EPS:
                axis,fixed,lo,hi='V',a[0],min(a[1],b[1]),max(a[1],b[1])
            else: continue
            if hi-lo<EPS: continue
            result.append({'axis':axis,'fixed':round(fixed,2),'lo':round(lo,3),'hi':round(hi,3),'handles':[first(e,5)]})
    return result

def merge_segments(segs):
    grouped=defaultdict(list)
    for s in segs: grouped[(s['axis'],s['fixed'])].append(s)
    out=[]
    for key,ss in sorted(grouped.items()):
        current=None
        for s in sorted(ss,key=lambda x:x['lo']):
            if current and s['lo']<=current['hi']+EPS:
                current['hi']=max(current['hi'],s['hi'])
                current['handles']=sorted(set(current['handles']+s['handles']))
            else:
                if current: out.append(current)
                current=dict(s)
        if current: out.append(current)
    return out

def near_point(axis,along,fixed,origin):
    xy=(along,fixed) if axis=='H' else (fixed,along)
    return [round(xy[i]-origin[i],3) for i in (0,1)]

def build_wall(path):
    es=entities(path)
    wall=path.stem.split()[-1]
    layers={first(e,8) for e in es if 'Membrane Sheet' in first(e,8)}
    seams=merge_segments(segment_records(es,layers))
    pts=[p for e in es if first(e,8) in layers and e['type'] in ('LINE','LWPOLYLINE') for p in points(e)]
    origin=[min(p[i] for p in pts) for i in (0,1)]
    size=[max(p[i] for p in pts)-origin[i] for i in (0,1)]
    center_es=[e for e in es if first(e,8)=='KC-2B Steel Wall' and first(e,6).startswith('CENTER')]
    topsegs=merge_segments(segment_records(center_es,{'KC-2B Steel Wall'}))
    topcoords={a:sorted({s['fixed'] for s in topsegs if s['axis']==a}) for a in ('H','V')}
    pitches={a:Counter(round(b-a0,2) for a0,b in zip(topcoords[a],topcoords[a][1:])).most_common(3) for a in ('H','V')}
    common={'wall':wall,'source_file':path.name,'origin_dxf':origin,'size_drawing':size,'observed_pitch':pitches,
            'coordinate_system':'DXF membrane bounding-box lower-left origin; +u=DXF x, +v=DXF y; millimetres assumed',
            'seams':seams,'top_lines':topsegs,'areas':[],'tasks':[]}
    if wall in ('PL','SL','PU','SU'):
        return {**common,'status':'pending_coordinate_transform','reason':'DXF vertical pitch is about 254.6, not 360. Surface-distance transform is not confirmed.'}
    tasks=[]
    short_runs=0
    for s in seams:
        cross=[t for t in topsegs if t['axis']!=s['axis'] and t['lo']-EPS<=s['fixed']<=t['hi']+EPS and s['lo']-EPS<=t['fixed']<=s['hi']+EPS]
        vals=sorted({t['fixed'] for t in cross})
        runs=[]
        for val in vals:
            if not runs or abs(val-runs[-1][-1]-360)>EPS: runs.append([val])
            else: runs[-1].append(val)
        for run in runs:
            short_runs+=(len(run)-1)%2
            for i in range(0,len(run)-2,2):
                pp=[near_point(s['axis'],v,s['fixed'],origin) for v in run[i:i+3]]
                tasks.append({'id':f'{wall}-T{len(tasks)+1:05d}','direction':s['axis'],'start':pp[0],'middle':pp[1],'end':pp[2],
                              'seam_handles':s['handles'],'top_handles':sorted({h for t in cross if t['fixed'] in run[i:i+3] for h in t['handles']})})
    assert len({(t['direction'],tuple(t['start']),tuple(t['end'])) for t in tasks})==len(tasks)
    # Candidate windows at the maximum AREA size, around task endpoints and on a 720 mm grid.
    boxes=[]
    for t in tasks:
        boxes.append((min(t['start'][0],t['end'][0]),min(t['start'][1],t['end'][1]),max(t['start'][0],t['end'][0]),max(t['start'][1],t['end'][1])))
    candidates=set()
    def clamp(v,dim,width): return round(max(0,min(v,dim-width)),3)
    for x0,y0,x1,y1 in boxes:
        xs=[x0-40,x1-(AREA_WIDTH-40),(x0//720)*720-40]
        ys=[y0-80,y1-(AREA_HEIGHT-80),(y0//720)*720-80]
        for x in xs:
            for y in ys:
                candidates.add((clamp(x,size[0],AREA_WIDTH),clamp(y,size[1],AREA_HEIGHT)))
    candidates=sorted(candidates,key=lambda c:(c[1],c[0]))
    covers=[]
    inverse=[[] for _ in tasks]
    for ci,(x,y) in enumerate(candidates):
        covered={i for i,(x0,y0,x1,y1) in enumerate(boxes) if x0>=x-EPS and y0>=y-EPS and x1<=x+AREA_WIDTH+EPS and y1<=y+AREA_HEIGHT+EPS}
        covers.append(covered)
        for i in covered: inverse[i].append(ci)
    remaining=[set(c) for c in covers]
    heap=[(-len(c),i) for i,c in enumerate(remaining) if c]
    heapq.heapify(heap)
    assigned=set()
    selected=[]
    while len(assigned)<len(tasks):
        neg,ci=heapq.heappop(heap)
        if -neg!=len(remaining[ci]): continue
        owned=set(remaining[ci])
        assert owned
        selected.append((ci,owned))
        assigned.update(owned)
        changed=set()
        for ti in owned:
            for cj in inverse[ti]:
                remaining[cj].discard(ti)
                changed.add(cj)
        for cj in changed:
            if remaining[cj]: heapq.heappush(heap,(-len(remaining[cj]),cj))
    areas=[]
    for k,(ci,owned) in enumerate(sorted(selected,key=lambda item:(candidates[item[0]][1],candidates[item[0]][0]))):
        x,y=candidates[ci]
        aid=f'{wall}-A{k+1:04d}'
        included=sorted(covers[ci])
        corners=[[x,y],[round(x+AREA_WIDTH,3),y],[round(x+AREA_WIDTH,3),round(y+AREA_HEIGHT,3)],[x,round(y+AREA_HEIGHT,3)]]
        areas.append({'id':aid,'corners':corners,'included_task_ids':[tasks[i]['id'] for i in included],
                      'assigned_task_ids':[tasks[i]['id'] for i in sorted(owned)],'included_count':len(included),'assigned_count':len(owned),
                      'assigned_H':sum(tasks[i]['direction']=='H' for i in owned),'assigned_V':sum(tasks[i]['direction']=='V' for i in owned)})
        for ti in owned: tasks[ti]['area_id']=aid
    assert sum(a['assigned_count'] for a in areas)==len(tasks)
    assert all(abs(((t['end'][0]-t['start'][0])**2+(t['end'][1]-t['start'][1])**2)**.5-720)<EPS for t in tasks)
    return {**common,'status':'draft','tasks':tasks,'areas':areas,'unpaired_one_pitch_runs':short_runs,
            'counts':{'areas':len(areas),'tasks':len(tasks),'H':sum(t['direction']=='H' for t in tasks),'V':sum(t['direction']=='V' for t in tasks)}}

def main():
    OUT.mkdir(parents=True,exist_ok=True)
    walls=[]
    for path in sorted((ROOT/'drawing'/'2D도면').glob('*.dxf')):
        w=build_wall(path)
        walls.append(w)
        print(w['wall'],w['status'],w.get('counts',{}),flush=True)
    data={'units':'mm','pitch':360,'task_length':720,'area_width':AREA_WIDTH,'area_height':AREA_HEIGHT,
          'status':'review_only_not_registered','algorithm':'Greedy maximum uncovered-task coverage over task-aligned candidate windows; no global optimum claim.',
          'limitations':['Membrane Sheet straight segments are candidate weld seams; detailed lines may need filtering.',
                         'CENTER line intersections are treated as tops as agreed by the user.',
                         'Coordinates use drawing membrane bounding box, not ACS wall origin. Unit metadata is unspecified in DXF.',
                         'Areas are clamped to bounding boxes only. Outer contour, holes, robot reach and floor bands are not validated.',
                         'Four chamfer walls are pending coordinate transform and excluded from totals.',
                         'IDs are stable preview labels for this calculation, not database IDs.'],
          'walls':walls}
    (OUT/'area-task-coordinates.json').write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf-8')
    def coord(p): return '('+', '.join(f'{v:.3f}'.rstrip('0').rstrip('.') for v in p)+')'
    md=['# 검사영역 좌표와 작업 매칭', '',
        '**검토용 산출물입니다. 실제 ACS 등록 좌표나 DB ID가 아닙니다.**', '',
        '- 단위: mm로 가정. 각 DXF의 멤브레인 도형 경계상자 좌하단이 (0, 0), +u=도면 x, +v=도면 y입니다.',
        '- 원시 DXF 좌표 = 이 목록의 좌표 + 해당 면의 원점. ACS 면 좌표로의 원점·방향 변환은 별도입니다.',
        f'- Area 최대 크기: {AREA_WIDTH} × {AREA_HEIGHT}mm. 이 검토용 배치는 최대 크기 창을 사용합니다. 작업: 360mm 피치의 top–top–top 직선 720mm.',
        '- 포함 수: 해당 Area에 완전히 들어오는 작업 수. 배정 수: 그 Area에만 연결된 고유 작업 수.',
        '- Area 간 겹침은 허용하고 작업 ID는 중복 배정하지 않았습니다.',
        '- 도면의 Membrane Sheet 직선을 용접선 후보로 사용했습니다. 상세 형상선 구분은 추가 확인이 필요합니다.',
        '- 경계상자 내부만 검사했습니다. 외곽 윤곽·개구부·장애물·층·로봇 도달 범위는 미검증입니다.',
        '- 작업을 많이 포함하는 후보부터 선택한 탐욕적 배치이며 전역 최적해는 아닙니다.',
        '- 경사면 PL·SL·PU·SU는 세로 반복 간격 약 254.6의 실거리 변환 미확정으로 집계에서 제외했습니다.', '',
        '## 면별 집계', '', '| 면 | Area 수 | 가로 작업 | 세로 작업 | 고유 작업 합계 |', '|---|---:|---:|---:|---:|']
    for w in walls:
        c=w.get('counts')
        md.append(f"| {w['wall']} | {c['areas']} | {c['H']} | {c['V']} | {c['tasks']} |" if c else f"| {w['wall']} | 변환 확인 필요 | — | — | — |")
    md += ['', '## Area당 실제 배정 작업 개수', '', '| 배정 작업 수 | Area 수 |', '|---:|---:|']
    distribution=Counter(a['assigned_count'] for w in walls for a in w['areas'])
    md += [f'| {n} | {count} |' for n,count in sorted(distribution.items())]
    for w in walls:
        if not w['areas']: continue
        md += ['',f"## {w['wall']} 검사영역",'',f"원점 DXF: {coord(w['origin_dxf'])}. 출처: {w['source_file']}",'',
               '| Area ID | P1 (u,v) | P2 (u,v) | P3 (u,v) | P4 (u,v) | 포함 | 배정 | 가로 | 세로 | 실제 배정 작업 ID |',
               '|---|---|---|---|---|---:|---:|---:|---:|---|']
        for a in w['areas']:
            md.append('| '+' | '.join([a['id'],*[coord(p) for p in a['corners']],str(a['included_count']),str(a['assigned_count']),str(a['assigned_H']),str(a['assigned_V']),', '.join(a['assigned_task_ids'])])+' |')
    (OUT/'area-coordinates.md').write_text('\n'.join(md)+'\n',encoding='utf-8')
    template=(ROOT/'tools'/'dxf_area_preview.html').read_text(encoding='utf-8')
    (OUT/'area-task-preview.html').write_text(template.replace('__DATA__',json.dumps(data,ensure_ascii=False,separators=(',',':'))),encoding='utf-8')

if __name__=='__main__': main()
