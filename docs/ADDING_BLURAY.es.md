Traducción al español · [Original](ADDING_BLURAY.md)

<a id="蓝光模式单层-bd-r"></a>
# Modo Blu-ray (BD-R de una capa)

La implementación de Blu-ray se encuentra en `src/DiscImageStudio.BluRay`. Hace referencia al Core independiente del medio y a `src/DiscImageStudio.Iso`, y no depende de los codificadores de CD/DVD. Se basa en ISO/IEC 30190:2021 y actualmente abarca **BD-R de 120 mm, una capa y 25 GB**; no admite doble capa, BDXL, BD-RE ni creación de Blu-ray de vídeo.

<a id="在应用中的位置"></a>
## Ubicación en la aplicación

Blu-ray es el tercer tipo de disco del proyecto (CD / DVD / BD), no un sexto espacio de trabajo. Al abrir un proyecto BD:

- **Diseño**: el lienzo y la imagen compuesta se distribuyen según el radio exterior de generación de BD; en «Archivos del disco», en la columna izquierda, se puede elegir una carpeta y su posición en la zona interior o exterior, en sincronía con la página de exportación; el módulo de tono de la columna derecha admite 2 niveles, linealización sRGB, brillo y polaridad 3T/6T.
- **Vista previa**: la proyección del diseño representa un mapa de categorías 3T/6T según las geometrías de generación y medida, el tono y la polaridad; la relectura del resultado clasifica directamente los bytes de datos útiles AA/44 de la ISO, sin aplicar la polaridad. Muestra un mapa de categorías, no el brillo reflejado.
- **Exportación**: genera una ISO BD completa (sólo dibujo o archivos UDF 2.01); al seleccionar una carpeta, los archivos pueden situarse en la zona interior o exterior, con la interior como opción predeterminada. La posición se guarda con el proyecto y se registra en el archivo auxiliar `<iso>.json` del mismo directorio.
- **Grabación**: envía el mismo flujo ISO directamente a la escritura secuencial MMC, sin guardar una imagen completa; el panel de tareas admite progreso y cancelación.
- **Calibración**: el patrón BD cubre toda la zona de datos y guarda los sectores, los radios, la polaridad, el inicio y el final del dibujo y las marcas de tiempo ISO; el ajuste utiliza un paso de pista constante y vuelve a los parámetros BD.

Los preajustes BD se guardan junto a los de CD/DVD en `bluRayPresets`, dentro de `disc-presets.json` (schema 4). Se incluyen tres: «BD estándar de 25 GB y una capa», «Ritek BD-R 10x» y «Verbatim BD-R (64099)»; los preajustes del usuario se pueden guardar en la parte inferior del diálogo de configuración del proyecto.

<a id="容量与几何"></a>
## Capacidad y geometría

- La capacidad de una capa es de 12,219,392 sectores de usuario de 2048 bytes, es decir, 25,025,314,816 bytes; 32 sectores forman un clúster de 65,536 bytes.
- El número de sectores de la partición del proyecto debe ser un múltiplo positivo de 32 y no superar la capacidad de una capa; el radio exterior no puede superar 60 mm.
- La generación y la grabación comparten el número total de sectores; la grabación siempre declara la capacidad completa del disco, y los sectores sin utilizar, aparte de los archivos y metadatos, contienen datos útiles de ceros.
- Paso de pista nominal de 0.320 µm, longitud de bit de canal de 74.5 nm y ángulo inicial de 0°.

<a id="生成与-iso"></a>
## Generación e ISO

`BluRayIsoGenerator` planifica primero los directorios UDF 2.01 y los extents de los archivos, y después asigna un intervalo de dibujo alineado a 32 sectores. El modo de zona interior coloca la partición de archivos al principio; el modo de zona exterior la coloca antes de la zona reservada al final del disco. La información de reconocimiento inicial, los anclajes y la información de respaldo final permanecen en sus posiciones prescritas. La longitud de salida es fija: `TotalSectors × 2048`.

