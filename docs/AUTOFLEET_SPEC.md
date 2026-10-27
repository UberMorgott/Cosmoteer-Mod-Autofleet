# Автофлот — Design Spec (module-based revision)

Sources (chat `69e1aa4d26c84cea1f6309eb4c03de0b`, 2026-10-26):

- **Module model:** Codex `c200afe1d665cc1253ef1564398828bd`. It supersedes the role-based and mothership model.
- **Economy, trade and research deltas:** Codex `9639a005a1b046edb509df6939ea2512` (duplicate `3f6586dda2c04a523e1472a41ef2eca3`). Still valid.
- **Base spec:** Codex `754b677c15926e533587997ec55afb5b` (duplicate `e7549657ea8e70c96f0ee085bac73311`). Its role, mothership and phase parts are superseded.

Codex note: evidence is source inspection plus forwarded decompile research. No gameplay testing or file edits were done.

## Recommendation

- One "Автофлот" mod. A ship's behaviour is a user-composed set of behaviour modules. There are no hardcoded roles or role-controller classes.
- **Starter profiles** (editable presets, not code): Шахтёр, Боец, Корова, Строитель, Торговец, Охрана. Users can create more profiles without adding code.
- **Anchors:** any ship can be an anchor. There is no privileged flagship. A fleet can have multiple anchors or none.
- **Preserve existing behaviour:** mining, enemy avoidance, manual-order handling, RU/EN strings, live-selection editing and ship state icons.

## 1. Information architecture (tabs)

- **Window shell:** the tab bar (Назначения, Профили, Настройки; later tabs listed under Настройки until they ship) is the top row; everything below depends on the tab. Fleet tree, target status and commands live inside Назначения; Профили and Настройки show no ship commands. The menu button uses the existing `gui.Rules.FormationIcon`.
- **Флот / Назначения** (replaces Флот / Роли):
  - Fleet command only: profile assignment, anchor/target bindings, work-circle placement, wing membership, manual commands.
  - No logic parameters and no per-ship overrides: the profile is the single source of every logic setting (modules, params, cargo, arbitration, anchor policy incl. follow distance/leash/hysteresis, offload-to-anchor). The ship keeps only fleet command: profile ID, anchor target + formation offset, offload/supply/protected targets, work zone, wing, manual/temp intent, runtime state.
  - **Targets = fleet tree checks**, independent of the game selection: on window open the eligible game selection is imported once, then only the tree (and the "From game selection" button) changes targets. Tree: "All ships" root → profile folders (library order, ↑↓ swap neighbouring folders = swap in the library, saved as list order, same order in Профили) → ships; unmanaged own ships in a last "Not in autofleet" folder. Checkboxes on every level; root/folder state (none / partial / all) counts collapsed ships; a click on a partial node checks all of it; checks are by ship ID (survive regrouping), new ships appear unchecked, gone ships are dropped. Rows: name + active module · state · cargo % (cargo % only with Mine/Loot/Offload active).
  - Top→bottom on the right: target title → status → command bar → quick start, profile, bindings. Mixed values shown for multi-target.
  - **Command bar:** row 1 universal: Start (unmanaged targets only), Pause / Resume (managed; Resume never enrols unmanaged ships), Unassign. Row 2 module commands, derived from each ship's effective (compiled) modules by ShipCommands: Offload → "Unload now" + "To offload point" (both need a resolved offload point); Mine, Loot, Guard, Anchor, Retreat, Avoid add none yet. Union rule: a command shows when any target's profile contributes it, is labelled "— K/N" when only K of N targets can run it, applies only to those K (revalidated on click), and is disabled with the reason as tooltip at K=0. "Cancel return" stays available for ships already returning even after the profile lost Offload.
  - Shows the active module, the preemption reason and blockers.
