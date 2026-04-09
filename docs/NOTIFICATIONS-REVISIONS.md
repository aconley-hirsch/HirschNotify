# NOTIFICATIONS.md — Proposed Revisions

Based on independent review by claude-opus, codex-5.3-xhigh, and gemini-3-pro.

---

## 1. Authenticate Instance Registration

**Current:** `POST /api/instances/register` has no authentication. Anyone with the relay URL can register.

**Change:** Require a pre-shared registration token. When you onboard a customer, generate a one-time registration token in the relay admin dashboard. The customer enters it in their on-prem Settings page. The on-prem service sends it in the Authorization header during registration.

```
POST /api/instances/register
Header: Authorization: Bearer {registrationToken}

Request:
{
  "name": "Airport Terminal A",
  "version": "1.0.0"
}
```

The relay validates the token, marks it as consumed, and returns the instanceId + apiKey. Registration tokens are single-use and expire after 24 hours.

**Add to relay admin dashboard:** A "Generate Registration Token" button under a new "Onboarding" section.

**Add to on-prem Settings page:** A "Registration Token" field in the Push Relay section, used only during initial registration.

---

## 2. Authenticate Device Endpoints

**Current:** `PUT /api/devices/{deviceId}/token`, `GET /api/devices/{deviceId}/instances`, and `DELETE /api/devices/{deviceId}/instances/{instanceId}` have no authentication. A guessed deviceId allows token hijacking, info leaks, and unpair attacks.

**Change:** Return a `deviceSecret` during pairing. Require it as a Bearer token on all `/api/devices/*` endpoints.

Updated pairing response:
```
Response:
{
  "deviceId": "...",
  "deviceSecret": "ds_abc123...",       // Store in iOS Keychain / Android Keystore
  "instanceId": "...",
  "instanceName": "Airport Terminal A"
}
```

All subsequent device API calls require:
```
Header: Authorization: Bearer {deviceSecret}
```

The relay stores a hash of the deviceSecret (not plaintext).

---

## 3. Rate-Limit Pairing Endpoint

**Current:** No rate limiting specified on `POST /api/devices/pair`.

**Change:** Add to Section 4.3 (Pairing Code Properties):

- Rate limit: max 5 failed pairing attempts per IP per 15 minutes
- After 10 consecutive failures for a given pairing code, invalidate the code
- Return 429 Too Many Requests with `Retry-After` header when rate limited
- Log all failed pairing attempts for security monitoring

---

## 4. Specify E2E Encryption Scheme Completely

**Current:** "e.g., X25519 or RSA-2048" — underspecified. RSA can't encrypt payloads larger than ~245 bytes. No replay protection.

**Change:** Replace Section 3.1 "How It Works" with a concrete specification:

### Encryption Scheme: X25519 + XChaCha20-Poly1305

Uses libsodium's sealed box construction. Available on all target platforms:
- .NET: `libsodium-net` or `NSec`
- iOS: `swift-sodium` or `CryptoKit`
- Android: `lazysodium-android`

### Key Generation (during pairing)
```
Mobile app generates:
  - X25519 key pair (32-byte public key, 32-byte private key)
  - Private key stored in device secure enclave (iOS Keychain / Android Keystore)
  - Public key sent to relay during pairing (base64-encoded)
```

### Encryption (on-prem, per notification)
```
For each device:
  1. Build plaintext JSON payload: { "title": "...", "body": "...", "data": {...}, "ts": 1775001234, "seq": 42 }
  2. Encrypt with libsodium sealed_box using the device's public key
  3. Base64-encode the ciphertext
  4. Include in the notifications array as "encryptedPayload"
```

### Payload Fields (inside encrypted envelope)
```json
{
  "title": "Forced Door Alert",
  "body": "Forced entry at Front Door\nType: ActiveAlarm",
  "data": { "eventId": "5001", "ruleName": "Forced Door" },
  "ts": 1775001234,
  "seq": 42
}
```

- `ts` — Unix timestamp (seconds). App rejects messages older than 5 minutes.
- `seq` — Monotonic sequence number per instance. App rejects already-seen sequence numbers.

### Decryption (mobile app)
```
1. Receive encrypted payload from push notification
2. Open sealed box with device's private key
3. Parse JSON
4. Validate ts (reject if > 5 min old) and seq (reject if already seen)
5. Display local notification with decrypted title and body
```

### Key Fingerprint Verification (optional, recommended)

During pairing, both the mobile app and the on-prem admin UI display a key fingerprint (first 8 bytes of SHA-256 of the public key, formatted as `AB12-CD34-EF56-7890`). The admin can visually verify they match. This mitigates relay MITM attacks on key exchange.