- Sin carpeta seleccionada: toda la zona de datos se dedica al dibujo, sin sistema de archivos. El programa puede volver a leer esta `.iso`, pero las herramientas de sistemas de archivos habituales no pueden explorarla.
- Con una carpeta seleccionada: escribe UDF 2.01 y admite archivos individuales de más de 4 GiB; `--file-position inner|outer` selecciona la posición de los archivos, con `inner` como valor predeterminado; `outer` requiere `--data-dir`. Los archivos, el relleno, el patrón y los metadatos finales del disco se escriben en un mismo flujo de avance secuencial.
- La ruta de salida debe estar fuera de la carpeta de origen y no puede sobrescribir la imagen de origen. La cancelación o un fallo conservan la ISO existente.
- Tras una generación correcta se escribe `<iso>.json` (`format: bd-r-image-v1`): sectores totales, radios interior y exterior, inicio y final del dibujo y sus radios, polaridad 3T/6T, `ToneProfile`, etiqueta del volumen, marcas de tiempo UDF, imagen de origen y fecha de generación. Al seleccionar esa ISO en la vista previa, estos parámetros se restauran automáticamente; una ISO externa sin archivo auxiliar se puede releer con la geometría del proyecto actual.

Los archivos utilizan bytes de usuario normales y no pueden someterse a la compensación de aleatorización del patrón; el patrón sigue utilizando las direcciones de sector originales del disco completo y su geometría global, sin estirar la zona de dibujo restante para ocupar todo el disco. La partición UDF sólo abarca la zona del sistema de archivos; el patrón no se marca como espacio disponible del sistema de archivos. La zona realmente dibujable depende tanto del espacio ocupado por los archivos como de la zona de respaldo final. La proyección del diseño en la GUI y la relectura del resultado con archivo auxiliar muestran la zona de archivos (incluido el sistema de archivos) en amarillo y con una leyenda; las ISO externas sin información de distribución no presuponen el intervalo de archivos.

`BluRayIsoGenerator.Prepare` examina primero la carpeta y planifica los directorios UDF 2.01 y los extents de los archivos. La distribución interior redondea el final de los archivos hacia arriba hasta un múltiplo de 32 sectores; la distribución exterior redondea el inicio de la partición de archivos hacia abajo hasta un múltiplo de 32 sectores. Se reservan 288 sectores al final del disco para los descriptores de volumen de respaldo y los anclajes. Esta API de sistema de archivos conserva un volumen vacío incluso si no se especifica una carpeta; debe quedar al menos un clúster de dibujo completo, y la capacidad mínima de prueba es de 640 sectores.

La salida a archivo y por flujo comparten la ruta de escritura secuencial. Si los archivos o el contenido de los directorios de origen cambian después de la planificación, se rechaza continuar con la distribución obsoleta. Las longitudes de archivo utilizan campos de 64 bits; los archivos grandes se dividen en varios extents de asignación de no más de `0x3ffff800` bytes y se leen secuencialmente con un búfer de 128 KiB. El tamaño de un archivo individual está limitado por la capacidad restante del disco y deja de tener el límite de 4 GiB. La ruta ISO9660/Joliet de DVD permanece sin cambios.