- **Профили** (module composer):
  - Add, remove and enable modules. Edit parameters and activation conditions.
  - Order task priorities.
  - Inspect module dependencies and conflicts, and preview the resolved behaviour.
  - Starter archetypes can be duplicated and edited.
  - Live profiles (no revisions): every edit, rename included, reaches all ships bound to the profile on their next tick; there is no apply/update step. A profile used by a running ship can't be deleted (the UI says how many; reassign first). On delete, drafts bound to it move to the default profile; enabled assignments of ships not running now (other save, stasis) keep the ID and idle with a "profile not found" blocker until a profile is picked.
- **Other tabs** (Строительство, Логистика / Экипаж, Экономика, Настройки) stay. Their orders and policies bind to capable ships, groups or modules, never to a hardcoded role.
- **Строительство:**
  - Blueprint/profile bindings, builder ship/group assignment, finite orders, desired fleet composition and the replacement queue.
  - Shows materials, crew requirements, reservations and commissioning progress.
  - Blueprint bindings reference profiles by ID (live, like assignments). Anchor/target bindings that can't be resolved during commissioning are shown.
- **Логистика / Экипаж:**
  - Repair rendezvous, support providers, cargo transfers, crew minimums/targets, redistribution and capped hiring.
  - Shows pending operations and inaccessible resources.
- **Экономика:**
  - Per-resource fleet stock table with: protected minimum, desired stock, actual available stock, reserved/incoming/outgoing quantities, production target, buy/sell policy.
  - Subviews: stocks, production, trade routes/orders.
  - Exposes budgets and price limits.
- **Настройки:**
  - Local presentation preferences, notifications and automation defaults.
  - Campaign policy scopes, spending limits and purchase allowances are kept in campaign state.
- **Editing semantics:**
  - Ship inspectors edit the window targets (checked ships). Multi-target shows mixed values and changes only the fields the user touched.
  - Explicit wing and policy-scope editors change those objects.
  - Resolution order: defaults → live profile → permitted wing policy. No ship-level overrides.
  - Fleet safety and spending limits always apply.

## 2. Data model

- **Role model removed:** there is no exclusive `Role` enum and no role controllers.
- **`BehaviourModuleDefinition`:**
  - A registered capability type with schema/version, parameter descriptors, supported intent channels, dependencies and conflicts.
  - Code implements module handlers, not archetype classes.
- **`BehaviourModuleInstance`:**
  - Fields: stable instance ID, definition type/version, enabled flag, parameters, activation conditions, priority.
  - Runtime state, cooldowns and pending-operation IDs are kept separate from configuration.
- **`Profile`:**
  - Fields: stable ID, name, module instances, parameters, arbitration settings, cargo policy, binding defaults. There is no mandatory role field and no revision.
  - Assignments and build orders reference the profile by stable ID and read it live: an edit applies to every ship using it at once. The resolver recompiles a ship's config whenever the profile library changes (`Store.LibraryVersion`).
  - Game-side effects follow automatically: the effective keep-on-board amounts are pushed as vanilla resource desire (`MPSetResourceDesireInput` via `NetManager.EnqueueInput`, owner client only) once they have been stable for 0.5 s and differ from the last push; absent types stay unmanaged.
  - Schema 2 (live). Schema 1 files load: the pinned `Snapshot` becomes `ProfileId`, `Revision`/`Modified` keys are dropped, other unknown fields are kept. Per-ship override keys written by older builds (`ParamOverrides`, `EnabledOverrides`, `CargoOverride`, `ResumeAfterManual`, `MissingPolicy`, `FollowDistance`, `OffloadToAnchor`) are dropped on load (state backed up once to `Autofleet.state.json.overrides.bak`); profiles are never changed by this. An unknown profile ID leaves the ship idle with a "profile not found" blocker.
- **`ShipAssignment`:**
  - Fields: fleet-slot/current ship identity, owner, profile ID, target bindings, optional wing, automation/manual state.
  - The UI shows the profile name and the active behaviour instead of a fixed role.
- **`AnchorLink`:**
  - An optional reference to any valid ship or fleet slot.
  - Parameters: follow distance, formation offset, leash, missing-target policy.
  - No privileged flagship. Different ships can reference different anchors, and a ship can operate without one.
