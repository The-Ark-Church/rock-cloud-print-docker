# Rock setup — print failure notifications

Print failure notifications need a workflow in Rock. This folder has one you can
import, so you don't have to build it from scratch.

The container posts a JSON payload to a Lava webhook; the webhook launches this
workflow; the workflow decides who is told and how. The proxy needs no change to
work with a different workflow — the contract is the payload, not the workflow.

The webhook itself is documented in the main [README](../../README.md#setting-it-up-in-rock),
including the template and the required Lava commands.

## What the workflow does

1. Resolves the printer's friendly name from `Device` by IP, falling back to the
   raw address.
2. Builds one message, sized for SMS, used unchanged on every channel.
3. Builds two recipient lists:
   - **Worker group** — every active member, every event, no schedule check. This
     is the guarantee that somebody is told on a Tuesday afternoon.
   - **Volunteer group** — only members scheduled *right now*, and only those not
     already in the worker list, so nobody is told twice.
4. Loops both lists through one activity that picks a channel per person: a
   push-capable device wins, otherwise the person's own communication preference.

## Importing it

Admin Tools → Power Tools → Workflow Import, and choose
`cloud-print-failure-notification.json`.

It imports **inert**: both group attributes and the schedule list are empty, so it
notifies nobody until you configure it.

## Then configure, in this order

| Setting | Where | Notes |
|---|---|---|
| **SMS From** | `(if sms) SMS Send` action | **Set this first — see the warning below** |
| Worker Group | workflow attribute | Start with a group containing only yourself |
| Volunteer Group | workflow attribute | Leave empty until the rest works |
| Schedules | workflow attribute | Which schedules count as a live service |
| Offset Minutes | workflow attribute | Defaults to 60 — how long before a service starts its volunteers become notifiable |
| Mobile Application | `(if push) Push Notification Send` action | Your Rock mobile app, if you have one. Safe to leave blank |
| Workflow type Guid | the webhook template | Copy it from the imported type into the template |

### ⚠️ The SMS From number is not optional

**If the SMS action has no From number, the entire workflow fails** — not just the
SMS step. Rock throws `Nullable object must have a value`, no workflow instance is
created, nobody is notified on any channel, nothing is written to the exception
log, and the webhook answers **HTTP 200 with a Lava error**.

Set a System Phone Number on the `(if sms) SMS Send` action before you use this,
or delete that action if you don't want SMS at all.

A blank **Mobile Application** on the push action and a blank **From** on the email
action are both fine — only SMS is fatal.

## Things that will cost you a day

These all fail *silently*, producing an empty value rather than an error.

1. **A webhook-launched workflow has no logged-in person, and Lava entity commands
   apply Rock's entity security**, so they return nothing. Add
   `securityenabled:'false'` to the command — the imported workflow already does
   this. You cannot catch this by testing through `/api/Lava/RenderTemplate`,
   which runs as your API user and succeeds either way.
2. **`Attribute:'Schedules'` returns the schedule NAMES, not guids.** Use
   `Attribute:'Schedules','RawValue'`.
3. **Nothing in Lava converts a UTC `Z`.** The message takes its time from `Now`,
   which Rock returns in your organisation's timezone, rather than from the
   payload's UTC timestamp. Don't "fix" this.
4. **`DeviceRegistrationId` cannot be filtered in `where:`** — `!= null` and
   `!= ''` both return the unnarrowed set, so a person with only stale devices
   looks push-capable. The channel step checks it inside the loop instead.
5. **Enum properties render as names but filter as integers** — `RSVP` displays
   `Yes` and filters as `1`. Never mix the two.

## Testing it

Use **Send test notification** in the proxy's Settings tab. It sends a real
request with `"event": "test"` and reports exactly what came back, so the URL, the
secret, the webhook, the workflow type and the activation are all proved at setup
time rather than during a service.

Point the worker group at a group containing only yourself first. If Rock is
configured correctly, pressing that button messages people.
