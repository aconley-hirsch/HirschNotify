# Push Notification System — Relay + Mobile App Spec

## 1. Overview

A cloud-hosted relay service and branded mobile app that enable on-prem Event Alert Service instances (behind firewalls) to deliver push notifications to users' phones. On-prem instances push outbound to the relay; the relay forwards to devices via APNs/FCM using Gorush as the push delivery engine.

```
┌──────────────────┐          ┌──────────────────────┐          ┌───────────┐          ┌──────────────┐
│   On-Prem        │ outbound │    Cloud Relay        │          │  Gorush   │  APNs /  │  Mobile App  │
│   Event Alert    │─────────>│    Service            │─────────>│  (push    │───FCM───>│  (branded)   │
│   Service        │  HTTPS   │                       │  HTTP    │  gateway) │          │              │
│                  │          │  - Device registration │          │           │          │  - Pair via  │
│  - Fires events  │          │  - Notification route  │          │  - APNs   │          │    code      │
│  - Matches rules │          │  - Instance mgmt      │          │  - FCM    │          │  - Multi-    │
│  - POSTs to relay│          │  - E2E encryption     │          │  - Retry  │          │    instance  │
└──────────────────┘          └──────────────────────┘          └───────────┘          └──────────────┘
                                        ↑
                                Mobile app registers
                                here with pairing code
```

### What You Control

- Cloud relay code and hosting
- Gorush configuration and deployment
- Mobile app code and app store distribution
- On-prem Event Alert Service (already built)
- Pairing and device registration system
- All notification content and routing

### External Dependencies (unavoidable)

- **Apple Push Notification Service (APNs)** — delivers iOS notifications
- **Firebase Cloud Messaging (FCM)** — delivers Android notifications
- **Gorush** — open-source push notification gateway (Go, MIT license, 8,700+ stars)
- **Apple Developer Account** ($99/year) — required for APNs and iOS App Store
- **Google Play Developer Account** ($25 one-time) — required for Play Store
- **Cloud hosting** for the relay + Gorush (small VPS, Azure, AWS, etc.)

---

## 2. System Components

### 2.1 Cloud Relay Service

A lightweight API that:
- Registers on-prem instances and issues API keys
- Manages device-to-instance pairings
- Receives notification payloads from on-prem instances
- Routes notifications to Gorush for APNs/FCM delivery
- Provides endpoints for the mobile app to register devices and manage pairings

### 2.2 Gorush (Push Delivery Gateway)

An open-source push notification gateway (github.com/appleboy/gorush) that:
- Handles all direct communication with APNs (HTTP/2) and FCM
- Provides a simple REST API: `POST /api/push` with device tokens and payload
- Manages connection pooling, retry logic, and error handling for APNs/FCM
- Exposes Prometheus metrics for monitoring push delivery health
- Runs as a stateless Go binary alongside the relay service

The relay service is the only caller of Gorush. Gorush is not exposed to the internet — it listens on localhost or an internal network only.

**Why Gorush instead of direct APNs/FCM integration:**
- Avoids building and maintaining APNs HTTP/2 connection management, certificate rotation, and FCM token handling
- Mature, battle-tested delivery engine (8,700+ stars, actively maintained)
- Standardized error responses for invalid tokens, rate limits, and failures
- Also supports Huawei HMS if needed in the future
- Stateless — easy to restart, upgrade, or scale without data loss concerns

### 2.3 Mobile App

A branded mobile app that:
- Registers for push notifications on the device (APNs/FCM)
- Pairs to one or more on-prem instances using a pairing code
- Displays incoming notifications
- Maintains a local notification history
- Allows managing paired instances (add, remove, rename)

### 2.4 On-Prem Service Changes

The existing Event Alert Service gains a new notification channel:
- A `RelaySender` that POSTs matched event notifications to the cloud relay
- Configuration for the relay URL and instance API key
- Pairing code generation and display in the admin UI

---

## 3. End-to-End Encryption (Optional, Per-Instance)

End-to-end encryption is configurable per on-prem instance. When enabled, notification content is encrypted on the on-prem instance and can only be decrypted by the paired mobile device. The relay never sees plaintext content. When disabled, notifications are sent in plaintext through the relay (simpler, more reliable push delivery).

**On-prem setting:** "Enable End-to-End Encryption" toggle in the Push Relay settings section. Defaults to **enabled**.

