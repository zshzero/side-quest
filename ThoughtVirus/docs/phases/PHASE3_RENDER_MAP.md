# Phase 3: Rendering — Map + Overlay (Weeks 5-6)

> **Note**: Rendering phase starts after Core Engine (Phase 1) is functional in headless mode.

---

## Week 5: World Map Renderer

### Days 29-30: Project Setup

#### Step 1: Add Rendering Project
```bash
mkdir -p src/ThoughtVirus.Render
cd src/ThoughtVirus.Render
dotnet new classlib -n ThoughtVirus.Render -f net10.0
cd ../..
dotnet add src/ThoughtVirus.Render reference src/ThoughtVirus.Core
dotnet add src/ThoughtVirus.App reference src/ThoughtVirus.Render
dotnet add src/ThoughtVirus.Render package MonoGame.Framework.DesktopGL
dotnet add src/ThoughtVirus.Render package monogame.extended
dotnet sln add src/ThoughtVirus.Render
```

#### Step 2: App Entry Point Update
In `src/ThoughtVirus.App/Program.cs`, add branch:
```csharp
if (args.Contains("--render")) {
    var game = new ThoughtVirus.Render.Game1(seed: int.Parse(args.FirstOrDefault(x => x.StartsWith("--seed"))?.Split('=')[1] ?? "42"));
    game.Run();
} else {
    // Headless mode
    RunHeadless(args);
}
```

#### Step 3: Create `Game1.cs`
```csharp
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using ThoughtVirus.Core.Simulation;

namespace ThoughtVirus.Render {
    public class Game1 : Game {
        private GraphicsDeviceManager _graphics;
        private World World;
        
        public Game1(int seed) {
            _graphics = new GraphicsDeviceManager(this);
            Content.RootDirectory = "Content";
            World = WorldDataLoader.LoadFullWorld();
        }
        
        protected override void Initialize() {
            _graphics.PreferredBackBufferWidth = 1920;
            _graphics.PreferredBackBufferHeight = 1080;
            _graphics.ApplyChanges();
            base.Initialize();
        }
    }
}
```

---

### Days 31-32: GeoJSON → Texture2D Pipeline

#### Step 1: Polygon → Vertex Buffer
Triangle via NetTopologySuite's built-in triangulation:
```csharp
// Already available through NetTopologySuite package
using NetTopologySuite.Triangulate;
```

Create `src/ThoughtVirus.Render/Map/CountryMeshBuilder.cs`:
```csharp
public static class CountryMeshBuilder {
    public static VertexPosition[] BuildVertices(Geometry polygon) {
        var vertices = new List<VertexPosition>();
        var triangulation = Triangulate(polygon);
        
        foreach (var triangle in triangulation) {
            vertices.Add(new VertexPosition(new Vector3((float)triangle.A.X, (float)triangle.A.Y, 0)));
            vertices.Add(new VertexPosition(new Vector3((float)triangle.B.X, (float)triangle.B.Y, 0)));
            vertices.Add(new VertexPosition(new Vector3((float)triangle.C.X, (float)triangle.C.Y, 0)));
        }
        
        return vertices.ToArray();
    }
}
```

#### Step 2: Bake All Countries
At game startup or build-time:
```csharp
// In Game1.Initialize()
var meshBuilder = new CountryMeshBuilder(World.Countries);
VertexBuffer = meshBuilder.BuildMergedBuffer(); // All countries in one buffer
```

#### Step 3: Render Passes
```csharp
// In Game1.Draw()
protected override void Draw(GameTime gameTime) {
    GraphicsDevice.Clear(Color.CornflowerBlue);
    
    // Draw world map - get dominant meme belief per country
    _spriteBatch.Begin();
    foreach (var country in World.Countries.Values) {
        var dominantMemeId = GetDominantMeme(country);
        var belief = dominantMemeId != null ? country.Beliefs.GetValueOrDefault(dominantMemeId) : 0f;
        var color = GetBeliefColor(belief);
        DrawCountry(countryMeshLookup[country.Id], color);
    }
    _spriteBatch.End();
    
    // Draw platform overlay
    DrawPlatformOverlay();
    
    // Draw UI
    DrawUI();
}
```

---

### Day 33: Camera System

### Camera Fields
| Field | Type | Notes |
|-------|------|-------|
| `Position` | `Vector2` | World-space center of view |
| `Zoom` | `float` | 0.01 (continent) to 10.0 (country-level) |
| `MinZoom` | `float` | 0.05 |
| `MaxZoom` | `float` | 8.0 |
| `Rotation` | `float` | 0 (no rotation for MVP) |

