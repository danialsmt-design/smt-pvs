# NAS Daiya Monitor

A small aggregator that runs on the Synology NAS and gives supervisors ONE line-monitoring page
(phone or PC) that mirrors each SMT line's PVS **Daiya**.

- **Read-only.** It polls each line PC's `http://<ip>:5199/api/daiya`, `/api/status`, `/api/health`
  every 30 s over Tailscale, caches the results, and serves a responsive dashboard. It never sends
  anything to the machines or changes PVS.
- **One page for all 5 lines** — model · shift, output vs shift target with a progress bar, hourly
  output, machines online/producing, total downtime, health.

## Files
- `app/server.js` — the Node aggregator (no npm dependencies; uses built-in `fetch`).
- `app/public/index.html` — the dashboard (auto-refreshes every 15 s).
- `docker-compose.yml` — Synology Container Manager project (Node 20 Alpine, host network).

## Deploy to the NAS
1. Copy this whole `nas-daiya/` folder to the NAS `docker` share → `\\<nas>\docker\nas-daiya\`
   (on the NAS that is `/volume1/docker/nas-daiya/`).
2. **Container Manager → Project → Create** → name `nas-daiya`, path `/volume1/docker/nas-daiya`,
   it reads `docker-compose.yml` → Next → Done. (First start pulls `node:20-alpine`.)
3. Open **`http://<nas-tailscale-ip>:8899`** from any phone/PC on Tailscale.

## Config
Edit the `PVS_LINES` env in `docker-compose.yml` if a line IP changes
(`Name=tailscale-ip`, comma-separated), then restart the project.

## Verify
- `http://<nas>:8899/healthz` → `ok`
- `http://<nas>:8899/api/all` → the raw aggregated JSON.
