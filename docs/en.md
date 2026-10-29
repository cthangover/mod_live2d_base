# Live2D Cubism — animated 2D characters

Live2D Cubism brings Live2D models into the game: drawn characters that breathe, blink, follow a point, move with physics (hair, clothes) and play the motions and expressions their artist made. It contains the Cubism runtime for C# and its native core libraries; other mods use it to show a model in a scene.

It is an optional block of the Cthangover game. The current story content still uses still portraits, and no built-in game ships a Live2D model yet; the mod's own demo shows a working model.

![The sample model Ren, rendered by this mod: arms lowered, and after one parameter (ParamArmR01) raised the right arm.](assets/arm-parameter.jpg)

## What a model can do

- **Idle life** — automatic blinking (on by default) and breathing (off by default).
- **Physics** — hair and clothes swing according to the model's `physics3.json`.
- **Looking** — the head and eyes turn toward a target point; the demo lets you drag the mouse over the character to steer them.
- **Motions and expressions** — play a motion from a group (`mtn_01`…) or set an expression (`exp_01`…) defined in the model.
- **Parameters** — set any of the model's parameters directly, as in the picture above, where the right arm is raised by one parameter.
- **Placement** — the model is fitted to its area automatically, or placed at a given position and scale.

## Model requirements

A model is the standard Cubism export folder, kept inside a mod:

```filestree
my_mod/
└── models/
    └── runtime/
        ├── ren.model3.json       the entry file you point to
        ├── ren.moc3
        ├── ren.physics3.json
        ├── ren.cdi3.json
        ├── ren.4096/texture_00.png
        ├── expressions/exp_01.exp3.json …
        └── motions/mtn_01.motion3.json …
```

- The model is a Cubism model (`.moc3` with a `model3.json`). The demo model is made with Cubism 5.3 and uses its masks and offscreen drawing.
- The native Cubism core is included for Windows (`.dll`) and Linux (`.so`).
- Sample models come with Live2D's own licence; read the model's `ReadMe.txt` before publishing it.

## Show a model from a scenario

The `add_live2d_model` action puts a model into the scene's view:

```scenario
action add_live2d_model model_path=models/runtime/ren.model3.json mod_id=my_mod node_name=Ren x=960 y=540 scale=0.5
```

- `model_path` — the `model3.json`, relative to the mod's folder;
- `mod_id` — the mod that contains it;
- `node_name` — a name for this model in the scene (a second action with the same name is ignored);
- `x`, `y`, `scale` — optional position and size; without them the model is fitted automatically.

A mod with code can create a model with `Live2DService.CreateModel(...)` and then call `StartMotion(group, number)`, `StartExpression(id)`, `SetLiveParameter(id, value)` or `SetTargetPoint(x, y)` on it.

## The demo

The mod's **avatar** test suite contains the sample model Ren. When it is turned on, Ren appears outside the villa in Cthangover and follows mouse drags; its automatic check raises the right arm and compares the frames. It is a developer demo and is off in normal play.

## If something goes wrong

1. **No model and a log message about `model_path` or the mod.** The path is relative to the mod's folder and must point to the `model3.json`; `mod_id` must be the id of the mod that has it.
2. **The model appears in the wrong place or size.** Give `x`, `y` and `scale`, or leave all three out for automatic fitting.
3. **The model does not load on a platform.** The Cubism core library is provided for Windows and Linux only.
