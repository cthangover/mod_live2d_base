# Live2D Cubism — personajes 2D animados

Live2D Cubism trae modelos de Live2D al juego: personajes dibujados que respiran, parpadean, siguen un punto, se mueven con física (pelo, ropa) y reproducen los movimientos y expresiones que hizo su artista. Contiene el runtime de Cubism para C# y sus bibliotecas nativas del núcleo; otros mods lo usan para mostrar un modelo en una escena.

Es un bloque opcional del juego Cthangover. El contenido de la historia actual sigue usando retratos fijos, y todavía ningún juego integrado incluye un modelo Live2D; la demo propia del mod muestra un modelo en funcionamiento.

![El modelo de ejemplo Ren, renderizado por este mod: brazos bajados, y tras subir un parámetro (ParamArmR01) el brazo derecho en alto.](assets/arm-parameter.jpg)

## Lo que puede hacer un modelo

- **Vida en reposo** — parpadeo automático (activado por defecto) y respiración (desactivada por defecto).
- **Física** — el pelo y la ropa se mueven según el `physics3.json` del modelo.
- **Mirar** — la cabeza y los ojos giran hacia un punto objetivo; en la demo puedes arrastrar el ratón sobre el personaje para dirigir su mirada.
- **Movimientos y expresiones** — reproducir un movimiento de un grupo (`mtn_01`…) o poner una expresión (`exp_01`…) definidas en el modelo.
- **Parámetros** — fijar directamente cualquier parámetro del modelo, como en la imagen de arriba, donde un solo parámetro levanta el brazo derecho.
- **Colocación** — el modelo se ajusta solo a su área, o se coloca en una posición y escala dadas.

## Requisitos del modelo

Un modelo es la carpeta de exportación estándar de Cubism, guardada dentro de un mod:

```filestree
my_mod/
└── models/
    └── runtime/
        ├── ren.model3.json       el archivo de entrada al que apuntas
        ├── ren.moc3
        ├── ren.physics3.json
        ├── ren.cdi3.json
        ├── ren.4096/texture_00.png
        ├── expressions/exp_01.exp3.json …
        └── motions/mtn_01.motion3.json …
```

- El modelo es un modelo Cubism (`.moc3` con un `model3.json`). El modelo de demo está hecho con Cubism 5.3 y usa sus máscaras y su dibujo fuera de pantalla.
- El núcleo nativo de Cubism viene incluido para Windows (`.dll`) y Linux (`.so`).
- Los modelos de ejemplo tienen la licencia propia de Live2D; lee el `ReadMe.txt` del modelo antes de publicarlo.

## Mostrar un modelo desde un escenario

La acción `add_live2d_model` pone un modelo en la vista de la escena:

```scenario
action add_live2d_model model_path=models/runtime/ren.model3.json mod_id=my_mod node_name=Ren x=960 y=540 scale=0.5
```

- `model_path` — el `model3.json`, relativo a la carpeta del mod;
- `mod_id` — el mod que lo contiene;
- `node_name` — un nombre para este modelo en la escena (una segunda acción con el mismo nombre se ignora);
- `x`, `y`, `scale` — posición y tamaño opcionales; sin ellos el modelo se ajusta automáticamente.

Un mod con código puede crear un modelo con `Live2DService.CreateModel(...)` y luego llamar `StartMotion(group, number)`, `StartExpression(id)`, `SetLiveParameter(id, value)` o `SetTargetPoint(x, y)`.

## La demo

El conjunto de pruebas **avatar** de este mod contiene el modelo de ejemplo Ren. Cuando se activa, Ren aparece fuera de la villa en Cthangover y sigue los arrastres del ratón; su comprobación automática levanta el brazo derecho y compara los fotogramas. Es una demo para desarrolladores y está apagada en el juego normal.

## Si algo falla

1. **No hay modelo y el log habla de `model_path` o del mod.** La ruta es relativa a la carpeta del mod y debe apuntar al `model3.json`; `mod_id` debe ser el id del mod que lo contiene.
2. **El modelo aparece en un lugar o tamaño equivocados.** Da `x`, `y` y `scale`, o no des ninguno de los tres para el ajuste automático.
3. **El modelo no carga en una plataforma.** La biblioteca del núcleo de Cubism solo existe para Windows y Linux.
