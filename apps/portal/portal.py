import http.client
import json
import os
import subprocess
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit

ROOT = Path(__file__).resolve().parent
PORT = 8080
HOST = f'127.0.0.1:{PORT}'
ORIGIN = f'http://{HOST}'
ERP_URL = os.getenv('SHIPYARD_ERP_URL', 'http://127.0.0.1:8001/')
UNITY_APP = Path(os.getenv('SHIPYARD_UNITY_APP', str(ROOT.parent / 'twin' / 'Builds' / 'ShipyardTwin.app')))
TWIN_URL = os.getenv('SHIPYARD_TWIN_URL', '')
UPSTREAM = urlsplit(ERP_URL)
DOWNLOAD = os.getenv('SHIPYARD_UNITY_DOWNLOAD_URL', '')
if any(url and urlsplit(url).scheme != 'https' for url in (DOWNLOAD,TWIN_URL)):
    raise ValueError('다운로드 주소는 HTTPS를 사용하세요.')


class Portal(BaseHTTPRequestHandler):
    def send(self, status, body, content_type='application/json'):
        if not isinstance(body, bytes):
            body = json.dumps(body, ensure_ascii=False).encode()
        self.send_response(status)
        self.send_header('Content-Type', content_type if content_type.startswith('image/') else content_type + '; charset=utf-8')
        self.send_header('Content-Length', str(len(body)))
        self.send_header('Cache-Control', 'no-store')
        self.send_header('X-Frame-Options', 'DENY')
        self.send_header('Content-Security-Policy', "frame-ancestors 'none'; base-uri 'none'")
        self.end_headers()
        self.wfile.write(body)

    def proxy(self, path, body=None):
        connection_type = http.client.HTTPSConnection if UPSTREAM.scheme == 'https' else http.client.HTTPConnection
        connection = connection_type(UPSTREAM.hostname, UPSTREAM.port, timeout=8)
        headers = {}
        if self.headers.get('Authorization'):
            headers['Authorization'] = self.headers['Authorization']
        if body is not None:
            headers['Content-Type'] = 'application/json'
        try:
            connection.request(self.command, path, body=body, headers=headers)
            response = connection.getresponse()
            payload = response.read()
            self.send(response.status, payload, response.getheader('Content-Type','application/json').split(';')[0])
        except (OSError, http.client.HTTPException):
            self.send(503, {'error':'ERP 서버에 연결할 수 없습니다.'})
        finally:
            connection.close()

    def do_GET(self):
        if self.headers.get('Host') != HOST:
            return self.send(403, {'message':'지정된 로컬 주소에서 접속하세요.'})
        if self.path == '/':
            return self.send(200, (ROOT / 'index.html').read_bytes(), 'text/html')
        if self.path == '/shipyard-icon.png':
            return self.send(200, (ROOT / 'shipyard-icon.png').read_bytes(), 'image/png')
        if self.path == '/twin-preview.png':
            return self.send(200, (ROOT / 'twin-preview.png').read_bytes(), 'image/png')
        if self.path == '/apps.json':
            return self.send(200, (ROOT / 'apps.json').read_bytes())
        if self.path == '/yard-hero.jpg':
            return self.send(200, (ROOT / 'yard-hero.jpg').read_bytes(), 'image/jpeg')
        if self.path in ('/map', '/map/'):
            return self.send(200, (ROOT / 'map.html').read_bytes(), 'text/html')
        if self.path == '/map/config':
            return self.send(200, {})
        map_assets = {
            '/map/twin-satellite.json': ('twin-satellite.json','application/json'),
            '/map/twin-satellite.jpg': ('twin-satellite.jpg','image/jpeg'),
            '/map/twin-satellite-detail.jpg': ('twin-satellite-detail.jpg','image/jpeg'),
        }
        if self.path in map_assets:
            name, media_type = map_assets[self.path]
            asset = ROOT / 'map-assets' / name
            if not asset.is_file():
                return self.send(404, {'message': '로컬 위성 자료가 없습니다.'})
            return self.send(200, asset.read_bytes(), media_type)
        if self.path in ('/guide', '/guide/', '/docs', '/docs/', '/brand/hoon-icon.png', '/brand/yard-hero.jpg'):
            return self.proxy(self.path)
        if self.path == '/erp/':
            return self.proxy('/')
        if self.path.startswith('/api/'):
            return self.proxy(self.path)
        if self.path == '/config':
            return self.send(200, {'erp':'/erp/', 'twin':TWIN_URL, 'installed':UNITY_APP.is_dir(), 'download':DOWNLOAD})
        self.send(404, {'message':'페이지가 없습니다.'})

    def do_POST(self):
        if self.headers.get('Host') != HOST or self.headers.get('Origin') != ORIGIN:
            return self.send(403, {'message':'대시보드에서 요청하세요.'})
        if self.path.startswith('/api/'):
            try:
                length = int(self.headers.get('Content-Length', '0'))
            except ValueError:
                length = 0
            if not 0 < length <= 65536 or self.headers.get_content_type() != 'application/json':
                return self.send(400, {'error':'유효한 JSON 요청이 필요합니다.'})
            return self.proxy(self.path,self.rfile.read(length))
        if self.path != '/launch':
            return self.send(404, {'message':'페이지가 없습니다.'})
        if not self.headers.get('Authorization'):
            return self.send(401, {'message':'로그인이 필요합니다.'})
        connection = http.client.HTTPConnection(UPSTREAM.hostname, UPSTREAM.port, timeout=5) if UPSTREAM.scheme == 'http' else http.client.HTTPSConnection(UPSTREAM.hostname, UPSTREAM.port, timeout=5)
        try:
            connection.request('GET','/api/me',headers={'Authorization':self.headers['Authorization']})
            response = connection.getresponse()
            if response.status != 200 or json.loads(response.read()).get('group_id') == 'LOCAL':
                return self.send(401, {'message':'ERP 세션을 확인할 수 없습니다.'})
        except (OSError, http.client.HTTPException, ValueError):
            return self.send(503, {'message':'ERP 서버에 연결할 수 없습니다.'})
        finally:
            connection.close()
        if not UNITY_APP.is_dir():
            return self.send(404, {'message':'Mac에서 디지털 트윈 앱을 찾지 못했습니다.'})
        try:
            subprocess.run(['/usr/bin/open', str(UNITY_APP)], check=True, timeout=8, capture_output=True)
        except (OSError, subprocess.SubprocessError):
            return self.send(503, {'message':'Mac에서 앱을 열 수 없습니다.'})
        self.send(200, {'message':'Mac에 디지털 트윈 앱 실행을 요청했습니다.'})


if __name__ == '__main__':
    with ThreadingHTTPServer(('127.0.0.1', PORT), Portal) as server:
        print(ORIGIN, flush=True)
        server.serve_forever()