### Camera Matrix
```csharp
public Matrix GetViewMatrix() {
    return Matrix.CreateTranslation(-(int)Position.X, -(int)Position.Y, 0) *
           Matrix.CreateScale(Zoom, Zoom, 1) *
           Matrix.CreateTranslation(viewport.Width / 2, viewport.Height / 2, 0);
}
```

### Camera Methods
| Method | Returns |
|--------|---------|
| `ScreenToWorld(Vector2 screenPos)` | `Vector2` — mouse position to world coords |
| `WorldToScreen(Vector2 worldPos)` | `Vector2` — world position to screen coords |
| `Pan(Vector2 delta)` | `void` — adjust Position based on drag |
| `ZoomTo(float amount, Vector2 anchor)` | `void` — zoom toward mouse cursor |
| `FitToBounds(BoundingBox bounds)` | `void` — center + zoom to show entire bounds |
| `HandleInput(InputState input)` | `void` — mouse drag, scroll wheel, keyboard |

### Day 33: Belief Heatmap Shader

Create `src/ThoughtVirus.Render/Effects/BeliefHeatmap.fx`:
```hlsl
// Vertex Shader
float4x4 WorldViewProjection;

struct VertexInput {
    float3 Position : POSITION;
    float2 TexCoord : TEXCOORD0;
};

struct VertexOutput {
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
};

VertexOutput VS(VertexInput input) {
    VertexOutput output;
    output.Position = mul(float4(input.Position, 1.0f), WorldViewProjection);
    output.TexCoord = input.TexCoord;
    return output;
}

// Pixel Shader
float MinColor : register(c0); // Blue (0% belief)
float MaxColor : register(c1); // Red (100% belief)
float Tick : register(c2);     // For animation

float4 PS(VertexOutput input) : SV_Target {
    float belief = tex2D(BeliefMapSampler, input.TexCoord).r;
    
    // Animate pulsing for new outbreaks
    float pulse = sin(Tick * 0.01) * 0.1 + 0.9;
    
    // Interpolate from blue → red
    float3 lowColor = float3(0.2, 0.5, 1.0);  // Blue
    float3 highColor = float3(1.0, 0.2, 0.2); // Red
    float3 color = lerp(lowColor, highColor, belief * pulse);
    
    return float4(color, 1.0);
}

technique BasicTechnique {
    pass Pass1 {
        VertexShader = compile vs_5_0 VS();
        PixelShader = compile ps_5_0 PS();
    }
}
```

### Country Color Logic (CPU fallback if shader fails)
```csharp
public static Color GetBeliefColor(float belief) {
    // belief: 0=uninfected (blue), 1=fully radicalized (red)
    int r = (int)(255 * belief);
    int b = (int)(255 * (1 - belief));
    return new Color(r, 0, b);
}

public static Color GetPolarizationColor(float polarization) {
    // 0 = neutral (gray), 1 = highly polarized (purple)
    return Color.Lerp(Color.Gray, Color.Purple, polarization);
}
```

---

## Week 6: Platform Overlay + Interaction

### Days 35-36: Force-Directed Layout

### Platform Hub Placement
Use lat/long from a reference (e.g., platform headquarters):
| Platform | Lat | Long | Screen Position |
|----------|-----|------|-----------------|
| TikTok | 31.2 | 121.5 | (X: 0.8W, Y: 0.4H) |
| YouTube | 37.4 | -122.1 | (X: 0.2W, Y: 0.3H) |
| Reddit | 38.9 | -77.0 | (X: 0.3W, Y: 0.4H) |
| WeChat | 22.5 | 113.9 | (X: 0.85W, Y: 0.6H) |

### Node Structure
```csharp
class GraphNode {
    public string Id;                  // CountryId or PlatformId
    public Vector2 Position;           // World coordinates
    public Vector2 Velocity;           // For Verlet integration
    public Vector2 ForceAccumulator;   // Accumulated force this frame
    public float Mass;                 // Heavier = less movement
    public float Radius;               // For collision
    public NodeType Type;              // Country, Platform
}
```

