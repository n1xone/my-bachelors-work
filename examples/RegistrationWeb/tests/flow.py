"""Integration test with a stateful Camunda REST stub; no running Camunda/SMTP required."""
import html as html_module
import http.server, json, threading, subprocess, tempfile, pathlib, os, time, urllib.request, urllib.parse, urllib.error, http.cookiejar, re
root = pathlib.Path(__file__).resolve().parents[1]
state = {'started':False,'task':True,'claim':False,'vars':{},'fetched':False,'ready':False,'verified':False,'messages':0}
extras = {}
class Engine(http.server.BaseHTTPRequestHandler):
    def log_message(self,*args): pass
    def reply(self,data=None,status=200):
        self.send_response(status); self.send_header('Content-Type','application/json'); self.end_headers()
        if data is not None: self.wfile.write(json.dumps(data).encode())
    def do_GET(self):
        p=self.path
        if p=='/engine-rest/process-instance/gone': return self.reply({},404)
        if p=='/engine-rest/process-instance/broken': return self.reply({'id':'broken'})
        if p=='/engine-rest/process-instance/broken/variables/emailVerified':
            state['cleanup_errors']=state.get('cleanup_errors',0)+1
            return self.reply({'message':'engine unavailable'},500)
        if p=='/engine-rest/process-instance/gone/variables/emailVerified':
            state['gone_variable_reads']=state.get('gone_variable_reads',0)+1
            return self.reply({'message':'execution does not exist'},500)
        if p.startswith('/engine-rest/process-instance/p') and '/variables' not in p and '?' not in p and not p.endswith('/p1'):
            item=next((x for x in extras.values() if x['id']==p.rsplit('/',1)[1]),None)
            return self.reply({'id':item['id']} if item else {},200 if item else 404)
        query=urllib.parse.parse_qs(urllib.parse.urlsplit(p).query)
        if p.startswith('/engine-rest/process-instance?'):
            key=query.get('businessKey',[''])[0]
            if key in extras: return self.reply([{'id':extras[key]['id']}])
            return self.reply([{'id':'p1'}] if state['started'] and key==state.get('key') else [])
        if p.startswith('/engine-rest/task?') and query.get('processInstanceId',[''])[0]!='p1':
            item=next(x for x in extras.values() if x['id']==query['processInstanceId'][0])
            return self.reply([{'id':item['id'],'assignee':None}] if item['active'] else [])
        if p.startswith('/engine-rest/task/p'):
            item=next(x for x in extras.values() if x['id']==p.rsplit('/',1)[1])
            return self.reply({},200 if item['active'] else 404)
        if p.startswith('/engine-rest/process-instance?'): return self.reply([{'id':'p1'}] if state['started'] else [])
        if p.startswith('/engine-rest/task?'): return self.reply([{'id':'t1','assignee':'invite-'+state.get('key','') if state['claim'] else None}] if state['task'] else [])
        if p=='/engine-rest/process-instance/p1': return self.reply({'id':'p1'},404 if state.get('deleted') else 200)
        if p=='/engine-rest/task/t1': return self.reply({},200 if state['task'] else 404)
        if 'event-subscription?' in p: return self.reply([{}] if state['ready'] and not state['verified'] else [])
        if p.endswith('/variables/emailVerified'): return self.reply({'value':state['verified'] if '/p1/' in p else False})
        if p.endswith('/variables/webRegistration'): return self.reply({'value':True},200 if '/p1/' in p else 404)
        self.reply({},404)
    def do_DELETE(self):
        pid=urllib.parse.urlsplit(self.path).path.rsplit('/',1)[1]
        state.setdefault('deletions',[]).append(pid)
        if pid=='p1': state['deleted']=True
        else:
            item=next(x for x in extras.values() if x['id']==pid)
            item['active']=False
        return self.reply(status=204)
    def do_POST(self):
        if self.headers.get('Transfer-Encoding') == 'chunked':
            raw=b''
            while True:
                size=int(self.rfile.readline().strip(),16)
                if size==0:
                    self.rfile.readline();break
                raw+=self.rfile.read(size);self.rfile.read(2)
        else: raw=self.rfile.read(int(self.headers.get('Content-Length',0)))
        data=json.loads(raw or '{}'); p=self.path
        if p.endswith('/start'):
            state['starts']=state.get('starts',0)+1
            if state['started']:
                item={'id':'p'+str(state['starts']),'active':True};extras[data['businessKey']]=item;return self.reply({'id':item['id']})
            state.update(started=True,key=data['businessKey']); return self.reply({'id':'p1'})
        if p.endswith('/claim'): state['claim']=True; return self.reply(status=204)
        if p.endswith('/task/t1/complete'): state.update(task=False,vars=data['variables']); return self.reply(status=204)
        if p.endswith('/fetchAndLock'):
            if not state['task'] and not state['fetched']:
                state['fetched']=True; return self.reply([{'id':'e1','topicName':'create_account','processInstanceId':'p1','variables':state['vars']}])
            if state['verified'] and not state.get('saved_fetched'):
                state['saved_fetched']=True
                return self.reply([{'id':'save1','topicName':'db_save','processInstanceId':'p1','variables':state['vars']}])
            if state['verified'] and not state.get('email_fetched'):
                state['email_fetched']=True
                return self.reply([{'id':'mail1','topicName':'send_email','processInstanceId':'p1','variables':state['vars']}])
            return self.reply([])
        if p.endswith('/external-task/save1/complete') or p.endswith('/external-task/mail1/complete'): return self.reply(status=204)
        if p.endswith('/external-task/e1/complete'): state['ready']=True; return self.reply(status=204)
        if p.endswith('/message'): state['messages']+=1;state['verified']=True;return self.reply(status=204)
        self.reply({},404)
