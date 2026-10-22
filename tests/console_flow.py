"""Real-engine console test. Creates only uniquely labelled instances; cleans up its own failures.
Run against a dedicated Camunda Run with invoice examples: CAMUNDA_URL=... python3 tests/console_flow.py
"""
import json
import os
from pathlib import Path
import subprocess
import urllib.request
import urllib.parse
import uuid

root = Path(__file__).resolve().parents[1]
base = os.environ.get('CAMUNDA_URL', 'http://localhost:18080/engine-rest/').rstrip('/') + '/'
env = {**os.environ, 'CAMUNDA_URL': base}
labels = []

def get(path):
    return json.load(urllib.request.urlopen(base + path, timeout=10))

def cli(args, lines=None):
    result = subprocess.run(['dotnet', str(root / 'bin/Debug/net10.0/bakalarka.dll'), *args],
                            input=lines, capture_output=True, text=True, env=env, cwd=root, timeout=40)
    if result.returncode != 0 or '  ! ' in result.stdout:
        raise AssertionError(result.stdout + result.stderr)
    return result.stdout

def start(key, variables=None):
    definitions = sorted(get('process-definition'), key=lambda d: (d['key'].casefold(), -d['version']))
    number = next(i+1 for i, d in enumerate(definitions) if d['key'] == key)
    label = 'console-test-' + uuid.uuid4().hex
    labels.append(label)
    # Hlavni menu / definice / spusteni / oznaceni / znama pole / zadani dalsich promennych.
    return label, ['1', str(number), '1', label, *(variables or []), '']

def assert_completed(label):
    rows = get('history/process-instance?processInstanceBusinessKey=' + urllib.parse.quote(label))
    assert len(rows) == 1 and rows[0]['state'] == 'COMPLETED', rows
    return rows[0]

try:
    cli(['deploy', 'models/registration-console.bpmn'])
    cli(['deploy', 'models/order-approval.bpmn'])
    label, lines = start('console_registration')
    lines += ['1','1','2','']  # task / first / claim / demo
    lines += ['1','1','3']     # unclaim
    lines += ['1','1','1','Test Student','IT','true','']  # submit form
    lines += ['2','1','1','']  # external task / complete / no variables
    lines += ['3','1','']      # direct message confirmation
    lines += ['','0']         # finished history / exit
    output = cli([], '\n'.join(lines) + '\n')
    assert 'Úloha převzata' in output and 'Úloha uvolněna' in output
    assert 'Žádné e-maily nebyly odeslány' in output
    assert_completed(label)
    print('PASS: registration entirely in console, form, claim/unclaim, exact external lock, direct message, completion')

    label, lines = start('order_approval')
    lines += ['1','1','1','false','','','0']
    cli([], '\n'.join(lines) + '\n')
    assert_completed(label)
    print('PASS: order rejected via typed Boolean input')

    label, lines = start('invoice', ['Example supplier','100','INV-CONSOLE','Misc',''])
    lines += ['1','1','1','true','']  # approve invoice
    lines += ['1','1','1','']         # prepare bank transfer
    lines += ['0','0']               # return to menu while async archival job may run
    cli([], '\n'.join(lines) + '\n')
    import time
    for _ in range(30):
        rows = get('history/process-instance?processInstanceBusinessKey=' + label)
        if rows[0]['state'] == 'COMPLETED': break
        time.sleep(.2)
    assert_completed(label)
    print('PASS: built-in Invoice Receipt started and approved from console; engine archive job completes')

    label, lines = start('invoice', ['Example supplier','100','INV-REVIEW','Misc',''])
    lines += ['1','1','1','false','']  # rejection calls ReviewInvoice
    lines += ['7','1']               # open child in the same console
    lines += ['1','1','1','demo','']  # assign reviewer
    lines += ['1','1','1','true','','']  # clarify; child history
    lines += ['1','1','1','true','']  # parent: approve clarified invoice
    lines += ['1','1','1','']        # prepare bank transfer
    lines += ['0','0']
    cli([], '\n'.join(lines) + '\n')
    for _ in range(30):
        rows = get('history/process-instance?processInstanceBusinessKey=' + label)
        if rows[0]['state'] == 'COMPLETED': break
        time.sleep(.2)
    assert_completed(label)
    print('PASS: Invoice rejection opens ReviewInvoice child; clarification returns to parent and finishes')

    label, lines = start('ReviewInvoice')
    lines += ['1','1','1','demo','']  # assign reviewer
    lines += ['1','1','1','true','','','0']  # clarify, completed history, exit
    cli([], '\n'.join(lines) + '\n')
    assert_completed(label)
    print('PASS: built-in ReviewInvoice assignment and clarification from console')
finally:
    for label in labels:
        for process in get('process-instance?businessKey=' + urllib.parse.quote(label)):
            req = urllib.request.Request(base + 'process-instance/' + process['id'] + '?deleteReason=console-test-cleanup', method='DELETE')
            urllib.request.urlopen(req, timeout=10).close()
