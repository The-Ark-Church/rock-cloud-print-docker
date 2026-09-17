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
sudo apt-get install -y docker.io docker-compose-v2
sudo systemctl enable --now docker
sudo usermod -aG docker $USER   # lets you run docker without sudo (re-login after)
```

`docker-compose-v2` is Ubuntu's package for Compose v2 — the same `docker compose`
command. The name `docker-compose-plugin` you will see elsewhere only resolves if
you have added Docker's own apt repository first, and fails on a stock Ubuntu with
`Unable to locate package`.

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

The web UI can be protected by a PIN or password. **Set one.**

Anyone who can reach port 8080 can view and change every setting. They can also
upload a label and print it to any address they choose, which means the port
offers a way to send arbitrary bytes to any host and port on your network. That
is inherent to what a print proxy does — but it makes the PIN worth setting even
on a network you trust, not only on one exposed beyond it.

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
| No PIN configured | Web UI is fully open — no login required, and anyone who can reach the port can change settings and use the printer test. **Set a PIN as soon as the container is running.** |
| PIN set via web UI | Login screen shown on every new browser session |
| PIN set via env var | Login required; PIN cannot be changed via web UI |
| Container restarts | In-memory sessions are cleared — users must log in again |
| PIN changed | All active sessions are invalidated immediately |

---

## Print failure notifications

The proxy can tell your Rock server when a printer fails, so somebody can be
told about it. It is off by default.

> ### This needs setting up in Rock first
>
> **The container only sends a message to a web address.** On its own that does
> nothing useful. Someone has to create three things in Rock, and the container
> cannot create any of them:
>
> 1. **A Lava webhook** to receive the message.
> 2. **A workflow** for that webhook to launch.
> 3. **The communications inside that workflow** — who gets told, and how.
>
> Until those exist, turning notifications on only records failures in the log.

### What the proxy sends

A POST with a JSON body and an `X-CloudPrint-Token` header holding your shared
secret. The URL must be `https` — the secret travels in a request header.

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

**The proxy expects HTTP 202 and a body containing `"accepted": true`.** Anything
else is treated as a failure and raises a banner — including a bare `200`, which
is what a Rock webhook returns when its Lava template fails.

### Setting it up in Rock

Create a **Lava Webhook** defined value (Admin Tools → General Settings →
Defined Types → Lava Webhook):

| Field | Value |
|---|---|
| Value | `/notifications/cloud-print` (this becomes the URL path) |
| Method | `POST` |
| Enabled Lava Commands | `RockEntity,WorkflowActivate` |
| Response Content Type | `application/json` |

Its template checks the secret, confirms the workflow type exists, launches it,
and answers with a status code the proxy can act on. A worked example, with the
non-obvious parts commented:

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

Both commands are required. `WorkflowActivate` launches the workflow;
**`RockEntity` is what makes the `{% workflowtype %}` check above work** — without
it the check finds nothing and every request returns 500.

**A ready-made workflow is included** — see [`docs/rock/`](docs/rock/), which has
an importable Rock workflow export and the configuration steps. It imports inert
and notifies nobody until you set the groups.

Then create the workflow type it launches. **Its attribute keys must match the
parameter names above exactly** — a parameter with no matching attribute is
silently discarded, leaving an empty field rather than an error.

Store the shared secret in a global attribute named `CloudPrintWebhookSecret`,
and set the same value in the proxy's settings.

#### Four things that will cost you a day

These are not obvious, and each one fails *silently* — producing an empty value
rather than an error.

1. **A webhook-launched workflow has no logged-in person, and Lava entity
   commands apply Rock's entity security.** `{% groupmember %}` returns nothing
   inside such a workflow while returning rows perfectly well when you test the
   same template through `/api/Lava/RenderTemplate`, because that runs as your
   API user. Add `securityenabled:'false'` to the entity command:

   ```liquid
   {% groupmember where:'GroupId == {{ groupId }} && GroupMemberStatus == 1' securityenabled:'false' %}
   ```

   Reading a collection off the entity instead — `group.Members` — also avoids
   it, since navigation properties are not filtered.

   **This one cannot be caught by testing.** The render endpoint runs as an
   administrator, so the command succeeds there either way; it even accepts
   parameters it does not recognise without complaint. Only a real
   webhook-launched run proves it.

2. **`Attribute:'Something'` returns display names, not guids.** A Schedules
   attribute renders as `Sunday 9am, Sunday 11am`, so `where:'Guid == "Sunday
   9am"'` matches nothing. Use `Attribute:'Something','RawValue'`.