engine=http.server.ThreadingHTTPServer(('127.0.0.1',0),Engine)
threading.Thread(target=engine.serve_forever,daemon=True).start()
with tempfile.TemporaryDirectory() as directory:
    pathlib.Path(directory,'Data').mkdir()
    pathlib.Path(directory,'Data','registrations.json').write_text(json.dumps([
        {'Token':'expired-gone','ProcessId':'gone','Expires':'2000-01-01T00:00:00+00:00'},
        {'Token':'expired-broken','ProcessId':'broken','Expires':'2000-01-01T00:00:00+00:00'}]))
    pathlib.Path(directory,'Data','users.json').write_text(json.dumps({'legacy-task':{'Name':'Legacy','Surname':'User','Email':'legacy@example.com','BirthDate':'1990-01-01'}}))
    env=os.environ|{'ASPNETCORE_ENVIRONMENT':'Development','ASPNETCORE_URLS':'http://127.0.0.1:5198','Camunda__BaseUrl':f'http://127.0.0.1:{engine.server_port}/engine-rest/','App__PublicUrl':'http://registration.test:5198','App__AdminKey':'test-key','Smtp__Delivery':'Pickup'}
    process=subprocess.Popen(['dotnet',str(root/'bin/Debug/net10.0/bakalarka.dll'),'--contentRoot',directory],env=env,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
    browser=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
    def request(path,form=None,client=browser):
        req=urllib.request.Request('http://127.0.0.1:5198'+path,None if form is None else urllib.parse.urlencode(form).encode())
        try:
            with client.open(req) as r: return r.status,r.read().decode()
        except urllib.error.HTTPError as e: return e.code,e.read().decode()
    try:
        for _ in range(80):
            try:
                if request('/')[0]==200: break
            except OSError: pass
            time.sleep(.1)
        status,html=request('/register');assert status==200 and 'Klíč správce' not in html
        token=re.search(r'/invite/([A-F0-9]+)',html)[1]
        assert state['started'] and state['task'] and state['claim']
        assert request('/register')[0]==200 and state['starts']==1
        state['task']=False;state['fetched']=True
        assert request('/register')[0]==410 and state['starts']==1
        assert request('/invite/'+token)[0]==410
        state['task']=True;state['fetched']=False
        assert request('/admin')[0]==200
        assert request('/invite/'+token,client=urllib.request.build_opener())[0]==409
        assert request('/invite/'+token+'/submit',{'name':'!bad'})[0]==400
        form={'name':'Jan','surname':'Novák','email':'jan@example.com','birthDate':'2000-01-01','password':'Strongpass123'}
        invalid_form=form | {'password':'short1'}
        status,validation=request('/invite/'+token+'/submit',invalid_form)
        validation=html_module.unescape(validation)
        assert status==400 and 'Některé údaje nejsou platné' in validation
        assert 'value="Jan"' in validation and 'value="Novák"' in validation
        assert 'value="jan@example.com"' in validation and 'value="2000-01-01"' in validation
        assert 'short1' not in validation and '/invite/'+token+'/submit' in validation
        assert request('/invite/'+token+'/submit',form)[0]==200
        assert state['started'] and state['claim']
        assert state['vars']['birthDate']['type']=='Date'
        assert 'userPassword' not in state['vars']
        assert request('/invite/'+token+'/submit',form)[0]==200
        for _ in range(80):
            files=list(pathlib.Path(directory,'Data','mail').glob('*.eml'))
            if files:break
            time.sleep(.1)
        assert files, 'No confirmation email generated'
        second=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
        _,second_html=request('/register',client=second)
        second_token=re.search(r'/invite/([A-F0-9]+)',second_html)[1]
        extras[second_token]['active']=False
        status,duplicate=request('/invite/'+second_token+'/submit',form,client=second)
        assert status==410, (status, duplicate)
        resend='/invite/'+second_token+'/resend'
        assert request(resend,{'email':form['email']},client=urllib.request.build_opener())[0]==410
        assert request(resend,{'email':form['email']},client=second)[0]==200
        for _ in range(80):
            if len(list(pathlib.Path(directory,'Data','mail').glob('*.eml')))==2:break
            time.sleep(.1)
        assert len(list(pathlib.Path(directory,'Data','mail').glob('*.eml')))==2, list(pathlib.Path(directory,'Data','mail').glob('*.eml'))
        assert request(resend,{'email':form['email']},client=second)[0]==200
        assert request(resend,{'email':'missing@example.com'},client=second)[0]==200
        records=json.loads(pathlib.Path(directory,'Data','approvals.json').read_text());approval=records[0]['Token']
        state['deleted']=True
        status,ended=request(resend,{'email':form['email']},client=second)
        assert status==409 and 'Registrace již není aktivní' in html_module.unescape(ended)
        state['deleted']=False
        assert request('/approve?token='+approval)[0]==200 and state['messages']==0
        assert request('/approve',{'token':approval})[0]==200 and state['messages']==1
        assert request('/approve',{'token':approval})[0]==200 and state['messages']==1
        assert request('/approve',{'token':'bad'})[0]==410
        assert request('/users')[0]==404
        assert request('/Data/users.txt')[0]==404
        data=pathlib.Path(directory,'Data','registrations.json').read_text(); assert 'Strongpass123' not in data and 'PasswordHash' in data
        for _ in range(100):
            saved=pathlib.Path(directory,'Data','users.txt')
            if saved.exists() and len(list(pathlib.Path(directory,'Data','mail').glob('*.eml'))) == 3: break
            time.sleep(.1)
        assert saved.exists() and 'jan@example.com' in saved.read_text()
        assert len(list(pathlib.Path(directory,'Data','mail').glob('*.eml'))) == 3
        status,summary=request('/register')
        assert status==200 and 'Jste zaregistrováni' in html_module.unescape(summary)
        assert 'jan@example.com' in summary and 'Znovu odeslat' not in summary
        assert request('/invite/'+token+'/status',client=urllib.request.build_opener())[0]==404
        status,body=request('/invite/'+token+'/status'); assert json.loads(body)['state']=='registered'
        status,summary=request('/approve?token='+approval)
        assert 'Jste zaregistrováni' in html_module.unescape(summary)
        assert 'legacy@example.com' in saved.read_text()
        assert 'Strongpass123' in saved.read_text() and 'PasswordHash' not in saved.read_text()
        # Zmena zdrojoveho textoveho souboru ovlivni hledani e-mailu; zastaraly JSON nema prednost.
        saved.write_text(saved.read_text().replace('jan@example.com','changed@example.com'))
        state['deleted']=True
        third=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()))
        _,page=request('/register',client=third)
        third_token=re.search(r'/invite/([A-F0-9]+)',page)[1]
        changed=form | {'email':'CHANGED@example.com'}
        assert request('/invite/'+third_token+'/submit',changed,client=third)[0]==409
        records=saved.read_text()
        email_position=records.index('Email       : "changed@example.com"')
        record_start=records.rfind('------------------------------------------------------------',0,email_position)
        assert record_start >= 0
        saved.write_text(records[:record_start])
        extras[third_token]['active']=False
        assert request('/invite/'+third_token+'/submit',changed,client=third)[0]==410
        cleanup_records=json.loads(pathlib.Path(directory,'Data','registrations.json').read_text())
        assert next(x for x in cleanup_records if x['Token']=='expired-gone')['Cancelled']
        assert not next(x for x in cleanup_records if x['Token']=='expired-broken')['Cancelled']
        assert state.get('gone_variable_reads',0)==0
        assert state['cleanup_errors']==1, 'Cleanup failure must back off without blocking normal work'
        # Novy pokus zrusi pouze nepotvrzenou registraci aktualniho prohlizece.
        old_id=extras[third_token]['id']
        approvals_path=pathlib.Path(directory,'Data','approvals.json')
        approvals=json.loads(approvals_path.read_text())
        approvals.append({'ProcessInstanceId':old_id,'Name':'Test','Surname':'User',
            'Email':'wrong@example.com','BirthDate':'2000-01-01','Token':'cancel-test',
            'Expires':'2099-01-01T00:00:00+00:00','Sent':True,'Confirmed':False})
        approvals_path.write_text(json.dumps(approvals))
        assert request('/register/new',{},client=third)[0]==200
        assert old_id in state['deletions'] and 'p1' not in state['deletions']
        assert request('/approve?token=cancel-test')[0]==410
        assert request('/approve',{'token':'cancel-test'})[0]==410
        assert request('/invite/'+third_token,client=third)[0]==410
        other_id=extras[second_token]['id']
        assert other_id not in state['deletions']
        # Existujici vyprsene pokusy uklidi worker bez noveho nasazeni.
        path=pathlib.Path(directory,'Data','registrations.json')
        records=json.loads(path.read_text())
        for item in records:
            if item['Token']==second_token: item['Expires']='2000-01-01T00:00:00+00:00'
        path.write_text(json.dumps(records))
        for _ in range(80):
            if other_id in state['deletions']: break
            time.sleep(.1)
        assert other_id in state['deletions']
        # Potvrzene ucty po stejne akci zustanou zachovany.
        assert request('/register/new',{})[0]==200
        assert 'p1' not in state['deletions']
        print('PASS: direct form without key, start and claim on opening, refresh reuses instance, expired form detection, browser ownership, validation, submission, plaintext demo password, live users.txt edits, email, read-only GET, idempotent confirmation, private user data, durable user save, final email, duplicate recovery buttons, resend and cooldown')
    finally:
        process.terminate();process.wait(timeout=10);engine.shutdown()
