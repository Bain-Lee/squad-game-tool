// Refresh the bundled vanilla RAAS data. Node 20+ only; installs no packages.
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomUUID } from 'node:crypto';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const output = path.join(root, 'assets', 'layers');
const rawDirectory = path.join(root, 'build', 'raas-raw');
const xml = await fs.readFile(path.join(root,'assets/maps/catalog.xml'),'utf8');
const sourceCommit = /sourceCommit="([a-f0-9]{40})"/.exec(xml)?.[1];
if (!sourceCommit) throw new Error('Map source revision missing');
const metadata = new Map();
let known;
try { known=JSON.parse(await fs.readFile(path.join(output,'catalog.json'),'utf8')); } catch(error) { if(error.code!=='ENOENT'&&!(error instanceof SyntaxError)) throw error; }
if(known?.schema===1 && known.sourceCommit===sourceCommit) {
  for(const map of known.maps||[]) if(Number.isFinite(map.originX)&&Number.isFinite(map.originY)&&typeof map.apiMap==='string') metadata.set(map.id,{apiMap:map.apiMap,origin:[map.originX,map.originY]});
} else {
  const source = await getText(`https://raw.githubusercontent.com/sh4rkman/SquadCalc/${sourceCommit}/src/data/maps.js`);
  for(const match of source.matchAll(/name:\s*"([^"\n]+)"[\s\S]*?mapURL:\s*"\/img\/maps\/([^/]+)\/"[\s\S]*?corner0:\s*\[([^\]]+)\]/g)) {
    if(!metadata.has(match[2])) metadata.set(match[2],{apiMap:match[1],origin:match[3].split(',').map(Number)});
  }
}
const maps = [...xml.matchAll(/<map\s+([^>]+)\/>/g)].map(match => {
  const item=Object.fromEntries([...match[1].matchAll(/([a-zA-Z]+)="([^"]*)"/g)].map(m=>[m[1],m[2]]));
  if(!metadata.has(item.id)) throw new Error('Missing source origin: '+item.id);
  return {...item,...metadata.get(item.id),layers:[]};
});
await fs.mkdir(rawDirectory,{recursive:true});
const staging=await fs.mkdtemp(path.join(root,'build','raas-download-'));
async function getText(url) {
  for(let attempt=0;attempt<3;attempt++) {
    try {
      const response=await fetch(url,{headers:{'X-App-Version':'46.1.0','User-Agent':'SquadTools-LayerData/0.5'},signal:AbortSignal.timeout(25000)});
      if(!response.ok) throw Object.assign(new Error('HTTP '+response.status+' '+url),{status:response.status});
      const text=await response.text();
      if(text.length>16*1024*1024) throw new Error('Layer response too large');
      return text;
    } catch(error) { if(attempt===2||error.status===404) throw error; await new Promise(r=>setTimeout(r,500*(attempt+1))); }
  }
}
async function getJson(url) { return JSON.parse(await getText(url)); }
async function pool(items,worker) {
  let cursor=0;
  await Promise.all(Array.from({length:3},async()=>{ while(cursor<items.length) await worker(items[cursor++]); }));
}
const fetchedAt=new Date().toISOString(), jobs=[];
await pool(maps,async map=>{
  let list;
  try { list=await getJson('https://squadcalc.app/api/get/layers?map='+encodeURIComponent(map.apiMap)); }
  catch(error) { if(map.legacy==='true'&&error.status===404) { map.note='已退役，数据源未提供图层'; list=[]; } else throw error; }
  if(!Array.isArray(list)) throw new Error('Invalid layer index for '+map.apiMap);
  map.layers=list.filter(l=>/_(?:RAAS|RVAAS)_/i.test(l.rawName));
  console.log(map.id+': '+map.layers.length+' RAAS layers');
  for(const layer of map.layers) jobs.push({map,layer});
});
function location(point,map) {
  const x=Number(point.location_x)/100-map.origin[0],y=Number(point.location_y)/100-map.origin[1];
  if(!Number.isFinite(x)||!Number.isFinite(y)) throw new Error('Invalid coordinates in '+map.id);
  return {x,y};
}
await pool(jobs,async({map,layer})=>{
  if(!/^[A-Za-z0-9_-]{1,150}$/.test(layer.rawName)) throw new Error('Invalid layer identifier');
  const url='https://squadcalc.app/api/get/layer?name='+encodeURIComponent(layer.rawName);
  const data=await getJson(url);
  if(data.rawName!==layer.rawName || !['RAAS','RVAAS'].includes(data.gamemode)) throw new Error('Layer mismatch: '+layer.rawName);
  await fs.writeFile(path.join(rawDirectory,layer.rawName+'.json'),JSON.stringify(data));
  const links=data.capturePoints?.lanes?.links?.length?data.capturePoints.lanes.links:data.capturePoints?.clusters?.links;
  const objects=Object.values(data.objectives||{});
  const normalized={schema:1,mapId:map.id,rawName:data.rawName,mode:data.gamemode,sourceCommit,fetchedAt,originX:map.origin[0],originY:map.origin[1],
    team1:data.teamConfigs?.team1?.defaultFaction||'',team2:data.teamConfigs?.team2?.defaultFaction||'',
    mains:objects.filter(o=>o.name==='Main').map(o=>({id:o.objectDisplayName,name:o.objectDisplayName,...location(o,map)})),
    clusters:objects.filter(o=>Array.isArray(o.points)).map(o=>({id:o.name,points:o.points.map(p=>({id:p.objectName,name:p.name,...location(p,map)}))})),
    links:(links||[]).map(l=>({from:l.nodeA,to:l.nodeB}))};
  if(normalized.mains.length!==2||!normalized.clusters.length||!normalized.links.length) throw new Error('No route graph: '+layer.rawName);
  await fs.writeFile(path.join(staging,layer.rawName+'.json'),JSON.stringify(normalized));
  console.log('  saved '+layer.rawName+'; '+normalized.clusters.length+' clusters');
});
const catalog={schema:1,sourceCommit,fetchedAt,maps:maps.map(m=>({id:m.id,name:m.name,apiMap:m.apiMap,originX:m.origin[0],originY:m.origin[1],note:m.note||(!m.layers.length?'本图没有 RAAS 图层':''),layers:m.layers.map(l=>({rawName:l.rawName,name:l.shortName}))}))};
await fs.writeFile(path.join(staging,'catalog.json'),JSON.stringify(catalog,null,2)+'\n');
try { await fs.copyFile(path.join(output,'README.md'),path.join(staging,'README.md')); } catch(error) { if(error.code!=='ENOENT') throw error; }
const backup=path.join(root,'build','raas-backup-'+randomUUID());
for(const target of [output,staging,backup]) {
  const relative=path.relative(root,path.resolve(target));
  if(!relative||relative.startsWith('..')||path.isAbsolute(relative)) throw new Error('Data move escaped workspace');
}
let preserved=false;
try { await fs.rename(output,backup); preserved=true; } catch(error) { if(error.code!=='ENOENT') throw error; }
try { await fs.rename(staging,output); }
catch(error) {
  if(preserved) { try { await fs.rename(backup,output); } catch(restore) { throw new Error('Data switch failed; previous data retained at '+backup,{cause:restore}); } }
  throw error;
}
console.log('DONE: '+maps.length+' maps audited; '+jobs.length+' RAAS layers saved.');