Add to the on-prem Pairing page: after a device pairs, show its key fingerprint next to the device name.
Add to the mobile app: in Instance Detail, show the key fingerprint under the instance name.

---

## 5. Fix iOS Push Delivery — Use Notification Service Extension

**Current:** E2E mode uses silent/data-only pushes. iOS throttles these aggressively. The spec's fallback visible push is mentioned in passing but not specified.

**Change:** Do not use silent pushes for E2E mode. Instead, use the iOS Notification Service Extension (NSE) approach (same as Signal/WhatsApp):

### E2E Push Delivery (revised)

**iOS:**
1. Relay sends a visible push notification with:
   - `mutable-content: 1` (triggers NSE)
   - Generic fallback: `title: "New Alert"`, `body: "from Airport Terminal A"` (configurable per instance)
   - `data.encryptedPayload`: the encrypted blob
2. iOS wakes the app's Notification Service Extension
3. NSE decrypts the payload using the device's private key
4. NSE replaces the generic title/body with the decrypted content
5. iOS displays the modified notification

**Android:**
1. Relay sends a data-only FCM message with the encrypted payload
2. App's FirebaseMessagingService decrypts and posts a local notification

**Fallback:** If the NSE fails to decrypt (timeout, error), the generic fallback title/body is displayed as-is. The app decrypts when next opened and updates the notification history.

### Impact on Tech Stack

The Expo recommendation needs a caveat: Expo does not support iOS Notification Service Extensions out of the box. Building the NSE requires:
- An Expo Config Plugin with native Swift code, OR
- Ejecting to a bare React Native workflow for the NSE module

This increases iOS development effort. Consider bare React Native (no Expo) if E2E is a priority feature.

---

## 6. Enforce APNs/FCM Payload Size Limits

**Current:** Not addressed. APNs has a 4KB limit. Base64 encoding inflates payload by ~33%.

**Change:** Add to Section 5 (Notification Flow):

- Maximum plaintext payload before encryption: 2.5KB (leaves room for base64 inflation + push envelope overhead)
- The on-prem `RelaySender` must truncate the `body` field if the total payload exceeds this limit, appending "..."
- The `data` field should contain only essential identifiers, not full event data
- If the encrypted + base64 payload exceeds 3.5KB, the relay should store the payload server-side and send only a reference ID in the push. The app fetches the full encrypted payload from `GET /api/notifications/{id}/payload` on receipt.

---

## 7. Map Push Recipients to Specific Devices

**Current:** "Push" as a NotifyVia channel sends to ALL paired devices for the instance. This conflicts with the per-recipient rule model in SPEC.md.

**Change:** Add a device/user mapping concept:

### Option A (simpler, recommended for v1):
- Each paired device can be assigned to a Recipient in the on-prem admin UI
- The Pairing page shows a dropdown: "Assign to recipient: [Dave] [Sarah] [Unassigned]"
- When a rule matches and targets "Dave", only Dave's assigned devices get the push
- Unassigned devices receive all push notifications (backwards compatible)

### Data model change:
Add to `DeviceInstancePairings`:
| Column | Type | Notes |
|---|---|---|
| RecipientId | int (nullable) | FK to on-prem Recipient, null = receive all |

The on-prem instance includes the target recipientIds in the notification POST. The relay filters to devices assigned to those recipients.

Updated notification endpoint:
```json
{
  "encrypted": true,
  "recipientIds": [1, 3],
  "notifications": [
    { "deviceId": "...", "encryptedPayload": "..." }
  ]
}
```

In plaintext mode, the relay handles the filtering:
```json
{
  "encrypted": false,
  "recipientIds": [1, 3],
  "title": "...",
  "body": "..."
}
```

---

## 8. Add API Versioning

**Current:** No version prefix on API endpoints.

**Change:** Prefix all endpoints with `/api/v1/`:
- `/api/v1/instances/register`
- `/api/v1/instances/{instanceId}/heartbeat`
- `/api/v1/instances/{instanceId}/notifications`
- `/api/v1/devices/pair`
- etc.

---

## 9. Define Error Response Format

**Current:** Only happy paths documented.

**Change:** Add a standard error envelope used by all endpoints:

```json
{
  "error": "pairing_code_expired",
  "message": "This pairing code has expired. Generate a new one.",
  "status": 410
}
```

### Error codes by endpoint:

**POST /api/v1/instances/register**
| Status | Error Code | Description |
|---|---|---|
| 401 | `invalid_registration_token` | Token is invalid or already consumed |
| 410 | `registration_token_expired` | Token has expired |

