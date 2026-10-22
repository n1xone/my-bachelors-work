"""Exercise the real CLI against a changing HTTP engine stub; no third-party packages."""
import http.server
import json
import os
from pathlib import Path
import signal
import subprocess
import tempfile
import threading
import time
from urllib.parse import urlsplit

root = Path(__file__).resolve().parents[1]
phase = 0
writes = []

class Engine(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args):
        pass

    def do_GET(self):
        path = urlsplit(self.path).path.removeprefix('/engine-rest/')
        code = 200
        if phase == 0:
            code, data = 503, {'message': 'starting'}
        elif path == 'process-instance':
            data = [] if phase >= 5 else [{'id': 'p1', 'definitionId': 'any:1', 'suspended': False}]
        elif path == 'process-instance/p1':
            code, data = (404, {}) if phase >= 5 else (200, {'id': 'p1'})
        elif path == 'task':
            data = [] if phase >= 4 else [{'id': 't1', 'name': 'Review', 'taskDefinitionKey': 'review',
                                         'processInstanceId': 'p1', 'assignee': 'demo' if phase >= 2 else None}]
        elif path == 'process-instance/p1/variables':
            data = {'customer': {'value': 'changed' if phase >= 3 else 'initial', 'type': 'String'},
                    'password': {'value': 'DO-NOT-PRINT-THIS', 'type': 'String'}}
        elif path == 'process-instance/p1/activity-instances':
            data = {'id': 'p1', 'activityId': 'any:1', 'activityType': 'processDefinition',
                    'childActivityInstances': [], 'childTransitionInstances': []}
        elif path == 'history/process-instance/p1':
            data = {'id': 'p1', 'state': 'COMPLETED', 'startTime': 'start', 'endTime': 'end'}
        else:
            data = []
        self.send_response(code)
        self.send_header('Content-Type', 'application/json')
        self.end_headers()
        self.wfile.write(json.dumps(data).encode())

    def do_POST(self):
        writes.append(self.path)
        self.send_response(405)
        self.end_headers()
    do_PUT = do_POST
    do_DELETE = do_POST

engine = http.server.ThreadingHTTPServer(('127.0.0.1', 0), Engine)
threading.Thread(target=engine.serve_forever, daemon=True).start()
with tempfile.TemporaryDirectory() as temp:
    log = Path(temp) / 'output.txt'
    with log.open('w') as output:
        process = subprocess.Popen(['dotnet', str(root / 'bin/Debug/net10.0/bakalarka.dll'), 'watch', '-', '200'],
            env={**os.environ, 'CAMUNDA_URL': f'http://127.0.0.1:{engine.server_port}/engine-rest/'},
            stdout=output, stderr=subprocess.STDOUT)
        def wait_for(text):
            until = time.monotonic() + 8
            while time.monotonic() < until:
                if text in log.read_text():
                    return
                if process.poll() is not None:
                    break
                time.sleep(.05)
            raise AssertionError(f'Missing {text}:\n{log.read_text()}')
        try:
            wait_for('ČEKÁM NA CAMUNDU')
            phase = 1
            wait_for('NOVÉ ÚLOHA t1')
            wait_for('SPOJENÍ OBNOVENO')
            time.sleep(.5)
            assert log.read_text().count('NOVÉ ÚLOHA t1') == 1
            phase = 2
            wait_for('ZMĚNA ÚLOHA t1 | Review | řešitel: demo')
            phase = 3
            wait_for('ZMĚNA PROMĚNNÁ customer')
            phase = 4
            wait_for('JIŽ NENÍ AKTIVNÍ ÚLOHA t1')
            phase = 5
            wait_for('PROCES UKONČEN p1 | COMPLETED')
            time.sleep(.4)
            assert process.poll() is None, 'Watcher stopped after process completion'
            assert 'DO-NOT-PRINT-THIS' not in log.read_text()
            assert not writes, f'Watcher changed engine state: {writes}'
            process.send_signal(signal.SIGINT)
            assert process.wait(timeout=5) == 130
            print('PASS: startup retry, new task, claim, variable change, disappearance, completed history, no duplicate events, hidden values, read-only, continues waiting, Ctrl+C')
        finally:
            if process.poll() is None:
                process.kill()
                process.wait()
            engine.shutdown()
