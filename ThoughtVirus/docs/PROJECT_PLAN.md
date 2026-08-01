# Thought Virus — Project Plan

## Vision
A satirical pandemic-style simulation where **memes/ideologies spread via social media platforms** across a real-world map. Player controls either the **Meme** (offense: evolve, spread, monetize) or the **Ministry of Truth** (defense: legislative simulation to contain spread).

**Core loop**: Real-time simulation (variable speed) → Platform algorithms amplify → Demographics adopt beliefs → Mutations unlock new traits → Legislative response → Win/lose.

---

## Scope (MVP)

| Aspect | Decision |
|--------|----------|
| **Map** | Full world (195 countries), GeoJSON borders |
| **Scenario** | "Algorithm Pipeline" — US-origin conspiracy → ideology → grift → cult mutation chain |
| **Meme Types** | 4: Conspiracy, Ideology, Grift, Cult (unique mechanics each) |
| **Defense** | Legislative simulation: Draft → Committee → Floor → Vote → Implement → Court |
| **Render** | World geographic map + force-directed platform overlay |
| **Architecture** | Deterministic, seed-based (async leaderboard ready) |
| **Framework** | .NET 10, MonoGame (DesktopGL), MessagePack, NetTopologySuite |

---

## Project Structure

```
ThoughtVirus/
├── src/
│   ├── ThoughtVirus.Core/           # Pure simulation (no rendering deps)
│   │   ├── Simulation/
│   │   │   ├── World.cs
│   │   │   ├── Country.cs
│   │   │   ├── Platform.cs
│   │   │   ├── Meme.cs
│   │   │   ├── Policy.cs
│   │   │   ├── Bill.cs              # Legislative bill state machine
│   │   │   ├── TimeController.cs
│   │   │   ├── SpreadSystem.cs
│   │   │   ├── PlatformSystem.cs
│   │   │   ├── LegislativeSystem.cs
│   │   │   ├── EvolutionSystem.cs
│   │   │   └── EventSystem.cs
│   │   ├── Data/
│   │   │   ├── WorldDataLoader.cs
│   │   │   ├── ScenarioLoader.cs
│   │   │   ├── MemeTemplates.cs
│   │   │   └── PolicyTemplates.cs
│   │   ├── Math/
│   │   │   ├── BeliefDynamics.cs
│   │   │   └── NetworkFlow.cs
│   │   └── Serialization/
│   │       └── MessagePackContext.cs
│   ├── ThoughtVirus.Render/         # MonoGame rendering
│   │   ├── Map/
│   │   │   ├── WorldMapRenderer.cs
│   │   │   ├── CountryMesh.cs
│   │   │   └── BeliefHeatmapShader.fx
│   │   ├── Overlay/
│   │   │   ├── PlatformGraphOverlay.cs
│   │   │   ├── ForceDirectedLayout.cs
│   │   │   └── PlatformHub.cs
│   │   ├── UI/
│   │   │   ├── MemePanel.cs
│   │   │   ├── LegislativePanel.cs
│   │   │   ├── CountryPanel.cs
│   │   │   └── Charts/
│   │   └── Camera.cs
│   └── ThoughtVirus.App/            # Entry point, DI, game loop
├── tests/
│   ├── ThoughtVirus.Core.Tests/
│   └── ThoughtVirus.Integration.Tests/
├── assets/
│   ├── data/
│   │   ├── countries.geojson
│   │   ├── demographics.json
│   │   ├── platforms.json
│   │   └── scenarios/
│   │       └── algorithm-pipeline.json
│   └── fonts/
└── ThoughtVirus.sln
```

---

## Core Data Models

### Country (replaces Pandemic Country)
```csharp
class Country {
    string Id, Name;                    // ISO3, "United States"
    int Population;
    Dictionary<string, float> PlatformAffinity; // platformId → 0-1 usage
    float TrustInInstitutions;          // 0-1
    float Polarization;                 // 0-1
    float MediaLiteracy;                // 0-1
    Dictionary<string, float> Beliefs;  // memeId → belief strength 0-1
    Demographics Demographics;          // Age/education/urban splits
}
```

