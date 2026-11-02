Traducción al español · [Original](CD_AUDIO_COMPATIBILITY.md)

<a id="cd-音轨格式字节序与重影排查"></a>
# Formato de pistas de CD, orden de bytes y diagnóstico de imágenes dobles

<a id="随盘文件与数据盘"></a>
## Archivos del disco y discos de datos

Al seleccionar una carpeta de archivos del disco en la página de diseño o de exportación de un proyecto CD, se cambia automáticamente a un disco de datos ISO9660/Joliet y la extensión de salida pasa a `.iso`; al vaciar la ruta de la carpeta, se vuelve a una pista WAV. Los archivos pueden situarse en la zona interior o exterior, con la interior como opción predeterminada, y el ajuste se guarda con el proyecto. Los metadatos del sistema de archivos siguen al principio del disco; el modo exterior coloca el contenido de los archivos al final. El dibujo conserva las coordenadas originales del disco completo, sin comprimirse en la zona restante.

```powershell
DiscImageStudio.exe cd-generate --input picture.png --output cd-image.iso `
  --data-dir "D:\随盘文件" --volume-label CD_IMAGE --file-position outer
```

La longitud de la ISO es `número de sectores × 2048` y se graba como disco de datos CD-ROM Mode 1. La grabación al vuelo integrada utiliza el mismo flujo de datos ISO; en un programa externo de grabación se debe seleccionar la función de grabar imagen. El interruptor de entrelazado de audio y el CUE sólo se utilizan en el modo de pista de audio. La zona de dibujo del disco de datos compensa por separado la aleatorización CD-ROM y la asignación de posiciones; los archivos del disco conservan su contenido original.

La grabación al vuelo de discos de datos CD comparte con las pistas de audio la sesión DAO y la comprobación del límite de lead-out, y conserva el número de sectores calibrado mediante ATIP y la distribución completa de archivos. El programa codifica los datos útiles ISO en sectores sin procesar Mode 1 y establece el identificador de pista de datos; al utilizar el preajuste de CD Ritek Medical Aqua, sigue escribiendo 359,845 sectores de datos, sin acortar el patrón ni los archivos por la menor capacidad disponible que informa la interfaz TAO. DAO mantiene la comprobación de overburn limitado a un máximo de 3 sectores del modo de pista de audio, y si se supera el límite se informa de un error antes de escribir.

La pista de datos utiliza la convención little-endian del canal principal de IMAPI: completa el EDC y los códigos de corrección P/Q del sector Mode 1 estándar y lo aleatoriza, manteniendo el orden de bytes de la cabecera de sincronización `00 FF…FF 00`. El intercambio de bytes adyacentes de la codificación física CD-ROM se compensa con el intercambio de conversión a little-endian; esto también concuerda con la [codificación RAW y conversión del orden de bytes del dispositivo de cdrdao](https://github.com/cdrdao/cdrdao/blob/master/dao/dao.cc). La distribución ISO de archivos interiores o exteriores y la asignación de coordenadas físicas del dibujo no cambian con esta convención de transferencia.

La proyección del diseño, la vista previa pequeña del tono y la relectura del resultado con información de distribución válida en `.iso.json` resaltan en amarillo la zona de archivos (incluido el sistema de archivos). Los metadatos del mismo nombre registran la geometría, el tono y la posición de los archivos; se recomienda moverlos junto con la ISO. La vista previa muestra la asignación de los datos útiles del dibujo; el efecto en un disco físico sigue requiriendo una grabación de prueba.

<a id="推荐用法"></a>
## Uso recomendado

Las nuevas tareas de generación producen `cd-track.wav` de forma predeterminada y generan también `cd-track.cue`. El WAV contiene PCM sin comprimir: 44.1 kHz, 16 bits y dos canales, con datos little-endian. Ambos archivos deben estar en el mismo directorio; si se mueven o renombran, también debe actualizarse el nombre de archivo del CUE. El CUE es opcional: al desmarcar «Exportar también un archivo CUE» en el espacio de exportación o pasar `--cue false` en la línea de comandos, sólo se escribe el WAV.

| Método de grabación | Uso |
| --- | --- |
| cdrecord | `cdrecord dev=设备 speed=速度 -audio "cd-track.wav"`, sin añadir `-swab` |
| ImgBurn | Abrir `cd-track.cue` en «Write image file to disc» |
| Grabación por flujo integrada | Genera automáticamente los mismos datos PCM little-endian, sin cabecera WAV ni pista temporal completa |
| Flujo de trabajo RAW anterior | Sigue siendo posible elegir la salida `.raw`; utilizar `cdrecord -audio "cd-track.raw"` sin `-swab` |

La extensión de salida determina el formato real; sólo se aceptan `.wav` y `.raw`. No se puede convertir un RAW anterior a WAV renombrándolo: debe generarse de nuevo. El CUE que acompaña al WAV utiliza `FILE "cd-track.wav" WAVE`, no `BINARY`. Para nombres de archivo Unicode se utiliza CUE UTF-8 con BOM, compatible con espacios y caracteres chinos. El autor de ImgBurn verificó la compatibilidad con nombres Unicode de este tipo de CUE e indicó que los problemas de la antigua versión 2.5.0.0 se corrigieron en 2.5.1.0. [Explicación del autor de ImgBurn](https://forum.imgburn.com/topic/13526-unicode-filename-problem-in-cue-file/)

Ejemplo de generación desde la línea de comandos (los parámetros geométricos deben corresponder a las mediciones propias):

```powershell
DiscImageStudio.exe cd-generate --input picture.png --output cd-track.wav --interleave true
```

`--cue` (`true` de forma predeterminada) controla si la salida WAV escribe también un CUE con el mismo nombre; la salida `.raw` nunca escribe un CUE.

La longitud del archivo WAV es `número de sectores × 2352 + 44`; la longitud de audio, el progreso de generación y la capacidad de grabación por flujo siguen siendo `número de sectores × 2352`. La cabecera no ocupa sectores de la pista del disco ni cambia la geometría del patrón.

<a id="影调抖动srgb-线性化灰阶与亮度"></a>
## Tono (tramado, linealización sRGB, escala de grises y brillo)

Cada byte de audio CD corresponde aproximadamente a 6.8 µm de longitud sobre el disco; a una distancia normal de observación, el ojo integra cientos o miles de bytes en un solo tono. Por ello, que «cada byte sólo tenga dos patrones, blanco y negro» no significa que «la imagen sólo tenga blanco y negro»: los tonos intermedios se reproducen mediante la proporción de bytes blancos y negros adyacentes.

| Parámetro | Significado |
| --- | --- |
| Intensidad de tramado 0–100% | El 0% aplica un umbral fijo de 128 (comportamiento anterior); el 100% elige proporcionalmente entre niveles adyacentes para cada byte, y los tonos intermedios se forman con la proporción espacial de bytes blancos y negros |
| Niveles de gris (sólo CD) | 2 niveles utilizan `0x0D` / `0xAA`; 4 niveles utilizan `0x0D` / `0x10` / `0x63` / `0xAA` (`0x0D` es negro y `0xAA` es blanco) |
| Linealización sRGB (gamma) | Decodifica primero con la función de transferencia sRGB y después aplica el tramado; cambia la densidad de puntos blancos y negros, sin cambiar los niveles de salida |
| Brillo | Desplaza toda la escala de grises en el intervalo −64…+64 (unidades de 0..255) |

Parámetros de línea de comandos (aceptados tanto por `cd-generate` como por `cd-preview-warp`):

```powershell
DiscImageStudio.exe cd-generate --input picture.png --output cd-track.wav `
  --tone-dither 1 --tone-levels 2 --tone-brightness 0 --tone-gamma false
```

