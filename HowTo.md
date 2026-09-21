# HowTo — Demo Script (EN / TH)

A start-to-finish runbook for demoing this project's 4 Agent Framework **Skills** (plus 3 bonus MCP
skills). Deep design rationale lives in [`docs/spec.md`](docs/spec.md) / [`CLAUDE.md`](CLAUDE.md) — this
file is only the "what to click / what to type" checklist.

**สรุป**: เอกสารนี้เป็นสคริปต์สำหรับสาธิตโปรเจกต์ ตั้งแต่การสตาร์ทระบบไปจนถึงการทดสอบ Skill ทั้ง 4 แบบ (บวก MCP skill
เสริมอีก 3 ตัว) พร้อม Prompt ตัวอย่างทั้งภาษาไทยและอังกฤษ

## What this demo shows

| # | Skill type | Feature | Triggered from |
|---|---|---|---|
| 1 | Class-based (`AgentClassSkill`) | Budgeting | `/Chat` |
| 2 | Inline (`AgentInlineSkill`, built per upload) | Receipt OCR | `/Receipts` |
| 3 | File-based (`SKILL.md` + `references/`) | Savings goals (guidance + exact math) | `/Chat`, `/Goals` |
| 4 | MCP-based (standing HTTP server, JWT bearer) | Monthly summary & advice | `/Chat` |

Plus 3 bonus MCP skills added later on the same server: `goals-progress`, `emergency-fund`,
`debt-payoff-strategies` (§6.5 below).

## Prerequisites

- .NET **10** SDK (`Directory.Build.props` pins `net10.0`)
- Docker Desktop (for the Postgres container)
- An LLM endpoint the app can reach. `src/FinanceApp.Web/appsettings.json` currently points at a local
  OpenAI-compatible endpoint (`AI:ENGINE_TYPE=OpenAI`, `AI:ENDPOINT=http://192.168.1.191:51234/v1`,
  `AI:MODEL_NAME=qwen/qwen3.5-9b`, `AI:SUPPORTS_VISION=true`) — edit the `AI` section (or override via
  env vars, same keys) to point at whatever Azure/OpenAI/Ollama/Gemini/Anthropic endpoint you actually
  have running.
- `python3` on PATH, **only** if you want to demo `savings-calculator`'s exact-math script path end to
  end — untested in some dev environments (see `CLAUDE.md`), but works fine on a normal machine with
  Python installed.

**ข้อกำหนดเบื้องต้น**: ต้องมี .NET 10 SDK, Docker Desktop, และปลายทาง LLM ที่เข้าถึงได้ตามค่าใน `appsettings.json`
(หรือแก้ค่า `AI:*` ให้ตรงกับของที่มี) ส่วน `python3` จำเป็นเฉพาะตอนสาธิต skill คำนวณเป๊ะ (`savings-calculator`)

## Step 1 — Start Postgres

```bash
docker compose up postgres -d
```

## Step 2 — Start the MCP server (must be started FIRST — standing HTTP service, not auto-spawned)

```bash
dotnet run --project src/FinanceApp.McpServer
```

Confirm it's listening on `http://localhost:5299` (from `src/FinanceApp.McpServer/appsettings.json`).

**สำคัญ**: ต้องสตาร์ท MCP server ก่อน Web เสมอ เพราะเป็นบริการ HTTP แบบ standing service ไม่ได้ถูกสั่งรันอัตโนมัติจาก Web อีกต่อไป

## Step 3 — Start the Web app

```bash
dotnet run --project src/FinanceApp.Web
```

It auto-migrates the database on startup (`AUTO_MIGRATE`). Browse to `http://localhost:5000`
(or `https://localhost:7177`).

**เปิดเว็บ**: หลังรันแล้วไปที่ `http://localhost:5000` ระบบจะ migrate ฐานข้อมูลให้อัตโนมัติ

## Step 4 — Register & log in

1. Go to `/Account/Register`, create a user.
2. Log in at `/Account/Login`.

**ลงทะเบียนและเข้าสู่ระบบ**: ไปที่ `/Account/Register` เพื่อสร้างผู้ใช้ แล้วเข้าสู่ระบบที่ `/Account/Login`

## Step 5 — Seed a bit of data

So the skills below have something real to talk about. The app always has these global categories
seeded (`SeedData.cs`, no setup needed): **Groceries, Dining, Transport, Utilities, Entertainment,
Income, Other, Savings**.

- `/Transactions` — add 2-3 transactions (e.g. an Income row, a Groceries expense, a Dining expense).
- `/Budgets` — set a monthly limit on **Groceries** (pick an amount your transactions above will get
  close to or over, so the budget-status skill has something interesting to report).