- **Target bindings:**
  - Separate bindings: `anchor`, `offloadTarget`, `supplyTarget`, `protectedTarget`, and a fixed work zone.
  - A module can explicitly reference the anchor or another binding.
  - Changing the anchor must not silently redirect every transfer destination.
- **Anchor safety:**
  - Linking to a ship gives no command authority over it.
  - Moving targets are resolved with hysteresis. Cyclic follow dependencies are rejected.
  - If the target is lost or goes into stasis, the configured wait/rally/fallback behaviour applies. The ship doesn't assume the target was destroyed and doesn't silently pick another anchor.
- **Policy scopes** (replace `FleetDoctrine`):
  - Shared policy scopes for fleet, wing and economy coordination remain; mandatory mothership semantics are removed.
  - Scope IDs determine stock accounting and reservations. Independent anchor groups don't have to share supplies.
- **`Wing`:**
  - Fields: leader, member fleet slots, mission, formation offsets, engagement/leash rules, retreat/rally policy.
  - Builder groups share work allocation. Support members keep their non-combat restrictions.
  - Source: base spec `754b677c`. The module revision didn't restate it; it keeps "optional wing".
- **`BlueprintBinding` / `FleetSlot`:**
  - Fields: blueprint identity/content revision, default profile, desired membership, replacement policy.
  - A rebuilt ship takes over its slot even though it gets a new ObjectID.
  - An unavailable blueprint revision blocks the slot, and the block is visible.
- **`BuildOrder`:**
  - Fields: stable order ID, builder/group, blueprint/profile ID, quantity or replacement slot, priority, reservations, submission evidence, produced ship IDs.
  - States: queued, blocked, submitted, constructing, commissioning, completed, reconciliation-required.
- **`StockPolicy`:**
  - Per resource: protected fleet minimum, desired stock, replenishment trigger, production/buy preference, surplus-sale permission, ship allocations, price/spending limits.
  - Individual ship reserves are preserved.
  - Fleet targets are split across ships; the full target is not applied to every ship.
- **`TradeOrder`:**
  - Fields: stable ID, trader, station, resource/direction, bounded quantity, quote/price limit, budget reservation, transfer-job IDs, delivery destination.
  - States: loading, travel, quoting, transferring, returning, unloading, blocked, reconciliation.
- **Persistence:**
  - Replace the mod-folder, ObjectID-only storage with versioned state bound to the exact save snapshot. This covers policy scopes, stock policies, production allocations, trade orders and reservations.
  - Embedded save integration is preferred if the game supports it. A campaign ID alone isn't enough when an older save is loaded.
  - Keep the reusable profile and blueprint libraries separate. Migrate existing settings without deleting them.
- **Reconciliation:**
  - After a load or reconnect, first resolve live and stasis ships, crew, transfer jobs, and submitted construction/trade operations. Only then issue replacements or retries.
  - A ship missing from the live list is never proof of destruction.
  - A confirmed loss creates at most one replacement per eligible fleet slot.

## 3. Architecture

- **Architecture / MP:**
  - `FleetDirector` observes → shared services allocate jobs → active modules propose intents → a per-ship arbiter selects → adapters call `NetManager.EnqueueInput`.
  - One deciding client per owned ship.
  - Authority handoff for shared control and disconnects must be defined.
- **Module catalog:**
  - Anchor-link, Guard, Hunt, Mine, Loot, Offload/Supply, Support, Build, Trade, Crew logistics, Retreat/Repair policy, Avoidance policy.
  - Production targets stay composable economy behaviour, implemented either as a Production module or as an explicit economy-policy binding.
- **Module execution:**
  - Active modules observe the shared snapshot and propose intents. They never issue competing commands directly.
  - Policy modules contribute constraints and retreat decisions.
  - Shared services provide routing, reservations and operation tracking.
