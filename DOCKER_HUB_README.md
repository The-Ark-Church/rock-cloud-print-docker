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

The web UI is open by default. **Set a PIN.** Anyone who can reach port 8080 can
change every setting, and can upload a label and print it to any address they
choose — which means the port offers a way to send arbitrary bytes to any host
and port on your network. That is inherent to what a print proxy does, but it
makes the PIN worth setting even on a network you trust.

To require a login:

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
| Printers | Test whether a printer can be reached, using the same connection a print uses. Nothing is printed |
| Blank Labels | Store ZPL templates and print pre-coded blank check-in labels. Works with Rock unreachable — see below |
| Settings → Connection | Rock server URL, Proxy ID, Proxy Name |
| Settings → Notifications | Tell Rock when a printer fails. Needs Rock-side setup first — see below |
| Settings → Security | Set, change, or remove web UI PIN |

---

## Blank labels

When check-in goes down, families fill in labels by hand — which only works if a
supply of pre-printed blanks already exists. Each blank carries a **security
code**, the same one on the child's tag and the parent's receipt, so pickup
still matches when the names are handwritten.

The **Blank Labels** tab prints them. **It needs nothing from the Rock
server, and that is the point** — the proxy already holds the templates and
already talks to the printers, so it still works when Rock does not. Print them in
advance, not during the outage.

Three demo templates are included, so there is something to print on a fresh
install. They can be deleted, and downloaded again from the repository.

**A label** is a ZPL file with `???` where the code goes. Write it anywhere that
produces ZPL and upload it. The label's size is fixed in the file by `^PW` and
`^LL`, so load the stock that matches — the size is shown beside each label.
Put anything in cutter mode (`^MMC`) last in the order, or the cut lands in the
middle of a copy.

**Codes** are random from an alphabet with no `0`, `O`, `1` or `I`, or
sequential. Sequential numbering is remembered between runs, so two batches
printed months apart cannot carry the same codes. Codes are reserved before
anything is sent, so a failed run leaves a gap in the numbering rather than
repeating itself later.

**A few things worth knowing:**

- Choose a printer that is not serving check-in. Nothing stops two things
  printing to the same printer at once.
- The run counts copies *handed to the printer*, not printed. A completed
  network write only means the bytes were accepted.
- There is no time limit on a run. A printer out of labels pauses and carries on
  when reloaded, so a slow run is usually waiting for paper. **Cancel** is the
  only thing that ends one early.
- The preview is drawn by [Labelary](https://labelary.com/), so that one button
  needs internet access. Printing does not.

Templates are stored in `config/labels/`, and the record of used codes in
`config/blank-labels.json` — both inside the folder you already back up.

---

## Print failure notifications

The proxy can tell your Rock server when a printer fails, or when a print
finishes too slowly to be any use, so somebody can be told. **Off by default.**

> ### This needs setting up in Rock first
>
> The container only posts to a web address. On its own that does nothing.
> Someone has to create three things in Rock, and the container cannot create
> any of them:
>
> 1. **A Lava webhook** to receive the message.
> 2. **A workflow** for that webhook to launch.
> 3. **The communications inside that workflow** — who gets told, and how.
>
> Until those exist, turning notifications on only records problems in the log.
>
> **An importable workflow and full setup steps are in the repository**, under
> [`docs/rock/`](https://github.com/The-Ark-Church/rock-cloud-print-docker/tree/main/docs/rock).
> It imports inert and notifies nobody until you set the groups.

Once Rock is ready, configure it in **Settings → Notifications**: the webhook
URL, the shared secret, and a quiet period. Then press **Send test
notification** — it sends a real request and reports exactly what came back, so
the URL, the secret, the webhook and the workflow are all proved at setup time
rather than during an outage.

**If Rock is configured correctly, pressing it may message people.** That is how
you know it worked.

A few things worth knowing:

- The webhook URL must be `https` — the shared secret travels in a request
  header. A plain `http` URL is refused rather than sent.
- Use the same host as your Rock server URL. Behind a CDN the webhook sees the
  CDN's address rather than your proxy's, and rejects it.
- Notifications are debounced per printer, per kind of problem, defaulting to a
  five minute quiet period. One printer failing ten times produces one
  notification; ten printers failing produce ten, because that is ten things to
  check.
- If a notification cannot be delivered, the dashboard shows a banner naming the
  actual fault — a rejected secret, a URL matching no webhook — rather than
  saying the request failed. It clears on the next success.

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
