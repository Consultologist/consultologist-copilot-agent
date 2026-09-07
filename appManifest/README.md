# Teams / M365 app package

- `manifest.json` — manifest schema 1.22, a bot-based **custom engine agent**
  (`copilotAgents.customEngineAgents`, `personal` scope, `supportsFiles: true`).
  Placeholders `${{AAD_APP_CLIENT_ID}}` and `${{BOT_DOMAIN}}` are filled at
  packaging time (the Microsoft 365 Agents Toolkit does this, or substitute by
  hand).
- `color.png` (192×192) and `outline.png` (32×32, transparent) are **placeholder**
  icons — replace them with real branding before publishing.

Zip these three files (manifest + both icons) to produce the sideloadable app
package. See the repo README's operator runbook.
