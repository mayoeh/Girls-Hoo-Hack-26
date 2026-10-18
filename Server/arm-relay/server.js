// Relay for the phone arm controller.
// Serves controller.html and forwards phone messages to the Unity game.
//
// Local:  npm install   then   node server.js
// Cloud:  runs on the Vultr server behind Caddy (HTTPS). See deploy/README.md.

const http = require('http');
const fs = require('fs');
const path = require('path');
const { WebSocketServer } = require('ws');

const PORT = Number(process.env.PORT) || 8080;
const HOST = process.env.HOST || '0.0.0.0';

// Files the phone page loads (font + star background)
const ASSETS = { '/AstroSpace.ttf': 'font/ttf', '/stars.jpg': 'image/jpeg' };

const server = http.createServer((req, res) => {
  if (req.url === '/health') {
    const counts = { phone: 0, game: 0 };
    for (const c of wss.clients) counts[c.role === 'phone' ? 'phone' : 'game']++;
    res.writeHead(200, { 'Content-Type': 'application/json' });
    res.end(JSON.stringify({ ok: true, ...counts }));
  } else if (ASSETS[req.url]) {
    res.writeHead(200, { 'Content-Type': ASSETS[req.url], 'Cache-Control': 'public, max-age=86400' });
    fs.createReadStream(path.join(__dirname, req.url.slice(1))).pipe(res);
  } else if (req.url === '/' || req.url.startsWith('/controller') || req.url.startsWith('/?')) {
    res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store' });
    fs.createReadStream(path.join(__dirname, 'controller.html')).pipe(res);
  } else {
    res.writeHead(404);
    res.end('Not found');
  }
});

const wss = new WebSocketServer({ server });

wss.on('connection', (ws, req) => {
  const role = new URL(req.url, 'http://localhost').searchParams.get('role') || 'game';
  ws.role = role;
  ws.isAlive = true;
  ws.on('pong', () => { ws.isAlive = true; });
  console.log(`[${new Date().toLocaleTimeString()}] ${role} connected`);

  ws.on('message', (data) => {
    // Phone -> every connected game
    if (ws.role !== 'phone') return;
    const text = data.toString();
    for (const client of wss.clients) {
      if (client !== ws && client.readyState === 1 && client.role === 'game') {
        client.send(text);
      }
    }
  });

  ws.on('close', () => console.log(`[${new Date().toLocaleTimeString()}] ${role} disconnected`));
});

// Keepalive: drop dead connections (phone locked, wifi dropped) so they don't linger.
setInterval(() => {
  for (const ws of wss.clients) {
    if (!ws.isAlive) { ws.terminate(); continue; }
    ws.isAlive = false;
    ws.ping();
  }
}, 25000);

server.listen(PORT, HOST, () => {
  console.log(`Relay running on http://${HOST}:${PORT}`);
  console.log(`Unity connects to ws://localhost:${PORT}/?role=game (or wss://<your-domain>/?role=game in the cloud)`);
});
