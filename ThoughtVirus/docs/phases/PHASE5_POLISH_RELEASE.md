# Phase 5: Polish & Release (Week 9)

---

## Week 9: Polish, Save/Load, Release Prep

---

## Days 57-58: Save/Load System

### Slot System
```csharp
public class SaveGame {
    public int Slot;
    public string ScenarioName;
    public string Timestamp; // "YYYY-MM-DD HH:MM"
    public int Seed;
    public WorldSnapshot Snapshot;
}

public class SaveManager {
    private const string SAVE_DIR = "saves/";
    
    public void Save(SaveGame save, int slot) {
        var data = MessagePackSerializer.Serialize(save);
        File.WriteAllBytes($"{SAVE_DIR}/save_{slot}.sav", data);
    }
    
    public SaveGame Load(int slot) {
        var data = File.ReadAllBytes($"{SAVE_DIR}/save_{slot}.sav");
        return MessagePackSerializer.Deserialize<SaveGame>(data);
    }
    
    public List<SaveGame> ListSaves() {
        var saves = new List<SaveGame>();
        for (int i = 1; i <= 10; i++) {
            var path = $"{SAVE_DIR}/save_{i}.sav";
            if (File.Exists(path)) {
                saves.Add(Load(i));
            }
        }
        return saves;
    }
}
```

### Save Screen UI
```
┌─────────────────────────────┐
│          LOAD GAME            │
├─────────────────────────────┤
│ [Slot 1] Algorithm Pipeline   │
│        2024-12-15 14:30  ▶  │  ← Click to load
│                               │
│ [Slot 2] Empty                │
│                               │
│ [Slot 3] Algorithm Pipeline   │
│        2024-12-16 09:15  ▶  │
│                               │
│ [ Cancel ]                    │
└─────────────────────────────┘
```

### Auto-save
- Auto-save every 5 minutes of real-time
- Save before and after major events (mutation, bill pass, court ruling)
- Max 5 auto-save slots, rotate

---

## Days 59-60: Settings Screen + Persistence

### Settings Data Model
```csharp
[MessagePackObject]
public class GameSettings {
    [Key(0)] public float MusicVolume { get; set; } = 0.8f;
    [Key(1)] public float SfxVolume { get; set; } = 1.0f;
    [Key(2)] public GraphicsQuality GraphicsQuality { get; set; } = GraphicsQuality.Medium;
    [Key(3)] public bool ShowTutorial { get; set; } = true;
    [Key(4)] public Difficulty Difficulty { get; set; } = Difficulty.Normal;
    [Key(5)] public int LastScenarioIndex { get; set; } = 0;
}
```

### Settings Screen
```
┌─────────────────────────────┐
│          SETTINGS            │
├─────────────────────────────┤
│ Music Volume  ████████░░  80% │
│ SFX Volume    ██████████ 100% │
│ Difficulty  [Normal ▼]       │  ← Dropdown
│ Graphics    [Medium ▼]       │
│ Show Tutorial  [On]          │
│                              │
│ [ Back ]  [ Reset Defaults ] │
└─────────────────────────────┘
```

### Graphics Quality Tiers
| Quality | Polygon Detail | Particles | Chart Points | Shader |
|---------|---------------|-----------|-------------|--------|
| Low | Simplified | 500 | 200 | Basic color |
| Medium | Normal | 1000 | 500 | Heatmap |
| High | Full | 2000 | All | Animated shader |

---

## Days 61-62: Sound + Visual Polish

### Sound Implementation
```bash
# Audio is included in monogame.extended (already added in Phase 3)
# No separate package needed
```

### Sound Effects List
| Event | File | Volume |
|-------|------|--------|
| Outbreak (belief >50%) | `sfx/outbreak.wav` | 0.8 |
| Mutation | `sfx/mutation.wav` | 0.7 |
| Bill passed | `sfx/bill_passed.wav` | 0.6 |
| Bill killed | `sfx/bill_failed.wav` | 0.6 |
| Court ruling | `sfx/court_bell.wav` | 0.5 |
| Button click | `ui/click.wav` | 0.4 |
| Viral video | `sfx/tiktok_swipe.wav` | 0.5 |

### Background Music
- Menu: `music/menu_theme.ogg` (calm, contemplative)
- Gameplay: `music/sim_theme.ogg` (tense, building)
- Win: `music/victory_theme.ogg` (triumphant or somber)
- Loop with crossfade between tracks