- **Arbitration:**
  - Order: manual orders and explicit pause → triggered emergency retreat → eligible task intents by configured priority (e.g. Guard > Mine).
  - Permissions, resource limits and configured safety constraints are applied before selection.
  - Ties are broken in a stable order. A minimum commitment time and hysteresis prevent flip-flopping.
- **Concurrent behaviour:**
  - Navigation/combat ownership is arbitrated separately from compatible background work. For example, following can run alongside production or crew planning.
  - Only one module owns movement at a time.
  - Resource and job reservations stop Build, Trade and Supply from committing the same assets.
- **Preemption:**
  - An interrupted module keeps its resumable goals, releases unused reservations and cancels only its own cancellable operations.
  - Submitted transfers and builds still have to be observed and reconciled. Switching to another intent can't erase work already committed.
- **Reservations:** one ledger reserves materials, crew, money and production output. Commands carry provenance, and ambiguous outcomes are reconciled before any retry.
- **Existing-miner migration:**
  - mining → Mine
  - wreck/loose collection → Loot
  - base unloading/reserves → Offload
  - moving work area (`MinerTask.Escort`) → Anchor-link plus anchor-relative Mine/Loot scope
  - enemy Avoid → Avoidance policy
  - Flee → Retreat policy
  - Attack → reactive Guard/defence
  - Filters, thresholds and manual-resume semantics are preserved.
  - Return-home and unload-now stay temporary intents, not profiles or roles.
- **Repair / construction:**
  - Uses the whitelisted `MPSetConstructionAssistanceInput`, `MPUpdateShipInput` and `MPNewShipInput`.
  - Any profile with a Build module can build. Replacement doesn't depend on an archetype name.
  - The "cow" supplies crew/material assistance; there are no repair beams.
  - Respects the Career block on building near recent enemy fire, and the new-ship requirements: visibility, allied proximity, a safe location and construction costs.
- **Crew / hiring:**
  - Redistribution uses `MPSetCrewHomeShipInput`, `MPSetCrewSourceInput` and `MPUpdateRoleInput`. Donor operational minimums are preserved, and crew away or in transit are counted.
  - Hiring uses `CommHireTab.MPHireCrewInput`. Pending hires are reserved against target, configured allowance, capacity, fame and budget.
  - Not enough crew blocks missions. "Never crewless" can't cover combat casualties.
- **Economy planning:**
  - Protected reserves and committed repair/build needs are met before any discretionary production or sales.
  - Internal transfers don't increase fleet stock.
  - Each shortage is allocated once across mining, production and purchases.
  - Sale cargo is reserved, so two traders can't sell the same surplus.
- **Production feasibility:**
  - `ResourceConverter` with `CheckResourceLimits` looks at the ship's resource desire and capacity.
  - Targets are set through the whitelisted `ResourceLimitsWindow.MPSetResourceDesireInput` / `MPSetResourceLimitInput`. Factories are controlled through `MPSetUITogglesInput` where supported.
  - Recipes and input reserves are coordinated across producer ships.
- **Production caveat:**
  - `ResourceConverter.cs:403–412` applies desire only when `resourceDesire > 0`. **A desire of zero does not stop production.**
  - For completed or zero quotas, use the supported factory controls.
  - Account for batch size, factories running at the same time, and conversions already in progress. Exact arbitrary output caps need more validation.
- **Station trade is feasible:**
  - Uses the base-whitelisted `MPMoveResourcesInput(source, sink, amount, manualID, sticky, TradeType.Buy/Sell, pricePerResource)`.
  - `CommTradeTab.cs:376` uses the same transfer path, so opening comms isn't needed.
  - Cargo moves through vanilla crew transfer jobs.
- **Trade conditions:**
  - `MPMoveResourcesInput.cs:115–175` checks crew-transfer distance, command permission, valid source/sink, capacity, trade eligibility, current price and purchase funds.
  - In Career the counterparty's `AI.WillTradeWith` must allow it; station operation, hostility and FTL state can block it.
  - This input path has no physical docking requirement, but usable crew/airlock access still matters.
