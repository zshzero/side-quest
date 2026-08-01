# Phase 4: Rendering — UI (Weeks 7-8)

> **Prerequisite**: Phase 3 (Map + Overlay) is rendering at 30+ FPS.

---

## Week 7: Meme + Legislative Panels

### Days 41-42: UI Framework Selection

Use **monogame.extended UI** (sub-library within `monogame.extended`). It provides:
- Anchor layouts
- Panels with borders
- Mouse/touch event handling
- Styling via skin files
- Integration with existing SpriteFont

Setup:
```bash
# monogame.extended already added in Phase 3, includes UI namespace
# If not yet added:
dotnet add src/ThoughtVirus.Render package monogame.extended
```

Create `src/ThoughtVirus.Render/UI/` folder.

---

### Days 41-42: MemePanel (Player = Meme/Offense)

#### Panel Layout
```
┌─────────────────────────────┐
│ Skibidi Conspiracy [Level 3] │ ← Meme name + level
├─────────────────────────────┤
│ Virality:   ████████░░  8/10 │ ← Trait bars
│ Conviction: ██░░░░░░░░  2/10 │
│ Adaptability: ██████░░░░ 6/10 │
│ Grift:      ███░░░░░░░  3/10 │
│ Tribalism:  █████░░░░░  5/10 │
├─────────────────────────────┤
│ Revenue: $12.4M            │ ← Engagement revenue counter
│ Mutation: 65% → NEXT: Alpha Male Ideology │
│ [Upgrade Virality] $2.1M   │ ← Button (costs revenue)
│ [Evolve → Alpha Male Ideology] $5.0M │
├─────────────────────────────┤
│ [Pause] [1x] [2x] [4x] [8x] │ ← Speed controls (shared)
└─────────────────────────────┘
```

#### Implementation
`src/ThoughtVirus.Render/UI/MemePanel.cs`:
```csharp
using MGUI = MonoGame.Extended.UI.Controls;

// ...

public class MemePanel : MGUI.Panel {
    private Meme _currentMeme;
    private MGUI.Button[] _upgradeButtons; // One per trait
    private MGUI.ProgressBar[] _traitBars;
    private MGUI.Label _revenueLabel;
    private MGUI.Label _mutationLabel;
    private MGUI.Button _mutateBtn;
    
    public MemePanel(Meme meme) {
        _currentMeme = meme;
        InitializeComponents();
    }
    
    private void InitializeComponents() {
        // Title
        var title = new MGUI.Label { Text = $"{_currentMeme.Name} [Mutated {_currentMeme.MutationLevel}]" };
        Children.Add(title);
        
        // Trait bars
        var traitNames = new[] { "Virality", "Conviction", "Adaptability", "Grift", "Tribalism" };
        _traitBars = new MGUI.ProgressBar[5];
        _upgradeButtons = new MGUI.Button[5];
        
        for (int i = 0; i < traitNames.Length; i++) {
            var traitValue = _currentMeme.Traits.GetTraitValue(i); // Extension method
            var bar = new MGUI.ProgressBar { 
                Value = traitValue, 
                Maximum = 10 
            };
            _traitBars[i] = bar;
            var label = new MGUI.Label { Text = $"{traitNames[i]}: {traitValue}/10" };
            Children.Add(label);
            Children.Add(bar);
            
            // Upgrade button
            var btn = new MGUI.Button { 
                Text = $"Upgrade (${GetUpgradeCost(traitValue):N0})",
                Enabled = _currentMeme.EngagementRevenue >= GetUpgradeCost(traitValue)
            };
            int idx = i; // capture for closure
            btn.Click += (s, e) => UpgradeTrait(idx);
            _upgradeButtons[i] = btn;
            Children.Add(btn);
        }
        
        // Revenue
        _revenueLabel = new MGUI.Label { Text = $"Revenue: {_currentMeme.EngagementRevenue:C0}" };
        Children.Add(_revenueLabel);
        
        // Mutate button
        _mutationLabel = new MGUI.Label { Text = GetMutationStatus() };
        Children.Add(_mutationLabel);
        
        _mutateBtn = new MGUI.Button { 
            Text = $"Mutate → {_currentMeme.NextMutationName} (${_currentMeme.MutationCost:N0})",
            Visible = _currentMeme.CanMutate 
        };
        _mutateBtn.Click += (s, e) => _currentMeme.Mutate();
        Children.Add(_mutateBtn);
    }
    
    private int GetUpgradeCost(int currentLevel) => currentLevel switch {
        5 => 1_000_000,
        6 => 2_000_000,
        7 => 5_000_000,
        8 => 10_000_000,
        9 => 20_000_000,
        _ => int.MaxValue
    };
}
```

#### Trait Upgrade Costs
| Trait Level Upgrade | Cost (Engagement Revenue) |
|---------------------|--------------------------|
| 5 → 6 | $1M |
| 6 → 7 | $2M |
| 7 → 8 | $5M |
| 8 → 9 | $10M |
| 9 → 10 | $20M |

---

### Days 43-44: LegislativePanel (Player = Defense)

