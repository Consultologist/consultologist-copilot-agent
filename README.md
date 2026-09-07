# Consultologist Copilot agent

A Microsoft 365 **custom-engine agent** (Microsoft 365 Agents SDK, .NET) that lets
a clinician turn a referral into a consult draft from inside Microsoft Teams — and
later Microsoft 365 Copilot. It signs the clinician in, presents their workflow
package's intake form as an Adaptive Card, and runs the consult on the
**Consultologist engine as the signed-in clinician**.

It opens **no new engine surface**: it is one more delegated-token *satellite*
over the doors the web app and the Epic/Cerner panels already use. Design of
record: `docs/COPILOT_AGENT_SPIKE.md` (spike #663, GO) and
`docs/SATELLITE_CALLERS.md` in the engine repo.

## Status — scaffold (issue #667, step 1)

This repository is the **built, tested application scaffold**. What is done and
what is deferred:

| Step | State |
|------|-------|
| 1. Build the agent app | ✅ **here** — code + tests, `dotnet build`/`dotnet test` green |
| 2. Register Entra app + Azure Bot + manifest | ⏳ deferred — see [Operator runbook](#operator-runbook-steps-2-3) |
| 3. Sideload + prove a live consult from Teams | ⏳ deferred — needs the owner's Azure/M365 admin |
| 4. Broad publish (org catalog / marketplace) | ➡️ tracked separately as engine issue **#554** |

Steps 2–3 need an interactive Azure/M365-admin session; the runbook below is what
that session follows.

## What it does

1. On a message, Teams **SSO** signs the clinician in; **On-Behalf-Of** exchanges
   that token for a delegated `access_as_user` bearer to the engine (the `api`
   handler, configured in `appsettings.json`).
2. It calls `GET WorkflowPackages/Current` — the same door the web app's setup
   form uses — and **builds the Adaptive Card intake form dynamically** from the
   package's declared inputs. No per-package code; no hardcoded field list.
3. An attached referral document is previewed via `POST DocumentExtractions` and
   held for the run.
4. On **Action.Execute** submit, it maps the typed fields to a
   `ConsultGenerationRequest`, refuses to start until required inputs are present,
   then `POST ConsultGenerationJobs` (→ `202 {JobId, StatusUrl}`), polls
   `GET ConsultGenerationJobs/{jobId}`, and returns the deliverable and the
   engine's `EffectiveInputHash`.

### The verifiability boundary

The agent is a delivery surface only. It assembles a candidate input set and
submits bytes/values. **Extraction, canonicalisation, the effective-input hash,
and per-input provenance are the engine's**, computed at job start. The agent
never supplies the hash and never asserts extracted text as authoritative.

## Layout

```
src/Consultologist.CopilotAgent.Core/   Engine client, wire DTOs, ConsultInputValue,
                                        and the two pure mapping functions.
                                        No Agents-SDK dependency → fully unit-tested.
  Engine/                               EngineApiClient, EngineDtos, ConsultInputValue
  Intake/                               IntakeCardBuilder (inputs → card),
                                        IntakeCardReader (submit → typed input map)
src/Consultologist.CopilotAgent/        The SDK host: Program.cs, IntakeAgent,
                                        AspNetExtensions.cs (vendored MIT helper),
                                        appsettings.json
tests/Consultologist.CopilotAgent.Tests Wire-contract + mapping tests
appManifest/                            Teams/M365 manifest + icons
```

## Build & test

```bash
dotnet build
dotnet test
```

Both run without any Azure/Teams dependency: the engine-facing logic (the card
mapping and the `ConsultInputValue` wire contract) lives in `.Core` and is unit
tested directly.

## Run locally

The host listens on **port 3978** (Bot Service convention). For a live Teams run
you need the provisioning below plus a public tunnel:

```bash
cp src/Consultologist.CopilotAgent/appsettings.Development.json.sample \
   src/Consultologist.CopilotAgent/appsettings.Development.json   # fill in real values
dotnet run --project src/Consultologist.CopilotAgent
# in another shell: devtunnel host -p 3978   (point the Azure Bot messaging endpoint at it)
```

The Microsoft 365 Agents Toolkit (VS/VS Code) automates the tunnel, local app
registration, and sideload via F5.

## Operator runbook (steps 2–3)

Run once by the owner (Azure + M365 admin). Placeholders map to `appsettings.json`
and `appManifest/manifest.json`.

1. **Register the agent's Entra app** — identifier URI `api://botid-<clientId>`,
   expose the `access_as_user` scope, and pre-authorize the Teams client ids for
   SSO. This client id is `AAD_APP_CLIENT_ID` in the manifest and
   `TokenValidation:Audiences` / `Connections:ServiceConnection:...:ClientId` in
   config.
2. **Provision the Azure Bot** (`Microsoft.BotService`): messaging endpoint
   `https://<host>/api/messages`, add the **Microsoft Teams** channel, and create
   the **`teams_sso`** OAuth connection. Prefer **federated credentials + a
   User-Assigned Managed Identity** (Teams SSO requires federated credentials)
   over a client secret.
3. **Admit the agent to the engine** — follow `docs/SATELLITE_CALLERS.md` §2 in
   the engine repo, verbatim (read-modify-write; never replace the array):
   - `az ad sp create --id <agent-appId>`
   - a tenant-wide `oauth2PermissionGrant` of `access_as_user`
     (`consentType: AllPrincipals`) against API app
     `b3866040-8bae-4c01-88ba-ecff646df451`
   - **append** the agent to `api.preAuthorizedApplications` on that API app.
4. **Fill configuration** — `TokenValidation` (Audiences = the Azure Bot client
   id, TenantId), `Connections:ServiceConnection` (auth type + credentials), and
   confirm the `api` handler's `AzureBotOAuthConnectionName: teams_sso`,
   `OBOConnectionName: ServiceConnection`, and
   `OBOScopes: ["api://b3866040-8bae-4c01-88ba-ecff646df451/access_as_user"]`.
   Real secrets go in App Service configuration (or `appsettings.Development.json`
   locally), never in git.
5. **Package + sideload** — replace the placeholder icons in `appManifest/`, zip
   `manifest.json` + `color.png` + `outline.png`, and upload via Teams
   "Upload a custom app" (dev) with a dev tunnel, or the Teams Admin Center for a
   scoped org rollout.
6. **Deploy** — publish the web app to Azure App Service (or a container) and
   point the Azure Bot messaging endpoint at it; enable the **M365 Copilot**
   channel on the same registration when ready (also add
   `AddAgentM365AttachmentDownloader()` in `Program.cs` for its file URLs).

**Live proof (step 3):** from a Teams personal chat, sign in, submit the intake
card, and confirm a real consult and its `EffectiveInputHash` come back. That is
the gate to closing #667.

## Publishing = engine issue #554

A tenant gets the agent only after an **M365 admin approves it and consents** to
`access_as_user` (M365 admin center → Agents → Requests; optionally AppSource /
the Commercial Marketplace via Partner Center). That per-tenant activation is
tracked as engine issue **#554**, not here.

## Configuration reference

| Key | Meaning |
|-----|---------|
| `Engine:ApiHost` | Engine API root (e.g. `https://east.ca.api.consultologist.ai/api`) |
| `Engine:DocumentInputSlot` | Declared input slot attached documents fill (default `referral`) |
| `Engine:PollIntervalSeconds` | Job poll interval |
| `TokenValidation:Audiences` / `:TenantId` | Inbound Bot Service / Entra token validation |
| `AgentApplication:UserAuthorization:Handlers:api` | SSO + OBO: `AzureBotOAuthConnectionName`, `OBOConnectionName`, `OBOScopes` |
| `Connections:ServiceConnection` | The MSAL connection backing OBO (federated cred / secret) |

## License

PolyForm Strict 1.0.0 — see `LICENSE.md`. `src/Consultologist.CopilotAgent/AspNetExtensions.cs`
is vendored from the Microsoft 365 Agents SDK samples under the MIT License (its
header is preserved).
