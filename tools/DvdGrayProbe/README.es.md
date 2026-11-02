<a id="dvd-64--e4-灰阶与混合测试"></a>
# Prueba de grises y mezclas DVD 64 / E4

> Traducción al español · [Original](README.md)

Esta herramienta prepara un disco comparativo para **Verbatim DVD-R AZO 43533**: usa `A5 / 66 / B9 / 92`, ya probado, como control y compara colores uniformes, mezclas tramadas e imágenes al sustituir `66` por `64` o `E4`. También prueba por separado la mezcla `64` con `E4`. Genera y verifica la imagen, pero no graba.

Todos los bytes son **bytes Q objetivo después de aleatorizar DVD**. El atlas especifica Q directamente y se lee por vecino más cercano al generar, sin interpolación bilineal ni nueva cuantización de grises.

<a id="生成与验证"></a>
## Generación y verificación

Requiere Windows y SDK .NET 10. Prepara un PNG de cuatro niveles de gris de `6000 × 6000`; se lee una región de `640 × 640` con esquina superior izquierda `(2880, 1920)`. Tras convertirla a Gray8 sólo puede contener `0 / 85 / 170 / 255`; se rechazan dimensiones o valores distintos. No escala ni vuelve a tramar la entrada, y el repositorio no incluye una imagen de ejemplo.

Desde la raíz, sustituye `four-level-6000.png` por tu entrada; el directorio de salida de `prepare` debe estar vacío:

```powershell
dotnet run --project tools/DvdGrayProbe --configuration Release -- prepare --source "four-level-6000.png" --output-dir "artifacts/dvd-gray-probe-64-e4"

dotnet run --project tools/DvdGrayProbe --configuration Release -- generate --prepared "artifacts/dvd-gray-probe-64-e4" --output "artifacts/dvd-gray-probe-64-e4/burn.iso" --parallelism 8

dotnet run --project tools/DvdGrayProbe --configuration Release -- verify --prepared "artifacts/dvd-gray-probe-64-e4" --iso "artifacts/dvd-gray-probe-64-e4/burn.iso"
```

Archivos de salida:

| Archivo | Uso |
|---|---|
| `burn.iso` | Imagen completa para grabar: 2.297.888 sectores de 2048 bytes, 4.706.074.624 bytes |
| `burn.iso.json` | Registro de generación y verificación |
| `probe.json` | Registro de distribución, origen y parámetros |
| `atlas.q8` | Atlas objetivo Q directo de 6000 × 6000 |
| `code-map.png` | Guía de distribución que distingue bytes Q por colores; no predice el color o brillo físico |
| `nominal-preview.png` | Vista previa según grises nominales para identificar el contenido, no para predecir el contraste grabado |

`burn.iso` carece intencionadamente de sistema de archivos: es normal que no pueda montarse. Selecciónalo mediante **«Escribir imagen / Write image file to disc»** del programa de grabación; no lo añadas como un archivo normal a un disco de datos. Usa discos 43533 y la misma grabadora y velocidad que en la prueba anterior para comparar.

**No pases los dos PNG de guía por el antiguo proceso de generación de imágenes de grises.** Volvería a cuantizarlos y no conservaría `64`, `E4` ni las proporciones especificadas.

<a id="如何看测试盘"></a>
## Interpretar el disco de prueba

Hay doce paneles de 30° en sentido horario desde las doce; el sufijo del número indica el candidato a gris oscuro:

```text
1-66  2-64  3-E4  |  4-64  5-E4  6-66  |  7-E4  8-66  9-64  |  10-66  11-E4  12-64
```

Cada grupo de tres comparte la entrada y usa `A5 / candidato / B9 / 92`. Los candidatos se repiten y cambian de orden entre ángulos para reducir el efecto de la posición. Los radios se miden desde el centro:

