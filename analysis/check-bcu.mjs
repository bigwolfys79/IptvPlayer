import { readFileSync, existsSync } from 'node:fs';
import { createHash } from 'node:crypto';

const key = 'xmltv:http://epg.one/epg.xml.gz';
const hash = createHash('sha256').update(key).digest('hex').toUpperCase();
const p = 'C:/Users/bigwo/AppData/Local/IptvPlayer/cache/' + hash + '.json';
console.log('cache file:', hash + '.json', existsSync(p) ? '(есть)' : '(НЕТ)');
if (existsSync(p)) {
  const t = readFileSync(p, 'utf8');
  const m = t.match(/"ChannelId":"6198"/g);
  console.log('программ у id 6198 в кэше EPG:', m ? m.length : 0);
  const parts = t.split('{"EventId":"');
  for (const part of parts.slice(1)) {
    const idM = /"ChannelId":"([^"]*)"/.exec(part);
    if (idM && idM[1] === '6198') {
      const nM = /"ChannelName":"((?:[^"\\]|\\.)*)"/.exec(part);
      console.log('имя канала в XMLTV:', nM ? JSON.parse('"' + nM[1] + '"') : '?');
      const sM = /"StartTime":"([^"]*)"/.exec(part);
      console.log('первая программа:', sM ? sM[1] : '?');
      break;
    }
  }
}

const pl = JSON.parse(readFileSync('C:/Users/bigwo/AppData/Local/IptvPlayer/playlist_cache.json', 'utf8'));
const bcu = pl.Channels.filter((c) => /trumotion/i.test(c.Name));
console.log('в плейлисте:', bcu.length ? bcu.map((c) => c.Name + ' | ' + c.StreamUrl).join('\n') : 'нет');
