#!/usr/bin/env python3
"""Run the registration app with an ngrok HTTPS URL. Ctrl+C stops owned processes."""
import json
import os
from pathlib import Path
import shutil
import socket
import subprocess
import time
import urllib.request

root = Path(__file__).resolve().parents[1]
os.chdir(root)

def tunnels():
    with urllib.request.urlopen('http://127.0.0.1:4040/api/tunnels', timeout=2) as response:
        return json.load(response)['tunnels']

def public_url():
    try:
        for tunnel in tunnels():
            if tunnel['public_url'].startswith('https://') and tunnel['config']['addr'].rstrip('/') in ('http://127.0.0.1:5102', 'http://localhost:5102'):
                return tunnel['public_url']
    except (OSError, ValueError, KeyError):
        pass
    return None

if not shutil.which('ngrok'):
    raise SystemExit('Je potreba ngrok: nainstalujte jej a nastavte ngrok config add-authtoken <token>.')
with socket.socket() as check:
    if check.connect_ex(('127.0.0.1',5102)) == 0:
        raise SystemExit('Port 5102 je obsazen. Ukoncete predchozi dotnet run (Ctrl+C) a spustte prikaz znovu.')
subprocess.run(['dotnet','build'],check=True)
ngrok = app = None
try:
    url = public_url()
    if not url:
        ngrok = subprocess.Popen(['ngrok','http','http://127.0.0.1:5102','--inspect=false','--log=stdout'])
        for _ in range(60):
            if ngrok.poll() is not None: raise RuntimeError('ngrok skoncil; zkontrolujte zpravu vyse.')
            url = public_url()
            if url: break
            time.sleep(.5)
    if not url: raise RuntimeError('Nepodarilo se ziskat verejnou adresu ngrok.')
    env = os.environ | {'App__PublicUrl':url, 'ASPNETCORE_ENVIRONMENT':'Development', 'ASPNETCORE_URLS':'http://127.0.0.1:5102'}
    app = subprocess.Popen(['dotnet',str(root/'bin/Debug/net10.0/bakalarka.dll')],env=env)
    for _ in range(60):
        if app.poll() is not None: raise RuntimeError('Aplikace se nespustila.')
        try:
            with urllib.request.urlopen('http://127.0.0.1:5102/register',timeout=2) as response:
                if response.status==200: break
        except OSError: time.sleep(.5)
    else: raise RuntimeError('Aplikace neodpovida.')
    print('\nPoslete odkaz: '+url+'/register',flush=True)
    print('Ponechte tento terminal a Camundu zapnute. Ctrl+C aplikaci ukonci.',flush=True)
    print('Bez SMTP se e-maily ukladaji pouze do Data/mail/*.eml.',flush=True)
    while app.poll() is None:
        if ngrok and ngrok.poll() is not None: raise RuntimeError('Tunel se zastavil.')
        time.sleep(1)
except KeyboardInterrupt:
    pass
finally:
    for process in (app, ngrok):
        if process and process.poll() is None:
            process.terminate()
            try: process.wait(timeout=10)
            except subprocess.TimeoutExpired: process.kill(); process.wait()