| Radio | Contenido |
|---|---|
| 24.5–25.6 mm | Parches interiores uniformes cada 15°: `A5、66、64、E4、B9、92` repetidos cuatro veces |
| 26.0–28.1 mm | Gradiente Floyd–Steinberg serpenteante de cuatro niveles; mismo mapa de niveles en todos los paneles, sólo cambia el byte de gris oscuro |
| 28.5–30.5 mm | Mezcla directa `64 / E4`; tres columnas horarias por panel con 25%, 50%, 75% de `64`, el resto `E4` |
| Radio central 37.5 mm | Mismo recorte cuadrado de imagen de 12,8 mm |
| 45.8–47.5 mm | Candidato mezclado con `A5` |
| 47.8–49.5 mm | Candidato mezclado con `B9` |
| 49.8–51.5 mm | Candidato mezclado con `92` |
| 52.2–57.3 mm | Parches exteriores uniformes en el mismo orden que los interiores, con etiquetas de byte |

Las últimas tres filas de mezclas dividen cada panel en tres columnas horarias: **25%, 50%, 75% del candidato** y el resto del otro byte de la fila. Todos los candidatos usan la misma máscara FS binaria.

Los porcentajes son proporciones de píxeles objetivo y `0 / 85 / 170 / 255` son grises nominales; ninguno implica intervalos iguales de brillo físico. La cuadrícula de imagen mide **20 µm** para probar la escala de mezcla tramada, no una alternancia de bytes Q individuales.

La entrada ya es un PNG tramado de cuatro niveles. La herramienta toma la región `(2880, 1920)`, de `640 × 640`, sin escalar ni volver a tramar. En el recorte experimental original, las cuatro proporciones eran aproximadamente `24.13% / 23.90% / 22.74% / 29.24%`; cambian con cada entrada. Entre los paneles de imagen sólo cambia el byte correspondiente al nivel original `85`.

El atlas descrito en el registro original se comprobó independientemente: 144 celdas de mezcla y 4.216.432 píxeles válidos. Excluyendo espacios vacíos, la mayor desviación frente a la proporción objetivo fue **0,501 puntos porcentuales** y, para `64/E4`, **0,197 puntos**, sin otros bytes Q mezclados. Consulta `mixture-measurements.csv` y `.json` en la salida. La ISO también se verificó por longitud y se contrastaron byte a byte 2.359.296 bytes Q objetivo de datos útiles en nueve radios. Es validación digital; el efecto físico queda pendiente de grabar y observar.

<a id="记录结果"></a>
## Registrar resultados

Primero compara cada grupo de tres con la misma luz y posición normal de observación. Gira el disco para revisar los mismos candidatos en otras posiciones y después cambia ligeramente el ángulo para distinguir «visible de forma estable desde un ángulo normal» de «visible sólo desde cierto ángulo».

Observa si se distingue el candidato tanto de `A5` como de `B9`, si aumentar la mezcla produce cambios ordenados, si se recupera detalle oscuro y si mezclar `64 / E4` produce niveles más útiles y estables angularmente que los colores puros. En el control, `B9` debe seguir distinguiéndose de `A5` y `92`.

Puedes registrar `0 = difícil de distinguir; 1 = distinguible desde cierto ángulo; 2 = distinguible de forma estable desde un ángulo normal`:

| Candidato | Frente a A5 | Frente a B9 | Tres mezclas ordenadas y distinguibles | Detalle oscuro de imagen | Notas |
|---|---|---|---|---|---|
| 66 | | | | | |
| 64 | | | | | |
| E4 | | | | | |

Anota por separado si se distinguen las mezclas 25%, 50% y 75% de `64 / E4` y si sólo funcionan desde ciertos ángulos. La adopción de un cuarto nivel depende de los resultados físicos.

<a id="固定校准参数"></a>
## Parámetros de calibración fijos

La prueba usa los parámetros 43533 ya confirmados en el registro original:

- Radio inicial: `23.991460627731737 mm`
- Radio final: `57.964893657925366 mm`
- Sectores: `2297888`
- Longitud de bit de canal: `133.3 nm`
- Coeficientes del polinomio de paso: `[-3.9256854319227684E-05, 6.964795915429256E-05, -4.670918950137523E-05]`
- Atlas: `6000 × 6000`, que cubre `120 × 120 mm`

Estos parámetros sirven para la correspondencia espacial del disco de la prueba; la guía no es un modelo físico de luminosidad.