#### Panel Layout
```
┌─────────────────────────────┐
│ MINISTRY OF TRUTH            │
├─────────────────────────────┤
│ Approval: ██████████░░░░ 80%│ ← Approval rating
│ Capital:  ████░░░░░░░░ 40%  │ ← Political capital
├─────────────────────────────┤
│ DRAFT BILL                   │ ← Button (cost: 15 capital)
│ ┌───────────────┐            │
│ │ Fact Check Act│ ← Committee (20d)     │
│ │ Truth in Algo │ ← Floor (Pass: 65%)   │
│ └───────────────┘            │
├─────────────────────────────┤
│ Active Policies:             │
│ • Fact Check Program (-30% spread) [1200/2000] │
│ • Platform Transparency Act    [800/5000]        │
└─────────────────────────────┘
```

#### Implementation
`src/ThoughtVirus.Render/UI/LegislativePanel.cs`:

```csharp
using MGUI = MonoGame.Extended.UI.Controls;

// ...

public class LegislativePanel : MGUI.Panel {
    private int _approval;
    private int _politicalCapital;
    private List<Bill> _activeBills;
    
    private void InitializeComponents() {
        // Resource bars
        var approvalBar = new MGUI.ProgressBar { Value = _approval, Maximum = 100 };
        var capitalBar = new MGUI.ProgressBar { Value = _politicalCapital, Maximum = 100 };
        
        // Bill queue
        var billList = new MGUI.PanelList { Spacing = new MGUI.Thickness(5) };
        RefreshBillList(billList);
        
        // Draft bill button
        var draftBtn = new MGUI.Button { Text = "Draft Bill (15 Capital)" };
        draftBtn.Click += OnDraftBill;
        
        // Active policy effects
        var policyList = new MGUI.PanelList();
        RefreshPolicyList(policyList);
        
        Children.AddRange(approvalBar, capitalBar, billList, draftBtn, policyList);
    }
    
    private void OnDraftBill(object sender, EventArgs e) {
        if (_politicalCapital < 15) return;
        
        var bill = SelectBillTemplate(); // Show modal dialog
        if (bill != null) {
            _politicalCapital -= bill.Cost;
            _activeBills.Add(bill);
            RefreshBillList();
        }
    }
}
```

#### Bill Drafting Modal
```csharp
// Modal dialog with bill templates from policies.json
public class BillDraftModal : ModalDialog {
    public BillDraftModal(List<PolicyTemplate> templates) {
        foreach (var template in templates) {
            var btn = new Button { 
                Text = $"{template.Name} — Cost: {template.Cost} Capital\n{template.Description}" 
            };
            btn.Click += (s, e) => SelectTemplate(template);
            Children.Add(btn);
        }
    }
}
```

---

### Days 45-46: CountryPanel (Shared, shows on country click)

#### Panel Layout
```
┌─────────────────────────────┐
│ 🇺🇸 United States            │
├─────────────────────────────┤
│ Population: 340,000,000      │
│ Media Literacy: 55%          │
│ Trust in Gov: 35%            │
│ Polarization: 80%            │
├─────────────────────────────┤
│ Belief: ██████████░░░░ 72%  │ ← Skibidi Conspiracy
│         ████░░░░░░░░ 30%     │ ← Alpha Male Ideology
│         ██░░░░░░░░░░ 10%     │ ← Crypto Grift
├─────────────────────────────┤
│ [Fact Check] [Downrank] [Ban]│ ← Apply policy buttons (if Defense)
└─────────────────────────────┘
```

---

## Week 8: Charts + Integration

### Days 47-48: Live Graph Charts

#### Data Collection
In `WorldSnapshot` or `SimulationRunner`:
```csharp
// Every tick, record:
chartData.Add(new ChartPoint {
    Tick = currentTick,
    WorldBeliefSum = sum of (country.Beliefs * country.Population) / worldPop,
    TotalRevenue = sum of meme.EngagementRevenue,
    PlayerApproval = currentApproval, // if Defense
    ActiveBills = activeBills.Count,
    MutationsActive = count of mutated memes
});
```

#### Chart Types
| Chart | Data Source | Visualization |
|-------|-------------|---------------|
| World Belief % | `WorldBeliefSum` | Line chart (0-100%) |
| Revenue | `TotalRevenue` | Area chart (log scale) |
| Approval | `PlayerApproval` | Bar or gauge |
| Policy Effectiveness | Before/after belief Δ | Bar comparison |
| Meme Distribution | Per-meme belief % | Pie chart (top 5) |

#### Implementation (`src/ThoughtVirus.Render/UI/Charts/`)
```csharp
public class LineChart {
    public Vector2[] DataPoints; // Screen-space coordinates
    public Color LineColor;
    public float LineThickness;
    
    public void AddData(ChartPoint point) {
        DataPoints.Add(ConvertToScreen(point));
    }
    
    public void Draw(SpriteBatch spriteBatch) {
        for (int i = 1; i < DataPoints.Length; i++) {
            spriteBatch.DrawLine(DataPoints[i-1], DataPoints[i], LineColor, LineThickness);
        }
    }
}
```

---