3. **`{% workflowactivate %}` parameters are delimited by single quotes.** A
   failure reason containing an apostrophe — `Couldn't connect` — ends the
   parameter early and breaks the tag. Sanitise free text into a variable before
   the tag rather than inline.

4. **Enum properties render as names but filter as integers.** `RSVP` displays
   `Yes` but filters as `1`; the same applies to group member status and
   communication preference. Compare the rendered name, or filter on the number
   inside `where:` — never mix them.

### Checking it works

Use **Send test notification** in Settings. It sends a real request marked
`"event": "test"` and reports exactly what came back — so the URL, the secret,
the webhook, the workflow type and the activation are all proved at setup time
rather than during an outage.

**If Rock is configured correctly, pressing it may message people.** That is how
you know it worked.

### How often it notifies

Per printer, per kind of event, with a quiet period that defaults to five
minutes. One printer failing ten times produces one notification; ten printers
failing produce ten. A clean, on-time print clears the quiet period for that
printer, so a genuine second outage is reported even if it follows closely.

---

## Blank labels

When check-in goes down, families fill in labels by hand. That only works if a
supply of pre-printed blanks already exists, and each blank has to carry a
**security code** — the same code on the child's tag and on the parent's
receipt, so pickup still matches when the names are handwritten.

This prints them.

**It needs nothing from Rock, and that is the entire point.** The proxy already
holds the templates and already talks to the printers, so it can print blanks
when the Rock server is unreachable — which is the situation blanks exist for.
Print them well in advance, not during the outage.

### Printing labels

1. Open the **Blank Labels** tab.
2. Tick the labels that make up one copy, and use the arrows to put them in the
   order they should print. **The order matters if any of them cuts** — see
   below.
3. Enter the printer's IP address.
4. Choose how many **copies** and whether codes are random or sequential.
5. Press **Preview** to see the label, or **Print one test copy** to check the
   printer and the stock.
6. Press **Print labels**.

Three demo templates are included, so there is something to print on a fresh
install.

### Choose a printer that is not serving check-in

Nothing in the proxy stops two things printing to the same printer at once. If
a blank run and a real check-in print land on one printer together, their data
can interleave and both come out wrong.

In practice this is avoided by circumstance rather than by code: whoever prints
blanks is stood at the printer and types its address in by hand, it is usually a
desk printer rather than a check-in one, and it is not happening during a
service. Keep it that way.

### The labels

A label is a ZPL file with a placeholder wherever the security code should go.
The proxy stores it and prints it; it does not edit ZPL and has no designer.

Two placeholders are recognised:

| | |
|---|---|
| `WWW` | The same placeholder Rock's own legacy check-in labels use, so a label designed in Rock needs no editing afterwards. Recognised only when it is the **whole** of a field — three letters turn up by accident in a way three question marks do not, and a field reading `www.example.com` should not have a code substituted into the middle of it |
| `???` | Recognised anywhere inside a field, so it can sit among other text. The supplied demo templates use this |

Write them in a text editor, in Zebra's designer, or in Rock's — whatever
produces ZPL. Upload the file on the **Blank Labels** tab. It is rejected if it
is not ZPL, if it is over a megabyte, or if there is no placeholder inside a
`^FD` field, since a blank with nowhere to put a code is not a blank. A
placeholder in a `^FX` comment does not count, because a comment is never
printed.

**The size is fixed in the template.** `^PW` and `^LL` set the printable width
and label length in dots, so a template written for 3x2 stock prints 3x2
whatever is loaded. The size is shown beside each label; load the stock that
matches.

**Anything that cuts, put last.** A template carrying `^MMC` puts the printer in
cutter mode, so the cut falls after that label. Put it anywhere but last in the
order and the cut lands in the middle of a copy. The included roster label is
the one that cuts, which is why it is last.

The three demos can be deleted like any other label. They are published in this
repository, so a deleted one can be downloaded from
`Rock.CloudPrint.Service/Labels/` and uploaded again.

### Codes

**Random** codes are drawn from 32 characters with no `0`, `O`, `1` or `I` in
them, because somebody reads the code off a label and says it out loud at a
pickup desk. They are deduplicated within a run.

**Sequential** codes count up from a number you give, and **the proxy remembers
where it got to**, so labels printed in March and labels printed in June
cannot carry the same numbers. Leave the start blank to carry on from where the
last run finished.

**Codes are reserved before anything is sent to the printer.** If a run fails
half way, the codes it reserved stay used and the next run carries on past them.
That leaves a gap in the numbering, which is harmless — a repeat would not be.

If the proxy cannot read its record of used codes, it says so and asks for a
starting number rather than guessing. Starting again from 1 would silently
reissue every code already printed, so it will not do that on its own.

