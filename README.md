# Rock Cloud Print — Docker Edition

A community-maintained Docker port of the [Rock RMS](https://www.rockrms.com/) Cloud Print proxy service. The original is a Windows-only desktop app; this runs as a headless Linux container with a browser-based admin UI.

Rock sends print jobs (usually ZPL label data) over a WebSocket to this proxy, which forwards them as raw TCP to a printer on your local network, normally port 9100.

```
Rock RMS Server  ──WebSocket──▶  This container  ──TCP:9100──▶  Local Printer
```

Commonly used for check-in label printing where the printers are on a church network but Rock is cloud-hosted.

---

## Prerequisites

- A Linux server (Ubuntu 22.04+ recommended) on the same network as your printers
- [Docker](https://docs.docker.com/engine/install/ubuntu/) and the Compose plugin
- A Rock RMS server (v17+) with a Cloud Print Proxy device record
- The printer reachable from the server by IP on port 9100

### Install Docker on Ubuntu

```bash
sudo apt-get update
sudo apt-get install -y docker.io docker-compose-v2
sudo systemctl enable --now docker
sudo usermod -aG docker $USER   # run docker without sudo (re-login after)
```

`docker-compose-v2` is Ubuntu's package for Compose v2 — the same `docker compose` command. The `docker-compose-plugin` name you will see elsewhere only resolves once you have added Docker's own apt repository, and fails on stock Ubuntu with `Unable to locate package`.

---

## Quick start

1. **Clone the repo onto your server**

   ```bash
   git clone https://github.com/The-Ark-Church/rock-cloud-print-docker.git /opt/rock-cloudprint
   cd /opt/rock-cloudprint
   ```

2. **Open port 8080**

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

   > Adjust `WorkingDirectory` if you installed elsewhere.

4. **Start it**

   ```bash
   docker compose up -d
   ```

5. **Configure**

   Open `http://<server-ip>:8080`, go to **Settings**, and enter:

   - **Rock Server URL** — e.g. `https://church.rockrms.com`
   - **Proxy ID** — the Device IdKey from Rock's Cloud Print Proxy device record
   - **Proxy Name** — optional; defaults to the container hostname

   Click **Save & Reconnect**. The Dashboard turns green within a few seconds.

   **Set a PIN** under Settings → Security while you are there. Anyone who can reach port 8080 can change every setting and print to any address.

---

## Other deployments

### TrueNAS SCALE

TrueNAS SCALE 25.10 can run the image from Docker Hub via **Install via YAML**.

1. In **Datasets**, create `rock-cloudprint` under your pool — e.g. `tank/rock-cloudprint`, path `/mnt/tank/rock-cloudprint`. Nothing needs to be put inside it; `appsettings.json` is created when you save settings.
2. **Apps → Discover → ⋮ → Install via YAML**, name the app, and paste:

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

3. Open `http://<truenas-ip>:8080` and configure as in Quick start step 5. Settings persist in the dataset and survive updates.

> TrueNAS Apps do not support `network_mode: host`, so port mapping is used. The container still reaches LAN printers through the host.

### Portainer

**Stacks → Add stack → Web editor**, name it `rock-cloudprint`, paste, and **Deploy the stack**:

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

Open the firewall (`sudo ufw allow 8080/tcp`), then configure as in Quick start step 5.

> **Why a named volume?** The container runs as UID 1000 (`appuser`). A fresh named volume inherits that ownership and stays writable. A bind mount to a host directory Docker auto-creates comes up owned by `root`, and saving settings fails.
>
> To bind-mount anyway (easier to back up), create it with the right owner first:
> ```bash
> sudo mkdir -p /opt/rock-cloudprint/config
> sudo chown -R 1000:1000 /opt/rock-cloudprint/config
> ```
> then replace the volume line with `- /opt/rock-cloudprint/config:/app/config` and delete the top-level `volumes:` block. Use an absolute path — `./config` does not resolve predictably in Portainer web-editor stacks.

---

## Configuration

Three ways. Environment variables take precedence over the web UI.

**A — Web UI (recommended).** Settings tab. Written to `config/appsettings.json` on the host, persisted through the `./config:/app/config` mount.

**B — Environment variables.** Uncomment the `environment` block in `docker-compose.yml`, then `docker compose up -d`:

```yaml
environment:
  - Url=https://church.rockrms.com
  - Id=da0BJR0Bpz
  - Name=Office Proxy
  - Password=mypin          # optional — locks the web UI behind a PIN
```

**C — Edit `config/appsettings.json` directly.** The container watches the file and reconnects automatically; no restart needed.

```json
{
  "Url": "https://church.rockrms.com",
  "Id": "da0BJR0Bpz",
  "Name": "Office Proxy"
}
```

---

## Security

### Built-in hardening

Always on:

| Layer | What it does |
|---|---|
| **Login rate limit** | `/api/auth/login` is capped at 5 attempts per minute; excess returns HTTP 429 |
| **Security headers** | Every response sets `X-Frame-Options: DENY`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `Cache-Control: no-store`, and a `Content-Security-Policy` restricting script/style/image sources |
| **Server header suppression** | `Server: Kestrel` is disabled, so the stack is not advertised |
| **Non-root runtime** | Runs as `appuser` (UID 1000), chosen to match the typical host volume owner so `./config` stays writable without privilege escalation |
| **Bearer tokens in `sessionStorage`** | Cleared when the tab closes, rather than `localStorage` |

### PIN protection

**Set one.** Anyone who can reach port 8080 can view and change every setting, and can upload a label and print it to any address — which means the port offers a way to send arbitrary bytes to any host and port on your network. That is inherent to what a print proxy does, and it is why the PIN is worth setting even on a network you trust.

**Via the web UI:** Settings → Security → enter a PIN (any alphanumeric value, not just digits) and confirm → **Set PIN**. You are sent to a login screen immediately, and the PIN is stored in `config/appsettings.json`. Change or remove it in the same place — enter the current PIN, then a new one, or leave it blank to remove protection.

**Via environment variable:** add `Password=mypin` to the `environment` block. Set this way it cannot be changed through the web UI, which suits automated deployments.

| Situation | Effect |
|---|---|
| No PIN configured | Web UI fully open — anyone reaching the port can change settings and use the printer test |
| PIN set via web UI | Login screen on every new browser session |
| PIN set via env var | Login required; PIN cannot be changed in the web UI |
| Container restarts | In-memory sessions cleared — users log in again |
| PIN changed | All active sessions invalidated immediately |

---

## Networking

The bundled `docker-compose.yml` uses `network_mode: host`: the container shares the host's network stack, port 8080 needs no mapping, and printers are reachable at their LAN addresses.

**Why host networking?** Printers use raw TCP on port 9100. Under bridge networking the container gets its own IP and may not reach devices on your subnet. Host mode removes the problem on Linux.

Bridge networking with port mapping — as used in the TrueNAS and Portainer stacks above — works fine for outbound TCP to printers. Switch a bridged stack to `network_mode: host` only if your Docker bridge subnet (`172.17.0.0/16` by default) overlaps your LAN; if you do, remove the `ports:` block, because Compose rejects a service declaring both.

> **Firewall:** `sudo ufw allow 8080/tcp`

> **No third-party CDN at runtime.** Tailwind CSS is compiled into the image at build time rather than fetched from a CDN, so the admin UI depends on no external host, loads faster, and makes no third-party requests.

### Printer addressing

Printers are addressed from Rock, not from here. In Rock's check-in printer configuration set the printer's local IP, with an optional port:

- `192.168.1.50` — default port 9100
- `192.168.1.50:9100` — explicit port

The proxy forwards to whatever IP:port Rock specifies.

### Rock server URL

Use your **origin URL** rather than your primary domain if the site sits behind a CDN. CDNs frequently do not forward WebSocket upgrade requests, so the server returns 400 instead of the expected 101 handshake. Origin URLs usually look like `https://origin.yourdomain.com`, or the direct hostname of your web server.

This error in the logs almost always means a CDN is in the way:

```
The server returned status code '400' when status code '101' was expected.
```

### Finding your Proxy ID in Rock

1. **Admin Tools → Check-in → Devices**
2. Open or create a proxy device
3. Copy the **IdKey** (a short encoded value like `da0BJR0Bpz`) or the full **Guid**. On the device page, click the three-dot menu at the top right, then click the `Id` label — it cycles through Id, Guid and IdKey
4. Paste it into **Proxy ID** in Settings

---

## Web UI reference

| Page | What it shows |
|---|---|
| **Dashboard** | Connection status (green/amber/grey), start time, time connected, labels requested since start, and a Print Results panel: labels printed, labels failed, prints that finished too slowly, and the reason for the most recent failure |
| **Logs** | Live service log in columns — date and time, log type, source and message — with toggles above for filtering by type and by source. A row must pass both filters to show |
| **Printers** | Test whether a printer can be reached, using the same connection a print uses. Nothing is printed |
| **Blank Labels** | Store ZPL templates and print pre-coded blank check-in labels. Works with Rock unreachable — see below |
| **Settings → Connection** | Rock server URL, Proxy ID, Proxy Name — saves to `config/appsettings.json` |
| **Settings → Failure Notifications** | Tell Rock when a printer fails. Requires Rock-side setup first — see below |
| **Settings → Security** | Set, change, or remove the web UI PIN |

The running version sits beside the title, next to a button that switches between the light and dark themes. The page follows your browser's own light/dark setting until you press it; after that it remembers your choice, kept in your browser rather than on the proxy so two administrators do not fight over it.

The dashboard refreshes every 2 seconds. The log panel refreshes every 3 seconds while visible, fetching only entries it has not already shown. The log buffer holds the last 2,000 entries in memory and is cleared on restart.

### Reading the Print Results panel

**Labels Requested** counts every label Rock asked for, whether or not it reached a printer. **Printed** and **Failed** split that total by what happened.

**Too Slow** counts prints that finished after Rock stopped waiting. Rock's check-in kiosk allows five seconds and then shows the operator its own timeout message, so anything slower finished too late for the operator to see the real result — even when the labels printed correctly a moment later. Adjust with `SlowPrintMilliseconds` (default `5000`, `0` disables the check).

When a print fails, the reason shown is the exact message the proxy returned to Rock, so it matches what appeared on the check-in screen.

---

## Blank labels

When check-in goes down, families fill in labels by hand. That only works if a supply of pre-printed blanks already exists, and each blank has to carry a **security code** — the same code on the child's tag and on the parent's receipt, so pickup still matches when the names are handwritten.

**It needs nothing from Rock, and that is the entire point.** The proxy already holds the templates and already talks to the printers, so it can print blanks when Rock is unreachable, which is the situation blanks exist for. Print them well in advance, not during the outage.

### Printing

1. Open **Blank Labels**.
2. Tick the labels that make up one copy and use the arrows to order them. **The order matters if any of them cuts** — see Cutting, below.
3. Choose the printer — type an address, or pick a saved one by name.
4. Choose how many **copies**, and whether codes are random or sequential.
5. **Preview** to see the label, or **Print one test copy** to check the printer and the stock.
6. **Print labels**.

Three demo templates are included, so there is something to print on a fresh install.

**Choose a printer that is not serving check-in.** Nothing stops two things printing to one printer at once; if a blank run and a real check-in print land together, their data can interleave and both come out wrong. In practice this is avoided by circumstance rather than by code — whoever prints blanks is stood at the printer, it is usually a desk printer, and it is not happening during a service. Keep it that way.

### The labels

A label is a ZPL file with a placeholder wherever the security code goes. The proxy stores and prints it; it does not edit ZPL and has no designer. Two placeholders are recognised:

| | |
|---|---|
| `WWW` | What Rock's own legacy check-in labels use, so a label designed in Rock needs no editing. Recognised only as the **whole** of a field — three letters turn up by accident in a way three question marks do not, and `www.example.com` should not have a code substituted into the middle of it |
| `???` | Recognised anywhere inside a field, so it can sit among other text. The demo templates use this |

Write them in a text editor, in Zebra's designer, or in Rock's — anything that produces ZPL — and upload on the **Blank Labels** tab. A file is rejected if it is not ZPL, is over a megabyte, or has no placeholder inside a `^FD` field, since a blank with nowhere to put a code is not a blank. A placeholder in a `^FX` comment does not count, because a comment is never printed.

**The size is fixed in the template.** `^PW` and `^LL` set printable width and length in dots, so a template written for 3x2 stock prints 3x2 whatever is loaded. The size is shown beside each label; load the stock that matches.

**Anything that cuts, put last.** A template carrying `^MMC` puts the printer in cutter mode, so the cut falls after that label; anywhere but last and the cut lands mid-copy. The included roster label is the one that cuts, which is why it is last.

The demos can be deleted like any other label, and downloaded again from `Rock.CloudPrint.Service/Labels/` in this repository.

### Codes

**Random** codes are drawn from every digit and capital letter, and are deduplicated within a run.

**Sequential** codes count up from a number you give, and **the proxy remembers where it got to**, so labels printed in March and in June cannot carry the same numbers. Leave the start blank to carry on from the last run.

**Codes are reserved before anything is sent to the printer.** If a run fails half way, the codes it reserved stay used and the next run carries on past them. That leaves a gap, which is harmless — a repeat would not be. If the proxy cannot read its record of used codes it says so and asks for a starting number rather than guessing; starting again from 1 would silently reissue every code already printed.

A blank's code colliding with a real one Rock issued is not a problem in practice: the parent holds the matching half and the handwritten names differ, so a volunteer sees the mismatch.

How many characters fit is decided by the label, not by this setting, because the code is printed large enough to read at a glance. **The demo templates are designed for three.** Longer codes are clipped rather than shrunk, so check first — the preview draws a real code, and **Print one test copy** prints W repeated to your chosen length, W being the widest character a code can contain. For longer codes, upload a template with the field laid out for it.

### "Handed to the printer" is not "printed"

The run panel counts copies **handed to the printer**, and the wording is deliberate. A completed network write only means the operating system accepted the bytes — measured on a development machine, close to a megabyte can still sit in buffers after a printer has stopped reading. So the count is what this end handed over, which is all it can honestly claim. Look at the labels.

**There is no time limit on a run, by design.** A printer out of labels pauses and carries on when reloaded, even with nobody there, so a slow run is usually a printer waiting for paper rather than a failure. Only **Cancel** ends a run early.

One run happens at a time; a second is refused rather than queued.

### Cutting

If the printer has a cutter, tick **Has cutter** beside the printer and the proxy sends the cut commands itself — one cut after the last label of each copy, so a child's tag, a parent's receipt and a roster label come off together.

Ticking it applies to that run. If you save the printer, the setting is remembered with it and returns whenever that printer is chosen — a cutter is a fact about the machine, and whoever picks a printer by name is exactly the person who would not know. You can still untick it for a single run.

This is worth ticking even if your labels already carry `^MMC`, because the cut then no longer depends on which label happens to be last. The commands are the ones Rock sends during check-in: the trailing `^XZ` becomes `^MMC^XZ` to cut, or `^XB^XZ` to suppress the backfeed and the cut with it. They are appended, so a template's own `^MMT` or `^MMC` need not be removed — ZPL takes the last command it is given. Leave it unticked and nothing is added, so a label that cuts by itself behaves exactly as before.

### Saving a printer

The Printer box takes an address or the name of a saved printer, and the list below it narrows as you type. Pick one and the box shows the name, not the address; the name is turned back into an address when the run starts. So whoever sets a printer up types its address once, and everybody after them types the name and never sees one.

Press **+** to save what is in the box. The dialog asks for a name, the address, and whether the printer has a cutter. Opening it on a printer already in the list edits that one instead. The cross on a row forgets that printer; the printer itself is untouched.

The list lives on the proxy, not in your browser, so it is the same for everyone who opens the page. Nothing in the print path reads it — a run is always given an address — so a lost or hand-edited file costs somebody some typing and cannot stop anything printing. Addresses are checked when saved, with the same parser a print uses, so an unusable address is refused where it is typed rather than when somebody presses print.

### Capturing a label from Rock

Rock will not hand out the ZPL for a label designed in its own designer. But the proxy sits in the middle of every print, so a label Rock prints arrives as raw ZPL whatever it was authored as — and **Capture from Rock** catches one by being an ordinary printer.

**This is for a label you designed *as a blank*** — with lines to write on and a placeholder where the code goes. A normal check-in label will not do: designed labels write text straight into fields and carry nothing to write on, because nothing is ever hand-written on them, so blanking one leaves empty space with no indication of what goes where.

1. On **Blank Labels**, press **Capture from Rock**, then **Wait for a label**. It listens on port 9100 by default.
2. In Rock, set a printer's address to this machine and print one label to it. A check-in label's test print does it. No port is needed — an address without one means 9100.
3. Pick which fields hold the security code, name the label, and save.

**Nothing here needs to be connected to Rock.** Whichever proxy Rock already sends that label to is the one that opens the connection, so the address only has to be reachable *from that proxy* — it is an ordinary printer address as far as it is concerned, and can be a different machine entirely. If the proxy you are capturing with is itself the one Rock routes to, use `127.0.0.1:9100` and nothing crosses a network.

It listens only while armed and stops after one label, so arming it and forgetting cannot quietly record a real child's label during a service. A connection that sends nothing — a reachability check, a port scan, the proxy's own printer test — is ignored and it keeps waiting.

**Expect the security code field to arrive empty.** It looks like something has gone wrong and has not. Rock's stored ZPL uses a token such as `WWW` where the code goes, but by the time a label is *printed* Rock has already substituted it, and a test print has no attendance behind it — so it substitutes to nothing.

That is why fields are chosen by position rather than by text, and why the list shows each field's font height. The security code is the one thing on a check-in label printed large, so on a real label it stands out from the captions by a factor of three or four; the tallest fields are ticked for you. Pick more than one where the label needs it — a receipt torn in half carries the code on both halves.

> **Legacy labels cannot be captured.** Rock's legacy label editor test-prints straight from the Rock server with its own socket and never consults the proxy, so the job never arrives here. Legacy labels are raw ZPL you can already see, so copy it out of the editor and upload it instead.

### Preview

The preview is drawn by [Labelary](https://labelary.com/), the same service Rock's own label designer uses, so **this one button needs internet access. Printing does not.** With no internet the preview says so and everything else carries on. The proxy fetches the image itself, so your browser never contacts a third party.

### Files it writes

| Path | What it is |
|---|---|
| `config/labels/*.zpl` | The stored templates |
| `config/blank-labels.json` | Where sequential numbering has reached, and the last ten runs |
| `config/printers.json` | Printers saved by name, with their addresses and cutter settings |

All are inside `config`, so whatever backs that up already covers them.

---

## Print failure notifications

The proxy can tell your Rock server when a printer fails, so somebody can be told. Off by default.

> ### This needs setting up in Rock first
>
> **The container only sends a message to a web address.** On its own that does nothing useful. Someone has to create three things in Rock, and the container cannot create any of them:
>
> 1. **A Lava webhook** to receive the message.
> 2. **A workflow** for that webhook to launch.
> 3. **The communications inside that workflow** — who gets told, and how.
>
> Until those exist, turning notifications on only records failures in the log.

### What the proxy sends

A POST with a JSON body and an `X-CloudPrint-Token` header holding your shared secret. The URL must be `https` — the secret travels in a request header.

```json
{
  "schema": 1,
  "event": "failed",
  "printer": "192.168.1.50",
  "reason": "No route to host",
  "labelCount": 2,
  "elapsedMs": 5001,
  "occurredAt": "2026-01-01T09:15:00Z",
  "proxyName": "Kids Check-in Proxy",
  "proxyId": "da0BJR0Bpz",
  "proxyVersion": "1.3.0",
  "consecutiveFailures": 3,
  "printersFailing": 5
}
```

| Field | Meaning |
|---|---|
| `event` | `failed`, `slow`, or `test` from the test button |
| `reason` | The exact error the proxy got. Empty for `slow` and `test` |
| `consecutiveFailures` | Failures for **this** printer since it last printed |
| `printersFailing` | How many **distinct** printers are failing right now. One printer failing five times is a printer problem; five printers failing once each is a network problem |

**The proxy expects HTTP 202 and a body containing `"accepted": true`.** Anything else is treated as a failure and raises a banner — including a bare `200`, which is what a Rock webhook returns when its Lava template fails.

### Setting it up in Rock

Create a **Lava Webhook** defined value (Admin Tools → General Settings → Defined Types → Lava Webhook):

| Field | Value |
|---|---|
| Value | `/notifications/cloud-print` (becomes the URL path) |
| Method | `POST` |
| Enabled Lava Commands | `RockEntity,WorkflowActivate` |
| Response Content Type | `application/json` |

Its template checks the secret, confirms the workflow type exists, launches it, and answers with a status code the proxy can act on:

```liquid
{%- assign workflowTypeGuid = 'your-workflow-type-guid' -%}
{%- assign expectedSecret = 'Global' | Attribute:'CloudPrintWebhookSecret' -%}

{%- comment -%} Header names arrive lowercased, so match case-insensitively. {%- endcomment -%}
{%- assign providedSecret = '' -%}
{%- for h in Headers -%}
{%- assign headerName = h[0] | Downcase -%}
{%- if headerName == 'x-cloudprint-token' -%}{%- assign providedSecret = h[1] -%}{%- endif -%}
{%- endfor -%}

{%- comment -%}
  Confirm the workflow type exists BEFORE activating. A workflow type keeps its
  Guid through an export/import but gets a new Id, so a template referencing an
  Id can silently launch the wrong workflow on another server. Always use the Guid.
{%- endcomment -%}
{%- assign typeFound = false -%}
{% workflowtype where:'Guid == "{{ workflowTypeGuid }}"' %}
{%- for t in workflowtypeItems -%}{%- assign typeFound = true -%}{%- endfor -%}
{% endworkflowtype %}

{%- if expectedSecret == '' or providedSecret != expectedSecret -%}
{% httpresponse status:'401' %}{% endhttpresponse %}
{"accepted":false,"error":"invalid token"}
{%- elseif Body.printer == null or Body.printer == '' -%}
{% httpresponse status:'400' %}{% endhttpresponse %}
{"accepted":false,"error":"missing printer"}
{%- elseif typeFound == false -%}
{% httpresponse status:'500' %}{% endhttpresponse %}
{"accepted":false,"error":"workflow type not found"}
{%- else -%}
{%- assign activateError = '' -%}
{% workflowactivate workflowtype:'{{ workflowTypeGuid }}' workflowname:'Cloud Print failure' printer:'{{ Body.printer }}' event:'{{ Body.event }}' reason:'{{ Body.reason }}' labelcount:'{{ Body.labelCount }}' elapsedms:'{{ Body.elapsedMs }}' occurredat:'{{ Body.occurredAt }}' proxyname:'{{ Body.proxyName }}' consecutivefailures:'{{ Body.consecutiveFailures }}' printersfailing:'{{ Body.printersFailing }}' rawbody:'{{ RawBody }}' %}
{%- assign activateError = Error -%}
{% endworkflowactivate %}
{% httpresponse status:'202' %}{% endhttpresponse %}
{"accepted":true,"workflowError":"{{ activateError | Escape }}"}
{%- endif -%}
```

Both commands are required. `WorkflowActivate` launches the workflow; **`RockEntity` is what makes the `{% workflowtype %}` check work** — without it the check finds nothing and every request returns 500.

**A ready-made workflow is included** — see [`docs/rock/`](docs/rock/), which has an importable Rock workflow export and the configuration steps. It imports inert and notifies nobody until you set the groups.

If you build your own instead, **its attribute keys must match the parameter names above exactly** — a parameter with no matching attribute is silently discarded, leaving an empty field rather than an error.

Store the shared secret in a global attribute named `CloudPrintWebhookSecret`, and set the same value in the proxy's settings.

#### Four things that will cost you a day

Each one fails *silently*, producing an empty value rather than an error.

1. **A webhook-launched workflow has no logged-in person, and Lava entity commands apply Rock's entity security.** `{% groupmember %}` returns nothing inside such a workflow while returning rows perfectly well when you test the same template through `/api/Lava/RenderTemplate`, because that runs as your API user. Add `securityenabled:'false'`:

   ```liquid
   {% groupmember where:'GroupId == {{ groupId }} && GroupMemberStatus == 1' securityenabled:'false' %}
   ```

   Reading a collection off the entity instead — `group.Members` — also avoids it, since navigation properties are not filtered.

   **This one cannot be caught by testing.** The render endpoint runs as an administrator, so the command succeeds there either way, and it even accepts parameters it does not recognise without complaint. Only a real webhook-launched run proves it.

2. **`Attribute:'Something'` returns display names, not guids.** A Schedules attribute renders as `Sunday 9am, Sunday 11am`, so `where:'Guid == "Sunday 9am"'` matches nothing. Use `Attribute:'Something','RawValue'`.

3. **`{% workflowactivate %}` parameters are delimited by single quotes.** A failure reason containing an apostrophe — `Couldn't connect` — ends the parameter early and breaks the tag. Sanitise free text into a variable before the tag rather than inline.

4. **Enum properties render as names but filter as integers.** `RSVP` displays `Yes` but filters as `1`; the same applies to group member status and communication preference. Compare the rendered name, or filter on the number inside `where:` — never mix them.

### Checking it works

Use **Send test notification** in Settings. It sends a real request marked `"event": "test"` and reports exactly what came back, so the URL, the secret, the webhook, the workflow type and the activation are all proved at setup time rather than during an outage.

**If Rock is configured correctly, pressing it may message people.** That is how you know it worked.

### How often it notifies

Per printer, per kind of event, with a quiet period defaulting to five minutes. One printer failing ten times produces one notification; ten printers failing produce ten. A clean, on-time print clears the quiet period for that printer, so a genuine second outage is reported even if it follows closely.

---

## Updating

```bash
git pull
docker compose pull
docker compose up -d
```

Settings in `config/` live outside the container and are unaffected.

**Portainer** will not re-pull a `latest` image on its own: use **Stacks → rock-cloudprint → Editor → Update the stack** with **Re-pull image** ticked. To pin a specific build, resolve the digest with `docker buildx imagetools inspect asdfinit/rock-cloudprint:latest` and use `image: asdfinit/rock-cloudprint@sha256:<digest>`.

### Versions and releases

Images are published by GitHub Actions whenever a version tag is pushed, so a Docker tag always corresponds to an exact commit.

| Docker tag | What it means |
|---|---|
| `1.1.0` | An exact release. Never changes once published |
| `1.1` | Follows patch releases within 1.1 (`1.1.0`, `1.1.1`, …) |
| `latest` | The most recent release |

All three come from a single build, so `latest` is always identical to the numbered release it came from. A pre-release tag such as `1.2.0-rc1` publishes only itself and leaves `latest` alone.

**For production, pin an exact version.** `latest` is convenient for trying the project out, but on a machine that prints check-in labels you want upgrades when you choose them:

```yaml
services:
  rock-cloudprint:
    image: asdfinit/rock-cloudprint:1.1.0   # pinned, not :latest
```

Rolling back is then editing that line to the previous version and running `docker compose up -d`.

Version numbers follow [semantic versioning](https://semver.org): patch for fixes, minor for new functionality that breaks nothing, major if an upgrade requires you to change something.

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

## Troubleshooting

**Dashboard shows "Connecting…" and never goes green**
- Verify the Rock Server URL is reachable from the server (`curl https://church.rockrms.com`)
- Check the Proxy ID matches the device record in Rock exactly
- Check logs: `docker compose logs -f`

**Dashboard shows "Not configured"**
- No URL or ID set yet. Go to Settings.

**Print jobs arrive (labels count increments) but nothing prints**
- The printer IP or port in Rock's device record is wrong or unreachable
- Open **Printers** and test the address — it opens the same connection a print does, without printing
- Make sure the printer is on and on the same network as this server
- If the test fails, work through *Diagnosing printer connectivity* below

**Port 8080 not accessible from browser**
- `sudo ufw status`, then `sudo ufw allow 8080/tcp`

**Container won't start**
- Run `docker compose up` without `-d` to see startup errors

**Settings saved via web UI don't survive a restart**
- The `config/` directory must exist on the host at startup; the mount maps `./config` to `/app/config`

### Diagnosing printer connectivity

Run these **inside the container** — the host reaching a printer does not guarantee the container can:

```bash
docker exec -it rock-cloudprint-rock-cloudprint-1 bash
```

| # | Command | What it tells you |
|---|---|---|
| 1 | `ip route get <printer-ip>` | Whether the printer is routed out through a gateway (`via …` appears) or wrongly treated as local (no `via`) |
| 2 | `ip -brief addr` | What address and subnet the container actually has |
| 3 | `ping -c3 <printer-ip>` | Whether the printer answers at all, and whether ARP resolves |
| 4 | `timeout 2 bash -c 'exec 3<>/dev/tcp/<printer-ip>/9100'` | Whether the printing port is open — the same thing a print does. Exit code 0 means open |
| 5 | `ip neigh` | What ARP resolved the address to |

**Steps 1 and 2 only matter on a bridged deployment** such as TrueNAS. With `network_mode: host` the container shares the host's stack, so they tell you nothing the host would not.

**The missing `via` in step 1 is the one to look for.** If the printer's address falls inside the container's own subnet, the container treats it as a neighbour and the traffic never leaves — while the host looks perfectly healthy. The fix is to move the container's network off the range the printers use.

Step 4 uses a `bash` feature rather than `nc`, which is not installed. The `nc -zv 192.168.1.50 9100` form works on the **host**, not in the container.

---

## What changed from the original Windows app

| Original | This version |
|---|---|
| Windows Service (background process) | Docker container (Linux) |
| WPF desktop GUI | Browser-based web UI on port 8080 |
| Named pipe IPC between GUI and service | REST API (`/api/status`, `/api/settings`) |
| Settings via Windows installer wizard | Settings via web UI or `config/appsettings.json` |
| EventLog / Windows Event Viewer logging | stdout / `docker compose logs` |
| Single-file Windows executable | Multi-stage Docker image |

The proxy's actual behaviour — the WebSocket connection to Rock and the raw TCP forwarding to printers — is unchanged from upstream. What changed around it is logging, metrics and address parsing, listed below.

### Source changes

| File | What changed |
|---|---|
| `Rock.CloudPrint.Service/Rock.CloudPrint.Service.csproj` | SDK changed from `Worker` to `Web`; removed Windows runtime identifier, single-file publish, and Windows-only packages |
| `Rock.CloudPrint.Service/Program.cs` | Replaced Windows Service host with `WebApplication`; added REST API endpoints; removed Named Pipe and EventLog; added authentication middleware |
| `Rock.CloudPrint.Service/CloudPrintOptions.cs` | Added `Password` for PIN protection and `SlowPrintMilliseconds` for the slow-print threshold |
| `Rock.CloudPrint.Service/ProxyClientWebSocket.cs` | Successful prints log at `Information` rather than `Debug`, each attempt is timed and recorded in `PrintMetrics`, and address parsing moved to `PrinterAddress` |
| `Rock.CloudPrint.Service/ProxyWorker.cs` | Passes `PrintMetrics` and the slow-print threshold to the proxy connection, and gives it its own `ILogger<ProxyClientWebSocket>` so print activity is attributed separately from worker activity |
| `Rock.CloudPrint.Service/PrintMetrics.cs` | New — records the outcome of each print attempt: labels printed, labels failed, prints that outran the server, and the reason for the most recent failure |
| `Rock.CloudPrint.Service/PrinterAddress.cs` | New — printer address parsing, lifted out of the print path so the web UI's test runs the same code rather than a copy of it |
| `Rock.CloudPrint.Service/PrinterTester.cs` | New — opens a connection to a printer and reports the result, without printing |
| `Rock.CloudPrint.Service/FailureNotifier.cs` | New — reports print failures to a Rock webhook, with per-printer debouncing, and remembers the last attempt so the dashboard can name the fault |
| `Rock.CloudPrint.Service/AuthService.cs` | New — in-memory bearer token manager for web UI authentication |
| `Rock.CloudPrint.Service/InMemoryLogSink.cs` | New — circular log buffer (2,000 entries) for the Logs panel. In memory only, cleared on restart. Entries carry a sequence number so the UI fetches only what is new |
| `Rock.CloudPrint.Service/InMemoryLoggerProvider.cs` | New — `ILoggerProvider` capturing `Rock.CloudPrint.*` entries only |
| `Rock.CloudPrint.Service/LabelStore.cs`, `ZplTemplate.cs`, `SecurityCode.cs`, `BlankLabelRunner.cs`, `BlankLabelState.cs`, `LabelCapture.cs`, `LabelPreview.cs`, `PrinterBook.cs`, `PrinterSocket.cs`, `AtomicFile.cs` | New — blank label printing: template storage, code generation and substitution, run execution, the record of used codes, capture, preview, and saved printers |
| `Rock.CloudPrint.Service/appsettings.json` | Removed EventLog config; added `Urls: http://+:8080` and default empty keys |
| `Rock.CloudPrint.Service/wwwroot/index.html` | New — single-page web UI: Dashboard, Logs, Printers, Blank Labels, and Settings with Connection, Notifications and Security panels. Tailwind is loaded from the bundled `/app.css` rather than `cdn.tailwindcss.com`, and the inline `<style>` block moved into `build/src/app.css` |
| `Rock.CloudPrint.Shared/Rock.CloudPrint.Shared.csproj` | Bumped `System.Text.Json` from `8.0.4` to `8.0.5` (CVE GHSA-8g4q-xg66-9fp4) |
| `package.json`, `build/tailwind.config.js`, `build/src/app.css` | New — Tailwind build tooling. `npm run css` compiles the stylesheet |
| `Dockerfile` | New — multi-stage Linux build; installs `iputils-ping` and `iproute2` for in-container diagnostics; pre-creates `/app/config`; a Node stage compiles the stylesheet so it cannot drift from `index.html`; takes a `VERSION` build argument so the version the UI reports comes from the release tag |
| `docker-compose.yml` | New — host networking, `./config:/app/config` mount, `Password` env var option |
| `config/appsettings.json` | New — persistent settings file, in the host `config/` directory mounted into the container |

---

## License

This project is based on [Rock RMS](https://github.com/SparkDevNetwork/Rock) and is licensed under the [Rock Community License](http://www.rockrms.com/license).
