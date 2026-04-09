# WebSocket Event Alert Service — Requirements Spec

## 1. Overview

A self-hosted Windows application that connects to a WebSocket endpoint, listens for JSON events, evaluates them against user-defined filter rules, and sends SMS alerts via Twilio to configured recipients. An admin web UI allows managing filters, recipients, throttling, and connection settings.

The application must run as a Windows Service. An MSSQL database is available on the host machine.

---

## 2. Core Features

### 2.1 WebSocket Connection

- Connect to a configurable WebSocket endpoint URL.
- Authenticate by submitting credentials to a configurable login endpoint (see Section 3).
- Receive and parse JSON events from the WebSocket in real time.
- Expected max event volume: ~10 events/second.

### 2.2 Filter Rules

- Admin defines filter rules through the web UI.
- Each rule has one or more conditions evaluated against incoming JSON event fields.
- Conditions are combined using a configurable logic operator: **ALL match (AND)** or **ANY match (OR)**.
- Each condition specifies:
  - **Field Path** — dot-notation path into the JSON event (e.g., `EventID`, `User`, `Data.Status`)
  - **Operator** — `equals`, `not_equals`, `contains`, `greater_than`, `less_than`
  - **Value** — the value to compare against
- When a rule matches an event, SMS alerts are sent to the rule's assigned recipients (subject to throttling).
- Rules can be activated or deactivated without deleting them.
- Rules are assigned to specific recipients (see Section 2.3).

**Example rule:** "EventID equals 5003 AND User equals Dave" → send SMS to Dave's phone and Ops Team.

**Future scope (not Phase 1):**

- Regex operator
- Nested condition groups (AND/OR groups within a rule)
- Array indexing in field paths (e.g., `Data.Tags[0]`)

### 2.3 Recipients

- Admin manages a list of SMS recipients, each with a name and phone number.
- Recipients can be activated or deactivated without deleting them.
- Each filter rule is linked to one or more recipients. When the rule matches, only those recipients receive the alert.
- A "Send Test SMS" function sends a test message to verify recipient phone numbers.

### 2.4 SMS Alerts via Twilio

- Alerts are sent via the Twilio SMS API.
- Twilio credentials (Account SID, Auth Token, From Number) are configured in the admin UI.
- SMS message includes the rule name, matched event field values, and timestamp.
- Messages should stay under 160 characters where possible. Truncate with `...` if needed.
- If Twilio returns an error, log it. Do not retry (Twilio handles retries internally).
- Surface Twilio credential errors on the admin dashboard.

### 2.5 Throttling

- Each filter rule has configurable throttle settings: **max N SMS per M minutes**.
- Throttling is tracked per rule per recipient. Example: "max 3 SMS per 10 minutes" means each recipient can receive at most 3 messages from that rule in any 10-minute window.
- SMS that exceed the throttle limit are silently suppressed.
- Expired throttle tracking data should be periodically cleaned up.

### 2.6 Connection Monitoring & Reconnection

**Reconnection:**

- If the WebSocket connection drops, automatically reconnect using exponential backoff.
- Reconnect base delay and max delay are configurable in settings.
- On each reconnect attempt, re-authenticate (fetch a fresh token) before connecting.
- If authentication fails (bad credentials), stop retrying and alert immediately.

**Disconnect Alerts:**

- If the connection remains down for longer than a configurable threshold (e.g., 120 seconds), send a single SMS alert to **all active recipients**.
- Do not re-alert until the connection is restored and drops again.

### 2.7 Admin Web UI

- Accessible via browser on a configurable port.
- Protected by simple username/password authentication.
- On first run, if no admin account exists, display a setup page to create one.

---

## 3. WebSocket Authentication Flow

The target WebSocket requires token-based authentication:

1. **POST** to a login endpoint with a JSON body containing username and password.
2. The login endpoint responds with a JSON body containing a token.
3. Connect to the WebSocket with the header: `Authorization: Bearer {token}`.
4. On 401 or disconnect, re-authenticate from step 1.