**Behavior difference:**

| | E2E Enabled | E2E Disabled |
|---|---|---|
| Relay can read content | No | Yes |
| APNs/FCM can read content | No | Yes (title + body in push payload) |
| Push delivery | Silent/data-only (slightly less reliable on iOS) | Standard visible push (most reliable) |
| Device key pair | Required | Not required |
| Mobile app decryption | Required | Not needed |

The mobile app must handle both modes — it checks whether the incoming payload is encrypted or plaintext and acts accordingly.

### 3.1 How It Works

```
Pairing (key exchange):
1. Mobile app generates an asymmetric key pair (e.g., X25519 or RSA-2048)
2. Private key stays on the device (never leaves)
3. Public key is sent to the relay during pairing
4. On-prem instance fetches paired devices' public keys from the relay

Sending:
1. On-prem builds the notification message (title + body)
2. For each paired device, encrypts the payload with that device's public key
3. POSTs the encrypted blobs to the relay
4. Relay forwards them to devices via silent/data-only push
5. Mobile app receives the encrypted blob, decrypts with its private key
6. App displays a local notification with the decrypted content
```

### 3.2 What Each Party Can See

| Party | Can See |
|---|---|
| **On-prem instance** | Everything (it originates the notification) |
| **Cloud relay** | Which instance sent to which devices, payload size, timestamps. Cannot see notification content. |
| **APNs / FCM** | That a data push was sent to a device. Cannot see notification content (it's encrypted in the data payload). |
| **Mobile app** | Everything (it holds the private key) |

### 3.3 Key Management

- Private keys are stored in the device's secure enclave (iOS Keychain / Android Keystore)
- If a user reinstalls the app, a new key pair is generated and the device must re-pair
- If an admin revokes a device, the on-prem instance stops fetching that device's public key
- On-prem instance should cache public keys and refresh periodically (e.g., every 5 minutes via the relay API)

### 3.4 Silent Push Trade-offs

Since content is encrypted, standard visible push notifications can't be used (APNs/FCM would need the plaintext title/body). Instead, silent/data-only pushes are used to wake the app, which then decrypts and posts a local notification.

- **iOS**: Silent pushes can be throttled by the OS if the app isn't opened regularly. For an actively used security app, this is unlikely to be a problem. As a fallback, a generic visible push ("New alert from Terminal A") can be sent alongside the encrypted data push.
- **Android**: Data-only FCM messages are reliably delivered and can display notifications even from the background.

---

## 4. Pairing Flow

Pairing connects a user's mobile device to a specific on-prem instance. The on-prem instance is behind a firewall, so the relay acts as a rendezvous point. The pairing process also performs the key exchange for end-to-end encryption.

### 4.1 Instance Registration (one-time setup)

```
1. Admin installs Event Alert Service on-prem
2. Admin enters the relay URL in Settings (e.g., https://relay.yourdomain.com)
3. On-prem service calls POST /api/instances/register
4. Relay responds with:
   - instanceId (UUID)
   - apiKey (random secret for authenticating future requests)
5. On-prem service stores instanceId and apiKey in its encrypted settings
```

### 4.2 Device Pairing (with key exchange)

```
1. Admin opens the Event Alert Service admin UI
2. Clicks "Generate Pairing Code" on a new Pairing page
3. On-prem service calls POST /api/instances/{instanceId}/pairing-codes
   - Relay generates a short-lived code (e.g., "ABCD-1234", expires in 10 minutes)
4. Admin shares the code with the user (shows on screen, or prints/emails it)
5. User opens the mobile app
6. App generates an asymmetric key pair (public + private)
7. User taps "Add Instance" and enters the pairing code
8. Mobile app calls POST /api/devices/pair with:
   - pairingCode
   - deviceToken (APNs or FCM token)
   - platform ("ios" or "android")
   - publicKey (the device's public encryption key)
9. Relay validates the code, stores the public key, creates the device-instance link
10. Mobile app receives the instance name and confirms pairing
11. Private key is stored in the device's secure enclave
```

### 4.3 Pairing Code Properties

- 8 characters, alphanumeric, uppercase (e.g., `ABCD-1234`)
- Expires after 10 minutes
- Single use — consumed on successful pairing
- Admin can generate multiple codes for multiple users
- Admin can revoke pairings from the admin UI

---

## 5. Notification Flow

### 5.1 With E2E Encryption (enabled)

```
1. On-prem Event Alert Service receives a WebSocket event
2. Filter engine matches the event to a rule
3. For recipients configured with "Push" notification channel:
   a. Build the notification message (using the rule's message template)
   b. Fetch paired devices' public keys from relay (cached, refreshed every 5 min):
      GET /api/instances/{instanceId}/devices
   c. For each device, encrypt the message payload with the device's public key
   d. POST to relay: POST /api/instances/{instanceId}/notifications
      Body: {
        "encrypted": true,
        "notifications": [
          { "deviceId": "...", "encryptedPayload": "base64..." },
          { "deviceId": "...", "encryptedPayload": "base64..." }
        ]
      }
      Header: Authorization: Bearer {apiKey}
4. Relay POSTs to Gorush (`POST /api/push`) for each device with:
   - Device token, platform, and the encrypted payload as a data-only push
5. Mobile app receives the push, decrypts with its private key
6. App displays a local notification with the decrypted title and body
7. App stores the decrypted notification in local history
```

### 5.2 Without E2E Encryption (disabled)

```
1. On-prem Event Alert Service receives a WebSocket event
2. Filter engine matches the event to a rule
3. For recipients configured with "Push" notification channel:
   a. Build the notification message (using the rule's message template)
   b. POST to relay: POST /api/instances/{instanceId}/notifications
      Body: {
        "encrypted": false,
        "title": "Forced Door Alert",
        "body": "Forced entry at input Front Door..."
      }
      Header: Authorization: Bearer {apiKey}
4. Relay POSTs to Gorush (`POST /api/push`) for each paired device with:
   - Device token, platform, and the title/body as a standard visible push
5. Device displays the notification natively (no app decryption needed)
6. When the app is opened, it stores the notification in local history
```

---

## 6. Cloud Relay API

### 6.1 Instance Endpoints

**POST /api/instances/register**
Register a new on-prem instance.
```
Request:
{
  "name": "Airport Terminal A",         // Human-readable instance name
  "version": "1.0.0"                    // On-prem service version (for diagnostics)
}

Response:
{
  "instanceId": "550e8400-e29b-41d4-a716-446655440000",
  "apiKey": "sk_live_abc123..."
}
```

**POST /api/instances/{instanceId}/heartbeat**
Periodic health check from on-prem instance (every 60 seconds).
```
Header: Authorization: Bearer {apiKey}

Request:
{
  "status": "connected",               // WebSocket connection status
  "eventsToday": 1234,
  "alertsToday": 5
}

Response: 200 OK
```

**POST /api/instances/{instanceId}/pairing-codes**
Generate a pairing code for device enrollment.
```
Header: Authorization: Bearer {apiKey}

Request:
{
  "label": "Dave's iPhone"              // Optional label for the admin UI
}

Response:
{
  "code": "ABCD-1234",
  "expiresAt": "2026-03-31T19:10:00Z"
}
```

**GET /api/instances/{instanceId}/devices**
List all paired devices. Includes public keys so the on-prem instance can encrypt notifications per-device.
```
Header: Authorization: Bearer {apiKey}

Response:
{
  "devices": [
    {
      "deviceId": "...",
      "label": "Dave's iPhone",
      "platform": "ios",
      "publicKey": "base64-encoded-public-key...",
      "pairedAt": "2026-03-30T14:00:00Z",
      "lastSeen": "2026-03-31T18:00:00Z"
    }
  ]
}
```

**DELETE /api/instances/{instanceId}/devices/{deviceId}**
Revoke a device pairing.
```
Header: Authorization: Bearer {apiKey}
Response: 204 No Content
```

### 6.2 Notification Endpoint

**POST /api/instances/{instanceId}/notifications**
Send push notifications to paired devices. Supports both encrypted and plaintext modes.
```
Header: Authorization: Bearer {apiKey}
```

**Encrypted mode** (`encrypted: true`): Each device gets its own encrypted payload. The relay cannot read the content.
```
Request:
{
  "encrypted": true,
  "notifications": [
    {
      "deviceId": "device-uuid-1",
      "encryptedPayload": "base64-encoded-ciphertext..."
    },
    {
      "deviceId": "device-uuid-2",
      "encryptedPayload": "base64-encoded-ciphertext..."
    }
  ]
}
```

**Plaintext mode** (`encrypted: false`): Relay sends a standard visible push to all paired devices.
```
Request:
{
  "encrypted": false,
  "title": "Forced Door Alert",
  "body": "Forced entry at input Front Door\nType: ActiveAlarm\nTime: 3/31/2026 6:41 PM",
  "data": {
    "eventId": "5001",
    "ruleName": "Forced Door",
    "eventType": "ActiveAlarm"
  }
}
```

**Response (both modes):**
```
{
  "sent": 2,
  "failed": 0
}
```

The plaintext payload structure (used in plaintext mode directly, or as the pre-encryption format in encrypted mode):
```
{
  "title": "Forced Door Alert",
  "body": "Forced entry at input Front Door\nType: ActiveAlarm\nTime: 3/31/2026 6:41 PM",
  "data": {
    "eventId": "5001",
    "ruleName": "Forced Door",
    "eventType": "ActiveAlarm"
  }
}
```

### 6.3 Device Endpoints (called by mobile app)

**POST /api/devices/pair**
Pair a device to an instance using a pairing code. The app always generates a key pair and sends the public key — the on-prem instance decides whether to use it based on its E2E encryption setting.
```
Request:
{
  "pairingCode": "ABCD-1234",
  "deviceToken": "abc123...",           // APNs or FCM device token
  "platform": "ios",                    // "ios" or "android"
  "publicKey": "base64-encoded-public-key...",  // Always sent; used if instance has E2E enabled
  "appVersion": "1.0.0"
}

Response:
{
  "deviceId": "...",
  "instanceId": "...",
  "instanceName": "Airport Terminal A"
}
```

**PUT /api/devices/{deviceId}/token**
Update device token (tokens rotate periodically).
```
Request:
{
  "deviceToken": "newtoken123..."
}

Response: 200 OK
```

**GET /api/devices/{deviceId}/instances**
List all instances this device is paired to.
```
Response:
{
  "instances": [
    {
      "instanceId": "...",
      "name": "Airport Terminal A",
      "status": "connected",
      "pairedAt": "2026-03-30T14:00:00Z"
    }
  ]
}
```

**DELETE /api/devices/{deviceId}/instances/{instanceId}**
Unpair this device from an instance.
```
Response: 204 No Content
```

---

## 7. Cloud Relay Data Model

### `Instances`

| Column | Type | Notes |
|---|---|---|
| Id | UUID (PK) | |
| Name | varchar(200) | Human-readable name |
| ApiKeyHash | varchar(256) | Hashed API key (store hash, not plaintext) |
| Version | varchar(20) | On-prem service version |
| Status | varchar(20) | Last reported status |
| EventsToday | int | From heartbeat |
| AlertsToday | int | From heartbeat |
| LastHeartbeat | timestamp | Last heartbeat time |
| CreatedAt | timestamp | |

### `Devices`

| Column | Type | Notes |
|---|---|---|
| Id | UUID (PK) | |
| DeviceToken | varchar(500) | APNs or FCM token (encrypted at rest) |
| Platform | varchar(10) | "ios" or "android" |
| PublicKey | text | Device's public encryption key (base64) |
| AppVersion | varchar(20) | |
| LastSeen | timestamp | Last API call from this device |
| CreatedAt | timestamp | |

### `DeviceInstancePairings`

| Column | Type | Notes |
|---|---|---|
| DeviceId | UUID (FK) | |
| InstanceId | UUID (FK) | |
| Label | varchar(100) | Optional label set during pairing |
| PairedAt | timestamp | |
| (composite PK) | | |

### `PairingCodes`

| Column | Type | Notes |
|---|---|---|
| Id | int (PK) | Auto-increment |
| InstanceId | UUID (FK) | |
| Code | varchar(10) | The pairing code |
| Label | varchar(100) | Optional label |
| ExpiresAt | timestamp | |
| ConsumedAt | timestamp | Null until used |
| ConsumedByDeviceId | UUID (FK, nullable) | |

### `NotificationLog` (metrics only — no content stored)

| Column | Type | Notes |
|---|---|---|
| Id | bigint (PK) | Auto-increment |
| InstanceId | UUID (FK) | |
| DevicesSent | int | |
| DevicesFailed | int | |
| PayloadSizeBytes | int | Size of encrypted payload (for monitoring) |
| CreatedAt | timestamp | |

---

## 8. On-Prem Service Changes

### 8.1 New Settings

Add to the Settings page under a new "Push Relay" section:

| Setting | Example | Description |
|---|---|---|
| `Relay:Url` | `https://relay.yourdomain.com` | Cloud relay base URL |
| `Relay:InstanceId` | `550e8400-...` | Assigned during registration |
| `Relay:ApiKey` | `sk_live_abc123...` (encrypted) | Assigned during registration |
| `Relay:HeartbeatIntervalSec` | `60` | Heartbeat frequency |
| `Relay:E2EEncryption` | `true` | Enable end-to-end encryption (default: true) |

### 8.2 New Notification Channel

Add "Push" as a `NotifyVia` option for recipients (alongside SMS, Pushover, Both).

When "Push" is selected, the `NotificationSender` calls a new `RelaySender` service that:
1. Fetches paired devices and their public keys from the relay (cached, refreshed every 5 min)
2. Builds the notification payload (title from rule name, body from rendered message template)
3. Encrypts the payload separately for each device using its public key
4. POSTs the encrypted payloads to `{Relay:Url}/api/instances/{instanceId}/notifications`
5. Authenticates with `Authorization: Bearer {apiKey}`

### 8.3 New Admin UI: Pairing Page (`/Pairing`)

```
┌─────────────────────────────────────────────────────────────┐
│  Device Pairing                                              │
├─────────────────────────────────────────────────────────────┤
│                                                              │
│  Instance: Airport Terminal A                                │
│  Status: Registered                                          │
│                                                              │
│  ── Generate Pairing Code ────────────────────────────────  │
│  Label (optional): [Dave's iPhone          ]                 │
│  [Generate Code]                                             │
│                                                              │
│  ┌──────────────────────────────┐                           │
│  │     Code: ABCD-1234          │                           │
│  │     Expires in: 9:42         │                           │
│  │     [QR Code]                │                           │
│  └──────────────────────────────┘                           │
│                                                              │
│  ── Paired Devices ───────────────────────────────────────  │
│                                                              │
│  Name              Platform   Paired        Last Seen        │
│  Dave's iPhone     iOS        3/30/2026     2 min ago  [X]  │
│  Ops Android       Android    3/28/2026     5 min ago  [X]  │
│                                                              │
└─────────────────────────────────────────────────────────────┘
```

### 8.4 Registration Flow in Settings

Add a "Register with Relay" button to the Settings page:
- Admin enters the relay URL
- Clicks "Register"
- On-prem service calls `POST /api/instances/register`
- Stores the returned `instanceId` and `apiKey`
- Displays "Registered" status

### 8.5 Heartbeat Worker

A new `RelayHeartbeatWorker` (BackgroundService) that:
- Every 60 seconds, POSTs to `/api/instances/{instanceId}/heartbeat`
- Sends current connection status, event count, alert count
- Allows the relay (and mobile app) to show instance health

---

## 9. Mobile App

### 9.1 Screens

**Instances List (Home)**
- List of paired instances with name, status, and last notification time
- "Add Instance" button
- Tap an instance to see its notification history

**Add Instance**
- Text field for pairing code (or camera for QR scan)
- "Pair" button
- On success, shows instance name and confirms

**Instance Detail**
- Instance name and connection status (from relay heartbeat)
- Notification history (stored locally on device)
- "Unpair" button

**Notification Detail**
- Full notification title, body, timestamp
- Structured data fields if available

**Settings**
- Notification preferences (sound, vibration)
- About / version

### 9.2 Push Notification Behavior

- **Foreground**: Show an in-app banner
- **Background/Locked**: Standard OS notification with title and body
- Tapping the notification opens the app to the relevant instance's notification list
- Badge count reflects unread notifications

### 9.3 Device Token Management

- On app launch, register for push notifications
- Send device token to relay via `POST /api/devices/pair` (during pairing) or `PUT /api/devices/{deviceId}/token` (on token refresh)
- APNs/FCM tokens can change — the app must detect changes and update the relay

### 9.4 Local Storage

- Paired instances (instanceId, name, deviceId)
- Notification history (last 500 notifications per instance)
- Read/unread state

### 9.5 Tech Stack Options

| Framework | Pros | Cons |
|---|---|---|
| **React Native (Expo)** | Fastest dev, best push notification DX, OTA updates | JS |
| **.NET MAUI** | Same language as backend | Slower dev, less push notification tooling |
| **Flutter** | Great performance, single codebase | Dart |

**Recommendation: Expo (React Native)** — Expo's push notification infrastructure is optional (you'd use APNs/FCM directly), but their `expo-notifications` library makes token management and notification handling significantly easier. OTA updates mean you can push app fixes without app store review.

