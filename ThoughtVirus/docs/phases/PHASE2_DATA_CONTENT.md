# Phase 2: Data & Content (Week 4)

## Objective
Replace stub data with real demographic, platform, and scenario data. Tune simulation parameters so spread behavior feels realistic.

---

## Days 22-23: GeoJSON Processing

### Task A: Download Natural Earth Data
- Source: https://www.naturalearthdata.com/downloads/110m-cultural-vectors/ne_110m_admin_0_countries/
- Files needed:
  - `ne_110m_admin_0_countries.geojson` (or `ne_50m_admin_0_countries.geojson` for better detail)
- Download: `wget -O Tools/geojson-source/countries.geojson "https://naciscdn.org/naturalearth/110m/cultural/ne_110m_admin_0_countries.zip"` then unzip
- Place raw GeoJSON in `Tools/geojson-source/`

### Task B: Build-Time Preprocessing Script
Write `Tooling/preprocess_geojson.py` (Python 3):

```python
import geopandas as gpd
import json
import sys

def preprocess(input_path, output_path):
    gdf = gpd.read_file(input_path)
    
    selected = gdf[[
        'ISO_A3', 'NAME', 'POP_EST', 
        'geometry'
    ]].copy()
    
    # Simplify geometries for performance (tolerance in degrees, ~1km at equator)
    selected['geometry'] = selected.simplify(tolerance=0.01, preserve_topology=True)
    
    selected.to_file(output_path, driver='GeoJSON')
    print(f"Preprocessed {len(selected)} countries")

if __name__ == "__main__":
    preprocess(sys.argv[1], sys.argv[2])
```

**C# Build Step** (in `.csproj` or Makefile):
- After preprocess, `dotnet run --project Tooling/MeshBuilder.csproj` to triangulate polygons into `assets/meshes/bin`.
- This outputs `Country.BoundingBox` and pre-triangulated vertex buffers.

### Task C: Update WorldDataLoader
```csharp
public class WorldDataLoader {
    public static World LoadFullWorld() {
        var countries = LoadCountries("assets/data/countries.geojson");
        var demographics = LoadDemographics("assets/data/demographics.json");
        var platforms = LoadPlatforms("assets/data/platforms.json");
        
        // Merge demographics + platform MAU
        foreach (var country in countries) {
            MergeDemographics(country, demographics[country.Id]);
            MergeMAU(country, platforms);
        }
        
        // Convert to dictionary keyed by Country.Id
        var countryDict = countries.ToDictionary(c => c.Id);
        return new World(countryDict, platforms);
    }
}
```

**Verify:** All 195 ISO_A3 codes load. Check ~190 countries (some have null ISO_A3 — map them manually).

---

## Days 23-24: Demographics Data

### Source Mapping
| Field | Source | Notes |
|-------|--------|-------|
| `population` | UN World Pop Prospects 2024 | |
| `trustInInstitutions` | V-Dem, Gallup World Poll | |
| `polarization` | V-Dem Electoral Democracy Index → polarization score | |
| `mediaLiteracy` | OECD Adult Skills Survey → mean literacy score / 5 | |
| `platformUsage` | DataReportal/GWI → % using each platform | |

### Implementation
Create `src/Tooling/DemographicsGenerator/Program.cs` that scrapes or reads CSV/JSON from sources:
```csharp
var output = new Dictionary<string, DemographicData>();

foreach (var countryRow in unPopulationCsv) {
    output[countryRow.Iso3] = new DemographicData {
        Population = int.Parse(countryRow.Pop2024),
        TrustInInstitutions = vDemData[countryRow.Iso3].TrustScore,
        Polarization = vDemData[countryRow.Iso3].Polarization,
        MediaLiteracy = oecdData[countryRow.Iso3].LiteracyScore / 5.0f,
        PlatformUsage = new Dictionary<string, float> {
            { "tiktok", dataReportal["tiktok_" + countryRow.Iso3] },
            { "youtube", dataReportal["youtube_" + countryRow.Iso3] },
            // ... etc
        }
    };
}

File.WriteAllText("assets/data/demographics.json", 
    JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
```

