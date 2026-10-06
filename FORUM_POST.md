# Forum Post Draft — community.rockrms.com

**Suggested category:** Dev Talk → Plugins & Extensions  
**Suggested title:** Run Rock Cloud Print in a Docker Container (Linux / Raspberry Pi / NAS) — Community Build

---

## Run Rock Cloud Print in a Docker Container (Linux / Raspberry Pi / NAS )

Hey Rock community! 😃

If you're using Rock's Cloud Print Proxy for check-in label printing and your server is cloud-hosted, you know the challenge: the official Rock Cloud Print app is Windows-only. That's fine if you have a Windows machine on your network, but it's overkill if you just want something small, always-on, and low-maintenance.

We've been running a community-maintained Docker port at our church and wanted to share it in case it helps anyone else.

---

### What it does

Rock Cloud Print is a lightweight proxy that sits between your Rock server and local network printers. Rock sends print jobs over a WebSocket, and the proxy forwards them as raw TCP data (ZPL, etc.) to the printer on port 9100.

```
Rock Server  ──WebSocket──▶  This container  ──TCP:9100──▶  Local Zebra/TSC Printer
```

The official Windows app handles this perfectly — this Docker version does the exact same thing, we repackaged it to run headlessly on any Linux box. We run it on Raspberry Pi's 4/5 that we already had(a few of them). It will also work on most NAS systems who support containers.

---

### What's different from the original

| Original | This version |
|---|---|
| Windows Service | Docker container (Linux, ARM64/AMD64) |
| WPF desktop app | Browser-based web UI on port 8080 |
| Named pipe IPC | REST API |
| EventLog | `docker compose logs` / in-app log panel |
| Windows installer | `docker compose up --build` |

The WebSocket/TCP proxy logic is upstream's, with one fix: a slow or asleep printer no longer holds up every other printer, because each printer prints on its own queue. Everything else replaced is the Windows-specific shell around it.

---

### Requirements

- A Linux server (or Raspberry Pi running Debian/Ubuntu) **on the same network as your printers**
- Docker and the Compose plugin
- Rock v17+ with a Cloud Print Proxy device record

That's it. No .NET SDK, no Visual Studio, no Windows.

---

### Getting set up Linux:

**1. Install Docker** (if not already installed):
```bash
sudo apt-get update && sudo apt-get install -y docker.io docker-compose-v2
sudo systemctl enable --now docker
sudo usermod -aG docker $USER
```

**2. Copy the project to your server** and build/start the container:
```bash
docker compose up -d --build
```

**3. Open `http://<server-ip>:8080`** and go to Settings. Enter your Rock server URL and Proxy ID (the device's IdKey or Guid from Admin Tools → Check-in → Devices).

**4. Check the Dashboard.** You should see a green "Connected" dot within a few seconds.

---

### For other instalation methods see the docker hub repo.

### A few tips we learned the hard way

**Use your origin URL, not your primary domain.**  
If your Rock site is behind Cloudflare or another CDN, WebSocket upgrade requests are blocked at the CDN level. You'll see `400` instead of `101` in the logs. Use the direct origin URL (e.g. `https://origin.yourchurch.com` or the bare IP of your web server) to bypass the CDN.

**The IdKey is the easy one to copy.**  
Rock accepts either the proxy device's short `IdKey` (like `da0BJR0Bpz`) or its Guid as the Proxy ID. The IdKey is shorter and harder to mistype.

**The default compose file uses host networking.**  
The `docker-compose.yml` uses `network_mode: host` so the container reaches printers at their LAN addresses with nothing to map. Bridge networking with `ports: - "8080:8080"` works too; it's what the TrueNAS and Portainer examples use. Host networking isn't available on Docker Desktop for Mac or Windows.

---

### Web UI

The browser UI lives at port 8080. It has five tabs:

- **Dashboard** — connection status (green/amber/grey dot), labels requested, and print results
- **Logs** — live service log with filters by type and source
- **Printers** — test whether a printer can be reached, without printing
- **Blank Labels** — print pre-coded blank check-in labels, even with Rock down
- **Settings** — Rock connection, failure notifications, an optional PIN, and a reverse proxy setting

There's also a **Restart** button in the header that gracefully restarts the container (Docker's `restart: unless-stopped` policy brings it right back).

---

### PIN protection

The web UI is open by default — anyone who can reach port 8080 can view and change settings. If your server is exposed at all, set a PIN under Settings → Security. You can also pre-configure it via an environment variable in `docker-compose.yml`:

```yaml
environment:
  - Password=mypin
```

---

### Auto-start on reboot

The container uses `restart: unless-stopped`, so it comes back automatically after a Docker crash. For full boot persistence, add a systemd unit:

```bash
sudo systemctl enable rock-cloudprint
```

(Full unit file in the repo README.)

---

### Where to find it

Docker Hub: **https://hub.docker.com/r/asdfinit/rock-cloudprint**  
Git Hub: **https://github.com/The-Ark-Church/rock-cloud-print-docker**  
  
The README has a full admin guide covering prerequisites, quick start, configuration, troubleshooting, CDN tips, and all of the above. Happy to answer questions here or in the repo.

---

Would love to hear if anyone else has a setup like this or has any idea for improvements(I have a few ideas)

— Jonathan / The Ark Church