Los valores predeterminados son `--tone-dither 0 --tone-levels 2 --tone-brightness 0 --tone-gamma false`, idénticos byte a byte a los de la versión anterior. Los proyectos nuevos utilizan 75% / 2 niveles de forma predeterminada en el módulo «Tono» de la columna derecha de la página de diseño; los proyectos antiguos sin sección `tone` se cargan con 0% y escriben esa sección al guardarse. La página de exportación sólo conserva las opciones de salida e indica en la tarjeta de salida que el tono se configura en la página de diseño.

La fase del tramado depende del **índice lógico del byte** (`globalByte`), en lugar de las coordenadas de pantalla. Después de que el entrelazado con retardo desplace los bytes dentro del archivo, la vista previa de relectura los reconstruye con el mismo índice lógico; por tanto, la generación, la proyección de calibración y la relectura muestran los mismos patrones. Los patrones de calibración (`GeneratePattern`) siempre utilizan el umbral fijo y no se someten a tramado.

El `.json` con el mismo nombre situado junto a la pista registra `toneDitherStrength`, `toneLevels`, `toneBrightness` y `toneSrgbLinearization`; al cargar una pista externa en la página de vista previa, estos valores se sincronizan junto con la geometría y el entrelazado.

La vista previa de relectura utiliza de forma predeterminada 1600 px y 16 muestras por sector, y **acumula y promedia** las muestras que caen en el mismo píxel. La versión anterior utilizaba siempre 900 px y 2 muestras por sector, y las escrituras posteriores sobrescribían las anteriores; el muestreo escaso dejaba puntos blancos en los tonos intermedios, parecidos a una trama de periódico. Al aumentar la densidad de muestreo, el mismo disco presenta tonos continuos. La «Proyección del diseño» de la página de vista previa configura inicialmente la geometría medida igual a la del proyecto, de modo que abrirlo produce una proyección neutra; las diferencias entre ambas geometrías sólo aparecen después de introducir los radios medidos.