- `/Goals` — create a savings goal (e.g. "Car", target 300000, current amount 50000, monthly
  contribution 5000).

**เตรียมข้อมูลตัวอย่าง**: เพิ่มธุรกรรม 2-3 รายการที่ `/Transactions`, ตั้งงบที่หมวด **Groceries** ที่ `/Budgets`,
และสร้างเป้าหมายออมเงินที่ `/Goals` (หมวดหมู่พื้นฐานที่มีอยู่แล้วคือ Groceries, Dining, Transport, Utilities,
Entertainment, Income, Other, Savings)

## Step 6 — Demo each skill

For every prompt below, watch the **Activity Log** panel next to the chat (or inline on `/Receipts`) —
that's the proof the skill actually fired (see Step 7). A gated action may also show an **Approve /
Reject** card; click **Approve** to let it continue (governed by the `AutoApproveWrites` /
`AutoApproveExecuteScript` toggles on the Profile page, `/Account/Manage`).

### 6.1 Class-based skill — Budgeting (`/Chat`)

| # | Intent | Thai prompt | English prompt |
|---|---|---|---|
| 1 | Budget status for one category | เดือนนี้งบหมวด Groceries ของฉันเป็นยังไงบ้าง | How am I doing on my Groceries budget this month? |
| 2 | Overall budget check | งบประมาณเดือนนี้เกินหมวดไหนบ้างไหม | Am I over budget in any category this month? |

### 6.2 Inline skill — Receipt OCR (`/Receipts`)

Not prompt-driven — it's a click path:

1. Upload a receipt photo (`InputFile`, up to 10 MB).
2. Click **"Extract with AI"** (only shown while the receipt is still `Pending` and
   `AI:SUPPORTS_VISION` is `true`).
3. Watch the inline streaming response, Activity Log, and (if the write is gated) the Approve card.
4. If extraction isn't available (no vision model configured), use the manual-entry form instead —
   this is the designed fallback, not a bug.

**Receipt OCR**: อัปโหลดรูปใบเสร็จที่หน้า `/Receipts` แล้วกด **"Extract with AI"** ระบบจะดึงข้อมูล (ร้าน, ยอดเงิน,
วันที่, หมวดหมู่) ให้อัตโนมัติผ่านโมเดลที่รองรับภาพ ถ้าไม่มีโมเดลที่รองรับภาพ ให้กรอกข้อมูลด้วยตนเองแทน

### 6.3 File-based skills — Savings Goals (`/Chat`, `/Goals`)

Two cooperating skills: **`savings-goals`** (qualitative guidance) and **`savings-calculator`**
(exact math via a Python script). Try one prompt from each row to show both firing separately.

| # | Intent | Skill | Thai prompt | English prompt |
|---|---|---|---|---|
| 1 | Qualitative — is a goal realistic | `savings-goals` | เป้าหมายเงินเก็บซื้อรถของฉันสมเหตุสมผลไหม | Is my car savings goal realistic? |
| 2 | Qualitative — how to prioritize/afford | `savings-goals` | ถ้าเงินเดือนจำกัด ควรแบ่งเงินออมยังไงดี (แนว 50/30/20) | I have a tight budget — how should I split spending vs. saving (50/30/20)? |
| 3 | Exact math — projected balance | `savings-calculator` | ถ้าฉันเก็บเดือนละ 5000 บาท ดอกเบี้ย 3% ต่อปี อีก 24 เดือนจะมีเงินเท่าไหร่ | If I save 5,000 THB/month at 3% annual interest, how much will I have in 24 months? |
| 4 | Exact math — debt payoff | `savings-calculator` | ถ้าฉันมีหนี้ 50000 บาท จ่ายเดือนละ 3000 บาท ดอกเบี้ย 18% ต่อปี จะหมดหนี้กี่เดือน | If I owe 50,000 THB, pay 3,000/month, at 18% annual interest, how many months to pay it off? |

Also try the **`/Goals` → "Get guidance"** button on a goal row — it hands off to `/Chat` with the
input pre-filled (not auto-sent) from that goal's real data.

**เป้าหมายออมเงิน**: มี 2 skill ทำงานร่วมกัน — `savings-goals` (ให้คำแนะนำเชิงคุณภาพ) และ `savings-calculator`
(คำนวณตัวเลขแม่นยำด้วยสคริปต์ Python) ลองถามทั้งสองแบบเพื่อดูว่า skill คนละตัวถูกเรียกต่างกัน หรือกดปุ่ม
**"Get guidance"** ที่หน้า `/Goals` เพื่อส่งต่อไปยัง `/Chat` พร้อมข้อความที่กรอกไว้ล่วงหน้า