### Days 49-50: Game Loop Integration

#### Update Loop
```csharp
protected override void Update(GameTime gameTime) {
    var input = InputState.Current;
    Camera.HandleInput(input);
    
    // Simulation tick (at sim Hz, not render Hz)
    if (_timeController.SimulateTick()) {
        _spreadSystem.Tick(World, _timeController.CurrentTick);
        _legislativeSystem.Tick(World, _timeController.CurrentTick);
        _evolutionSystem.Tick(World, _timeController.CurrentTick);
        RecordChartData();
    }
    
    _platformOverlay.Update(); // Force-directed physics runs every render frame
    
    // UI panels update
    _memePanel.Update(gameTime);
    _legislativePanel.Update(gameTime);
    
    // Check win/lose
    if (CheckWinCondition()) {
        EndGameScreen("You radicalized the world!");
    }
}
```

#### Input Mapping
| Input | Action |
|-------|--------|
| Mouse drag | Pan camera |
| Mouse wheel | Zoom |
| Click country | Select country (show CountryPanel) |
| Click platform hub | Show platform details modal |
| Spacebar | Pause/resume |
| 1/2/3/4 keys | Speed: 1x/2x/4x/8x |
| Esc | Pause menu |
| S key | Save game |
| L key | Load game |

---

### Days 51-52: Scenario Select + Settings

#### Main Menu State
```csharp
enum GameState { MainMenu, Playing, Paused, GameOver }

class MainMenuScreen {
    Button[] scenarioButtons;  // "Algorithm Pipeline", "Future Scenarios..."
    Button settingsButton;
    Button exitButton;
    
    void OnScenarioSelected(Scenario scenario) {
        World = ScenarioLoader.Load(scenario.Name);
        GameState = Playing;
    }
}
```

#### Settings Modal
| Option | Type | Values |
|--------|------|--------|
| Difficulty | Enum | Easy, Normal, Hard (affects belief decay, capital gain) |
| Graphics Quality | Enum | Low, Medium, High (LOD levels, shader complexity) |
| Music Volume | Slider | 0-100 |
| SFX Volume | Slider | 0-100 |
| Show Tutorial | Toggle | On/Off |
| Data Export | Button | Export current run as CSV |

---

### Days 51-52: Win/Lose Screens

#### Offensive Win
```
┌────────────────────────────────────┐
│ 🧠 VIRAL VICTORY! 🧠                 │
├────────────────────────────────────┤
│ You turned 52% of the world into    │
│ a giant Skibidi Toilet.             │
│                                     │
│ Final Stats:                        │
│ • Peak Belief: 78% global           │
│ • Revenue Generated: $2.3B          │
│ • Countries Infected: 187/195       │
│                                     │
│ [Export CSV] [Menu] [Replay]        │
└────────────────────────────────────┘
```

#### Defensive Win
```
┌────────────────────────────────────┐
│ 🇺🇸 TRUTH PREVAILS! 🇺🇸              │
├────────────────────────────────────┤
│ Through decisive legislative       │
│ action, you saved democracy.       │
│                                     │
│ Final Stats:                        │
│ • Approval: 64%                     │
│ • Bills Passed: 8                   │
│ • Meme Reduction: 94%               │
│                                     │
│ [Export CSV] [Menu] [Replay]        │
└────────────────────────────────────┘
```

---

### Days 53-56: Polish + Performance

#### Performance Targets
| Metric | Target |
|--------|--------|
| Render FPS | 60 FPS at 1920×1080 |
| UI Update | <2ms |
| Chart drawing | <3ms (downsample to 500 points max) |
| Particle effects | Max 2000 particles |

#### Polish Features
1. **Smooth interpolation** between camera positions (lerp 0.1 per frame)
2. **Hover tooltips** on countries and platforms (fade in/out)
3. **Sound effects**:
   - `sfx/outbreak.wav` — when belief spikes in a country
   - `sfx/mutation.wav` — when meme evolves
   - `sfx/vote_pass.wav` — bill passes
   - `ui/click.wav` — button clicks
4. **Animations**:
   - Belief color transition (smoothlerp between ticks)
   - Panel slide-in from side
   - Particle burst on mutation
5. **Font fallback**: Ensure emoji characters (🧠🇺🇸) render

---

## Phase 4 Deliverables Checklist
- [ ] **MemePanel** with trait bars, upgrade buttons, revenue display, mutation button
- [ ] **LegislativePanel** with approval/capital bars, bill queue, draft button, policy list
- [ ] **CountryPanel** with belief breakdown, policy apply buttons
- [ ] **Live charts**: World belief %, revenue, approval (3 chart types)
- [ ] **Game loop**: 10Hz sim + 60Hz render, decoupled via TimeController
- [ ] **Input**: mouse pan/zoom/click, keyboard shortcuts, speed keys
- [ ] **Scenario select** screen with menu
- [ ] **Settings modal** with quality/difficulty options
- [ ] **Win/lose screens** with stats export
- [ ] **60 FPS** with all 195 countries + platform overlay
- [ ] **Headless mode** still compiles and runs without MonoGame dependency