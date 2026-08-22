import { readFileSync } from 'node:fs';
const PKG='C:/Users/bigwo/AppData/Local/Packages/AD674664-F9A8-474B-871C-B328CDB993C1_1z32rh13vfry6/LocalCache/Local/IptvPlayer';
const NOISE_TOKEN=/\b(hd|fhd|uhd|sd|4k|hevc|full\s*hd)\b/gi, TIMESHIFT=/\+\s*\d{1,2}\s*$/;
const CODES=new Set(('uk,us,fr,de,it,es,pl,br,cn,jp,kr,in,tr,ua,by,kz,az,ge,am,lt,lv,ee,rs,hu,ro,bg,gr,nl,se,no,dk,fi,at,ch,be,pt,ie,cz,sk,si,hr,md,il,ae,sa,eg,za,ng,th,vn,id,my,sg,au,nz,ca,mx,ar,cl,co,pe,eu,intl').split(','));
const MARKERS=new Set(['orig','50','60','hdr','1080p','1080i','720p','2160p','50p','60p']);
function norm(n,k){if(!n||!n.trim())return'';let s=n.trim().toLowerCase().replace(/ё/g,'е');s=s.replace(/\.([a-zа-я]{2,3})$/,'');s=s.replace(/\([^)]*\)/g,' ');s=s.replace(NOISE_TOKEN,' ');s=s.replace(/\+0/g,' ');if(!k)s=s.replace(TIMESHIFT,' ');s=s.replace(/\+(?!\d)/g,' plus ');s=s.replace(/[^\p{L}\p{Nd}\s]/gu,' ');s=s.replace(/\s+/g,' ').trim();const t=s?s.split(' '):[];while(t.length>1&&(CODES.has(t[t.length-1])||MARKERS.has(t[t.length-1])))t.pop();return t.join(' ');}
const m3u=readFileSync('C:/Users/bigwo/Downloads/edem_epg_ico3.m3u8','utf8');
const strict=new Map();
for(const m of m3u.matchAll(/#EXTINF:[^,]*tvg-id="([^"]*)"[^,]*,(.*)/g)){const id=m[1].trim(),name=m[2].trim();if(id&&name){const k=norm(name,true);if(k&&!strict.has(k))strict.set(k,id);}}
const fullIds=new Set(readFileSync('epg-full-ids.txt','utf8').split('\n').map(s=>s.trim()).filter(Boolean));
const unesc=s=>JSON.parse('"'+s+'"');
function ids(p){const t=readFileSync(p,'utf8');const s=new Set();let m;const re=/"ChannelId":"([^"]*)"/g;while((m=re.exec(t))!==null)s.add(unesc(m[1]));return s;}
const cur=new Set([...ids(PKG+'/cache/3D1B62B4C10EE9A8410C23AEB017089078E25E6695342AB24212DDE8EA817BBB.json'),...ids(PKG+'/cache/A622E8F8CA9100BED4D512D2F91A7C4D5316CE61F9EF03675CE020B24E304F89.json')]);
const pl=JSON.parse(readFileSync(PKG+'/playlist_cache.json','utf8'));
let fOnly=0,cOnly=0,both=0,none=0;
for(const c of pl.Channels){const id=strict.get(norm(c.Name,true));if(!id){none++;continue;}
 if(fullIds.has(id)&&cur.has(id))both++;else if(fullIds.has(id))fOnly++;else if(cur.has(id))cOnly++;else none++;}
console.log(`id только в полном фиде: ${fOnly}, только в текущих: ${cOnly}, в обоих: ${both}, нет нигде: ${none}`);
console.log(`Полный фид один покроет: ${fOnly+both} из ${pl.Channels.length}`);
