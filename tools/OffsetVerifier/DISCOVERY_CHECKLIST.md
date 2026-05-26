# PoE2 Offset Discovery Checklist

Run through this in one session with Cheat Engine open alongside PoE2.
After each block, record the pointer chain in OffsetVerifier.cs.

## Tools Needed
- [Cheat Engine](https://cheatengine.org/) — free, open source
- [ReClass.NET](https://github.com/ReClassNET/ReClass.NET) — visualizes C++ struct layouts in memory
- This verifier (run `dotnet run` after each block to confirm)

## Setup
- [ ] Start PoE2, log into a character in a safe area (town or hideout)
- [ ] Open Cheat Engine → File → Open Process → select PathOfExile2.exe
- [ ] Set scan type to "Exact Value" unless noted otherwise
- [ ] Enable hex display for addresses

---

## Block 1 — Player HP (30–60 min)

**Goal:** Find the int32 that holds current player HP.

- [ ] Note your current HP exactly (e.g. 847)
- [ ] Cheat Engine: Value Type = `4 Bytes`, scan for `847`
- [ ] Take a small hit from a monster (step into a hazard)
- [ ] Note new HP (e.g. 731)
- [ ] Next Scan → `731`
- [ ] Repeat 3–4 times until you have <20 addresses
- [ ] Hover each address — the one that updates live in real-time is HP
- [ ] Right-click the address → "Find out what accesses this address"
- [ ] Note the instruction and module offset shown in the popup
- [ ] Use "Pointer Scanner" → "Scan for this address" with max depth 6
- [ ] Let it run, then filter results that have small, stable offsets
- [ ] Verify: restart game, use the pointer chain, confirm HP still reads correctly

**Record:** `Player.Life.CurHP` chain = `[...]`

---

## Block 2 — Player MaxHP, Mana, ES (30 min)

HP, Mana, and ES are almost certainly in the same struct (Life component),
so their offsets relative to CurHP are small fixed differences.

- [ ] With CurHP address found, look at surrounding memory in ReClass.NET
- [ ] MaxHP is likely 4 bytes before or after CurHP (try offset ±4, ±8)
- [ ] Verify: MaxHP should stay constant while taking damage
- [ ] CurMana: cast a skill, scan for changed value near HP address region
- [ ] MaxMana: stays constant when using skills
- [ ] CurES: take a hit that hits ES, scan near HP region
- [ ] MaxES: stays constant

**Shortcut:** In ReClass.NET, open the Life component address and look for
a cluster of 6 integers that change in the right pattern. This is the
HP/Mana/ES struct layout shown in ShareData.cs:
`[MaxHP][CurHP][Reserved][MaxMana][CurMana][Reserved][MaxES][CurES][Reserved]`

---

## Block 3 — Player Grid Position (45 min)

Position values are floats (32-bit floating point), not integers.

- [ ] Cheat Engine: Value Type = `Float`, scan type = `Unknown initial value`
- [ ] Walk north 5 steps → Next Scan → `Increased value`
- [ ] Walk north 5 more steps → Next Scan → `Increased value`  
- [ ] Walk south → Next Scan → `Decreased value`
- [ ] Repeat until <50 addresses remain
- [ ] Walk east/west to isolate X vs Y coordinates
- [ ] Verify: values should move smoothly and correlate to minimap position
- [ ] Run pointer scanner on both X and Y addresses

**Note:** GridPos is in grid units (1 unit ≈ 23 world units per ShareData.cs).
The values will be in the hundreds to low thousands depending on map size.

---

## Block 4 — Player Level (15 min)

- [ ] Value Type = `4 Bytes`, scan for your character level (e.g. `42`)
- [ ] This will match thousands of things — narrow by also checking:
  - Remaining addresses should NOT change when you take damage
  - Should NOT change when you move
  - SHOULD change when you level up
- [ ] Level is often stored near HP in the Player component
- [ ] Check: is it 4–8 bytes away from the CurHP address you already found?

---

## Block 5 — Gold (15 min)

- [ ] Note exact gold amount (e.g. `12450`)
- [ ] Scan for `12450` as `4 Bytes`
- [ ] Buy something from a vendor for a known cost (e.g. 500 gold)
- [ ] Next Scan → `11950`
- [ ] Should narrow to 1–3 addresses quickly
- [ ] Verify by selling something and confirming the value increases

---

## Block 6 — Area Hash (30 min)

This is a unique identifier for the current zone instance.

- [ ] Scan for `Unknown initial value`
- [ ] Waypoint to a different zone
- [ ] Next Scan → `Changed value`
- [ ] Waypoint back to original zone  
- [ ] Next Scan → `Changed value`
- [ ] After 3–4 zone changes, you should have a small set of candidates
- [ ] The area hash changes EVERY zone entry (even re-entering same zone = new value)
- [ ] The area RAW NAME is a string pointer — look for a pointer near the hash
  that points to ASCII text matching the zone ID (e.g. "g1_1" for Riverbank)

---

## Block 7 — Network Latency / Ping (15 min)

- [ ] Look at your in-game ping display
- [ ] Scan for the integer value shown (e.g. `42`)  
- [ ] Wait for ping to change naturally, Next Scan for new value
- [ ] This is typically in ServerData and very easy to find
- [ ] Good early-win to verify your pointer chain approach works end-to-end

---

## Block 8 — Entity List (1–2 weeks, separate sessions)

This is the hard one. Don't start here — complete blocks 1–7 first.

**Approach A — Count-based:**
- [ ] Clear an area of all monsters
- [ ] Scan for `0` (entity count = 0)
- [ ] Move to area with exactly 5 visible monsters
- [ ] Next Scan → `5`
- [ ] Kill one: Next Scan → `4`
- [ ] This finds the count field — from there, find the array it belongs to

**Approach B — Known entity path:**
- [ ] PoE2 entities have a Path string (e.g. "Metadata/Monsters/...")
- [ ] Search memory for the ASCII string "Metadata/Monsters"
- [ ] Look for repeated occurrences in a contiguous memory region
- [ ] This region IS the entity list — work backwards to find the pointer to it

**Reference:** ShareData.cs uses `GameController.EntityListWrapper.OnlyValidEntities`
which internally is a linked list or array of Entity pointers. Each entity
starts with a vtable pointer (8 bytes), then ID (4 bytes), then other fields.

---

## After Each Block — Run Verifier

```powershell
# In the tools/OffsetVerifier directory:
dotnet run

# After filling in all found offsets, save a baseline:
dotnet run --baseline
```

The verifier will show green checkmarks for valid offsets and red X for
anything that broke after a patch — telling you exactly what to re-discover.

---

## Post-Patch Workflow (5–15 min per patch)

1. Game patches → run `dotnet run` in OffsetVerifier
2. Check which offsets show red
3. For each broken offset, run the corresponding block above
4. Update the Chain array in OffsetVerifier.cs
5. Run verifier again to confirm green
6. Commit the updated offsets to git
