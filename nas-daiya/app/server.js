// NAS Daiya Monitor — a small aggregator that polls each SMT line's PVS and serves ONE mobile/PC
// line-monitoring page for supervisors. Pure read: it never sends anything to the machines or changes PVS.
// Runs on the NAS (Synology Container Manager). Reaches the line PCs over Tailscale.
//
// It reflects each line's PVS **Daiya** (/api/daiya) plus /api/status (machines) and /api/health (verdict).
'use strict';
const http = require('http');
const fs = require('fs');
const path = require('path');

// Lines: "Name=tailscale-ip" comma-separated. Override with env PVS_LINES.
const LINES = (process.env.PVS_LINES ||
  'Line 1=100.69.81.105,Line 2=100.94.102.44,Line 3=100.105.64.115,Line 4=100.82.187.65,Line 5=100.101.8.76'
).split(',').map(s => { const i = s.indexOf('='); return { name: s.slice(0, i).trim(), ip: s.slice(i + 1).trim() }; });

const PORT = parseInt(process.env.PORT || '8899', 10);
const POLL_MS = Math.max(10, parseInt(process.env.POLL_SEC || '30', 10)) * 1000;
const PVS_PORT = process.env.PVS_PORT || '5199';
const cache = {}; // name -> snapshot

async function fetchJson(url, ms = 8000) {
  const ctl = new AbortController();
  const t = setTimeout(() => ctl.abort(), ms);
  try { const r = await fetch(url, { signal: ctl.signal }); return r.ok ? await r.json() : null; }
  catch { return null; }
  finally { clearTimeout(t); }
}

async function pollLine(l) {
  const base = `http://${l.ip}:${PVS_PORT}`;
  const [daiya, status, health] = await Promise.all([
    fetchJson(`${base}/api/daiya`), fetchJson(`${base}/api/status`), fetchJson(`${base}/api/health`),
  ]);
  cache[l.name] = { name: l.name, ip: l.ip, ok: !!(daiya || status), daiya, status, health, at: new Date().toISOString() };
}

let lastPoll = null;
async function pollAll() { await Promise.allSettled(LINES.map(pollLine)); lastPoll = new Date().toISOString(); }
pollAll();
setInterval(pollAll, POLL_MS);

let indexHtml = '<!doctype html><h1>loading…</h1>';
try { indexHtml = fs.readFileSync(path.join(__dirname, 'public', 'index.html')); } catch (e) { console.error('no index.html', e); }

http.createServer((req, res) => {
  const url = (req.url || '/').split('?')[0];
  if (url === '/api/all') {
    res.writeHead(200, { 'Content-Type': 'application/json', 'Cache-Control': 'no-store' });
    res.end(JSON.stringify({
      generatedAt: new Date().toISOString(), lastPoll, pollSec: POLL_MS / 1000,
      lines: LINES.map(l => cache[l.name] || { name: l.name, ip: l.ip, ok: false }),
    }));
    return;
  }
  if (url === '/healthz') { res.writeHead(200); res.end('ok'); return; }
  res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store' });
  res.end(indexHtml);
}).listen(PORT, () => console.log(`NAS Daiya monitor on :${PORT} — polling ${LINES.length} lines every ${POLL_MS / 1000}s`));