**POST /api/v1/devices/pair**
| Status | Error Code | Description |
|---|---|---|
| 404 | `pairing_code_not_found` | Code doesn't exist |
| 410 | `pairing_code_expired` | Code has expired |
| 410 | `pairing_code_consumed` | Code already used |
| 429 | `rate_limited` | Too many attempts |

**POST /api/v1/instances/{instanceId}/notifications**
| Status | Error Code | Description |
|---|---|---|
| 401 | `invalid_api_key` | API key is wrong or revoked |
| 413 | `payload_too_large` | Encrypted payload exceeds 3.5KB limit |
| 422 | `no_paired_devices` | No devices paired to this instance |

**All device endpoints**
| Status | Error Code | Description |
|---|---|---|
| 401 | `invalid_device_secret` | Device secret is wrong or revoked |
| 404 | `device_not_found` | Device ID doesn't exist |

---

## 10. Add API Key Rotation

**Current:** No way to rotate instance API keys without re-registering.

**Change:** Add endpoint:

```
POST /api/v1/instances/{instanceId}/rotate-key
Header: Authorization: Bearer {currentApiKey}

Response:
{
  "apiKey": "sk_live_newkey456...",
  "previousKeyValidUntil": "2026-04-01T20:00:00Z"
}
```

- The old key remains valid for 1 hour after rotation (grace period for in-flight requests)
- The on-prem instance stores the new key and starts using it immediately
- Add a "Rotate API Key" button in the on-prem Settings page

---

## 11. Notification Delivery Feedback

**Current:** Synchronous response only. No delivery confirmation from devices.

**Change (v2, after MVP):**

### Delivery Receipts
- When the mobile app successfully decrypts and displays a notification, it ACKs back to the relay: `POST /api/v1/devices/{deviceId}/ack/{notificationId}`
- The relay tracks delivery status per device per notification
- The on-prem instance can poll: `GET /api/v1/instances/{instanceId}/notifications/{id}/status`
- Response includes per-device delivery status: `delivered`, `pending`, `failed`

### Escalation
- If no device ACKs within a configurable window (e.g., 2 minutes), the relay can trigger a fallback (e.g., notify the on-prem instance via the heartbeat response, which then falls back to SMS/Pushover)

---

## 12. Relay Failure Handling

**Current:** Heartbeat failure is silent. No retry policy for APNs/FCM failures.

**Change:**

### APNs/FCM Retry Policy
- On transient failure (5xx, timeout): retry up to 3 times with exponential backoff (1s, 5s, 15s)
- On permanent failure (APNs 410 Gone / FCM invalid token): mark device as `stale`, stop sending, include `stale: true` in the device list response so the on-prem instance and app know to re-register
- Return detailed failure info in notification response:
```json
{
  "sent": 1,
  "failed": 1,
  "results": [
    { "deviceId": "...", "status": "sent" },
    { "deviceId": "...", "status": "failed", "reason": "invalid_token" }
  ]
}
```

### Heartbeat Failure Handling (on-prem side)
- If heartbeat POST fails, log a warning
- After 3 consecutive failures, surface in the admin UI: "Relay unreachable since {time}"
- After 5 consecutive failures, fall back to SMS/Pushover for the next matched event and log: "Push relay unavailable, falling back to SMS"

---

## 13. Mobile App Security

**Current:** No app-level authentication. No local data encryption specified.

**Change:**

### App Lock
- Add optional biometric/PIN lock in app Settings
- When enabled, require authentication to view notification content or manage instances
- Push notifications still display (with decrypted content via NSE), but opening the app requires auth

### Local Data Encryption
- Notification history stored in an encrypted local database (iOS: Core Data with NSFileProtectionComplete, Android: EncryptedSharedPreferences or SQLCipher)
- Encryption key derived from device secure enclave
- Define retention policy: auto-delete notifications older than 30 days

---

## 14. Miscellaneous

### Database: Use PostgreSQL from the start
The spec says "PostgreSQL (or SQLite for small scale)." SQLite doesn't handle concurrent writes well. Multiple instances sending notifications simultaneously will cause contention. Start with PostgreSQL — the cost difference on a small VPS is negligible.

### Automated database backups
The relay stores instance registrations, device pairings, and API key hashes. If the database is lost, every instance and device must re-register. Add automated daily backups with 30-day retention.

### Data retention policy
- `PairingCodes`: Delete expired/consumed codes after 24 hours
- `NotificationLog`: Delete entries older than 90 days
- `Devices` marked stale: Delete after 30 days with no activity

### Audit logging
Log all security-relevant actions with timestamp, source IP, and instanceId/deviceId:
- Instance registration and key rotation
- Device pairing and unpairing
- Failed authentication attempts
- Rate limit triggers