---

## 10. Cloud Relay Infrastructure

### 10.1 Hosting Requirements

The relay is lightweight — it routes notifications and delegates push delivery to Gorush:
- Receives a POST from on-prem (~1 KB payload)
- Looks up paired devices (DB query)
- Forwards to Gorush on localhost for APNs/FCM delivery

**Minimal infrastructure:**
- A single small VPS or container (1 vCPU, 1 GB RAM)
- Two processes: relay service + Gorush (Go binary, ~15 MB)
- PostgreSQL
- TLS certificate (Let's Encrypt)

**Deployment:**
```
docker-compose.yml
├── relay          # Your custom relay service (exposed to internet, port 443)
├── gorush         # Push gateway (internal only, port 8088, not exposed)
└── postgres       # Database (internal only)
```

Gorush is configured via `gorush.yml`:
```yaml
core:
  port: "8088"
  address: "127.0.0.1"    # Internal only — not exposed to internet

ios:
  enabled: true
  key_path: "/certs/apns-auth-key.p8"
  key_id: "ABC123"
  team_id: "DEF456"

android:
  enabled: true
  apikey: "your-fcm-server-key"
```

The relay calls Gorush via:
```
POST http://localhost:8088/api/push
{
  "notifications": [
    {
      "tokens": ["device-token-abc"],
      "platform": 1,                    // 1=iOS, 2=Android
      "message": "New Alert",           // Visible push title (plaintext mode)
      "data": {                         // Encrypted payload (E2E mode)
        "encryptedPayload": "base64..."
      },
      "mutable_content": true,          // Triggers iOS NSE
      "content_available": true
    }
  ]
}
```

Gorush returns per-device delivery results:
```json
{
  "success": "ok",
  "counts": 1,
  "logs": [
    { "type": "success", "platform": "ios", "token": "device-token-abc" }
  ]
}
```

The relay parses these results to detect invalid tokens (`type: "failed"`) and mark stale devices.

**Scaling path (if needed later):**
- Horizontal scaling: run multiple relay instances behind a load balancer, all pointing to the same Gorush + PostgreSQL
- Multiple Gorush instances for higher push throughput
- Redis for caching device lookups

### 10.2 Estimated Costs

| Item | Cost |
|---|---|
| VPS (DigitalOcean/Hetzner) | $5-10/month |
| Domain + TLS | $10/year (domain), free (Let's Encrypt) |
| Apple Developer Account | $99/year |
| Google Play Developer Account | $25 one-time |
| Gorush | Free (MIT open source) |
| FCM | Free |
| APNs | Free (included with dev account) |
| **Total year 1** | **~$250** |
| **Total ongoing** | **~$170/year** |

### 10.3 Security Considerations

- **End-to-end encryption**: Notification content is encrypted on-prem with each device's public key. The relay, Gorush, and APNs/FCM only see ciphertext. See Section 3.
- **Gorush isolation**: Gorush listens on localhost only (127.0.0.1). It is never exposed to the internet. Only the relay service communicates with it.
- **Instance API keys**: Hashed in the database (like passwords). Transmitted over TLS only.
- **Pairing codes**: Short-lived (10 min), single-use, not guessable (alphanumeric, 8 chars = ~2.8 billion combinations).
- **Device tokens**: Stored encrypted at rest. Only used server-side to send pushes.
- **Device private keys**: Stored in the device's secure enclave (iOS Keychain / Android Keystore). Never transmitted.
- **Notification log**: The relay stores only metrics (count, size, timestamp). No notification content is ever stored on the relay.
- **Rate limiting**: The relay should rate-limit per instance to prevent abuse.
- **TLS everywhere**: All relay endpoints require HTTPS. On-prem instances must validate the relay's TLS certificate.

---

## 11. Relay Admin Dashboard

A metrics-only web UI for the relay operator. No notification content is visible — only operational metrics.

### 11.1 Dashboard Home

```
┌─────────────────────────────────────────────────────────────┐
│  Relay Admin                                                 │
├─────────────────────────────────────────────────────────────┤
│                                                              │
│  Registered Instances: 12                                    │
│  Total Paired Devices: 47                                    │
│  Notifications Today:  1,284                                 │
│  Notifications This Week: 8,921                              │
│                                                              │
│  ── Instance Health ──────────────────────────────────────  │
│                                                              │
│  Name                 Status      Devices  Last Heartbeat    │
│  Airport Terminal A   Connected   8        2 min ago         │
│  Airport Terminal B   Connected   5        1 min ago         │
│  Warehouse East       Disconnected 3       47 min ago  [!]  │
│  Office HQ            Connected   4        30 sec ago        │
│  ...                                                         │
│                                                              │
└─────────────────────────────────────────────────────────────┘
```

### 11.2 Instance Detail

```
┌─────────────────────────────────────────────────────────────┐
│  Airport Terminal A                                          │
├─────────────────────────────────────────────────────────────┤
│                                                              │
│  Status: Connected          Registered: 3/15/2026            │
│  Version: 1.0.0             Last Heartbeat: 2 min ago        │
│  Events Today: 342          Alerts Today: 12                 │
│                                                              │
│  ── Paired Devices ───────────────────────────────────────  │
│                                                              │
│  Label            Platform   Paired        Last Seen         │
│  Dave's iPhone    iOS        3/30/2026     5 min ago         │
│  Ops Android      Android    3/28/2026     12 min ago        │
│  Front Desk iPad  iOS        3/25/2026     1 hr ago          │
│                                                              │
│  ── Notification Volume (last 7 days) ────────────────────  │
│                                                              │
│  Mon: 1,204  Tue: 1,187  Wed: 1,342  Thu: 1,098             │
│  Fri: 1,455  Sat: 432    Sun: 203                            │
│                                                              │
└─────────────────────────────────────────────────────────────┘
```

### 11.3 What Is NOT Shown

- Notification content (title, body, data) — the relay never has access to this
- API keys — only hashes are stored
- Device tokens — shown only as masked identifiers
- Public keys — not displayed in the dashboard

### 11.4 Access Control

- Protected by its own admin login (separate from on-prem instances)
- Intended for the relay operator (you), not for customers
- Customers manage their own instances via the on-prem admin UI

---

## 12. Implementation Phases

### Phase 1: Cloud Relay MVP
1. Deploy Gorush with APNs certificate (.p8) and FCM server key
2. Build the relay API (instance registration, pairing, notification forwarding)
3. Wire relay to forward notifications to Gorush (`POST localhost:8088/api/push`)
4. Parse Gorush responses to detect delivery failures and stale tokens
5. Deploy relay + Gorush + PostgreSQL via Docker Compose with TLS
6. Add the heartbeat endpoint

### Phase 2: On-Prem Integration
6. Add `RelaySender` notification channel to the Event Alert Service
7. Add relay settings to the Settings page (URL, register button)
8. Add the Pairing page (generate codes, list/revoke devices)
9. Add the `RelayHeartbeatWorker`
10. Add "Push" as a `NotifyVia` option for recipients

### Phase 3: Mobile App
11. Scaffold the app (Expo/React Native)
12. Implement push notification registration (APNs/FCM token)
13. Build the pairing flow (enter code, confirm instance)
14. Build the instances list and notification history
15. Handle background/foreground notifications
16. Test on iOS and Android devices

### Phase 4: Polish and Distribution
17. App store submission (iOS App Store, Google Play)
18. QR code generation for pairing codes
19. Relay monitoring/alerting (uptime, error rates)
20. Instance health dashboard in the mobile app

---

## 13. Resolved Decisions

| Question | Decision | Rationale |
|---|---|---|
| **Notification content** | Full event details, end-to-end encrypted | E2E encryption means the relay never sees plaintext. Users get actionable details on their phone. |
| **Multi-tenant relay** | Yes, single relay serves all customers | Simpler to operate. Can scale horizontally later if needed. |
| **Offline queuing** | Rely on APNs/FCM built-in queuing | APNs/FCM handle this well enough. No relay-side queue needed for MVP. |
| **Notification grouping** | Individual notifications, throttling configured per rule | The on-prem service already has per-rule throttling. Each notification arrives individually. |
| **Relay admin dashboard** | Metrics-only dashboard, no notification content visible | See Section 11. Shows instance health, device counts, notification volume. |
