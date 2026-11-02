<a id="可编辑盘片预设"></a>
# Preajustes de discos editables

> Traducción al español · [Original](DISC_PRESETS.md)

Los preajustes tienen dos capas:

- La integrada se distribuye y actualiza con la app y contiene los preajustes oficiales.
- La del usuario está en este archivo y sólo conserva adiciones, sustituciones y desactivaciones del usuario:

```text
%LOCALAPPDATA%\DiscImageStudio\disc-presets.json
```

En el primer inicio se añaden tres preajustes editables a la capa del usuario: «Parámetros CD personalizados», «Parámetros DVD personalizados» y «Parámetros Blu-ray personalizados». Los integrados no se copian al archivo del usuario; por ello, sus correcciones y las nuevas entradas se aplican automáticamente al actualizar.

Puedes añadir preajustes en el diálogo Configuración del proyecto: «Guardar parámetros como preajuste» solicita un nombre en una ventana separada y añade los parámetros a la capa del usuario. Las plantillas de proyecto nuevo muestran todos los preajustes no manuales del catálogo, agrupados en «Recientes», «CD», «DVD» y «Blu-ray».

Configuración del proyecto también lista los preajustes del tipo de disco actual: elegir otro sobrescribe la geometría del diálogo, así que primero pide confirmación; cancelar conserva los parámetros y la selección. Puedes eliminar con el botón derecho preajustes personalizados en Proyecto nuevo y Configuración del proyecto; se guarda inmediatamente en la capa del usuario y se actualiza la lista. Si editas directamente el JSON (añadir, renombrar o desactivar), debes reiniciar.

<a id="合并规则"></a>
## Reglas de combinación

- Un preajuste del usuario con un `id` nuevo se añade tras los integrados.
- Si comparte `id` con uno integrado, lo sustituye por completo.
- `disabledCdPresetIds`, `disabledDvdPresetIds` y `disabledBluRayPresetIds` ocultan preajustes integrados o del usuario que no se necesiten.
- Al actualizar los integrados, cambian automáticamente los parámetros que no estén sustituidos por una entrada del usuario con el mismo ID.

<a id="添加-dvd-预设"></a>
## Añadir un preajuste DVD

Copia un objeto inicial del usuario en `dvdPresets` y cambia `id`, nombre y parámetros. Ejemplo (se conserva el texto de datos del original):

```json
{
  "id": "verbatim-dvd-r-azo-43533",
  "displayName": "Verbatim DVD-R AZO (43533)（实测）",
  "description": "Verbatim DVD-R AZO 43533 已验证标定参数：23.9914606277–57.9648936579 mm，共 2,297,888 个扇区；采用三次轨距分布。",
  "totalSectors": 2297888,
  "innerRadiusMm": 23.991460627731737,
  "outerRadiusMm": 57.964893657925366,
  "channelBitLengthNm": 133.3,
  "pitchLinear": -3.9256854319227684E-05,
  "pitchQuadratic": 6.964795915429256E-05,
  "pitchCubic": -4.670918950137523E-05
}
```

En DVD, `totalSectors` debe ser positivo y múltiplo de 16 para cumplir el bloque ECC y la distribución de grabación en streaming. `channelBitLengthNm` es un campo reservado, descrito más adelante.

Este preajuste Verbatim DVD procede de `calibration-f2f851f7.calibration.json`, cuya validación fue confirmada en el proyecto original, y conserva toda la precisión del registro.

`pitchLinear`, `pitchQuadratic` y `pitchCubic` son los tres coeficientes de forma relativos del paso cúbico; su valor predeterminado es 0, compatible con preajustes antiguos. El modelo es `p(x)=p0*(1+pitchLinear*x+pitchQuadratic*x²+pitchCubic*x³)`, donde `x=(r-innerRadiusMm)/(outerRadiusMm-innerRadiusMm)`. El polinomio completo tiene cuatro términos; `p0` se calcula con la longitud total de pista, y la interfaz muestra los cuatro valores reales en μm. El paso debe ser estrictamente positivo en todo `0≤x≤1`, no sólo en los límites interior y exterior. Modificar un coeficiente activa el estado personalizado sin guardar; volver a elegir un preajuste integrado de paso constante restablece los coeficientes a cero.