### Visual Polish Tasks
- [ ] Add bloom effect on high-belief countries
- [ ] Particle system for infection spread (particles travel edges)
- [ ] Country hover tooltip with name + belief %
- [ ] Animated transition when speed changes
- [ ] Screen shake on mutation event
- [ ] Vignette effect when approval is low (Defense)
- [ ] Color palette: "Brainrot Blue" (#6A0DAD) to "Radical Red" (#FF0000) gradient

---

## Days 63-64: Performance Final Pass

### Profiling Checklist
| Component | Target | Method |
|-----------|--------|--------|
| Map rendering | <1ms | Batch draw, no per-country calls |
| Platform overlay | <2ms | Limit to 10 nodes, 50 edges |
| UI rendering | <2ms | Cache layouts, dirty rectangles |
| Chart drawing | <3ms | Downsample to 500 points |
| Physics (overlay) | <2ms | Fixed 60Hz, simple Verlet |
| Memory | <500MB | Track with VS Diagnostic Tools |
| GC pressure | <1KB/frame | Struct pooling, no per-tick allocs |

### Optimization Techniques
1. **Object Pooling** for `Particle` and `GraphEdge` objects
2. **Dirty Flag** system: only re-render chart/UI when data changes
3. **LOD** already implemented in Phase 3 for map
4. **Text Caching**: `StringBuilder` for dynamic labels, pre-render static text
5. **Texture Atlas**: Combine all UI icons into one `Texture2D`

---

## Days 65-66: README + Documentation

### README.md Sections
1. **Description**: "A satirical pandemic-style simulation where memes spread through social media platforms..."
2. **Screenshots**: 4-5 images (map, panels, win screen)
3. **Installation**: `dotnet run --project src/ThoughtVirus.App -- --render` (with MonoGame deps)
4. **Controls**: Table mapping keys to actions
5. **Headless Mode**: How to run simulations + analyze CSV
6. **Building from Source**: Prerequisites (MonoGame redist), build commands
7. **Data Sources**: Credit for Natural Earth, UN, DataReportal
8. **License**: MIT or similar

### Build Instructions
```bash
# Clone
git clone https://github.com/you/thought-virus.git
cd thought-virus

# Build
dotnet build ThoughtVirus.sln

# Run (game)
dotnet run --project src/ThoughtVirus.App -- --render --seed 1337

# Run (headless)
dotnet run --project src/ThoughtVirus.App -- --headless --scenario algorithm-pipeline --seed 42 --ticks 50000 --output runs/analysis.csv
```

### CI/CD Workflow (`.github/workflows/main.yml`)
```yaml
name: CI
on: [push, pull_request]

jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '9.0.x' }
      - run: dotnet restore
      - run: dotnet build --no-restore
      - run: dotnet test --no-build --verbosity normal
      - name: Headless simulation test
        run: |
          mkdir -p /tmp/test-output
          dotnet run --project src/ThoughtVirus.App -- --headless --seed 42 --ticks 10000 --output /tmp/test-output/results.csv
          test -f /tmp/test-output/results.csv && test $(($(wc -l < /tmp/test-output/results.csv) - 1)) -gt 100
```

---

## Days 67-68: Final Testing + Bug Fixes

### Integration Test Scenarios
| Test | Steps | Expected |
|------|-------|----------|
| Full Offensive Playthrough | Play as meme, let it spread, watch mutations | World >50% belief within 30min |
| Full Defensive Playthrough | Play as Ministry, pass 3 bills | Reduce belief to <5% within 30min |
| Headless Determinism | Run headless twice with same seed | CSV files are byte-identical |
| Save/Load | Save at tick 5000, quit, load | Simulation continues seamlessly |
| Platform Toggle | Disable TikTok in settings | Conspiracy no longer spreads via TikTok |
| Speed Abuse | Rapidly switch 1x→8x→1x | No desync, no crash |

### Edge Cases to Handle
1. Division by zero: Country with population 0
2. Null meme: Country has beliefs but meme was deleted
3. Overflow: Engagement revenue > float.MaxValue
4. Unicode: Country names with special characters (Côte d'Ivoire, Saudi Arabia emoji flag)
5. Very wide screen: Ultra-wide 21:9 monitor layout
6. Very small screen: 1080p scaling down to 720p

---

## Days 69-70: Release Packaging

### Multi-Platform Build
```bash
# Windows
dotnet publish src/ThoughtVirus.App -c Release -r win-x64 --self-contained false -o dist/win-x64

# Linux
dotnet publish src/ThoughtVirus.App -c Release -r linux-x64 --self-contained false -o dist/linux-x64

# macOS
dotnet publish src/ThoughtVirus.App -c Release -r osx-x64 --self-contained false -o dist/osx-x64
```

### Asset Bundles
```bash
# Create zip of assets
zip -r ThoughtVirus-assets.zip assets/
# Place alongside executable
```

### Release Checklist
- [ ] Build artifacts for Windows + Linux
- [ ] Download size < 100MB (GeoJSON must be compressed)
- [ ] `dotnet --info` version compatibility noted
- [ ] MonoGame redistributable noted in README
- [ ] CHANGELOG.md created with v0.1.0 entry
- [ ] GitHub release created with binaries + changelog
- [ ] `.gitignore` updated to exclude `bin/`, `obj/`, `saves/`

---

### CHANGELOG.md
```markdown
# Changelog

## [0.1.0] - 2024-12-15
### Added
- Full world map with 195 countries via GeoJSON
- 4 meme types (Conspiracy, Ideology, Grift, Cult) with mutation chains
- Legislative simulation: Draft → Committee → Floor → Vote → Implement → Court
- Force-directed platform overlay (TikTok, YouTube, X, etc.)
- Belief heatmap with shader effects
- Live charts: World belief %, engagement revenue, approval rating
- 6 policy templates
- Headless mode with CSV export
- Save/load system (10 slots)
- Settings persistence
- Sound effects + background music
- Scenario: "Algorithm Pipeline" (US → global)

### Known Issues
- macOS color rendering may differ
- WeChat MAU data estimated for some countries
- Court challenges can sometimes deadlock (fixed in 0.1.1)
```

---

## Phase 5 Deliverables Checklist
- [ ] **Save/load system** with 10 slots + auto-save
- [ ] **Settings persistence** (volume, quality, difficulty)
- [ ] **Sound effects** for all major events + BGM
- [ ] **Visual polish**: particles, bloom, tooltips, screen shake
- [ ] **60 FPS** maintained at all quality settings
- [ ] **README.md** with install/controls/headless instructions
- [ ] **CI workflow** (.github/workflows/main.yml) passing
- [ ] **Release builds** for Windows + Linux
- [ ] **CHANGELOG.md** + GitHub release
- [ ] All 5 phases compile cleanly with `dotnet build ThoughtVirus.sln`
- [ ] Headless mode produces valid CSV for analysis