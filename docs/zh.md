# Live2D Cubism——会动的 2D 角色

Live2D Cubism 把 Live2D 模型带进游戏：画出来的角色会呼吸、会眨眼、会盯着一个点看、会带物理地运动（头发、衣服），并播放美术做好的动作和表情。这个 mod 包含面向 C# 的 Cubism 运行时和它的原生核心库；其他 mod 用它在场景里显示模型。

它是 Cthangover 游戏的可选部件。目前的剧情内容仍在使用静态立绘，内置游戏也还没有附带 Live2D 模型；mod 自带的演示展示了一个能跑的模型。

![本 mod 渲染的示例模型 Ren：双臂垂下，而把一个参数（ParamArmR01）调高后右臂抬起。](assets/arm-parameter.jpg)

## 模型能做什么

- **闲置的生机**——自动眨眼（默认开启）和呼吸（默认关闭）。
- **物理**——头发和衣服按模型 `physics3.json` 的规则摆动。
- **注视**——头和眼睛转向一个目标点；演示中可以在角色身上拖动鼠标来引导它。
- **动作与表情**——播放模型里定义的某组动作（`mtn_01`…）或设置表情（`exp_01`…）。
- **参数**——直接设置模型的任意参数，就像上图那样：一个参数就抬起了右臂。
- **摆放**——模型自动适配自己的区域，或按给定的位置和缩放摆放。

## 模型的要求

一个模型就是标准的 Cubism 导出文件夹，放在 mod 里面：

```filestree
my_mod/
└── models/
    └── runtime/
        ├── ren.model3.json       你要指向的入口文件
        ├── ren.moc3
        ├── ren.physics3.json
        ├── ren.cdi3.json
        ├── ren.4096/texture_00.png
        ├── expressions/exp_01.exp3.json …
        └── motions/mtn_01.motion3.json …
```

- 模型必须是 Cubism 模型（`.moc3` 加一个 `model3.json`）。演示模型用 Cubism 5.3 制作，用到了它的遮罩和离屏绘制。
- 原生 Cubism 核心已随 mod 提供 Windows（`.dll`）和 Linux（`.so`）版本。
- 示例模型带有 Live2D 自己的许可协议；发布前请先阅读模型的 `ReadMe.txt`。

## 从剧本显示模型

`add_live2d_model` 动作把一个模型放进场景视图：

```scenario
action add_live2d_model model_path=models/runtime/ren.model3.json mod_id=my_mod node_name=Ren x=960 y=540 scale=0.5
```

- `model_path`——`model3.json`，相对 mod 文件夹的路径；
- `mod_id`——包含它的 mod；
- `node_name`——这个模型在场景中的名字（同名第二个动作会被忽略）；
- `x`、`y`、`scale`——可选的位置和大小；不填则模型自动适配。

带代码的 mod 可以用 `Live2DService.CreateModel(...)` 创建模型，然后调用它的 `StartMotion(group, number)`、`StartExpression(id)`、`SetLiveParameter(id, value)` 或 `SetTargetPoint(x, y)`。

## 演示

本 mod 的 **avatar** 测试套件包含示例模型 Ren。开启后，Ren 会出现在 Cthangover 的别墅外并跟随鼠标拖动；它的自动检查会抬起右臂并比较画面帧。这是给开发者的演示，正常游戏中不开启。

## 出问题了？

1. **没有模型，日志提到 `model_path` 或 mod。** 路径相对 mod 文件夹，必须指向 `model3.json`；`mod_id` 必须是拥有它的那个 mod 的 id。
2. **模型位置或大小不对。** 给出 `x`、`y` 和 `scale`，或者三个都不填让它自动适配。
3. **模型在某个平台上加载不出来。** Cubism 核心库只提供 Windows 和 Linux 版本。