<a id="添加-cd-预设"></a>
## Añadir un preajuste CD

Copia un objeto existente en `cdPresets` y cambia `id`, nombre, número de sectores y radios. Ejemplo:

```json
{
  "id": "cd-74",
  "displayName": "标准 74min CD",
  "description": "ECMA-130／ISO/IEC 10149 标准速度 1200 mm/s；120 mm 盘、约 74 分钟，333,000 扇区（24.5–56.8 mm）。",
  "sectors": 333000,
  "innerRadiusMm": 24.5,
  "outerRadiusMm": 56.8,
  "linearVelocityMmPerSecond": 1200
}
```

<a id="添加蓝光预设"></a>
## Añadir un preajuste Blu-ray

Copia un objeto existente en `bluRayPresets` y cambia `id`, nombre y parámetros. Ejemplo:

```json
{
  "id": "bd-r-ritek-10x",
  "displayName": "铼德 BD-R 10x（实测）",
  "description": "实测参数：内半径 23.988221 mm、外半径 58.023235 mm、12,219,392 扇区。",
  "totalSectors": 12219392,
  "innerRadiusMm": 23.98822105616562,
  "outerRadiusMm": 58.023234731100764,
  "invertMapping": false
}
```

En BD, `totalSectors` debe ser positivo, múltiplo de 32 y no superar los 12.219.392 sectores de una capa; el radio exterior no puede superar 60 mm. `invertMapping` es la polaridad 3T/6T inicial, que aún se puede cambiar en Tono de la página Diseño después de aplicarla al proyecto.

El integrado `bd-r-verbatim-64099` (Verbatim BD-R (64099)) usa el preajuste «Verbatim 64099» guardado en el archivo original del usuario `disc-presets.json`, con toda su precisión: radio interior `23.987042157339335` mm, exterior `57.987960341783236` mm, `12,219,392` sectores e `invertMapping: false`. La GUI y la CLI usan los mismos parámetros.

<a id="固定参数与保留字段"></a>
## Parámetros fijos y campos reservados

La velocidad de exploración, el ángulo inicial, el radio exterior de imagen y la longitud de bit de canal DVD son magnitudes físicas fijas del esquema, sin formularios propios: al generar se usan siempre 1200 mm/s (ECMA-130), 0°, 57,5 mm y 133,3 nm.

Se conservan sus campos JSON porque eran obligatorios en configuraciones antiguas y se busca poder leer directamente los archivos escritos por esas versiones. El original indica que estos dos valores no intervienen en el cálculo de generación.

<a id="i18n-兼容字段"></a>
## Campos compatibles con i18n

El formato reserva `fallbackLanguage`, `displayNameResourceKey` y `descriptionResourceKey` opcionales. Según la descripción de formato original, la versión actual muestra directamente `displayName` y `description`; al integrar recursos multilingües se dará prioridad a las claves con el texto existente como alternativa, sin volver a cambiar la estructura JSON. Los preajustes del usuario no necesitan claves de recurso.

<a id="编辑规则"></a>
## Reglas de edición

- La raíz actual es `"schemaVersion": 4`, con `cdPresets`, `dvdPresets`, `bluRayPresets`, tres listas de desactivación y `fallbackLanguage` opcional. También se aceptan versiones `2` y `3`; si falta BD se completan sus plantillas integradas y al guardar se actualiza a 4.
- Cada grupo debe tener `id` únicos. `__manual__` está reservado al estado «Parámetros personalizados sin guardar» y no puede escribirse en JSON.
- Sólo se permiten los parámetros de generación de la plantilla. Otros campos se rechazan para evitar mezclar composición o calibración con los preajustes.
- Los radios, `linearVelocityMmPerSecond` y `channelBitLengthNm` deben ser números finitos positivos; el radio exterior de generación debe superar al interior.
- Se permiten comentarios `//` o `/* ... */` y comas al final de arrays.
- Si el formato o los parámetros son inválidos, se explica el error y se conserva el último preajuste cargado correctamente, sin sobrescribir automáticamente el archivo del usuario.