- **Trader policy:**
  - Pick known eligible stations via safe routes. Load only reserved surplus. Get a fresh quote on arrival.
  - Buy only the remaining deficit, within price, budget and capacity limits. Then return (to the bound target/anchor) and distribute.
  - Don't spend expected sale money until the sale is confirmed.
  - Partial transfers, rejected prices and the station leaving all need reconciliation.
  - The trader's safety takes priority over finishing the trip.
- **Combat micro:**
  - Vanilla facing optimises firepower only, so shield-side/armour preservation, protecting endangered weapons and tactical retreat are implemented separately.
  - Inputs: shield arc/health/operational data, weapon coverage/resources and local power observations.
  - Weigh rotation time and exposure, keep firing arcs where practical, and prevent oscillation.
  - Recharge/ceasefire decisions must reflect actual shared power pressure. Reactor output alone doesn't prove power is available locally.
- **Vanilla AI decision:**
  - Baseline: the owner-client planner using whitelisted inputs.
  - `ShipAiMenu.MPSetAIInput` is blocked in Career. Enabling it would need an explicit whitelist patch, matching clients and saved-AI compatibility.
  - Vanilla AI replaces commands and lacks the requested advanced micro.
  - An optional hybrid needs exclusive control ownership.

## 4. Phases

Source: `c200afe1`. It supersedes the phase lists in `754b677c` and `9639a005`.

- **Phase 0 — composition foundation:**
  - Module schemas, validation, intent arbitration, provenance, reservations and exact-save reconciliation.
  - *Acceptance:* priority resolution is deterministic, and preemption or reload never duplicates committed operations.
- **Phase 1 — miner parity / composer MVP:**
  - Migrate current settings into editable module profiles. Deliver the assignment/anchor UI and the composer.
  - *Acceptance:* anchored and unanchored mining, multiple independent anchors, mixed selection and manual interruption all keep existing behaviour.
- **Phase 2 — combat composition:**
  - Guard, Hunt, formation-capable Anchor-link, and Retreat/Avoidance policies.
  - *Acceptance:* Guard preempts Mine, retreat preempts Guard, interrupted work resumes, and anchor movement or loss leads to bounded recovery.
- **Phase 3 — support/logistics:**
  - Support, Supply and Crew logistics modules. Safe repair rendezvous, construction assistance and capped hiring.
  - *Acceptance:* combined profiles keep donor minimums and don't duplicate transfers or hires.
- **Phase 4 — economy:**
  - StockPolicy integration, production allocation and the Trade module. Finite orders come before automatic replenishment and surplus sales.
  - *Acceptance:* separate and shared economy scopes behave correctly, and pending production/purchases count toward targets and budgets.
- **Phase 5 — construction:**
  - Build module, finite blueprint orders, profile inheritance and binding resolution, commissioning and confirmed-loss replacement.
  - *Acceptance:* any capable profile can build, and replacement doesn't depend on an archetype name.
- **Phase 6 — advanced tactics / integration:**
  - Add damage-aware facing, weapon protection and power/recharge policies to the same arbitration system.
  - Validate representative composed profiles in SP and two-client Career MP.
  - Keep all established Career restrictions on inputs, trade, hiring and construction.

## 5. Risks / validation

- Major remaining risks: exact save integration, ownership changes, stasis, resource accessibility, production batches, local power estimation, and transfers to moving stations.
- Cross-sector/FTL fleet coordination needs its own validated policy.
- SP and two-client Career MP checks are required before claiming the system is operationally reliable.

## Research findings (decompile-verified, from lead addenda)

### Vanilla AI on player ships

- Creative `ShipAiMenu.MPSetAIInput` (ShipAiMenu.cs:29-75) sets `ship.AI` without an owner check.
  - It is NOT whitelisted in Career (GameModeManager.cs:602, CareerGameModeManager.cs:951). Using it needs a Harmony `AllowMPInput` postfix, and every client must have the mod.
