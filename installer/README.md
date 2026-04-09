# Installer

WiX v5 MSI installer for the HirschNotify Windows service. Lives inside the
HirschNotify repo and builds against the published binaries from the parent
project.

## What it does

The installer guides users through:

1. Welcome
2. Install directory selection
3. **Port number** for the web UI (default 5100)
4. **Event source mode** (WebSocket or VelocityAdapter)
5. **Service account** credentials (DOMAIN\Username + password)
6. Verify ready → Install → Finish

After install it:

- Registers `HirschNotify` as a Windows Service (auto-start, restart on failure)
- Configures the service to run as the chosen account via `sc config`
- Adds a Windows Firewall inbound TCP rule for the chosen port
- Writes `install-config.json` so the service applies the chosen Event Source mode on first startup
- Starts the service and opens the web UI in the default browser

On uninstall it stops and removes the service, deletes files, and removes the firewall rule.

## Building locally (Windows only)

WiX v5 requires Windows. Run from the repo root:

```powershell
# Publish HirschNotify first
dotnet publish -c Release -r win-x64 --self-contained HirschNotify.csproj

# Build the installer
dotnet build -c Release installer\HirschNotify.Installer.wixproj

# MSI lands at:
#   installer\bin\Release\HirschNotify-v1.0.msi
```

## Building via GitHub Actions

The repo's `.github/workflows/build-msi.yml` workflow builds the MSI on a
`windows-latest` runner. It triggers on:

- Push to `main` (builds + uploads MSI as a workflow artifact)
- Push of any `v*` tag (builds + creates a GitHub Release with the MSI attached)
- PRs to `main` (validation only)
- Manual `workflow_dispatch`

### Cutting a release

```bash
git tag -a v1.0.1 -m "Release 1.0.1"
git push origin v1.0.1
```

The tag push triggers a build, attaches the MSI to a new GitHub Release, and
auto-generates release notes from commits since the previous tag.

## Branding

Located in `assets/`:

| File | Format | Size | Purpose |
|---|---|---|---|
| `banner.bmp` | 24-bit BMP | 493×58 | Top banner on most installer dialogs |
| `dialog.bmp` | 24-bit BMP | 493×312 | Left panel of Welcome and Finish dialogs |
| `app-icon.ico` | Multi-res ICO | 16/32/48/64/128/256 | Title bar + Add/Remove Programs icon |

The PNG sources are kept alongside the BMP/ICO files. If you change them,
regenerate the WiX-compatible versions:

```bash
# 24-bit BMP for banner
magick banner.png -resize 493x58! -background white -alpha remove -alpha off \
  -type TrueColor BMP3:banner.bmp

# 24-bit BMP for dialog
magick dialog.png -resize 493x312! -background white -alpha remove -alpha off \
  -type TrueColor BMP3:dialog.bmp

# Multi-resolution Windows ICO
magick app-icon.png -resize 256x256 \
  -define icon:auto-resize=256,128,64,48,32,16 app-icon.ico
```

## File layout

```
installer/
├── assets/                           Branding (PNG sources + BMP/ICO)
├── Dialogs/                          Custom WiX dialogs
│   ├── PortDialog.wxs
│   ├── EventSourceDialog.wxs
│   └── ServiceAccountDialog.wxs
├── HirschNotify.Installer.wixproj    WiX project file
├── Package.wxs                       Main installer definition
└── README.md
```