<a id="已确认的问题"></a>
## Problemas confirmados

Comparación en disco físico aportada por un usuario el 2026-09-12: el mismo RAW existente funcionaba correctamente con `-audio` y `speed` de cdrecord; añadir `-swab` reproducía la imagen doble. Esto confirmó que el RAW existente debía interpretarse como entrada de audio big-endian, el valor predeterminado de cdrecord.

Antes, la exportación a archivo y la grabación por flujo compartían exactamente la misma salida de `GenerateToStream`, y la ruta por flujo no convertía el orden de bytes. Sin embargo, las convenciones de cada entrada son distintas:

| Entrada | Requisitos de los datos de audio |
| --- | --- |
| cdrecord sin cabecera `-audio` | Big-endian de forma predeterminada |
| cdrecord sin cabecera `-audio -swab` | Little-endian; esta opción declara el orden de entrada que requiere intercambio |
| cdrecord WAV | Analiza la cabecera y reconoce automáticamente el orden de bytes del audio |
| `FILE ... BINARY` de CUE | Little-endian; no se puede utilizar directamente un RAW big-endian existente en un CUE BINARY |
| IMAPI `AddTrack(IMAPI_CD_SECTOR_AUDIO, stream)` | La misma disposición de muestras que los datos de un WAV de 16 bits, dos canales y 44.1 kHz, es decir, PCM little-endian sin cabecera |

