"""Create sample files and back them up through the real API/agent. No credentials are written to disk."""
from pathlib import Path
import urllib.request,json,os,subprocess,hashlib
root=Path(__file__).resolve().parents[1];os.chdir(root)
base=os.environ.get('VAULTSYNC_API','http://127.0.0.1:9251')
owner=os.environ.get('OWNER_KEY','vaultsync-local-dev')
if not base.startswith(('http://127.0.0.1:','http://localhost:')):raise SystemExit('Demo seeding is restricted to a local API.')
def api(path,body):
 req=urllib.request.Request(base+'/api'+path,data=json.dumps(body).encode(),headers={'Content-Type':'application/json','X-Vault-Key':owner})
 with urllib.request.urlopen(req) as r:return json.load(r)
exe=os.environ.get('DOTNET_EXE','dotnet')
for i,name in enumerate(['Örnek · Ürün tasarımı','Örnek · Proje dosyaları','Örnek · Marka arşivi']):
 source=root/'.runtime/demo'/str(i);source.mkdir(parents=True,exist_ok=True)
 (source/'README.md').write_text('# Örnek çalışma alanı\nBu dosyalar VaultSync tanıtım verisidir.\n',encoding='utf8')
 (source/'design-notes.md').write_text('Tipografi, içerik ve sayfa düzeni üzerine örnek notlar.\n'*12,encoding='utf8')
 (source/'release-plan.csv').write_text('milestone,status\nAPI,completed\nWeb,review\n',encoding='utf8')
 (source/'sample-assets.bin').write_bytes(bytes([i+40])*1048576+bytes([i+70])*32000)
 device=api('/devices',{'name':name})
 env=os.environ.copy();env.update(VAULTSYNC_DEVICE_KEY=device['key'],VAULTSYNC_API=base,VAULTSYNC_STATE=str(root/'.runtime/demo-state'/device['id']))
 for version in range(2 if i else 4):
  (source/'release-plan.csv').write_text(f'milestone,status\nAPI,completed\nWeb,review-{version+1}\n',encoding='utf8')
  p=subprocess.run([exe,str(root/'agent/bin/Debug/net10.0/VaultSync.Agent.dll'),str(source),'--once'],env=env,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
  if p.returncode:raise RuntimeError(p.stderr.decode('utf8',errors='replace'))
 print(name,'backed up')
