#!/usr/bin/env python3
"""
가짜 SAIGE 수신기 — HD_ACS 로봇 상태 전송(SAIGE 연동 사양서 v2.6 §9) 확인용.

SAIGE 실물 없이 ACS가 무엇을 보내는지 보고 싶을 때 띄운다. 받은 요청을 콘솔에 한 줄씩 찍고(선택: 파일에도 기록),
사양서 §9.5의 응답을 흉내 낸다. 실제 수신기처럼 Content-Length 기준으로 본문을 읽는다
(chunked 전송이면 본문이 비어 보인다 — 2026-09-21 E2E에서 잡은 결함의 재현 조건).

사용:
    python tools/fake_saige_receiver.py                 # 127.0.0.1:58080, 항상 200
    python tools/fake_saige_receiver.py --port 8080 --log saige.log
    python tools/fake_saige_receiver.py --respond 503   # 50302(미응답) 흉내 → ACS 백오프/알람 확인
    python tools/fake_saige_receiver.py --respond 400   # 40001(형식 오류) 흉내 → ACS 폐기+알람 확인

ACS 쪽 설정(appsettings.json 또는 환경변수):
    Acs:Saige:Enabled=true  Acs:Saige:BaseUrl=http://127.0.0.1:58080
확인:  GET http://localhost:5199/api/integrations/saige  (lastOkAt·totalSent·robots[])
"""
import argparse, json, sys, time
from http.server import BaseHTTPRequestHandler, HTTPServer

RESPONSES = {
    200: (200, {"status": 200, "message": "success"}),
    400: (400, {"code": 40001, "message": "Invalid format."}),
    500: (500, {"code": 50001, "message": "Internal server error."}),
    503: (503, {"code": 50302, "message": "The SAIGE receiver is not responding."}),
}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--host", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=58080)
    ap.add_argument("--log", help="수신 본문을 append 할 파일")
    ap.add_argument("--respond", type=int, default=200, choices=sorted(RESPONSES), help="흉내 낼 SAIGE 응답")
    a = ap.parse_args()
    logf = open(a.log, "a", encoding="utf-8") if a.log else None
    status, body = RESPONSES[a.respond]

    class H(BaseHTTPRequestHandler):
        def do_POST(self):
            n = int(self.headers.get("Content-Length") or 0)          # 실제 수신기처럼 길이 기준
            raw = self.rfile.read(n).decode("utf-8", "replace")
            try:
                d = json.loads(raw) if raw else None
                line = (f"{d['robotId']} {d['status']} L{d['position']['level']} "
                        f"({d['position']['x']},{d['position']['y']}) yaw={d['position']['yaw']} bat={d['battery']}%") if d else "<EMPTY BODY>"
            except (ValueError, KeyError, TypeError):
                line = f"<UNPARSEABLE> {raw[:120]}"
            stamp = time.strftime("%H:%M:%S")
            print(f"[{stamp}] POST {self.path}  {line}  -> {status}", flush=True)
            if logf:
                logf.write(f"{stamp} {self.path} {raw}\n"); logf.flush()
            out = json.dumps({**body, "timestamp": int(time.time() * 1000)}).encode()
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(out)))
            self.end_headers()
            self.wfile.write(out)

        def log_message(self, *_):  # 기본 액세스 로그 억제(위 한 줄로 대체)
            pass

    print(f"fake SAIGE receiver on http://{a.host}:{a.port}  (responding {status})", flush=True)
    try:
        HTTPServer((a.host, a.port), H).serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    sys.exit(main())
