# Phase 1: Core Engine (Weeks 1-3)

## Week 1: Project Setup + Data Models

### Day 1-2: Solution Bootstrap
| Step | Command | Purpose |
|------|---------|---------|
| Create solution | `dotnet new sln -n ThoughtVirus` | Root |
| Core lib | `dotnet new classlib -n ThoughtVirus.Core -f net10.0` | Pure simulation |
| Core tests | `dotnet new xunit -n ThoughtVirus.Core.Tests -f net10.0` | Unit tests |
| App | `dotnet new console -n ThoughtVirus.App -f net10.0` | Headless + entry |
| Add to sln | `dotnet sln add src/ThoughtVirus.Core src/ThoughtVirus.Render src/ThoughtVirus.App tests/ThoughtVirus.Core.Tests` | |
| Reference | `dotnet add src/ThoughtVirus.App reference src/ThoughtVirus.Core src/ThoughtVirus.Render`<br>`dotnet add src/ThoughtVirus.Render reference src/ThoughtVirus.Core`<br>`dotnet add tests/ThoughtVirus.Core.Tests reference src/ThoughtVirus.Core` | |
| Packages | `dotnet add src/ThoughtVirus.Core package MessagePack`<br>`dotnet add src/ThoughtVirus.Core package NetTopologySuite`<br>`dotnet add src/ThoughtVirus.Core package NetTopologySuite.IO.GeoJSON`<br>`dotnet add src/ThoughtVirus.Core package System.Text.Json` | Serialization, GeoJSON, config |

### Day 2-3: Directory Structure
Create folders matching the plan:
- `src/ThoughtVirus.Core/Simulation/`
- `src/ThoughtVirus.Core/Data/`
- `src/ThoughtVirus.Core/Math/`
- `tests/ThoughtVirus.Core.Tests/`

### Days 3-4: Core Enums + Structs
| File | Contents |
|------|----------|
| `ThoughtVirus.Core/Simulation/MemeType.cs` | `enum { Conspiracy, Ideology, Grift, Cult }` |
| `ThoughtVirus.Core/Simulation/BillStage.cs` | `enum { Drafting, Committee, Floor, Vote, Implementation, Court, Completed }` |
| `ThoughtVirus.Core/Simulation/BillOutcome.cs` | `enum { Passed, Failed, Enjoined, StruckDown }` |
| `ThoughtVirus.Core/Simulation/PolicyAction.cs` | `enum { FactCheck, Downrank, Demonetize, Ban, Prebunk, Transparency }` |

### Days 4-5: Country Model
| Field | Type | Notes |
|-------|------|-------|
| `Id` | `string` | ISO3, e.g. "USA" |
| `Name` | `string` | "United States" |
| `Population` | `int` | |
| `PlatformAffinity` | `Dictionary<string, float>` | platformId → 0-1 |
| `TrustInInstitutions` | `float` | 0-1 |
| `Polarization` | `float` | 0-1 |
| `MediaLiteracy` | `float` | 0-1 |
| `Beliefs` | `Dictionary<string, float>` | memeId → 0-1 |
| `BoundingBox` | `Envelope` | GeoJSON envelope for rendering |
| `Polygon` | `Geometry` | NetTopologySuite polygon |

### Days 5-7: Platform Model
| Field | Type | Notes |
|-------|------|-------|
| `Id` | `string` | "tiktok" |
| `Name` | `string` | "TikTok" |
| `MAUGlobal` | `int` | |
| `AlgorithmBoost` | `float` | 0-1, multiplier on spread |
| `ModerationStrictness` | `float` | 0-1, reduces spread |
| `MAUByCountry` | `Dictionary<string, int>` | CountryId → users |
| `ConnectedCountries` | `List<string>` | Countries with >1M users |

### Days 6-7: Meme Model
| Field | Type | Notes |
|-------|------|-------|
| `Id` | `string` | |
| `Name` | `string` | |
| `Type` | `MemeType` | |
| `Traits` | `MemeTraits` | See trait sub-model below |
| `EngagementRevenue` | `float` | Accumulated $ (evolution currency) |
| `BeliefByCountry` | `Dictionary<string, float>` | |
| `MutationChain` | `List<string>` | List of unlocked mutation memeIds |