**The following must be configurable in settings:**

- Login endpoint URL
- Username and password
- Request body field names for username and password (e.g., the field might be called `user` instead of `username`)
- Response body field name for the token (e.g., might be `access_token` instead of `token`)

Credentials and tokens must be stored encrypted.

---

## 4. Admin UI Pages

### 4.1 Dashboard

Displays at-a-glance system status:

- **Connection status** — Connected / Disconnected / Reconnecting (updates automatically)
- **Connected since** — timestamp of current connection
- **Events received** — running count (does not need to persist across restarts)
- **Alerts sent today** — count of SMS sent in the current day
- **Active rules** — e.g., "5 of 8 rules active"
- **Active recipients** — e.g., "3 of 4 recipients active"

### 4.2 Filter Rules

**List view:**

- Shows all rules with their name, condition summary, assigned recipients, throttle settings, and active/inactive status.
- Actions: edit, delete, toggle active.

**Create/Edit view:**

- **Name** and optional **description**
- **Logic operator** — dropdown: "Match ALL conditions (AND)" / "Match ANY condition (OR)"
- **Conditions** — dynamic list of rows, each with Field Path, Operator, and Value fields. Admin can add and remove condition rows without a full page reload.
- **Throttle settings** — "Max [N] SMS per [M] minutes"
- **Recipients** — checklist of all recipients, select which ones receive alerts from this rule.

### 4.3 Recipients

**List view:**

- Table showing name, phone number, active/inactive status.
- Actions: edit, delete, toggle active.
- "Send Test SMS" button — sends a test message to all active recipients.

**Create/Edit view:**

- Name, phone number (E.164 format), active toggle.

### 4.4 Settings

Organized in sections:

**WebSocket Connection:**

- Endpoint URL
- Login URL
- Username
- Password (masked)
- Request username field name
- Request password field name
- Response token field name
- Reconnect base delay (seconds)
- Reconnect max delay (seconds)

**Disconnect Alerts:**

- Alert after disconnected for (seconds)

**Twilio:**

- Account SID
- Auth Token (masked)
- From Number

**Actions:**

- "Test Connection" — attempts login + WebSocket connect, reports result inline.
- "Test SMS" — sends a test message to all active recipients.

### 4.5 Login

Simple username/password form. Redirects to dashboard on success.

---

## 5. Data Requirements

The application must persist the following:

- **Recipients** — name, phone number, active flag, timestamps
- **Filter Rules** — name, description, active flag, logic operator (AND/OR), throttle max count, throttle window (minutes), timestamps
- **Filter Conditions** — linked to a rule; field path, operator, value, sort order
- **Rule-Recipient Assignments** — which recipients are linked to which rules
- **Throttle State** — per rule-recipient: window start time, SMS count in window
- **Application Settings** — key-value pairs for all configurable settings (see Section 4.4)

Sensitive values (passwords, tokens, Twilio auth token) must be stored encrypted.

---

## 6. Non-Functional Requirements

- **Runs as a Windows Service** on a Windows Server machine.
- **Single deployment artifact** — minimize external dependencies for installation.
- **Auto-migrates the database** on first run or upgrade.
- **Logging** — log to file with configurable log levels. Logs should include: connection events, authentication attempts, matched filter events, SMS send results, and errors.
- **Graceful shutdown** — on service stop, close the WebSocket connection cleanly and finish any in-flight SMS sends.

---

## 7. Out of Scope (Future Considerations)

These are not part of the initial build but may be added later:

- Regex operator and nested condition groups in filter rules
- Event history / audit log storage
- Customizable SMS message templates per rule
- Multiple WebSocket source connections
- Email or other alert channels beyond SMS
- Multi-user admin with roles/permissions
- Event schema auto-discovery (inspect a sample event to suggest field paths)