El comportamiento de cdrecord y la definición de BINARY anteriores proceden del [manual oficial de cdrecord](https://cdrtools.sourceforge.net/private/man/cdrecord/cdrecord.1.html); la disposición de muestras de audio de IMAPI procede de la [definición de Microsoft del sector AUDIO](https://learn.microsoft.com/en-us/windows/win32/api/imapi2/ne-imapi2-imapi_cd_sector_type). El autor de ImgBurn también proporcionó un ejemplo de grabación correcta con un CUE `FILE ... WAVE`. [Ejemplo de CUE WAVE de ImgBurn](https://forum.imgburn.com/topic/25173-extract-only-session-two-from-audio-cd/page/2/)

Aquí `AudioSectorType = 0` es el valor AUDIO de la enumeración IMAPI; no significa que la pista se escriba en el modo físico Mode 0. `IRawCDImageCreator::AddTrack` sólo admite actualmente el tipo AUDIO. [Documentación de AddTrack de Microsoft](https://learn.microsoft.com/en-us/windows/win32/api/imapi2/nf-imapi2-irawcdimagecreator-addtrack)

<a id="为什么会形成两组轮廓"></a>
## Por qué aparecen dos contornos

El `CddaInterleaver` existente compensa el retardo de los bytes objetivo. Intercambiar los dos bytes de cada muestra de 16 bits en la salida final introduce de forma cruzada en la cadena de codificación bytes que originalmente pertenecían a posiciones de retardo distintas. Por ello, el cambio no puede entenderse como una simple variación del valor de audio ni compensarse con un intercambio previo al muestreo de la imagen.

En un experimento de permutación de etiquetas enteras con la tabla de retardos existente, excluido el relleno inicial y final, el intercambio adicional de bytes de las muestras desplaza las posiciones pares de la asignación inversa `+73` bytes objetivo y las impares `-73` bytes objetivo. Esto explica el mecanismo del doble contorno; es una deducción del modelo de permutación con retardo del programa, no un modelo CIRC completo de una unidad óptica ni una medición de un disco físico.

La corrección conserva el muestreo y la tabla de retardos y elige el orden de bytes en el límite de salida del sector de 2352 bytes, una vez completada la compensación: big-endian para el RAW anterior; little-endian para WAV e IMAPI. La cabecera WAV permite que las herramientas externas reconozcan el formato y el orden de bytes, mientras que la interfaz por flujo sigue recibiendo sólo los datos conforme a su convención.

<a id="读回必须抵消延迟交织"></a>
## La relectura debe compensar el entrelazado con retardo

La compensación de retardo desplaza el byte lógico i al índice de archivo `i + offset(i % 24)`: la tabla de entrelazado se almacena como una línea de retardo de 28×D tramas (D=4), con 24 bytes por trama, de modo que el desplazamiento oscila entre 72 y 2592 (la suma del retardo de trama y la rotación de columnas dentro de ella). Distribuir los bytes sobre la espiral directamente en el orden del archivo equivale a rotar y cizallar todo el patrón: en una comparación de la misma imagen con 60000 sectores y 900 px, aproximadamente el 17.5% de los píxeles dibujados no coincide con el original, lo que se percibe como una «ligera distorsión».

Por ello, `cd-preview-track` aplica la asignación inversa de forma predeterminada (`--deinterleave`, `true` por defecto): el byte lógico i se obtiene del índice de archivo `i + CddaInterleaveTable.FileOffsetFor(i % 24)`, lo que requiere una ventana de anticipación de 2592 bytes. El retardo directo de esta tabla y `CddaInterleaver` comparten `CddaInterleaveTable`, por lo que ambas rutas no pueden divergir por separado.

El coste al final: cuando termina el archivo, la línea de retardo aún contiene las últimas tramas; esos bytes lógicos no se han escrito (1318–1332 bytes, menos de un sector), por lo que en la relectura sólo pueden dejarse vacíos. Se encuentran en la zona más exterior de la pista y su proporción respecto a la pista completa disminuye al aumentar el número de sectores.

`cd-generate` escribe ahora `<track>.json` junto a la pista (`format: cd-da-track-v1`, con r0/r1/número de sectores/velocidad lineal/ángulo inicial/radio exterior de la imagen/si está entrelazada). Al cambiar de pista, el espacio de vista previa lo lee y carga la geometría de calibración y el interruptor de entrelazado, lo que permite releer con el modelo correcto incluso las pistas generadas en otro equipo.

<a id="标定投影源图片与两套几何"></a>
## Proyección de calibración: imagen de origen y dos geometrías

`cd-preview-warp` ofrece una comparación rápida de la ruta de relectura y es el comando que utiliza el modo «Vista previa de calibración» del espacio de vista previa: su entrada es la imagen de origen, no la pista de audio, y sus parámetros incluyen dos geometrías. La geometría de generación determina de qué punto de la imagen se toma la muestra del byte i; la geometría medida determina dónde se dibuja ese byte sobre el disco (`--actual-r0`, `--actual-r1`, con valores predeterminados de 24.3 / 56.6 mm). Cuando ambas geometrías coinciden, la proyección es la imagen original; si difieren, el patrón se desplaza según el disco medido, precisamente el fenómeno que se pretende observar al calibrar los radios. Ambas geometrías comparten el número de sectores, la velocidad lineal, el ángulo inicial y el radio exterior de la imagen; los tres últimos están fijados en 1200 mm/s, 0° y 57.5 mm.

Como sólo lee la imagen de origen y no recorre toda la pista, el coste de actualizar tras cambiar radios o geometría depende del tamaño de la imagen y no del de la pista (unos 845 MB para un disco de 80 minutos). Resulta apropiado para probar parámetros en la vista previa de calibración y después confirmar el resultado final con la simulación de relectura.

<a id="验证与边界"></a>
## Validación y límites

- RAW y WAV: para un patrón asimétrico, tanto con entrelazado activado como desactivado, la sección de datos WAV coincide exactamente con la secuencia de bytes del RAW anterior tras intercambiar los dos bytes de cada muestra de 16 bits; la longitud no cambia.
- Salida por flujo: al generar audio little-endian en un flujo de avance secuencial que prohíbe Seek/Position, el resultado coincide byte a byte con la sección de datos WAV y no contiene cabecera.
- Vista previa WAV: analiza `fmt ` / `data` de RIFF, omite bloques desconocidos y el relleno de longitudes impares, limita el intervalo de lectura y normaliza el orden de bytes; rechaza datos que no sean PCM, de 44.1 kHz, de 16 bits o de dos canales. Con distintos pasos de muestreo coincide con la vista previa RAW correspondiente. La vista previa sigue siendo una asignación de bytes del archivo y no simula el CIRC/EFM físico completo, por lo que no sustituye la evaluación de un disco real.
- Coherencia de relectura: se generan pistas entrelazadas y sin entrelazar a partir de la misma imagen; la relectura de la pista entrelazada con `--deinterleave true` y la lectura por orden de archivo de la pista sin entrelazar coinciden píxel a píxel, salvo por los píxeles correspondientes a menos de un sector al final de la línea de retardo (con 900 px y una imagen de prueba asimétrica, las diferencias sólo aparecen dentro de la proyección final; una prueba de mutación que modifica en 24 bytes el retardo de trama detecta 245 discrepancias).
- Coherencia de la proyección de calibración: se genera primero una pista con la misma imagen y la geometría de generación `(24.5, 56.8)`; después se comparan píxel a píxel la proyección de `cd-preview-warp` (medida=generación, medida=`24.3/56.6`) y el resultado de releerla con `cd-preview-track` y la geometría correspondiente. Tras aplicar el umbral, coinciden por completo (900 px); al reducir los radios medidos, también disminuye el radio del centro de gravedad de la proyección, por lo que las dos geometrías no se intercambian.
- Experimento en memoria de Windows: se introducen 300 sectores de audio asimétricos en el `MsftRawCDImageCreator` existente y se recorren los sectores RAW resultantes completos de 2448 bytes; todos los bytes de audio se conservan tal cual. El generador no intercambia bytes por cuenta de quien lo llama. La prueba no crea un dispositivo de grabación ni escribe en un disco.
- El 2026-09-12, tras esta corrección, un usuario realizó una prueba con un disco físico y comunicó que ya no aparecía la imagen doble. Es un resultado de disco físico aportado por el usuario; no se registraron por separado los detalles de las pruebas individuales de las tres entradas: flujo, cdrecord e ImgBurn.

La documentación de `WriteMedia` de Microsoft no describe su implementación interna del intercambio de bytes; las conclusiones anteriores no equiparan el experimento en memoria con la observación de un intercambio durante la transferencia al dispositivo. [Documentación de WriteMedia de Microsoft](https://learn.microsoft.com/en-us/windows/win32/api/imapi2/nf-imapi2-idiscformat2rawcd-writemedia)
