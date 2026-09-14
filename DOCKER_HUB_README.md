# Rock Cloud Print — Docker

A community-maintained Docker port of the [Rock RMS](https://www.rockrms.com/) Cloud Print proxy service.

The original application is Windows-only. This image runs as a headless Linux container with a browser-based admin UI and is designed to run on any Linux server — including a Raspberry Pi or TrueNAS SCALE — on the same network as your label printers.

```
Rock RMS Server  ──WebSocket──▶  This container  ──TCP:9100──▶  Local Printer
```

Commonly used for Rock Check-in label printing where printers are on a local church network but the Rock server is cloud-hosted.

---

## Quick start (Linux server / Raspberry Pi)

```bash
# 1. Create a config folder (settings are written here by the web UI)
mkdir -p rock-cloudprint/config

# 2. Create docker-compose.yml
cat > rock-cloudprint/docker-compose.yml <<'EOF'
services:
  rock-cloudprint:
    image: asdfinit/rock-cloudprint:latest
    network_mode: host
    volumes:
      - ./config:/app/config
    restart: unless-stopped
EOF

# 3. Start the container
cd rock-cloudprint
docker compose up -d

# 4. Open the web UI
# Navigate to http://<server-ip>:8080
```

Open the web UI, go to **Settings**, enter your Rock server URL and Proxy ID, and click **Save & Reconnect**.

---

## TrueNAS SCALE

TrueNAS SCALE 25.10 supports Docker Compose via the **Install via YAML** path in the Apps section.

### 1. Create a dataset

In TrueNAS → **Datasets**, create a new dataset named `rock-cloudprint` under your pool (e.g. `tank/rock-cloudprint`, path `/mnt/tank/rock-cloudprint`).

### 2. Install via YAML

Go to **Apps → Discover → ⋮ (top right) → Install via YAML**, name the app `rock-cloudprint`, and paste:

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

> Adjust the host path if your pool is named differently.

### 3. Configure

Open `http://<truenas-ip>:8080` → **Settings** → enter your Rock server URL and Proxy ID → **Save & Reconnect**.

Settings persist in the dataset and survive container updates.

---

## Portainer

Deploy as a **Stack** — Portainer's equivalent of a Compose file.

Go to **Stacks → Add stack → Web editor**, name it `rock-cloudprint`, and paste:

```yaml
services:
  rock-cloudprint:
    image: asdfinit/rock-cloudprint:latest
    container_name: rock-cloudprint
    ports:
      - "8080:8080"
    volumes:
      - rock-cloudprint-config:/app/config
    restart: unless-stopped

volumes:
  rock-cloudprint-config:
```

Click **Deploy the stack**, then open `http://<server-ip>:8080` → **Settings** → enter your Rock server URL and Proxy ID → **Save & Reconnect**.

> **Use a named volume, not a bind mount.** The container runs as UID 1000 (`appuser`). A fresh named volume inherits that ownership and stays writable; a host directory Docker auto-creates comes up root-owned and saving settings will fail. For a bind mount, `chown -R 1000:1000` the host path first and use an absolute path — `./config` does not resolve predictably in Portainer web-editor stacks.

> **Updating:** Portainer will not re-pull `latest` on its own. Use **Stacks → rock-cloudprint → Editor → Update the stack** with **Re-pull image** ticked.

---

## Configuration

Settings can be provided two ways. Environment variables take precedence over the web UI.

### Web UI (recommended)

Open `http://<server-ip>:8080`, go to **Settings**, and fill in:

| Field | Description |
|---|---|
| Rock Server URL | Full URL of your Rock instance, e.g. `https://origin.church.com` |
| Proxy ID | The **IdKey** (e.g. `da0BJR0Bpz`) from Rock's Cloud Print Proxy device record |
| Proxy Name | Optional friendly name; defaults to the container hostname |

### Environment variables

```yaml
environment:
  - Url=https://origin.church.com
  - Id=da0BJR0Bpz
  - Name=Office Proxy
  - Password=mypin        # optional PIN to protect the web UI
```

---

## PIN / password protection

The web UI is open by default. To require a login:

- **Via web UI:** Settings → Security → Set PIN
- **Via env var:** Add `- Password=mypin` to your `docker-compose.yml` environment block

Tokens are in-memory only. Users must log in again after a container restart.

---

## Networking

On a standard Linux server this image uses `network_mode: host` so the container can reach printers at their local IP addresses (port 9100) and the web UI is available on port 8080 with no port mapping needed.

On **TrueNAS SCALE**, host networking is not available in the Apps system. Use explicit port mapping (`ports: - "8080:8080"`) instead — the container can still reach LAN printers through the host's network.

> **macOS / Windows Docker Desktop:** Host networking is not supported. This image is intended for Linux servers only.

---

## CDN / origin URL note

If your Rock site sits behind Cloudflare or another CDN, use the **origin server URL** rather than the primary domain. CDNs typically do not forward WebSocket upgrade requests, causing connection failures with the error:

```
The server returned status code '400' when status code '101' was expected.
```

Your origin URL is often `https://origin.yourdomain.com` or the direct IP/hostname of your web server.

---

## Printer addressing

Printers are configured in Rock — not in this app. In Rock's check-in printer settings, set the printer address to the printer's local IP:

- `192.168.1.50` — uses default port 9100
- `192.168.1.50:9100` — explicit port

---

## Auto-start on reboot (Linux / Raspberry Pi)

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

---

## Updating

```bash
docker compose pull
docker compose up -d
```

Settings are stored in the `config/` directory outside the container and are unaffected by updates.

---

## Web UI reference

| Tab | Description |
|---|---|
| Dashboard | Connection status, uptime, labels printed counter |
| Logs | Live service log (last 300 entries), color-coded by level |
| Settings → Connection | Rock server URL, Proxy ID, Proxy Name |
| Settings → Security | Set, change, or remove web UI PIN |

---

## Useful commands

```bash
docker compose pull          # pull latest image
docker compose up -d         # start / apply updates
docker compose logs -f       # live logs
docker compose restart       # restart container
docker compose down          # stop
```

---

## Source

[github.com/The-Ark-Church/rock-cloud-print-docker](https://github.com/The-Ark-Church/rock-cloud-print-docker)

Based on [Rock RMS](https://github.com/SparkDevNetwork/Rock) — licensed under the [Rock Community License](http://www.rockrms.com/license).