- `ShipAI.FixedUpdate` (ShipAI.cs:774-819) is deterministic and included in the state hash (Ship.cs:1297).
- Ship AI is saved (Ship.cs:1253-1258), so saves that use it need the mod.
- The AI uses `EnqueueCommand(append:false)`, which overrides player orders. Civilian ships are only down-weighted, not excluded.

### Vanilla combat AI

- Weak overall. Facing only maximises the ship's own firepower (AttackCommand.cs:243-259).
- No shield-side choice, weapon protection, recharge wait, power awareness, damage-based retreat or formation.
- Available modules:
  - `AggroEnemies`
  - `DefaultAttackRadiusAndRotation`
  - `RandomAttackAngle` (`CoordinateFlanking`)
  - `ValueTargeter`
  - `FleeFromEnemies` (range only)
  - `SelfRepair` (`RepairDamaged` / `RebuildDestroyed`)
  - `AreaPatrol`
  - `FollowFleetLeader` (`MPMakeLeadShipInput`)
  - `HoldPosition`

### Repair

- `SelfRepair` works through `ShipUpdateInfo`.
- Career blocks building near recent enemy fire (CareerSimModeManager.cs:700-716).
- Assisting other ships uses `MPSetConstructionAssistanceInput` (Off/Owned/Allied), which is whitelisted.
- There are no repair beams.

### Rebuild

- `MPNewShipInput(RulesID, Name, PlayerIndex, Location, Roles, Paint)` is whitelisted.
- It is gated by `AllowStartNewShip`: the site must be in sight, near an allied ship and away from enemies (CareerSimModeManager.cs:737-757).
- It is followed by `MPUpdateShipInput` with flags `Build|Repair|UpdateDestroyed|AllowPartialConstruction|ApplyBlueprintMetadata|BlueprintsOnly`. Cost is checked.

### Crew

- `MPSetCrewHomeShipInput`, `MPSetCrewSourceInput` and `MPUpdateRoleInput` are whitelisted.
- Hiring uses `CommHireTab.MPHireCrewInput(From, To, Soul, Source)`, which is whitelisted.
  - `From` must be a station or trade ship with `AICrewProviderModule`.
  - Range: `LoseHomeShipWarningDistance`.
  - Caps: `RemainingCrewCapacity`, `GetCurrentMaxCrew`, fame.
- No input for selling crew was found.

### Planner data

- `ArcShield`: `EffectiveHealth`, `MaxEffectiveHealth`, `DamageFraction`, `DetWorldArc`, `IsOperational`.
- Shield HP is the battery charge in generator storage.
- Power = sum of reactor `BatteryProducer` output vs. consumers. There is no ship-wide field for it.
- Weapons: `Weapon.IsInCoverage`, `HasResources`, `GetTotalFirePower`.
- Hull ratio: `BasePhysicalValue` / `BaseBlueprintCost`; destroyed parts via `DestroyedParts`.

### Trade

- Trade uses `MPMoveResourcesInput(source, sink, amount, manualID, sticky, TradeType.Buy/Sell, pricePerResource)` (MPMoveResourcesInput.cs:70), which is whitelisted in the base game.
- Checks at :115-175:
  - within `LoseHomeShipWarningDistance`
  - `CanExecuteCrewCommandsFrom`
  - `AllowResourceTrade` (CareerSimModeManager.cs:1198-1205) → `callee.AI.WillTradeWith`: no enemies nearby, station operating, no FTL prep (AITradeModule.cs:35-53)
- The price passed must equal the current price, or the input is rejected. Buying checks `PlayerState.Money`.
- No comms UI is needed. Crew carry the cargo (`ManualResourceTransfer`).
- Prices: `ResourceRules.BuyPrice` / `SellPrice ?? BuyPrice` (SimModeManager.cs:414-426), multiplied by the reputation factors `ResourceBuyCostFactor` / `ResourceSellCostFactor` and by `EconDifficulty.ResourceSaleRefund` (CareerSimModeManager.cs:771-802).
- Money inputs: `MPTransferMoneyInput`, `CommTechsTab.MPBuyTechInput`.