### Data Priority
1. Top 20 countries by population (full data)
2. Next 75 (medium detail: ~3 platforms each)
3. Remaining 100 (global averages adjusted ±20%)

### Expected File Size
`demographics.json` should be ~150KB uncompressed.

---

## Days 24-25: Platform Data

### Platforms List (10 total)
| Platform | Global MAU (2024) | AlgorithmBoost | ModerationStrictness |
|----------|-------------------|----------------|---------------------|
| TikTok | 1.5B | 0.9 | 0.3 |
| YouTube | 2.5B | 0.7 | 0.6 |
| Instagram | 2.0B | 0.5 | 0.5 |
| Facebook | 2.0B | 0.3 | 0.7 |
| X/Twitter | 550M | 0.8 | 0.4 |
| Reddit | 50M | 0.4 | 0.8 |
| WeChat | 1.3B | 0.1 | 0.95 |
| Discord | 400M | 0.6 | 0.9 |
| Telegram | 800M | 0.7 | 0.2 |
| VK | 100M | 0.3 | 0.85 |

### MAU By Country Logic
- Use DataReportal's "Social Media Users" dataset
- For each `(platform, country)` pair:
  - If `country.population * 0.1 > platform.global_mau`: set MAU = 0.1 * population
  - Otherwise: proportional based on region + internet penetration
- `ConnectedCountries` = countries where `MAU > 1_000_000`

### Output Schema
```json
{
  "tiktok": {
    "name": "TikTok",
    "mauGlobal": 1500000000,
    "algorithmBoost": 0.9,
    "moderationStrictness": 0.3,
    "mauByCountry": {
      "USA": 160000000,
      "IND": 200000000,
      "BRA": 80000000
    }
  }
}
```

---

## Days 25-26: Scenario — Algorithm Pipeline

### Scenario File
`assets/data/scenarios/algorithm-pipeline.json`:
```json
{
  "name": "algorithm-pipeline",
  "description": "A conspiracy theory spreads via TikTok, mutating into extremism and then into a monetized cult.",
  "seed": 42,
  "startingCountry": "USA",
  "startingMeme": {
    "id": "skibidi-conspiracy",
    "name": "Skibidi Conspiracy",
    "type": "Conspiracy",
    "traits": { "virality": 8, "conviction": 2, "adaptability": 6, "grift": 3, "tribalism": 5 },
    "initialStrength": 0.03
  },
  "mutationChain": [
    {
      "from": "skibidi-conspiracy",
      "to": "alpha-male-ideology",
      "triggerAdaptability": 5,
      "name": "Alpha Male Ideology",
      "type": "Ideology",
      "traits": { "virality": 6, "conviction": 6, "adaptability": 5, "grift": 4, "tribalism": 9 }
    },
    {
      "from": "alpha-male-ideology",
      "to": "crypto-grift-token",
      "triggerAdaptability": 8,
      "name": "Crypto Grift Token",
      "type": "Grift",
      "traits": { "virality": 7, "conviction": 5, "adaptability": 7, "grift": 10, "tribalism": 8 }
    },
    {
      "from": "crypto-grift-token",
      "to": "doomer-cult",
      "triggerAdaptability": 10,
      "name": "Doomer Cult",
      "type": "Cult",
      "traits": { "virality": 4, "conviction": 10, "adaptability": 3, "grift": 8, "tribalism": 10 }
    }
  ],
  "winConditions": [
    {
      "type": "offense",
      "description": "Any meme reaches 40% belief in 50%+ of world population",
      "threshold": 0.40,
      "populationFraction": 0.50
    },
    {
      "type": "defense",
      "description": "All memes below 5% belief + National Approval > 35%",
      "maxBelief": 0.05,
      "minApproval": 0.35
    }
  ],
  "startingBills": [
    "fact-check-program",
    "platform-transparency-act",
    "media-literacy-initiative"
  ]
}
```