### Platform (replaces Airports/Ports)
```csharp
class Platform {
    string Id, Name;                    // "tiktok", "x", "youtube", "facebook", "discord", "telegram", "wechat", "vk", "line"
    int MonthlyActiveUsersGlobal;
    float AlgorithmViralityBoost;       // 0-1, multiplies meme virality
    float ModerationStrictness;         // 0-1
    Dictionary<string, int> MAUByCountry; // Country-specific reach
    PlatformPolicy CurrentPolicy;
}
```

### Meme (replaces Pathogen)
```csharp
class Meme {
    string Id, Name;
    MemeType Type;                      // Conspiracy, Ideology, Grift, Cult
    // 5 Traits (0-10, upgraded with EngagementRevenue)
    int Virality;       // Base spread rate
    int Conviction;     // Resistance to debunking
    int Adaptability;   // Mutation speed, bypass moderation
    int Grift;          // Engagement $ per believer per tick
    int Tribalism;      // In-group reinforcement
    
    float EngagementRevenue;            // DNA points equivalent
    List<string> UnlockedMutations;
    Dictionary<string, float> BeliefByCountry;
}
```

### Bill (Legislative Simulation)
```csharp
class Bill {
    string Id, Name;                    // "Truth in Algorithms Act"
    BillStage Stage;                    // Drafting, Committee, Floor, Vote, Implementation, Court
    List<PolicyAction> Actions;         // FactCheck, Downrank, Demonetize, Ban, Prebunk, MediaLiteracy
    int PoliticalCapitalCost;
    float PublicApprovalImpact;
    int SponsorIdeology;                // Affects committee survival
    int TicksInStage;
    BillOutcome? Outcome;               // Passed, Failed, Enjoined, StruckDown
}
```

---

## Simulation Loop (Per Tick = 100ms base)

```
1. TimeController.Advance(dt * SpeedMultiplier)  // 0, 1, 2, 4, 8x
2. PlatformSystem:
   - Apply algorithm events (viral boost, shadowban waves)
   - Apply active moderation policies
3. SpreadSystem (per meme × country):
   beliefDelta = Virality × PlatformBoost × Affinity × (1-MediaLiteracy) × TribalismMod
   - Cross-border via platform shared demographics
4. LegislativeSystem:
   - Advance bills through stages (RNG + whip count + public opinion)
   - Implemented policies reduce spread
   - Court challenges can enjoin/strike down
5. EvolutionSystem:
   - Adaptivity thresholds trigger mutations
   - Spend EngagementRevenue → upgrade traits
6. EventSystem:
   - Scandals, elections, platform policy changes, viral moments
7. Win/Lose:
   - Offense: Any meme >50% belief in >50% world population
   - Defense: All memes <5% belief + Approval >30%
```

---

## Mutation Chain: "Algorithm Pipeline"

| Stage | Meme | Type | Key Traits | Trigger |
|-------|------|------|------------|---------|
| 1 | "Skibidi Conspiracy" | Conspiracy | High Virality, Low Conviction | Start (US) |
| 2 | "Alpha Male Ideology" | Ideology | High Tribalism, Medium Conviction | Adaptivity ≥ 5 |
| 3 | "Crypto Grift Token" | Grift | High Grift, Medium Virality | Adaptivity ≥ 8 |
| 4 | "Doomer Cult" | Cult | High Conviction, High Tribalism | Adaptivity ≥ 10 |

---

## Rendering Architecture

| Layer | Technique |
|-------|-----------|
| **World Map** | GeoJSON → triangulated meshes (build-time), `Texture2D` per country, belief heatmap shader |
| **Platform Overlay** | Force-directed graph (Verlet integration): 10 platform hubs, edges to countries weighted by MAU |
| **Camera** | Pan/zoom, auto-focus on outbreak, smooth interpolation |
| **UI** | monogame.extended GUI: panels, charts, bill tracker, trait trees |