### Production

- Factories are `ResourceConverter`s. `CheckResourceLimits` caps output at min(capacity, `ResourceDesire`) only when the desire is > 0 (ResourceConverter.cs:403-412, `if (resourceDesire > 0)` at :408).
  - **Resolved (D19, decompile-verified):** a desire of 0 removes the limit and does NOT stop a factory. Completed quotas are stopped by switching the factory off via `MPSetUITogglesInput` (`on_off`); the Production module does this.
- Per-ship targets are set with `ResourceLimitsWindow.MPSetResourceDesireInput` / `MPSetResourceLimitInput` (ResourceLimitsWindow.cs:390-510). Both are whitelisted and need `IsCommandableBy`.
- A factory is switched on/off with `MPSetUITogglesInput` (toggle `on_off`).

### Lead recommendation

- Build an own planner on whitelisted MP inputs (the MiningDrone pattern), with a single deciding client (the owner).
- Optional hybrid: use vanilla `SelfRepair` / `ValueTargeter` through a whitelist patch.
- Custom `[SerialDerivedType]` AI modules are plausible but unverified (load order), and they would tie saves to the mod.

## User requirements: logistics & trade (P3/P4 input, 2026-10-26)

- Per-ship stock policy both ways: keep at least N (existing keep) AND keep at most M ("держать не больше"); surplus above M is pickup-eligible.
- Production chains: ships refine raw ore into processed goods (e.g. ore → metal) up to targets; factories for unwanted outputs can be switched off; surplus is sold.
- Stations are known on the sector map → traders fly there and sell. A station may be full / refuse: retry later when space frees, or try other stations; never drop cargo.
- Hauler archetype (Торговец/Логист): never mines, never fights, flies fast, avoids enemies; moves cargo between fleet groups anywhere on the map (e.g. combat wing far away fights/repairs/loots on site; haulers come, collect surplus above caps, bring it to a warehouse ship).
- Warehouse ship sells by itself when near a station; if every warehouse is full and nowhere to unload, the hauler sells directly.
- All of this must be configurable comfortably in the UI (profiles + bindings: pickup sources, deliver-to warehouse, sell policy, stations allow/deny).

## Open gaps (still uncovered / unresolved)

- **Cross-sector / FTL fleet coordination:** Codex says it needs a separate validated policy. No policy is defined.
- **Embedded save data:** marked "preferred if supported". Whether the game supports it is unverified, and no fallback mechanism for exact-snapshot binding is specified.
- **Production stop semantics:** zero-`ResourceDesire` question resolved (D19: desire 0 does not stop factories, ResourceConverter.cs:408; stop = `on_off` toggle). Arbitrary output caps (batch size, conversions in progress) still overshoot by up to one batch per factory.
- **Local power estimation:** there is no ship-wide power field, and how to estimate power locally isn't specified.
- **Custom `[SerialDerivedType]` AI modules:** load-order feasibility isn't addressed in the spec.
- **Trader station discovery:** "known eligible stations" — the source of the station list and how it's discovered aren't specified.
- **Hiring range:** `MPHireCrewInput` only works within range of a station or trade ship. The spec doesn't say which module drives travel to a hire source.
- **Module details:** per-module parameter schemas, activation-condition syntax and the default priority order of the starter profiles aren't defined. The only given example is Retreat > Guard > Mine.
- **Starter profile contents:** the module set for each of Шахтёр, Боец, Корова, Строитель, Торговец and Охрана isn't specified.
- **Wing vs AnchorLink:** how wing formation/leader relates to an AnchorLink formation offset isn't defined.
- **Production module vs economy-policy binding:** Codex left this open.
- **Wing / BlueprintBinding / BuildOrder:** carried over from `754b677c`. The module revision doesn't restate them.