#### Trait Sub-Model (`MemeTraits.cs`)
| Trait | Min | Max | Effect |
|-------|-----|-----|--------|
| `Virality` | 0 | 10 | Spread multiplier |
| `Conviction` | 0 | 10 | Resistance to debunking |
| `Adaptability` | 0 | 10 | Mutation speed |
| `Grift` | 0 | 10 | $ per believer/tick |
| `Tribalism` | 0 | 10 | In-group reinforcement |

---

## Week 2: World Data Loading + Simulation Systems

### Days 8-9: GeoJSON + WorldDataLoader
| Method | Returns | Notes |
|--------|---------|-------|
| `LoadCountries(string geoJsonPath)` | `List<Country>` | Parse GeoJSON with NetTopologySuite |
| `LoadDemographics(string jsonPath)` | `void` | Merge into Country objects |
| `LoadPlatforms(string jsonPath)` | `Dictionary<string, Platform>` | |
| `ValidateWorld()` | `bool` | All countries connected via ≥1 platform |

Data files needed (create stubs):
- `assets/data/countries.geojson` (placeholder: 5 countries)
- `assets/data/demographics.json` (stub: basic values for 5)
- `assets/data/platforms.json` (stub: 6 platforms)

**GeoJSON parsing notes:**
```csharp
// Add package: dotnet add src/ThoughtVirus.Core package NetTopologySuite.IO.GeoJSON
using NetTopologySuite.IO;
using NetTopologySuite.Geometries;

var reader = new GeoJsonReader();
var featureCollection = reader.Read<FeatureCollection>(jsonText);
foreach (var feature in featureCollection.Features) {
    if (feature.Geometry is null) continue;
    var country = new Country {
        Id = feature.Attributes["ISO_A3"]?.ToString() ?? "UNK",
        Name = feature.Attributes["NAME"]?.ToString() ?? "Unknown",
        Polygon = feature.Geometry,
        BoundingBox = feature.Geometry.EnvelopeInternal
    };
}
```

### Days 10-11: TimeController
| Field | Type | Notes |
|-------|------|-------|
| `TickDurationMs` | `int` | 100 (base) |
| `SpeedMultiplier` | `float` | 0, 1, 2, 4, 8 |
| `CurrentTick` | `long` | |
| `IsPaused` | `bool` | |
| `DeltaTime` | `float` | ms since last tick |

| Method | Returns |
|--------|---------|
| `Advance()` | `void` — increments tick if not paused |
| `SetSpeed(float multiplier)` | `void` |
| `Pause()` / `Resume()` | `void` |
| `SimulateTick()` | `bool` — true if tick should advance |

### Days 11-12: SpreadSystem

**Core formula:**
```
beliefDelta = Virality × AlgorithmBoost × Affinity × (1 - MediaLiteracy) × TribalismMod × dtFactor
```

Where:
- `dtFactor` = time delta scaled to per-tick basis
- `TribalismMod` = 1 + (Tribalism/20) for in-group spread (similar platform affinity profiles), 1 - (Tribalism/20) for out-group
- Cross-border spread = `Affinity × SharedUsers(MemeSourceCountry, TargetCountry, Platform)`

| Method | Returns |
|--------|---------|
| `CalculateSpread(Meme, Country, Country, Platform)` | `float` |
| `CalculateCrossBorderSpread(Meme, Country, Country, Platform)` | `float` |
| `ApplyBeliefChange(Country, MemeId, float delta)` | `void` |
| `ClampBeliefs(Country)` | `void` — keeps beliefs in [0, 1] |

### Days 12-13: WorldState + Basic Game State
| Field | Type |
|-------|------|
| `Countries` | `Dictionary<string, Country>` |
| `Platforms` | `Dictionary<string, Platform>` |
| `ActiveMemes` | `List<Meme>` |
| `ActiveBills` | `List<Bill>` |
| `Time` | `TimeController` |