La implementación UDF se basa en [ECMA-167](https://ecma-international.org/publications-and-standards/standards/ecma-167/) y [ECMA TR/112-4 (UDF 2.01)](https://ecma-international.org/wp-content/uploads/ECMA_TR-112-4_1st_edition_december_2023.pdf), y escribe volúmenes cerrados de sólo lectura. Está destinada a discos de datos y no ofrece creación de Blu-ray de vídeo.

<a id="影调与极性"></a>
## Tono y polaridad

El tratamiento tonal de BD se realiza una sola vez a la resolución de la imagen de origen: linealización sRGB → desplazamiento de brillo → tramado por byte → binarización con umbral 128. La generación, la grabación y la vista previa comparten esta trama binaria, sin calcular un hash para cada uno de los aproximadamente 25 mil millones de bytes de datos útiles.

`BluRayRunLengthMapping` sólo realiza el último paso: las zonas oscuras escriben `AA`, que favorece el número de intervalos 3T, y las claras escriben `44`, que favorece el número de intervalos 6T; `InvertMapping` intercambia ambos extremos. Es una convención de asignación de imagen: no promete 3T/6T puros ni representa una polaridad óptica medida. La dirección real de claro y oscuro debe observarse con un disco de calibración; el campo de polaridad se guarda por separado de `ToneProfile`.

<a id="3t--6t-数量映射与扩展"></a>
## Asignación por número de intervalos 3T / 6T y extensiones

La cadena de imagen utiliza `BluRayImageLoader` para decodificar y componer la transparencia sobre fondo blanco, y después toma muestras mediante `BluRayImagePayloadMapping`. La imagen conserva su proporción; el lado más largo corresponde al diámetro exterior del disco y las zonas vacías son blancas. La posición de muestreo tiene en cuenta el espacio del EDC de cada trama, el entrelazado LDC, la inserción BIS, los bits DC, la sincronización y el run-in; el centro nominal de cada byte se calcula a partir de la tasa de modulación 2:3 y no coincide necesariamente con los límites exactos de las palabras de código 17PP de longitud variable.

El `BluRayRunLengthMapping` integrado aplica primero el umbral 128 y después devuelve `AA` o `44` en el dominio aleatorizado. La elección se basa en el número de intervalos 3T / 6T entre unos de modulación consecutivos en un clúster completo con los mismos 958,272 bits de canal; se cuentan tanto marcas como espacios y no el ciclo de trabajo de un determinado nivel. En el barrido de los 256 bytes repetidos con PSN de referencia `0x100000`, `AA` tiene 265,136 intervalos 3T y `44` tiene 116,575 intervalos 6T, los máximos respectivos del recuento objetivo. La comprobación en cuatro direcciones mantiene una preferencia fuerte, aunque no garantiza el primer puesto en cada dirección. Los detalles están en el [análisis de bytes](BLURAY_BYTE_DENSITY.es.md#按-3t--6t-数量接入绘图).

Implementar `IBluRayLuminanceMapping.Map` permite sustituir la estrategia y devolver el byte elegido en el dominio aleatorizado para la luminancia de la imagen de origen y la posición nominal del byte. Para experimentos sin imagen también se puede implementar directamente `IBluRayPayloadMapping.FillScrambledPayload`, rellenar las 2048 posiciones de bytes de usuario de un sector y entregarlas a `BluRayStreamingGenerator`; la función recibe el sector nominal, el PSN y los parámetros del disco.

`BluRayDiscModule` recibe mediante su constructor el delegado de decodificación de imágenes y mantiene su independencia de WPF; la CLI de App utiliza `BluRayImageLoader.Load`. Cuando se genera una imagen directamente con la API del módulo, también debe proporcionarse este delegado. `BluRayConstantByteMapping` sigue reservado a experimentos numéricos explícitos. Tanto las imágenes como la calibración utilizan `BluRayIsoGenerator` para generar archivos o suministrar un flujo al grabador.

La selección de candidatos se explica en el [análisis de densidad de bytes y períodos de bits](BLURAY_BYTE_DENSITY.es.md). `tools/BluRayByteProbe` puede reproducir las estadísticas de clúster completo de los 256 bytes y los períodos más precisos; las cadenas periódicas de bits pertenecen a otras estrategias experimentales, mientras que la asignación actual por recuento utiliza bytes repetidos. Los resultados del análisis aún no fijan la polaridad de blanco y negro del disco físico.

<a id="命令行"></a>
## Línea de comandos

```powershell
# Ver los parámetros del disco de una capa o exportar JSON
dotnet run --project src/DiscImageStudio.App -- bd-info --output bd-parameters.json

# ISO sólo de dibujo (1024 sectores sirven para una comprobación funcional rápida, no para un disco completo real)
dotnet run --project src/DiscImageStudio.App -- bd-generate --input image.png --output bd-test.iso --total-sectors 1024 --invert true

# ISO de capacidad completa con archivos UDF en la zona interior
dotnet run --project src/DiscImageStudio.App -- bd-generate --input image.png --output bd-image.iso --data-dir C:\Files --volume-label BD_IMAGE

# Tono: tramado, brillo y linealización sRGB
dotnet run --project src/DiscImageStudio.App -- bd-generate --input image.png --output bd-tone.iso --tone-dither 0.75 --tone-gamma true --tone-brightness -4

# Sonda de datos útiles sin procesar y diagnóstico de un clúster ECC
dotnet run --project src/DiscImageStudio.App -- bd-probe --byte 0xA6 --sectors 32 --output bd-probe.bin
dotnet run --project src/DiscImageStudio.App -- bd-encode --input bd-probe.bin --psn 0x100000 --output bd-ecc.bin
```

`--preset` de `bd-generate` permite elegir `bd-r-sl-25gb`, `bd-r-ritek-10x` o `bd-r-verbatim-64099`; `--total-sectors`, `--r0`, `--r1` y `--start-angle` pueden sobrescribir sus valores correspondientes. `--invert` acepta `true`/`false`. `bd-generate` ya no acepta los antiguos parámetros de intervalo `--start-sector` / `--sectors`; los intervalos sólo se mantienen en `bd-probe`.

`--byte` de `bd-probe` admite 0..255 o `0x00`..`0xFF`, y el intervalo se alinea a 32. La entrada de `bd-encode` debe tener exactamente 65,536 bytes y su salida es un clúster ECC de 76,880 bytes, sin 17PP, control DC ni sistema de archivos; no puede utilizarse como imagen grabable.

<a id="编码链"></a>
## Cadena de codificación

| Parte | ISO/IEC 30190:2021 |
| --- | --- |
| Carga útil de 2048 bytes + EDC de 4 bytes | §13.2–13.3 |
| LFSR de 16 bits, semilla derivada de los bits PSN 19..5; reinicio en cada trama de datos | §13.4 |
| LDC RS(248,216), 304 palabras de código y entrelazado | §13.5–13.8 |
| Direcciones, RS(9,5), BIS RS(62,30) y multiplexación | §13.9–13.14 |
| 17PP, control DC y tramas de sincronización | §13.15–13.17 |
| Espaciado de unidades de grabación | §14 |
| Paso de pista nominal de 0.320 µm | §15.4.3 |
| PSN de una capa 0x00100000..0x00CA73FF | §17 |
| Zona de datos de una capa: 381,856 clústeres | §19 |

Las pruebas de patrones escriben **datos útiles de usuario con la aleatorización compensada**. La unidad real calcula el EDC y después aleatoriza la trama de datos completa de 2052 bytes; la sonda de bytes concretos no puede sobrescribir arbitrariamente los datos EDC, de corrección de errores, direcciones, sincronización, etc.

`BluRayRecordingFrameEncoder` acepta 28 bits de control DC proporcionados explícitamente por quien lo llama. `BluRayRecordingUnitEncoder` ofrece además una simulación de diagnóstico con run-in, 496 tramas, run-out y Guard3 opcional, representando cada bit de modulación / nivel NRZI con un byte. La estrategia automática se denomina explícitamente `BluRayGreedyDcControlEncoder`: toma decisiones voraces con anticipación, suponiendo que los bits posteriores aún no decididos son 0. Cuando se fijan esos bits de control posteriores, puede cambiar la elección local que antes era óptima; el valor devuelto `SuboptimalDcControlDecisions` informa de estas situaciones. Esta estrategia es experimental, no garantiza la optimalidad DC final exigida por la norma ni la coincidencia con la secuencia real de bits del grabador. `bd-encode` de la CLI sólo exporta la etapa ECC determinista; no exporta esta simulación de diagnóstico como imagen grabable.

`BluRayTrackGeometry` utiliza una espiral independiente de paso constante y calcula el paso efectivo a partir de la longitud total de pista correspondiente al número total manual de sectores y los radios interior y exterior; el último sector llega al radio exterior configurado. Vista desde la cara de datos, la trayectoria de muestreo en coordenadas fijas del disco avanza hacia fuera en sentido horario (§9.8, §15.3); la transformación a pantalla invierte el eje Y una sola vez. Cada unidad de grabación ocupa `2760 + 496 × 1932 + 1104 = 962136` bits de canal, con una longitud de bit de 74.5 nm. El intervalo local de `bd-probe` utiliza la geometría de la capacidad total completa. El Guard3 al final de la secuencia no se incluye en la extensión repetida de cada unidad de grabación.

Los 24–58 mm predeterminados son una geometría nominal, no parámetros medidos en un disco concreto. La grabación por flujo integrada ajusta el modelo `sector → PSN` manteniendo el BD-R sin formatear y sin asignar una zona de reserva para la gestión de defectos. Los radios reales, la posición y el contraste óptico siguen requiriendo calibración en un disco físico; al grabar la ISO con software externo también se debe utilizar un método sin zona de reserva.

<a id="无备用区流式刻录"></a>
## Grabación por flujo sin zona de reserva

La capacidad disponible que `IDiscFormat2Data` informa para un BD-R sin formatear puede corresponder al esquema de reserva predeterminado: 11,826,176 sectores, 768 MiB menos que los 12,219,392 sectores originales. La ruta Blu-ray envía `WRITE(10)` secuencialmente desde LBA 0 mediante `IDiscRecorder2Ex.SendCommandSendDataToDevice`, en bloques de 32 sectores (64 KiB), con un tiempo de espera de 200 segundos por comando para permitir la inicialización de la primera escritura. El búfer respeta la alineación de direcciones y el límite máximo de transferencia del adaptador. Esta ruta no llama a `Write` de la interfaz de grabación de datos, a `FORMAT UNIT` ni a operaciones que asignen una zona de reserva.

La interfaz pública `IWriteEngine2` no tiene una propiedad configurable de tiempo de espera de comandos; por ello, el tiempo de espera de las consultas o del vaciado de caché no puede prolongar la espera interna de `WriteSection`. La ruta actual de comandos explícitos informa del LBA, el número de sectores, el tiempo real transcurrido, el HRESULT y el Sense del comando que falla. Un tiempo de espera agotado implica que el resultado en el dispositivo puede ser incierto; el programa no reenvía automáticamente ese intervalo.

El registro de errores también conserva los tiempos de las 8 últimas escrituras correctas y contabiliza por separado la espera de lectura del flujo de origen, la espera de los comandos del dispositivo y el volumen acumulado de datos confirmados. Si no hay un Sense válido, muestra expresamente «No se devolvieron datos Sense válidos del dispositivo»; una respuesta de ceros no puede interpretarse como ausencia de errores en el disco. `0x8007048F` significa que Windows informa de que el dispositivo no está conectado. Si un comando con un tiempo de espera declarado de 200 segundos falla en unos milisegundos y después se vuelve a enumerar el dispositivo, es necesario investigar la conexión, el firmware y los eventos USB; no debe atribuirse únicamente a un tiempo de espera insuficiente. Todas las consultas y grabaciones del dispositivo de esta instancia se ejecutan en cola para evitar que una consulta todavía activa se solape con la escritura del disco.

Después de bloquear el disco en exclusiva, el programa utiliza MMC para leer el perfil actual, la información del disco, la capacidad de formato y la información de pista. Debe ser un BD-R SRM de una capa, en blanco y sin formatear, cuya primera pista comience en 0 y cuya capacidad original de escritura esté íntegra; se rechazan las distribuciones desconocidas, utilizadas o formateadas, sin reducir automáticamente el número total de sectores. El total de sectores configurado en la interfaz se convierte a bytes a razón de 2048 bytes por sector para formar la misma longitud de solicitud, que incluye el prefijo UDF, la zona de dibujo y los metadatos finales; una capacidad manual menor sigue escribiendo exactamente ese número de sectores.

Al terminar la escritura se vacía la caché, se cierra la pista y se finaliza el disco con la función 6 de finalización de BD-R. La cancelación surte efecto cuando termina el comando actual del dispositivo; si se cancela o falla la escritura, sólo se intenta vaciar la caché y liberar el dispositivo, sin finalizar una imagen incompleta como si fuera un resultado correcto. Cada operación de desbloqueo sólo se marca como completada tras finalizar correctamente; las que fallen se intentan una vez más y los errores se conservan en los diagnósticos; las operaciones emparejadas con contador que ya hayan tenido éxito no se repiten. Una vez iniciada la finalización, se espera a que termine el comando. El modo sin zona de reserva carece de capacidad de repuesto para reasignar bloques defectuosos; tras una interrupción, normalmente no se puede continuar escribiendo en el mismo disco.

La velocidad BD en tiempo real se calcula a partir de los comandos de escritura cuya recepción ha confirmado el grabador, por lo que incluye el efecto de la caché del dispositivo y no equivale a la velocidad instantánea del láser sobre el disco; el éxito final sigue dependiendo de que terminen el vaciado de caché y la finalización. La tasa nominal de datos de usuario a 4× es de 18 MB/s (1× = 4.5 MB/s). La interfaz utiliza MB/s decimales; no se debe sustituir el rendimiento real por el multiplicador solicitado. El modo automático sigue seleccionando la configuración más baja que informe el disco actual.

Justo antes de escribir se registra la velocidad configurada que devuelve el dispositivo; el valor configurado y el rendimiento en tiempo real se conservan por separado. Durante la escritura se registra una ventana de diagnóstico cada 10 segundos y después del último bloque; distingue el tiempo de lectura del flujo de origen (incluidas la espera y la copia) del tiempo de los comandos WRITE (incluidas la transferencia y la espera del dispositivo), y registra la tasa media de recepción confirmada. Estos registros poco frecuentes no añaden consultas al dispositivo ni alteran el progreso o la vigencia de la velocidad de refresco; el tiempo de lectura del flujo de origen no puede interpretarse como tiempo de cálculo puro del hilo productor, y el tiempo de los comandos tampoco permite separar por sí solo la transferencia USB de la escritura física.

El suministro de la imagen por flujo utiliza lotes acotados de 16 clústeres (1 MiB) con un máximo de 8 hilos de trabajo; una vez terminado el cálculo, escribe en el orden original. La asignación integrada por umbral 3T/6T y el XOR de aleatorización utilizan SIMD entero, mientras que el cálculo de posiciones conserva su precisión original; los parámetros del disco ya validados dejan de comprobarse repetidamente para cada byte. Las imágenes de trama inmutables activan el paralelismo automáticamente, y los patrones de calibración integrados declaran explícitamente que su muestreo es seguro entre hilos; las funciones de muestreo personalizadas se mantienen secuenciales de forma predeterminada. El almacenamiento previo en búfer sólo puede absorber diferencias de velocidad temporalmente: la grabación sostenida sigue exigiendo que el suministro del flujo supere la velocidad seleccionada del dispositivo.

Las pruebas del protocolo y del flujo de trabajo sólo utilizan dispositivos simulados y un flujo UDF de 1024 sectores (2 MiB); para los 12,219,392 sectores completos sólo se validan los metadatos de capacidad y geometría, sin generar una ISO de disco completo. Los resultados de grabación física siguen requiriendo validación en un disco real.

Bases de la implementación: [interfaz de Microsoft para envío de datos y tiempos de espera de comandos](https://learn.microsoft.com/en-us/windows/win32/api/imapi2/nf-imapi2-idiscrecorder2ex-sendcommandsenddatatodevice), [implementación MMC de libburn](https://dev.lovelyhq.com/libburnia/libburn/src/branch/master/libburn/mmc.c), [tiempo de espera de escritura de libburn](https://dev.lovelyhq.com/libburnia/libburn/src/branch/master/libburn/spc.h).

<a id="验证范围"></a>
## Alcance de la validación

Las comprobaciones automáticas abarcan 17PP/EDC/Reed-Solomon, reproducción de RUB completo, asignación de imágenes, generación paralela, ISO/UDF, archivos de más de 4 GiB, comandos de grabación secuencial, intervalo de dibujo, archivos auxiliares y cancelación. Las pruebas UDF guardadas en disco sólo utilizan 1024 sectores, con un máximo de 2 MiB por ISO; las pruebas de archivos grandes habilitan y verifican primero el atributo de archivo disperso y después crean un archivo de origen lógico de más de 4 GiB (con una asignación real inferior a 1 MiB), que se envía secuencialmente a un flujo que valida y descarta los datos, sin escribir una ISO grande. La capacidad real del disco sólo se valida matemáticamente. Las pruebas no acceden a dispositivos de grabación reales ni demuestran el brillo visible o la polaridad de un disco físico.
