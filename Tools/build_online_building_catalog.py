"""Extract a stable ownership catalogue from bundled PLATEAU batch-table metadata (no Unity/cloud calls)."""
import hashlib,json,math,struct,re
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
SCENE=ROOT/'Assets/Scenes/SampleScene.unity'
scene=SCENE.read_text(encoding='utf-8-sig')
lat=float(re.search(r'^  _latitude: (.+)$',scene,re.M)[1]);lon=float(re.search(r'^  _longitude: (.+)$',scene,re.M)[1]);height=float(re.search(r'^  _height: (.+)$',scene,re.M)[1])
def ecef(lat,lon,h):
 lat=math.radians(lat);lon=math.radians(lon);e2=6.6943799901413165e-3
 n=6378137/math.sqrt(1-e2*math.sin(lat)**2)
 return ((n+h)*math.cos(lat)*math.cos(lon),(n+h)*math.cos(lat)*math.sin(lon),(n*(1-e2)+h)*math.sin(lat))
origin=ecef(lat,lon,height);phi=math.radians(lat);lam=math.radians(lon)
def local(latitude,longitude,h):
 x,y,z=[a-b for a,b in zip(ecef(latitude,longitude,h),origin)]
 return (-math.sin(lam)*x+math.cos(lam)*y, -math.sin(phi)*math.cos(lam)*x-math.sin(phi)*math.sin(lam)*y+math.cos(phi)*z)
rows={};files=sorted((ROOT/'Assets/StreamingAssets/FuchuBuildings').rglob('*.b3dm'))
if not files:raise RuntimeError('Bundled building data is required')
for file in files:
 d=file.read_bytes();magic,version,length,fj,fb,bj,bb=struct.unpack_from('<4s6I',d)
 if magic!=b'b3dm' or version!=1 or length!=len(d):raise ValueError(file)
 features=json.loads(d[28:28+fj]);o=28+fj+fb;table=json.loads(d[o:o+bj]);binary=d[o+bj:o+bj+bb]
 def value(key,i):
  v=table.get(key)
  if isinstance(v,list):return v[i] if i<len(v) else None
  if isinstance(v,dict):
   fmt={'DOUBLE':'d','FLOAT':'f','INT':'i','UNSIGNED_INT':'I','BYTE':'b','UNSIGNED_BYTE':'B','SHORT':'h','UNSIGNED_SHORT':'H'}[v['componentType']]
   return struct.unpack_from('<'+fmt,binary,v['byteOffset']+struct.calcsize(fmt)*i)[0]
  return None
 for i in range(features['BATCH_LENGTH']):
  bid=value('gml_id',i)
  if not bid:continue
  longitude=value('_x',i);latitude=value('_y',i)
  if longitude is None or latitude is None:continue
  x,z=local(latitude,longitude,value('_zmin',i) or 0)
  if x*x+z*z>3000**2:continue
  roof=value('uro:BuildingDetailAttribute_uro:buildingRoofEdgeArea',i);floors=value('bldg:storeysAboveGround',i)
  area=float(roof)*int(floors) if roof and floors and float(roof)>0 and 0<int(floors)<9999 else 0
  points=math.floor(area+0.5) if area>0 and math.isfinite(area) else 100
  lod=value('_lod',i) or 0
  candidate=(lod,x,z,points)
  if bid not in rows or candidate[0]>rows[bid][0]:rows[bid]=candidate
header=f'# FuchuOwnershipCatalog v1 WGS84 {lat} {lon} {height}; source: PLATEAU Fuchu 2025 3D Tiles batch tables\n'
content=header+''.join(f'{bid}\t{row[1]:.3f}\t{row[2]:.3f}\t{row[3]}\n' for bid,row in sorted(rows.items()))
(ROOT/'Assets/Resources/FuchuOnlineBuildings.txt').write_text(content,encoding='utf-8')
print(f'{len(files)} tiles, {len(rows)} unique building IDs, SHA256 {hashlib.sha256(content.encode()).hexdigest()}')