### Mutation Type Mechanics
| Type | Mechanic |
|------|----------|
| **Conspiracy** | High virality via viral videos; weak conviction; can be debunked |
| **Ideology** | High tribalism; spreads through echo chambers; hard to debunk (high conviction) |
| **Grift** | Generates engagement $ fast; moderate spread; easily caught by moderation |
| **Cult** | Very high conviction; slow spread; near-zero belief decay |

---

## Days 26-27: Trait Balancing

### Balancing Parameters Table
| Parameter | Starting Value | Range | Notes |
|-----------|----------------|-------|-------|
| `BaseSpreadRate` | 0.02 | 0.005–0.1 | Belief delta per tick per connected country |
| `BeliefDecayRate` | 0.001 | 0–0.01 | Natural skepticism; reduced by high Conviction |
| `MediaLiteracyEffectiveness` | 0.3 | 0.1–0.6 | Multiplies reduction of belief delta |
| `PlatformAlgorithmMultiplier` | 2.0 | 1.0–5.0 | Virality gets squared at very high platform boost |
| `TribalismCrossBorderPenalty` | 0.5 | 0.1–0.8 | In-group memes spread poorly cross-border |

### Tuning Process
1. Run headless for 1000 ticks with stub 5-country data
2. Check CSV: belief should grow from 3% → 20% in source country after 100 ticks
3. After mutation at tick ~200: belief in adjacent countries should spike
4. By tick 500: belief should plateau at ~60-70% in source (saturation)
5. By tick 1000: should see spread to connected countries

Adjust `BaseSpreadRate` up/down by 25% increments.

---

## Days 27-28: Defense Policy Templates

### Policy Actions JSON
`assets/data/policies.json`:
```json
{
  "fact-check-program": {
    "name": "Fact-Check Program",
    "type": "FactCheck",
    "politicalCapitalCost": 10,
    "durationTicks": 2000,
    "effect": { "reduceStat": "virality", "amount": 0.3 },
    "approvalImpact": -0.1
  },
  "platform-transparency-act": {
    "name": "Platform Transparency Act",
    "type": "Transparency",
    "politicalCapitalCost": 25,
    "durationTicks": 5000,
    "effect": { "reduceStat": "algorithmBoost", "amount": 0.2 },
    "approvalImpact": -0.05
  },
  "media-literacy-initiative": {
    "name": "Media Literacy Initiative",
    "type": "MediaLiteracy",
    "politicalCapitalCost": 15,
    "durationTicks": 3000,
    "effect": { "increaseStat": "mediaLiteracy", "amount": 0.15 },
    "approvalImpact": 0.1
  },
  "protect-children-from-brainrot-bill": {
    "name": "Protect Children From Brainrot Act",
    "type": "Ban",
    "politicalCapitalCost": 50,
    "durationTicks": 3000,
    "effect": { "reduceStat": "affinity", "amount": 0.4 },
    "approvalImpact": -0.25
  },
  "digital-services-modification-act": {
    "name": "Digital Services Modification Act",
    "type": "Downrank",
    "politicalCapitalCost": 20,
    "durationTicks": 2000,
    "effect": { "reduceStat": "algorithmBoost", "amount": 0.5 },
    "approvalImpact": 0.05
  },
  "creator-accountability-act": {
    "name": "Creator Accountability Act",
    "type": "Demonetize",
    "politicalCapitalCost": 30,
    "durationTicks": 4000,
    "effect": { "reduceStat": "grift", "amount": 0.4 },
    "approvalImpact": -0.15
  }
}
```

Each policy maps to a `PolicyAction` enum and gets applied as a modifier in `SpreadSystem`.

---

## Phase 2 Deliverables Checklist
- [ ] 195 countries loaded from GeoJSON (Natural Earth)
- [ ] Top 20 countries have full demographic data
- [ ] All 10 platforms in `platforms.json` with MAU + algorithm params
- [ ] `algorithm-pipeline.json` scenario file complete
- [ ] 4 mutation stages defined with trait adjustments
- [ ] Balanced spread formula (verified via headless run)
- [ ] 6 policy templates in `policies.json`
- [ ] All data validated against Phase 1 unit tests