### 6.4 MCP-based skill — Monthly Summary & Advice (`/Chat`)

| # | Intent | Thai prompt | English prompt |
|---|---|---|---|
| 1 | Current month summary | เดือนนี้การเงินฉันเป็นยังไงบ้าง | How did I do financially this month? |
| 2 | Named month summary | สรุปการเงินเดือนสิงหาคม 2026 ให้หน่อย | Give me a spending summary for August 2026. |

This resolves to the MCP resource `summary-<year>-<month>` (e.g. `summary-2026-09`), computed live
from the same Postgres database. If the MCP server isn't running, this skill is simply absent from the
Activity Log — the app doesn't crash (graceful degradation, see Troubleshooting).

**สรุปรายเดือน**: ดึงข้อมูลจาก MCP server แบบเรียลไทม์ ถ้าไม่ได้สตาร์ท MCP server ไว้ skill นี้จะหายไปเฉยๆ
โดยแอปจะไม่ล่ม (เป็นการออกแบบไว้)

### 6.5 Bonus MCP skills

Added later on the same MCP server, same standing-service mechanism as 6.4.

| # | Skill | Thai prompt | English prompt |
|---|---|---|---|
| 1 | `goals-progress` — live status of every goal | เป้าหมายออมเงินของฉันไปถึงไหนแล้วบ้าง | Am I on track with my savings goals? |
| 2 | `emergency-fund` — sizing/where to keep it | ควรมีเงินสำรองฉุกเฉินเท่าไหร่ และควรเก็บไว้ที่ไหน | How much should I keep for emergencies, and where should I keep it? |
| 3 | `debt-payoff-strategies` — which debt to pay off first | ควรจ่ายหนี้แบบ avalanche หรือ snowball ดี | Should I use the avalanche or snowball method to pay off my debts? |

## Step 7 — How to verify a skill actually fired

Don't judge by the reply text alone — the model could answer from general knowledge without ever
touching your data. Check the **Activity Log** panel for entries like:

```
load_skill          -> e.g. "budgeting", "savings-goals", "savings-calculator"
read_skill_resource  -> e.g. "summary-2026-09", "current" (goals-progress)
run_skill_script     -> e.g. "scripts/project-savings.py"
```

An **Approve / Reject** card appearing mid-stream means a gated action (a write, or a script
execution) is waiting on you — click **Approve** to let the skill finish.

**การยืนยันว่า skill ทำงานจริง**: อย่าดูแค่คำตอบที่ได้ ให้ดูที่แผง Activity Log ว่ามีการเรียก `load_skill` /
`read_skill_resource` / `run_skill_script` จริงหรือไม่ และถ้ามีการ์ด Approve/Reject ขึ้นมา ให้กด Approve
เพื่อให้ทำงานต่อ

## Optional — Data isolation demo

Register a **second** user, add a few transactions/goals under it, and confirm:

- Neither user's CRUD pages (`/Transactions`, `/Budgets`, `/Goals`, `/Receipts`) ever show the other
  user's rows.
- Chat prompts under one user's session never return the other user's data — including if you try
  phrasing that guesses at the other user's data.
- The monthly-summary / goals-progress MCP skills, tried from both sessions, return only each user's
  own figures.

**สาธิตการแยกข้อมูลผู้ใช้**: ลงทะเบียนผู้ใช้คนที่สอง เพิ่มข้อมูลแยกกัน แล้วยืนยันว่าไม่มีข้อมูลของอีกฝ่ายรั่วไหลไปยังอีกฝ่าย
ไม่ว่าจะผ่านหน้า CRUD หรือผ่านแชท

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Monthly-summary / goals-progress / emergency-fund / debt-payoff skills never appear in chat | `FinanceApp.McpServer` wasn't started, or `Mcp:BaseUrl` is wrong | Start it first (Step 2); check it's reachable at the configured `Mcp:BaseUrl` |
| "Extract with AI" button missing on `/Receipts` | `AI:SUPPORTS_VISION` is `false`, or the configured model isn't vision-capable | Set `AI:SUPPORTS_VISION=true` and point at a vision-capable model, or use manual entry |
| `savings-calculator` prompts fail/error | `python3` not on PATH for the process running `FinanceApp.Web` | Install Python 3 and ensure `python3` resolves on PATH |
| Chat looks "stuck" after a skill call | A gated action is waiting for approval | Look for the Approve/Reject card and click Approve |
| Login/Register page looks broken only after a moment (not on first load) | Known MudBlazor/Blazor Server render-mode interaction (see `docs/spec.md` section 7) — already fixed in this codebase | Should not occur; if it does, hard-refresh and report it |