### Days 13-14: Unit Tests (Week 1)
| Test Name | Tests |
|-----------|-------|
| `Country_CanHoldBelief()` | Basic model works |
| `Platform_HasCorrectMAU()` | Data loads correctly |
| `Meme_TraitsWithinBounds()` | 0-10 clamp |
| `WorldDataLoader_LoadsGeoJSON()` | ≥5 countries |
| `TimeController_PausesCorrectly()` | Tick doesn't advance when paused |
| `TimeController_SpeedMultiplierWorks()` | 2x = 2x speed |

---

## Week 3: Legislative System + Headless Runner + Serialization

### Days 15-16: Bill + Policy Models
| Bill Field | Type |
|-----------|------|
| `Id` | `string` |
| `Name` | `string` |
| `Stage` | `BillStage` |
| `Actions` | `List<PolicyAction>` |
| `PoliticalCapitalCost` | `int` |
| `ApprovalImpact` | `float` |
| `SponsorIdeology` | `int` |
| `TicksInStage` | `int` |
| `Outcome` | `BillOutcome?` |

Policies: Simple structs or enums that the `SpreadSystem` checks (e.g., `FactCheck` → reduce `Virality` by 30%).

### Days 16-17: LegislativeSystem
| Method | Returns | Description |
|--------|---------|-------------|
| `DraftBill(Meme, Resources)` | `Bill?` | Create new bill |
| `AdvanceBill(Bill)` | `void` | Progress stage based on RNG + factors |
| `CommitteeStage(Bill)` | `bool` | Will this bill survive committee? |
| `FloorVote(Bill)` | `bool` | Pass/fail based on sponsor, approval |
| `ImplementPolicy(Bill)` | `void` | Apply policy effects to active memes |
| `CourtChallenge(Bill)` | `void` | RNG: enjoin, strike down, or uphold |

**RNG approach:** Use `Random` seeded from scenario seed + bill.Id + currentTick + stage index.

### Days 17-18: Headless Runner (`Program.cs`)
| Args | Flag |
|------|------|
| `--scenario <name>` | Which scenario to load |
| `--seed <int>` | Random seed |
| `--ticks <int>` | Max ticks (default 50000) |
| `--output <path>` | CSV output path |

| CSV Column | Description |
|-----------|-------------|
| `tick` | Simulation tick |
| `country` | Country ID |
| `memeId` | Meme identifier |
| `belief` | Belief strength 0-1 |
| `population` | Country population |
| `engagementRevenue` | Meme revenue |
| `billStage` | Active bill stage (if any) |

### Days 18-19: MessagePack + Save System
```csharp
[MessagePackObject]
public class WorldSnapshot {
    [Key(0)] public Dictionary<string, Country> Countries { get; set; }
    [Key(1)] public List<Meme> Memes { get; set; }
    [Key(2)] public List<Bill> Bills { get; set; }
    [Key(3)] public long Tick { get; set; }
}
```
Methods: `SaveSnapshot(World, string path)`, `LoadSnapshot(string path)`.

### Days 19-21: Integration Tests + Headless Run
| Test | Verifies |
|------|----------|
| `FullSimulation_HeadlessRun_ProducesCSV()` | End-to-end from 5 countries |
| `Determinism_SameSeed_SameCSV()` | Two runs match byte-for-byte |
| `Legislative_BillCanPassThrough()` | Bill completes all stages |
| `SpreadSystem_BeliefStaysInBounds()` | [0,1] clamp works |
| `Mutation_TriggerAtAdaptability5()` | Mutation chain fires |

---

## Phase 1 Deliverables Checklist
- [ ] .NET 10 solution with 3 projects
- [ ] `Country`, `Platform`, `Meme`, `MemeTraits`, `Bill` models
- [ ] `TimeController` with speed + pause
- [ ] `SpreadSystem` with basic formula
- [ ] `WorldDataLoader` with GeoJSON stub
- [ ] 6+ unit tests passing
- [ ] Headless runner produces CSV with tick/country/belief columns