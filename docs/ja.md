# Live2D Cubism——動く 2D キャラクター

Live2D Cubism は、Live2D モデルをゲームに持ち込みます: 描かれたキャラクターが呼吸し、まばたきし、一点を見つめ、物理(髪、服)で動き、絵師が作ったモーションと表情を再生します。C# 用の Cubism ランタイムとそのネイティブコアライブラリを含み、ほかの mod はこれを使ってシーンにモデルを表示します。

Cthangover ゲームの任意のブロックです。現在のストーリーコンテンツはまだ静止ポートレートを使っており、内蔵ゲームはどれも Live2D モデルを同梱していません。mod 自前のデモが動くモデルを見せてくれます。

![この mod が描画したサンプルモデル Ren: 腕を下げた状態と、パラメータ(ParamArmR01)をひとつ上げたあとの右腕が上がった状態。](assets/arm-parameter.jpg)

## モデルにできること

- **待機のいのち** — 自動まばたき(既定でオン)と呼吸(既定でオフ)。
- **物理** — 髪や服が、モデルの `physics3.json` に従って揺れます。
- **視線** — 頭と目が目標点へ向きます。デモでは、キャラクターの上をマウスでドラッグして視線を導けます。
- **モーションと表情** — モデルに定義されたグループ(`mtn_01`…)のモーションを再生したり、表情(`exp_01`…)を設定したりします。
- **パラメータ** — モデルのパラメータをどれでも直接設定できます。上の絵は、ひとつのパラメータで右腕が上がったところです。
- **配置** — モデルは自分の領域に自動で収まるか、指定した位置と倍率で置かれます。

## モデルの要件

モデルは Cubism の標準エクスポートフォルダで、mod の中に置きます:

```filestree
my_mod/
└── models/
    └── runtime/
        ├── ren.model3.json       指し示す入口のファイル
        ├── ren.moc3
        ├── ren.physics3.json
        ├── ren.cdi3.json
        ├── ren.4096/texture_00.png
        ├── expressions/exp_01.exp3.json …
        └── motions/mtn_01.motion3.json …
```

- モデルは Cubism モデル(`model3.json` 付きの `.moc3`)です。デモモデルは Cubism 5.3 で作られ、そのマスクとオフスクリーン描画を使っています。
- ネイティブの Cubism コアは Windows(`.dll`)と Linux(`.so`)向けが同梱されています。
- サンプルモデルは Live2D 独自のライセンスが付きます。公開する前に、モデルの `ReadMe.txt` を読んでください。

## シナリオからモデルを表示する

`add_live2d_model` アクションが、シーンのビューへモデルを置きます:

```scenario
action add_live2d_model model_path=models/runtime/ren.model3.json mod_id=my_mod node_name=Ren x=960 y=540 scale=0.5
```

- `model_path` — `model3.json`。mod フォルダからの相対パス。
- `mod_id` — それを含む mod。
- `node_name` — このモデルのシーン内での名前(同じ名前の 2 つ目のアクションは無視されます)。
- `x`、`y`、`scale` — 省略できる位置と大きさ。なければモデルは自動で収まります。

コードを持つ mod は `Live2DService.CreateModel(...)` でモデルを作り、`StartMotion(group, number)`、`StartExpression(id)`、`SetLiveParameter(id, value)`、`SetTargetPoint(x, y)` を呼べます。

## デモ

この mod の **avatar** テストスイートに、サンプルモデル Ren が入っています。有効にすると、Ren は Cthangover のヴィラの外に現れ、マウスのドラッグに従います。自動チェックが右腕を上げ、フレームを比較します。開発者向けのデモで、通常のプレイではオフです。

## うまくいかないとき

1. **モデルが出ないのに、ログに `model_path` や mod についての表示がある。** パスは mod フォルダからの相対で、`model3.json` を指す必要があります。`mod_id` は、モデルを持つ mod の id でなければなりません。
2. **モデルの場所や大きさがおかしい。** `x`、`y`、`scale` を指定するか、3 つとも省略して自動収任せにしてください。
3. **プラットフォーム上でモデルが読み込めない。** Cubism コアライブラリは Windows と Linux 向けしかありません。