---

## Data Files Required

| File | Source | Records |
|------|--------|---------|
| `countries.geojson` | Natural Earth | 195 |
| `demographics.json` | GWI, DataReportal, UN | 195 × age/edu/urban splits |
| `platforms.json` | Statista, company reports | 10 platforms |
| `algorithm-pipeline.json` | Custom scenario | 1 |

---

## Development Commands

```bash
# Create solution
dotnet new sln -n ThoughtVirus
dotnet new classlib -n ThoughtVirus.Core -o src/ThoughtVirus.Core -f net10.0
dotnet new classlib -n ThoughtVirus.Render -o src/ThoughtVirus.Render -f net10.0
dotnet new console -n ThoughtVirus.App -o src/ThoughtVirus.App -f net10.0
dotnet new xunit -n ThoughtVirus.Core.Tests -o tests/ThoughtVirus.Core.Tests -f net10.0
dotnet sln add src/ThoughtVirus.Core src/ThoughtVirus.Render src/ThoughtVirus.App tests/ThoughtVirus.Core.Tests

# References
dotnet add src/ThoughtVirus.App reference src/ThoughtVirus.Core src/ThoughtVirus.Render
dotnet add src/ThoughtVirus.Render reference src/ThoughtVirus.Core
dotnet add tests/ThoughtVirus.Core.Tests reference src/ThoughtVirus.Core

# Packages
dotnet add src/ThoughtVirus.Core package MessagePack
dotnet add src/ThoughtVirus.Core package NetTopologySuite
dotnet add src/ThoughtVirus.Core package System.Text.Json
dotnet add src/ThoughtVirus.Render package MonoGame.Framework.DesktopGL
dotnet add src/ThoughtVirus.Render package monogame.extended

# Run
dotnet run --project src/ThoughtVirus.App -- --render --seed 1337                # Full game (render mode)
dotnet run --project src/ThoughtVirus.App -- --headless --scenario algorithm-pipeline --seed 12345 --ticks 50000 --output results.csv
dotnet test ThoughtVirus.sln
```

---

## Phase Breakdown

| Phase | Weeks | Deliverable |
|-------|-------|-------------|
| **1. Core Engine** | 3 | Country, Platform, Meme, Bill, SpreadSystem, LegislativeSystem, EvolutionSystem, TimeController, headless runner, CSV output, unit tests |
| **2. Data & Content** | 1 | GeoJSON processing, demographic/platform JSON, scenario file, trait balance |
| **3. Render - Map + Overlay** | 2 | World map renderer, belief heatmap, force-directed platform graph, camera |
| **4. Render - UI** | 2 | Meme panel, Legislative panel, Country panel, Global charts, scenario select |
| **5. Integration & Polish** | 1 | Connect render↔core, save/load, win/lose screens, settings, CSV export |

---

## MVP Acceptance Criteria

1. **Headless**: Runs "Algorithm Pipeline" for 50k ticks, outputs per-country belief CSV
2. **Deterministic**: Same seed = identical CSV
3. **Render**: World map loads, platform overlay animates, country click → panel
4. **Legislative**: Draft → pass → implement a bill, observe spread reduction
5. **Mutation**: Starting meme mutates through all 4 types at correct thresholds
6. **Tests**: >80% coverage on SpreadSystem, LegislativeSystem, EvolutionSystem

---

## Risks & Mitigations

| Risk | Mitigation |
|------|------------|
| GeoJSON triangulation slow at runtime | Pre-process to binary mesh at build |
| 195 × 10 platforms = perf | Spatial partitioning, dirty flags, 10Hz sim tick |
| Legislative scope creep | Hard-code bill templates for MVP |
| Satire tone calibration | Flavor text in JSON, iterate without recompile |

---

## Next Steps

1. Confirm plan matches vision
2. Begin Phase 1: Create solution, Core models, WorldDataLoader with GeoJSON
3. Weekly check-ins on simulation balance
