# Rock Cloud Print — Docker Edition

A community-maintained Docker port of the [Rock RMS](https://www.rockrms.com/) Cloud Print proxy service. The original application is a Windows-only desktop app; this version runs as a headless Linux container with a browser-based admin UI.

---

## What it does

Rock Cloud Print is a lightweight proxy that bridges your Rock RMS server and local network printers. Rock sends print jobs (typically ZPL label data) over a WebSocket connection to this proxy, which forwards them as raw TCP data to the printer on your local network — usually on port 9100.

```
Rock RMS Server  ──WebSocket──▶  This container  ──TCP:9100──▶  Local Printer
```

This is commonly used for check-in label printing where the printers are on a local church network but the Rock server is cloud-hosted.

---

## Prerequisites

- A Linux server (Ubuntu 22.04+ recommended) on the same network as your printers
- [Docker](https://docs.docker.com/engine/install/ubuntu/) and the Compose plugin installed
- A Rock RMS server (v17+) with a Cloud Print Proxy device record configured
- The printer must be reachable from the server by IP address on port 9100

### Install Docker on Ubuntu

```bash
sudo apt-get update
sudo apt-get install -y docker.io docker-compose-plugin
sudo systemctl enable --now docker
sudo usermod -aG docker $USER   # lets you run docker without sudo (re-login after)
```

---

## Quick start

1. **Clone the repo onto your server**

   ```bash
   git clone https://github.com/The-Ark-Church/rock-cloud-print-docker.git /opt/rock-cloudprint
   cd /opt/rock-cloudprint
   ```

2. **Open port 8080 in the firewall**

   ```bash
   sudo ufw allow 8080/tcp
   ```

3. **Enable auto-start on reboot**

   ```bash
   sudo bash -c 'cat > /etc/systemd/system/rock-cloudprint.service <<EOF
   [Unit]
   Description=Rock Cloud Print Proxy
   After=docker.service
   Requires=docker.service

   [Service]
   Type=oneshot
   RemainAfterExit=yes
   WorkingDirectory=/opt/rock-cloudprint
   ExecStart=/usr/bin/docker compose up -d
   ExecStop=/usr/bin/docker compose down
   TimeoutStartSec=300

   [Install]
   WantedBy=multi-user.target
   EOF'
   sudo systemctl daemon-reload
   sudo systemctl enable rock-cloudprint
   ```

   > Adjust `WorkingDirectory` if you installed to a different path.

4. **Pull and start the container**

   ```bash
   docker compose up -d
   ```

5. **Open the web UI**

   Navigate to `http://<server-ip>:8080` in your browser.

6. **Configure the connection**

   Go to the **Settings** tab and enter:
   - **Rock Server URL** — the full URL of your Rock instance, e.g. `https://church.rockrms.com`
   - **Proxy ID** — the Device IdKey from Rock's Cloud Print Proxy device record
   - **Proxy Name** — optional friendly name; defaults to the container hostname

   Click **Save & Reconnect**. The Dashboard will show a green "Connected" status within a few seconds.

---

## Configuration

Settings can be provided two ways. Environment variables take precedence over the web UI.

### Option A — Web UI (recommended for most users)

Use the Settings tab in the browser. Settings are written to `config/appsettings.json` on the host and persist across container restarts via the `./config:/app/config` Docker volume mount.

### Option B — Environment variables

Edit `docker-compose.yml` and uncomment the `environment` block:

```yaml
environment:
  - Url=https://church.rockrms.com
  - Id=da0BJR0Bpz
  - Name=Office Proxy
  - Password=mypin          # optional — locks the web UI behind a PIN
```

Then restart: `docker compose up -d`

### Option C — Edit the config file directly

Edit `config/appsettings.json` on the server:

```json
{
  "Url": "https://church.rockrms.com",
  "Id": "da0BJR0Bpz",
  "Name": "Office Proxy"
}
```

The container detects file changes and reconnects automatically — no restart needed.

---

## Security

### Built-in hardening

The container ships with several baseline protections that are always on:

| Layer | What it does |
|---|---|
| **Login rate limit** | `/api/auth/login` is capped at 5 attempts per minute. Excess attempts return HTTP 429. Protects the PIN from brute-force attacks. |
| **Security headers** | Every response sets `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `Cache-Control: no-store`, and a `Content-Security-Policy` that restricts script/style/image sources. |
| **Server header suppression** | The `Server: Kestrel` response header is disabled so the underlying stack is not advertised. |
| **Non-root runtime** | The container runs as `appuser` (UID 1000) rather than root. The UID is chosen to match the typical host volume owner so the `./config` bind mount stays writable without privilege escalation. |
| **Bearer tokens in `sessionStorage`** | The web UI stores its auth token in `sessionStorage` (cleared when the tab closes) rather than `localStorage`. |

### PIN protection

The web UI can be protected by a PIN or password. Anyone who can reach port 8080 can view and change all settings, so you should enable this if the server is reachable outside your local network, or if you simply want an extra layer of protection.

### Set a PIN via the web UI (recommended)

1. Open the **Settings** tab and scroll to the **Security** section.
2. Enter a PIN (any alphanumeric value — it does not have to be numeric) and confirm it.
3. Click **Set PIN**. You will be redirected to a login screen immediately.
4. After logging in, the PIN is stored in `config/appsettings.json`.

To change or remove the PIN later, use the **Security** section in Settings — enter the current PIN, then either enter a new one or leave it blank to remove protection.

### Set a PIN via environment variable

Add `Password` to the `environment` block in `docker-compose.yml`:

```yaml
environment:
  - Password=mypin
```

When a PIN is set this way it cannot be changed through the web UI — the Settings page will display a note pointing you back to `docker-compose.yml`. This is useful for automated deployments where you want the PIN locked to a fixed value.

### Behavior notes

| Situation | Effect |
|---|---|
| No PIN configured | Web UI is fully open — no login required |
| PIN set via web UI | Login screen shown on every new browser session |
| PIN set via env var | Login required; PIN cannot be changed via web UI |
| Container restarts | In-memory sessions are cleared — users must log in again |
| PIN changed | All active sessions are invalidated immediately |

---

## Web UI reference

| Page | What it shows |
|---|---|
| **Dashboard** | Connection status (green/amber/grey), start time, time connected, total labels printed since start |
| **Logs** | Live service log stream (last 300 entries), color-coded by level. Add `?limit=2000` to `/api/logs` for the full buffer |
| **Settings → Connection** | Rock server URL, Proxy ID, Proxy Name — saves to `config/appsettings.json` |
| **Settings → Security** | Set, change, or remove the web UI PIN |

The dashboard auto-refreshes every 2 seconds. The log panel refreshes every 3 seconds when visible.

---

## Rock server URL tips

Use your **origin server URL** rather than your primary domain if your site sits behind a CDN (Cloudflare, Cloudfront, etc.). CDNs frequently do not forward WebSocket upgrade requests, causing the server to return a 400 instead of the expected 101 handshake. Your origin URL is typically something like `https://origin.yourdomain.com` or the direct IP/hostname of your web server.

If you see this error in the logs it almost always means a CDN is in the way:
```
The server returned status code '400' when status code '101' was expected.
```

---

## Finding your Proxy ID in Rock

1. In Rock, go to **Admin Tools → Check-in → Devices**
2. Open or create a proxy device device
3. Copy the **IdKey** (short encoded value like `da0BJR0Bpz`) or the full **Guid**  
  a. tip when viewing the device page, click the 3 dot menu in the top right - just below your account image  
  b. click the `Id` label, it will cycle through, Id, Guid, IdKey  
5. Paste it into the Proxy ID field in this app's Settings tab

---

## Networking

This container uses `network_mode: host` in `docker-compose.yml`. This means:

- The container shares the host's network stack directly
- **Port 8080** (web UI) is available on the host with no port mapping needed
- The container can reach printers at their local IP addresses (e.g. `192.168.1.50:9100`)

> **Why host networking?** Printers use raw TCP sockets on port 9100. In standard Docker bridge networking the container gets its own IP and may not be able to reach devices on your local subnet. Host mode eliminates that problem entirely on Linux.

> **No internet required for the admin UI.** Tailwind CSS is compiled into the image
> at build time rather than fetched from a CDN, so the web UI renders correctly on an
> isolated print VLAN with no outbound internet access.

> **Firewall note:** If your server runs `ufw`, open port 8080:
> ```bash
> sudo ufw allow 8080/tcp
> ```

---

## TrueNAS SCALE deployment

TrueNAS SCALE 25.10 can run this container directly from Docker Hub using the **Install via YAML** path in the Apps section.

### 1. Create a dataset for persistent settings

In TrueNAS → **Datasets**, create a new dataset:

- **Name:** `rock-cloudprint` (under your pool, e.g. `tank/rock-cloudprint`)
- **Path on disk:** `/mnt/tank/rock-cloudprint`

No files need to be created inside the dataset — the app creates `appsettings.json` automatically when you save settings via the web UI.

### 2. Install via YAML

1. Go to **Apps → Discover**
2. Click the **⋮** (three-dot) menu in the top right → **Install via YAML**
3. Give the app a name (e.g. `rock-cloudprint`) and paste the compose YAML below
4. Click **Save**

```yaml
services:
  rock-cloudprint:
    image: asdfinit/rock-cloudprint:latest
    ports:
      - "8080:8080"
    volumes:
      - /mnt/tank/rock-cloudprint:/app/config
    restart: unless-stopped
```

> Adjust the host path if your dataset is under a different pool or name.

> **Note:** TrueNAS Apps do not support `network_mode: host`. Port mapping is used instead — the container can still reach LAN printers through the host's network.

### 3. Configure

Once the app starts, open `http://<truenas-ip>:8080`, go to **Settings**, fill in your Rock server URL and Proxy ID, and click **Save & Reconnect**. Settings persist in the dataset and survive container updates.

---

## Printer addressing

Printers are addressed from Rock, not from this app. In Rock's check-in printer configuration, set the printer address to the printer's local IP (and optional port):

- `192.168.1.50` — uses default port 9100
- `192.168.1.50:9100` — explicit port

The proxy forwards data to whatever IP:port Rock specifies in the print job.

---

## Common commands

```bash
# Pull latest image and start
docker compose pull && docker compose up -d

# Restart the container (same as the Restart button in the web UI)
docker compose restart

# View live logs
docker compose logs -f

# Stop
docker compose down

# Check container status
docker compose ps

# Rebuild from source after a code change
docker compose up -d --build --force-recreate

# Rebuild just the web UI stylesheet during local development
# (the Docker build does this automatically)
npm install && npm run css
```

---

## Versions and releases

Images are published automatically by GitHub Actions whenever a version tag is
pushed, so a Docker tag always corresponds to an exact commit.

| Docker tag | What it means |
|---|---|
| `1.1.0` | An exact release. Never changes once published. |
| `1.1` | Follows patch releases within 1.1 (`1.1.0`, `1.1.1`, ...). |
| `latest` | The most recent release. |

All three are published from a single build, so `latest` is always identical to
the numbered release it came from.

**For production, pin an exact version.** `latest` is convenient for trying the
project out, but on a machine that prints check-in labels you generally want
upgrades to happen when you choose them:

```yaml
services:
  rock-cloudprint:
    image: asdfinit/rock-cloudprint:1.1.0   # pinned, not :latest
```

Rolling back is then just editing that line to the previous version and running
`docker compose up -d`.

Version numbers follow [semantic versioning](https://semver.org): the patch
number changes for fixes, the minor for new functionality that breaks nothing,
and the major if an upgrade requires you to change something on your end.

## Updating

When a new version is available:

```bash
git pull
docker compose pull
docker compose up -d
```

Your settings in `config/appsettings.json` are stored outside the container and are unaffected by updates.

---

## Troubleshooting

**Dashboard shows "Connecting…" and never goes green**
- Verify the Rock Server URL is correct and reachable from the server (`curl https://church.rockrms.com`)
- Check that the Proxy ID matches the device record in Rock exactly
- Check logs: `docker compose logs -f`

**Dashboard shows "Not configured"**
- No URL or ID has been set yet. Go to the Settings tab.

**Print jobs arrive (labels count increments) but nothing prints**
- The printer IP or port in Rock's device record is wrong or unreachable
- Test reachability from the server: `nc -zv 192.168.1.50 9100`
- Make sure the printer is on and on the same network as this server

**Port 8080 not accessible from browser**
- Check the firewall: `sudo ufw status`
- Allow the port: `sudo ufw allow 8080/tcp`

**Container won't start**
- Run `docker compose up` (without `-d`) to see startup errors in the terminal

**Settings saved via web UI don't survive a container restart**
- Make sure the `config/` directory exists on the host before starting
- The volume mount in `docker-compose.yml` mounts `./config` to `/app/config` — the directory must exist at startup

---

## What changed from the original Windows app

The upstream Rock Cloud Print is a Windows-only application with two parts:

| Original | This version |
|---|---|
| Windows Service (background process) | Docker container (Linux) |
| WPF desktop GUI | Browser-based web UI on port 8080 |
| Named pipe IPC between GUI and service | REST API (`/api/status`, `/api/settings`) |
| Settings via Windows installer wizard | Settings via web UI or `config/appsettings.json` |
| EventLog / Windows Event Viewer logging | stdout / `docker compose logs` |
| Single-file Windows executable | Multi-stage Docker image |

The core proxy logic — WebSocket connection to Rock, raw TCP forwarding to printers — is unchanged from the upstream source.

### Source changes

| File | What changed |
|---|---|
| `Rock.CloudPrint.Service/Rock.CloudPrint.Service.csproj` | SDK changed from `Worker` to `Web`; removed Windows runtime identifier, single-file publish, and Windows-only packages |
| `Rock.CloudPrint.Service/Program.cs` | Replaced Windows Service host with `WebApplication`; added REST API endpoints; removed Named Pipe and EventLog; added authentication middleware |
| `Rock.CloudPrint.Service/CloudPrintOptions.cs` | Added `Password` property for PIN/password protection |
| `Rock.CloudPrint.Service/AuthService.cs` | New — in-memory bearer token manager for web UI authentication |
| `Rock.CloudPrint.Service/InMemoryLogSink.cs` | New — circular log buffer (2,000 entries) for the Logs panel. In memory only: cleared on restart |
| `Rock.CloudPrint.Service/InMemoryLoggerProvider.cs` | New — `ILoggerProvider` that captures `Rock.CloudPrint.*` log entries only |
| `Rock.CloudPrint.Service/appsettings.json` | Removed EventLog config; added `Urls: http://+:8080` and default empty keys |
| `Rock.CloudPrint.Service/wwwroot/index.html` | New — single-page web UI (Dashboard, Logs, Settings with Security panel) |
| `Rock.CloudPrint.Shared/Rock.CloudPrint.Shared.csproj` | Bumped `System.Text.Json` from `8.0.4` to `8.0.5` (CVE GHSA-8g4q-xg66-9fp4) |
| `Rock.CloudPrint.Service/wwwroot/index.html` | Tailwind is now loaded from the bundled `/app.css` instead of `cdn.tailwindcss.com`; the inline `<style>` block moved into `build/src/app.css` |
| `package.json`, `build/tailwind.config.js`, `build/src/app.css` | New — Tailwind build tooling. `npm run css` compiles the stylesheet |
| `Dockerfile` | New — multi-stage Linux build; pre-creates `/app/config` directory; a Node stage compiles the Tailwind stylesheet so it can never drift from `index.html` |
| `docker-compose.yml` | New — host networking, `./config:/app/config` directory mount, `Password` env var option |
| `config/appsettings.json` | New — persistent settings file (lives in host `config/` directory, mounted into container) |

---

## License

This project is based on [Rock RMS](https://github.com/SparkDevNetwork/Rock) and is licensed under the [Rock Community License](http://www.rockrms.com/license).