A blank's code colliding with a real one Rock issued is not a problem in
practice: the parent is holding the matching half and the handwritten names
differ, so a volunteer sees the mismatch.

A security code is printed large enough to read at a glance, so how many
characters fit is decided by the label, not by this setting. **The three demo
templates are designed for three**, which is the common case. Longer codes are
clipped rather than shrunk, so check before committing to a run — the preview
draws a real code, and **Print one test copy** prints W repeated to your chosen
length, W being the widest character a code can contain.

If you want longer codes, that is a change to the label rather than to the
proxy: upload your own template with the field laid out for it.

### "Handed to the printer" is not "printed"

The run panel counts copies **handed to the printer**, and the wording is
deliberate. A completed network write only means the operating system accepted
the bytes — measured on a development machine, close to a megabyte can still be
sitting in buffers after a printer has stopped reading. So the count is what
this end handed over, which is all it can honestly claim. Look at the labels.

**There is no time limit on a run, by design.** A printer that runs out of
labels pauses and carries on when it is reloaded, even if nobody is there when
it stops — so a run that is taking a while is usually a printer waiting for
paper, not a failure. The only thing that ends a run early is **Cancel**.

One run happens at a time. A second is refused rather than queued.

### Cutting

If the printer has a cutter, tick **Has cutter** beside the printer address and
the proxy sends the cut commands itself — one cut after the last label of each
copy, so a child's tag, a parent's receipt and a roster label come off together.

It is a setting for that run, not a saved printer: the proxy has no printer
records.

This is worth ticking even if your labels already carry `^MMC`, because it means
the cut no longer depends on which label happens to be last in the order. The
commands are the same ones Rock sends during check-in — the label's trailing
`^XZ` becomes `^MMC^XZ` to cut, or `^XB^XZ` to suppress the backfeed and the cut
with it. They are appended, so a template's own `^MMT` or `^MMC` does not have to
be removed; ZPL takes the last command it is given.

Leave it unticked and nothing is added, so a label that cuts by itself still
behaves exactly as it did.

### Capturing a label from Rock

Rock will not hand out the ZPL for a label designed in its own designer. But the
proxy sits in the middle of every print, so a label Rock prints arrives as raw
ZPL whatever it was authored as — and **Capture from Rock** catches one by being
an ordinary printer.

**This is for a label you designed *as a blank*** — with lines to write on and a
placeholder where the code goes. A normal check-in label will not do. Designed
labels write text straight into fields and carry nothing to write on, because
nothing is ever hand-written on them, so blanking one leaves empty space with no
indication of what goes where.

1. On the **Blank Labels** tab, press **Capture from Rock**, then **Wait for a
   label**. It listens on port 9100 by default.
2. In Rock, set a printer's address to this machine and print one label to it. A
   check-in label's test print does it. No port is needed — an address without
   one means 9100.
3. Pick which fields hold the security code, name the label, and save.

**Nothing here needs to be connected to Rock.** Whichever proxy Rock already
sends that label to is the one that opens the connection, so the address only has
to be reachable *from that proxy* — it is an ordinary printer address as far as
it is concerned. It can be a different machine entirely. If the proxy you are
capturing with is itself the one Rock routes to, use `127.0.0.1:9100` and nothing
crosses a network at all.

It listens only while armed and stops after one label, so arming it and
forgetting cannot quietly record a real child's label during a service. A
connection that sends nothing — a reachability check, a port scan, the proxy's
own printer test — is ignored and it keeps waiting.

#### The security code field is usually empty

Expect this, because it looks like something has gone wrong and has not.

Rock's stored ZPL uses a token such as `WWW` where the security code goes, but by
the time a label is *printed* Rock has already substituted it. A test print has
no attendance behind it, so it substitutes to **nothing** — and the code field
arrives empty.

That is why fields are chosen by position rather than by text, and why the list
shows the height of each field's font. The security code is the one thing on a
check-in label printed large, so on a real label it stands out from the captions
by a factor of three or four. The tallest fields are ticked for you.

Pick more than one where the label needs it — a receipt torn in half carries the
code on both halves.

### Preview