### Force Calculation (per frame)
```csharp
void CalculateForces(GraphNode node) {
    // Repulsion (anti-gravity) - all nodes repel each other
    foreach (var other in AllNodes) {
        if (node == other) continue;
        var diff = node.Position - other.Position;
        var dist = diff.Length() + 0.1f;
        var force = diff.Normalized() * (RepulsionStrength / dist);
        node.ForceAccumulator += force;
    }
    
    // Attraction (springs) - country ↔ platform connections
    foreach (var edge in node.Edges) {
        var target = edge.Target;
        var diff = target.Position - node.Position;
        var dist = diff.Length();
        var force = diff * (SpringStrength * (dist - edge.OptimalLength));
        node.ForceAccumulator += force;
    }
}
```

### Verlet Integration
```csharp
void UpdateNodes(float dt) {
    foreach (var node in AllNodes) {
        var newPos = node.Position + node.Velocity + 0.5f * node.ForceAccumulator / node.Mass * dt * dt;
        node.Velocity = (newPos - node.Position) / dt;
        node.Position = newPos;
        node.ForceAccumulator = Vector2.Zero;
    }
}
```

### Day 36: Animation + Edge Rendering
```csharp
// Draw edges
foreach (var edge in edges) {
    var start = edge.Source.Position;
    var end = edge.Target.Position;
    var thickness = MathHelper.Clamp(edge.Weight * 3, 0.5f, 5.0f);
    spriteBatch.DrawLine(start, end, Color.White * 0.3f, thickness);
}

// Animate edge flow (particles moving along edge)
foreach (var edge in edges) {
    var t = (GameTime.TotalGameTime.TotalMilliseconds % 3000) / 3000.0f;
    var pos = Vector2.Lerp(edge.Source.Position, edge.Target.Position, t);
    spriteBatch.Draw(pixel, pos, Color.Yellow * 0.8f, 0, Vector2.Zero, 4, SpriteEffects.None, 0);
}
```

### Days 37-38: Click Detection

### Raycasting Approach
```csharp
public string GetCountryAtScreenPos(Vector2 screenPos) {
    var worldPos = Camera.ScreenToWorld(screenPos);
    var point = new NetTopologySuite.Geometries.Point(worldPos.X, worldPos.Y);
    
    // Use spatial index for performance (quadtree)
    var candidates = Quadtree.Query(new Envelope(point.X - 0.1, point.X + 0.1, point.Y - 0.1, point.Y + 0.1));
    
    foreach (var country in candidates) {
        if (country.Polygon.Contains(point)) {
            return country.Id;
        }
    }
    return null;
}
```

### Highlight System
```csharp
// In Draw()
if (hoveredCountry != null) {
    var outline = GetOutlineVertices(hoveredCountry.Mesh);
    // Draw outline with thickness = 3px, color = yellow
}

if (selectedCountry != null) {
    // Draw thicker outline = 5px, color = white
    // Show CountryPanel
}
```

### Camera Focus
```csharp
public void FocusOnCountry(string countryId) {
    var country = World.Countries[countryId];
    var bounds = country.BoundingBox; // Envelope
    Camera.FitToBounds(bounds);
}
```

### Days 39-40: Performance Optimization

#### Spatial Partitioning (Quadtree)
```csharp
// At startup
var quadtree = new Quadtree(new BoundingBox(worldBounds));
foreach (var country in World.Countries.Values) {
    quadtree.Insert(new QuadtreeItem { Bounds = country.BoundingBox, Data = country });
}

// Query only visible
var visible = quadtree.Query(cameraFrustum);
```

#### Level of Detail
| Zoom Level | Detail |
|------------|--------|
| < 0.5 | Render all countries as single color (no outlines) |
| 0.5-2.0 | Render with country borders |
| > 2.0 | Render platform overlay nodes too |
| > 5.0 | Show demographic breakdown labels |

#### Batch Rendering
```csharp
// Build vertex buffer once at startup
var vertexBuffer = new VertexBuffer(GraphicsDevice, typeof(VertexPositionColor), totalVertices, BufferUsage.WriteOnly);

// In Draw()
GraphicsDevice.Vertices[0].Set(vertexBuffer);
GraphicsDevice.DrawPrimitives(PrimitiveType.TriangleList, 0, totalTriangles);
```

---

## Phase 3 Deliverables Checklist
- [ ] MonoGame project compiles with 195-country GeoJSON
- [ ] World map renders with belief heatmap coloring
- [ ] Force-directed platform overlay with animated edges
- [ ] Click detection works on countries and platform hubs
- [ ] Camera: pan, zoom, fit-to-bounds
- [ ] Frustum culling + LOD for 195 countries at interactive FPS
- [ ] Belief shader effects (pulsing, color gradient)
- [ ] Headless mode still works (no rendering dependency)