The preview is drawn by [Labelary](https://labelary.com/), the same service
Rock's own label designer uses, so **this one button needs internet access.
Printing does not.** If there is no internet the preview reports that and
everything else carries on working.

The proxy fetches the image itself and sends it to your browser, so the browser
never contacts a third party.

### Files it writes

| Path | What it is |
|---|---|
| `config/labels/*.zpl` | The stored templates |
| `config/blank-labels.json` | Where sequential numbering has reached, and the last ten runs |

Both are inside `config`, so whatever backs that up already covers them.

---

## Web UI reference

| Page | What it shows |
|---|---|
| **Dashboard** | Connection status (green/amber/grey), start time, time connected, labels requested since start, and a Print Results panel: labels printed, labels failed, prints that finished too slowly, and the reason for the most recent failure |
| **Logs** | Live service log stream, color-coded by level |
| **Printers** | Test whether a printer can be reached, using the same connection a print uses. Nothing is printed |
| **Blank Labels** | Store ZPL templates and print pre-coded blank check-in labels. Works with Rock unreachable — see above |
| **Settings → Failure Notifications** | Tell Rock when a printer fails. Requires Rock-side setup first — see above |
| **Settings → Connection** | Rock server URL, Proxy ID, Proxy Name — saves to `config/appsettings.json` |
| **Settings → Security** | Set, change, or remove the web UI PIN |

The running version is shown beside the title in the header.

The dashboard auto-refreshes every 2 seconds. The log panel refreshes every 3
seconds when visible, fetching only entries it has not already shown.

### Reading the Print Results panel

**Labels Requested** counts every label the Rock server asked for, whether or not
it reached a printer. **Printed** and **Failed** split that total by what actually
happened.

**Too Slow** counts prints that finished after the Rock server stopped waiting.
Rock's check-in kiosk allows five seconds for a print and then shows the operator
its own timeout message, so anything slower than that finished too late for the
operator to see the real result — even when the labels printed correctly a moment
later. Adjust the threshold with `SlowPrintMilliseconds` (default `5000`, set to
`0` to disable the check).

When a print fails, the reason shown is the exact message the proxy returned to
Rock, so it matches what appeared on the check-in screen.

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

> **No third-party CDN at runtime.** Tailwind CSS is compiled into the image at build
> time rather than fetched from a CDN, so the admin UI does not depend on an external
> host being reachable, loads faster, and makes no third-party requests.

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

## Portainer deployment

Portainer can deploy this image as a **Stack** — its equivalent of a Compose file. No local clone or build is needed; the image is pulled from Docker Hub.

### 1. Create the stack

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

Click **Deploy the stack**.

> **Why a named volume?** The container runs as UID 1000 (`appuser`). A fresh named volume inherits that ownership from the image and stays writable. A bind mount to a host directory that Docker auto-creates comes up owned by `root`, and saving settings from the web UI will fail.
>
> To use a bind mount anyway (easier to back up), create it with the right owner first:
> ```bash
> sudo mkdir -p /opt/rock-cloudprint/config
> sudo chown -R 1000:1000 /opt/rock-cloudprint/config
> ```
> then replace the volume line with `- /opt/rock-cloudprint/config:/app/config` and delete the top-level `volumes:` block. Use an absolute path — `./config` does not resolve predictably in Portainer web-editor stacks.

### 2. Open the firewall

If the host runs `ufw`:

```bash
sudo ufw allow 8080/tcp
```

### 3. Configure

Open `http://<server-ip>:8080`, go to **Settings**, enter your Rock server URL and Proxy ID, and click **Save & Reconnect**. The Dashboard should show a green "Connected" status within a few seconds.

Set a PIN under **Settings** as well — anyone who can reach port 8080 can read and change every setting.

### Networking

The stack above uses port mapping rather than `network_mode: host`. Outbound TCP to LAN printers on port 9100 works fine from bridge networking, so host mode is not required.

Switch to `network_mode: host` only if your Docker bridge subnet (`172.17.0.0/16` by default) overlaps your LAN. If you do, remove the `ports:` block — Compose rejects a service that declares both.

### Updating

Because the image is tagged `latest`, Portainer will not re-pull it on its own. Go to **Stacks → rock-cloudprint → Editor → Update the stack** and tick **Re-pull image**. Settings in the volume are unaffected.

To pin a specific build instead, resolve the digest:

```bash
docker buildx imagetools inspect asdfinit/rock-cloudprint:latest
```

and use `image: asdfinit/rock-cloudprint@sha256:<digest>` in the stack.

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

> **Portainer:** use **Stacks → rock-cloudprint → Editor → Update the stack** with **Re-pull image** ticked instead — see [Portainer deployment](#portainer-deployment).

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
- Open the **Printers** tab and test the address — it opens the same connection
  a print does, without printing anything
- Make sure the printer is on and on the same network as this server
- If the test fails, work through *Diagnosing printer connectivity* below

### Diagnosing printer connectivity

Run these **inside the container**, which is what matters — the host being able
to reach a printer does not guarantee the container can:

```bash
docker exec -it rock-cloudprint-rock-cloudprint-1 bash
```

| # | Command | What it tells you |
|---|---|---|
| 1 | `ip route get <printer-ip>` | Whether the printer is routed out through a gateway (`via …` appears) or wrongly treated as local (no `via`) |
| 2 | `ip -brief addr` | What address and subnet the container actually has |
| 3 | `ping -c3 <printer-ip>` | Whether the printer answers at all, and whether ARP resolves |
| 4 | `timeout 2 bash -c 'exec 3<>/dev/tcp/<printer-ip>/9100'` | Whether the printing port is open. This is the same thing a print does — exit code 0 means open |
| 5 | `ip neigh` | What ARP resolved the address to |

**Steps 1 and 2 only matter on a bridged deployment** such as TrueNAS. With
`network_mode: host` the container shares the host's network stack, so they tell
you nothing the host would not.

**The missing `via` in step 1 is the one to look for.** If the printer's address
falls inside the container's own subnet, the container treats it as a neighbour
and the traffic never leaves — while the host looks perfectly healthy. The fix is
to move the container's network off the range the printers use.

Step 4 uses a feature built into `bash` rather than `nc`, which is not installed.
The `nc -zv 192.168.1.50 9100` form works on the **host**, not in the container.

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
| `Rock.CloudPrint.Service/CloudPrintOptions.cs` | Added `Password` property for PIN/password protection, and `SlowPrintMilliseconds` for the slow-print threshold |
| `Rock.CloudPrint.Service/PrintMetrics.cs` | New — records the outcome of each print attempt: labels printed, labels failed, prints that outran the server, and the reason for the most recent failure |
| `Rock.CloudPrint.Service/PrinterAddress.cs` | New — printer address parsing, lifted out of the print path so the web UI's test runs the same code rather than a copy of it |
| `Rock.CloudPrint.Service/PrinterTester.cs` | New — opens a connection to a printer and reports the result, without printing |
| `Rock.CloudPrint.Service/FailureNotifier.cs` | New — reports print failures to a Rock webhook, with per-printer debouncing, and remembers the last attempt so the dashboard can name the fault |
| `Rock.CloudPrint.Service/ProxyClientWebSocket.cs` | Successful prints log at `Information` rather than `Debug`, each attempt is timed and recorded in `PrintMetrics`, and address parsing moved to `PrinterAddress` |
| `Rock.CloudPrint.Service/ProxyWorker.cs` | Passes `PrintMetrics` and the slow-print threshold to the proxy connection |
| `Rock.CloudPrint.Service/AuthService.cs` | New — in-memory bearer token manager for web UI authentication |
| `Rock.CloudPrint.Service/InMemoryLogSink.cs` | New — circular log buffer (2,000 entries) for the Logs panel. In memory only: cleared on restart. Entries carry a sequence number so the UI can fetch only what is new |
| `Rock.CloudPrint.Service/InMemoryLoggerProvider.cs` | New — `ILoggerProvider` that captures `Rock.CloudPrint.*` log entries only |
| `Rock.CloudPrint.Service/appsettings.json` | Removed EventLog config; added `Urls: http://+:8080` and default empty keys |
| `Rock.CloudPrint.Service/wwwroot/index.html` | New — single-page web UI (Dashboard, Logs, Settings with Security panel) |
| `Rock.CloudPrint.Shared/Rock.CloudPrint.Shared.csproj` | Bumped `System.Text.Json` from `8.0.4` to `8.0.5` (CVE GHSA-8g4q-xg66-9fp4) |
| `Rock.CloudPrint.Service/wwwroot/index.html` | Tailwind is now loaded from the bundled `/app.css` instead of `cdn.tailwindcss.com`; the inline `<style>` block moved into `build/src/app.css` |
| `package.json`, `build/tailwind.config.js`, `build/src/app.css` | New — Tailwind build tooling. `npm run css` compiles the stylesheet |
| `Dockerfile` | New — multi-stage Linux build; installs `iputils-ping` and `iproute2` for in-container network diagnostics; pre-creates `/app/config` directory; a Node stage compiles the Tailwind stylesheet so it can never drift from `index.html`; takes a `VERSION` build argument so the version the UI reports comes from the release tag |
| `docker-compose.yml` | New — host networking, `./config:/app/config` directory mount, `Password` env var option |
| `config/appsettings.json` | New — persistent settings file (lives in host `config/` directory, mounted into container) |

---

## License

This project is based on [Rock RMS](https://github.com/SparkDevNetwork/Rock) and is licensed under the [Rock Community License](http://www.rockrms.com/license).
