Traducción al español · [Original](DVD_ENGINE.md)

<a id="dvd-nrzi-图像约束求解器"></a>
# Solucionador de restricciones de imagen NRZI para DVD

Este proyecto trata 16 sectores de datos DVD consecutivos como un bloque ECC y busca datos de usuario legibles a partir de una imagen bidimensional o de restricciones NRZI pit/land especificadas.

Convenciones fijas:

- Zona de datos de un disco de una capa, con Sector Information igual a `0x00`;
- RSV de la trama de datos DVD-R compuesto por seis `0x00`;
- Sólo se introduce el LBA inicial del bloque ECC; los sectores posteriores utilizan automáticamente `LBA + 1 ... LBA + 15`;
- `PSN = PSN offset + LBA`, con offset predeterminado `0x30000`;
- Si los valores absolutos de DSV son iguales, se elige Stream 1;
- `pit = 0 = negro`, `land = 1 = blanco`;
- Cada salida contiene 32768 bytes, es decir, 16 cargas de datos útiles consecutivas de 2048 bytes.

La cadena de codificación incluye ID, IED, RSV a cero, EDC, aleatorización de datos, RS-PC PO/PI, entrelazado de filas PO, tablas principal y alternativa de EFMPlus, control SYNC/DSV y conversión NRZI.

La [herramienta experimental independiente de escala de grises](../tools/DvdGrayProbe/README.es.md) utiliza `FastDispersionImageWriter.WriteScrambledCodewordPatternStream` para especificar directamente Q aleatorizado en el mismo sistema de coordenadas de centros de palabras de código de datos útiles, y después escribe `P = Q XOR mask`. Esta interfaz no pasa por una cuantización fija de grises y sirve para probar palabras de código candidatas como `64/E4` y conjuntos de imágenes con tramado discreto; la paleta de la generación normal de imágenes no cambia. La generación rápida no reproduce el ECC/EFM del disco completo ni predice el brillo óptico. Las pruebas de regresión reproducen ECC/EFM completo en muestras pequeñas de palabras de código mixtas y verifican además los datos útiles reales, la geometría, la coincidencia en paralelo y la cancelación.

<a id="构建和自检"></a>
## Compilación y autoprueba

La compilación requiere Windows y .NET 10 SDK; ejecutar directamente una salida no autocontenida requiere el runtime de escritorio Windows de .NET 10:

```powershell
dotnet build DvdImageSolver.slnx --configuration Release
dotnet run --project src/DvdImageSolver --configuration Release -- selftest
```

La autoprueba abarca:

- Muestreo de la tabla de modulación ECMA-267;
- ID, IED, RSV y EDC;
- Ida y vuelta del XOR de aleatorización;
- Resto cero de las 208 filas PI y las 172 columnas PO;
- Cantidad y determinismo de los 619008 niveles físicos NRZI e intervalos de datos RLL(2,10);
- Gris precalculado, nivel final e incremento DSV de las palabras de código de 16 bits con niveles iniciales pit y land;
- Coincidencia de gris por reproducción de palabras de código, selección SYNC y State/DSV/nivel finales de tramas SYNC aleatorias con el codificador bit a bit;
- Coincidencia exacta del estado de prefijo EFMPlus de dos flujos con el codificador bit a bit cuando no es necesario reproducir con SYNC alternativo;
- Coincidencia del límite final de state-control con la codificación directa DVD completa, sin materializar un array de niveles de 619008 bits durante la resolución;
- Coincidencia de la asignación `0x92/0xA5` del dominio aleatorizado de dispersion, los datos útiles ISO con aleatorización compensada y el límite final con la codificación completa;
- Descriptores ISO9660 mínimos, Path Table, directorio raíz, LBA de datos útiles y resultado de sustitución en plantilla.
- Vista previa PNG de calibración de radios y salida de imagen sin procesar de varios bloques ECC consecutivos.
- Protección de aislamiento entre archivos ISO9660/Joliet interiores, subdirectorios Unicode y datos de dibujo exteriores.

<a id="目标文件"></a>
## Archivo de objetivos

El archivo de objetivos utiliza caracteres para representar niveles físicos NRZI:

```text
0 o P     pit, negro
1 o L     land, blanco
?         Sin restricción
```

Se ignoran los espacios en blanco y los guiones bajos. El archivo debe contener una de estas dos opciones:

- 38688 símbolos: restringen un sector físico; `--target-sector 0..15` especifica su posición dentro del bloque ECC;
- 619008 símbolos: restringen todo el bloque ECC.

SYNC, RLL, EDC y ECC limitan los patrones realizables, por lo que una imagen arbitraria bit a bit normalmente no tiene una solución de error cero. El modo `full` utiliza como objetivo los bits NRZI que no coinciden; el modo de imagen `state-control` binariza el objetivo a negro o blanco, calcula primero si la mayoría pit/land tiene la dirección incorrecta y después calcula la distancia de gris entre el número real de land y el negro puro 0 o el blanco puro 16. El objetivo de búsqueda sólo utiliza palabras de código de datos útiles controlables, mientras que la visibilidad final contabiliza todas las palabras de código del canal.

El modo `dispersion` no controla la mayoría absoluta pit/land, sino que forma texturas de dos niveles blanco y negro mediante la densidad de transiciones EFMPlus. Utiliza los bytes posteriores a la aleatorización `Q=0xA5/0x92`, cuyas representaciones en todos los estados tienen respectivamente 5/2 transiciones fijas. Las luminancias de imagen 0–127 se asignan a A5 y 128–255 a 92; las luminancias de vista previa son 0/255. Las imágenes nuevas ya no utilizan 66 ni B9 como niveles de gris independientes. Invertir globalmente NRZI no modifica las posiciones de transición.

Si una imagen en gris ya tramada y completamente opaca sólo contiene `0/255` (también se admite negro o blanco uniforme), la generación y la vista previa de calibración utilizan muestreo del píxel más cercano, conservando los niveles originales de los píxeles; las imágenes normales de grises continuos, color o con transparencia siguen utilizando muestreo bilineal. La duplicación circular CD/DVD del programa principal aplica primero escalado y rotación de alta calidad a las entradas discretas en blanco y negro y después vuelve a aplicar tramado de dos niveles, para evitar perder la gradación de la mezcla al cuantizar directamente los grises continuos del escalado. Los objetivos binarios de `full` y `state-control` mantienen sus reglas de muestreo anteriores.

<a id="图片映射"></a>
## Asignación de imagen

Se puede introducir directamente un PNG, JPEG, BMP, TIFF o GIF:

```powershell
dotnet run --project src/DvdImageSolver --configuration Release -- solve `
  --image picture.png `
  --total-sectors 2295104 `
  --iso-output solved-disc.iso `
  --output payload.bin `
  --lba 0 `
  --psn-offset 0x30000 `
  --inner-radius-mm 24 `
  --outer-radius-mm 58 `
  --channel-bit-nm 133.3 `
  --start-angle-deg 0 `
  --spiral-direction ccw `
  --image-threshold 128 `
  --alpha-threshold 1 `
  --iterations 10000 `
  --constraint-output mapped-target.txt `
  --channel-output channel.bin
```

`--total-sectors` es el número total de bloques de datos de 2048 bytes que informa el disco; también acepta el alias `--total-blocks`. Junto con los 38688 bits de canal por sector y `--channel-bit-nm`, determina la longitud total de pista. Después, el programa resuelve la escala del paso de pista a partir de los radios interior y exterior y de la curva de paso.

<a id="随半径变化的三次轨距"></a>
### Paso de pista cúbico variable con el radio

La generación DVD, la salida por flujo, la vista previa de imagen, la relectura ISO y la calibración asistida comparten `DvdTrackGeometry`. Sea el radio normalizado `x=(r-ri)/(ro-ri)`; el paso local es:

```text
p(x) = a0 + a1*x + a2*x² + a3*x³                 [mm]
     = p0 * (1 + c1*x + c2*x² + c3*x³)
dr/dθ = p(x)/(2π)
```

El polinomio cúbico completo tiene cuatro coeficientes. Los parámetros de entrada `--pitch-linear`, `--pitch-quadratic` y `--pitch-cubic` son los tres coeficientes relativos adimensionales `c1,c2,c3`; el término constante `a0=p0` se resuelve automáticamente a partir de la longitud total fija de pista, y los otros tres términos son `ai=p0*ci`. Por tanto, los cuatro términos cumplen la restricción de longitud impuesta por la capacidad y los radios interior y exterior, en lugar de ser todos independientes. La interfaz muestra los cuatro términos completos en μm; su variable es `x`, no el radio absoluto en mm.

La longitud de pista utiliza `L=∫sqrt(1+(2πr/p(r))²)dr` y el ángulo utiliza `θ(r)=∫2π/p(r)dr`; con paso variable, el ángulo ya no puede calcularse con la fórmula de paso constante `(r-ri)/b`. El programa comprueba que el paso sea estrictamente positivo en toda la corona de generación, incluidos los extremos internos del polinomio. Si los tres coeficientes son cero, se utiliza el modelo anterior de paso constante; los parámetros y preajustes antiguos que omiten estos campos utilizan cero por defecto.

Por ejemplo, se puede añadir `--pitch-linear 0.02 --pitch-quadratic -0.03 --pitch-cubic 0.015` al comando `solve` anterior. Es sólo un ejemplo de formato de parámetros, no una medición de ningún disco. `calibrate` utiliza las mismas opciones para especificar la curva de generación y `--actual-pitch-linear`, `--actual-pitch-quadratic` y `--actual-pitch-cubic` para la curva real; también acepta los alias `--generated-pitch-*`. Los `--pitch-*` de `simulate` se refieren a la curva del disco real.

El JSON de imágenes y vistas previas registra los coeficientes relativos y el paso base; las sesiones de calibración guardan los coeficientes relativos, los radios interior y exterior, el número de sectores y la longitud de bit, y permiten reconstruir los cuatro términos completos. El giro global de la fotografía sigue siendo una variable de ajuste independiente.

Convenciones de coordenadas y muestreo:

- El centro de la imagen corresponde al centro del disco; el lado corto corresponde al diámetro exterior y se conserva la zona adicional del lado largo;
- La pista comienza a 0° en el radio interior, con 0° hacia la derecha de la imagen; el sentido matemático predeterminado es antihorario y puede cambiarse a horario con `--spiral-direction cw`;
- El negro de la imagen se asigna a pit y el blanco a land; un gris inferior a `--image-threshold` se considera negro;
- Las zonas transparentes con alfa inferior a `--alpha-threshold` no imponen restricciones;
- `--lba` cuenta desde cero a partir del primer bloque de datos de la pista; el programa sólo asigna los 16 sectores consecutivos que comienzan en ese LBA;
- `--constraint-output` es opcional y exporta la restricción lineal de 619008 símbolos realmente generada para inspeccionarla.

<a id="求解"></a>
## Resolución

```powershell
dotnet run --project src/DvdImageSolver --configuration Release -- solve `
  --target target.txt `
  --output payload.bin `
  --lba 0 `
  --psn-offset 0x30000 `
  --initial-state 1 `
  --initial-dsv 0 `
  --initial-level land `
  --target-sector 0 `
  --iterations 10000 `
  --mutation-bytes 4 `
  --seed 1 `
  --channel-output channel.bin
```

Parámetros principales:

- `--lba`: LBA inicial del bloque ECC, no 16 LBA;
- `--psn-offset`: decimal o hexadecimal con `0x`, predeterminado `0x30000`;
- `--initial-state`: estado EFMPlus de 1 a 4;
- `--initial-dsv`: DSV acumulado antes de entrar en el bloque ECC actual;
- `--initial-level`: estado físico `pit` o `land` antes de entrar en el bloque actual;
- `--seed-payload`: solución inicial opcional de 32768 bytes; si no se proporciona, se genera una solución inicial aleatoria determinista;
- `--initial zero`: utiliza datos útiles iniciales a cero;
- `--channel-output`: produce además los niveles NRZI finales, empaquetando cada 8 niveles con MSB primero.

Se debe elegir entre `--target` y `--image`. El modo de imagen también requiere `--total-sectors` o `--total-blocks`.

<a id="state-control码字灰度求解模式"></a>
### Modo de resolución por gris de palabras de código state-control

El algoritmo anterior, que no restringe las posiciones de datos útiles, se conserva como `--algorithm full`, también con el alias `exact`. Se eliminó el antiguo algoritmo de conjunto de bytes de control espacial; el modo nuevo se activa con estos parámetros:

```powershell
dotnet run --project src/DvdImageSolver --configuration Release -- solve `
  --image picture.png `
  --total-sectors 2297888 `
  --output payload.bin `
  --algorithm state-control `
  --bridge-bytes 2 `
  --beam-width 8 `
  --retry-beam-width 32 `
  --retry-frame-fraction 0.25 `
  --batch-frames 8 `
  --batch-candidates 4 `
  --search-lanes 1 `
  --iterations 3
```

state-control trata cada trama Sync de 91 bytes como un segmento de resolución. EDC, PI, PO, los campos fijos y SYNC siguen generándose exactamente y participan en la propagación de los límites de State, DSV y pit/land; no forman parte del objetivo de búsqueda de bytes libres, pero sí de la puntuación final de visibilidad del disco completo. El programa calcula el par óptimo de bytes negro/blanco según `estado EFMPlus × nivel de entrada × DSV acumulado actual`. En cada posición objetivo y para cada Stream activo sólo prueba el byte negro o blanco correspondiente a su estado exacto actual; en cada prefijo conserva únicamente las `--beam-width` rutas mejor puntuadas. Cada segmento libre con objetivos de imagen también utiliza los primeros y últimos `--bridge-bytes` bytes de datos útiles como posiciones de control; éstas sólo eligen dentro del par de bytes del DSV actual, sin enumerar ya los 256 valores.

Al iniciarse, el programa precalcula para las 65536 palabras de código de 16 bits, con nivel inicial pit y land:

- El número de land entre las 16 posiciones NRZI, es decir, 17 niveles de gris de 0 a 16;
- El pit/land al terminar la palabra de código;
- El incremento DSV exacto;
- El número de ceros iniciales y finales, para comprobar los límites RLL.

Para cada representación EFMPlus posible, el par de bytes calcula tanto el error de gris como `|DSV|` después de escribir esa palabra de código, con el coste conjunto `peor error de gris × 16 + peor |DSV| de salida`. Por tanto, un nivel de gris sigue siendo más importante que 15 unidades DSV, pero el signo y la magnitud del DSV actual pueden cambiar la elección entre bytes de gris parecido. Los pares de bytes para el intervalo habitual DSV -256..256 se precalculan; si el valor recibido o alcanzado durante la ejecución supera ese intervalo, se calculan al momento con el DSV exacto, sin truncarlo. Estos bytes son bytes de trama de grabación después de la aleatorización y antes de EFMPlus; los datos útiles originales escritos en la ISO pasan por la asignación inversa del XOR de aleatorización, por lo que sus valores no se limitan necesariamente a esos pocos.

Los candidatos internos se puntúan por palabra de código EFMPlus, sin desplegar los 16 niveles de canal. Cada ruta del haz conserva a la vez Stream 1 y Stream 2, sin descartar prematuramente una de ellas por error. Los códigos SYNC principal y secundario, la elección de Stream 1 en los empates, el DSV acumulado y los límites finales se reproducen exactamente palabra a palabra con las reglas enteras del codificador original; la secuencia superviviente de cada trama Sync también se recalcula con el codificador completo de 91 bytes a nivel de palabra para tratar el SYNC alternativo cuando DSV sale de rango. El resultado final sólo verifica mayoría, gris y límites a nivel de palabra; sólo despliega los datos bit a bit si se solicita expresamente `--channel-output`.

La búsqueda state-control utiliza un objetivo de orden lexicográfico estricto: primero minimiza `controlled-majority-error` de los datos útiles libres; sólo si los recuentos de mayoría incorrecta son iguales compara `controlled-gray-error`, y sólo si éstos también empatan compara límites y DSV. El `majority-error`/`score` principal de la consola y del JSON representa el error visible final de todas las palabras de código del canal: cada trama Sync contiene 2 palabras SYNC y 91 palabras de datos, por lo que el denominador de un bloque ECC con imagen opaca es fijo: `416 × 93 = 38688`. El `gray-error` principal también abarca esas 38688 palabras. Las puntuaciones de búsqueda de bytes libres se guardan por separado en `controlledScore` y `controlledGrayScore`; en todos los errores, cuanto menor, mejor.

Cada pasada exterior calcula primero con exactitud el error de mayoría/gris controlado de cada trama Sync del bloque ECC completo actual y selecciona la fracción de peores tramas `--retry-frame-fraction` (0.25 por defecto). Primero utiliza esas tramas como un candidato de arranque en caliente que abarca el bloque para alejarse rápidamente de la solución inicial aleatoria; por buena que sea la puntuación EFM local, sólo puede aceptarse después de actualizar EDC, PO y PI y completar una reproducción exacta del bloque entero. La actualización ECC del candidato reutiliza las tramas de grabación actuales: sólo reconstruye los sectores cuyos datos útiles cambian y las columnas PO y filas PI afectadas; la autoprueba confirma palabra a palabra que el resultado coincide con una reconstrucción ECC completa. Después agrupa los candidatos en pequeños lotes de `--batch-frames` (8 por defecto) y cada grupo genera y verifica en paralelo `--batch-candidates` (4 por defecto) secuencias EFM distintas. Sólo se acepta una mejora estricta de la puntuación controlada del bloque completo. Si falla un lote, se divide sucesivamente en grupos menores hasta una sola trama; si ésta falla, se puede reintentar una vez con `--retry-beam-width` (32 por defecto). Cada división continúa únicamente por la primera rama que tiene éxito, evitando enumerar todo el árbol de divisiones. Si no se acepta ningún candidato en una pasada, se termina antes de tiempo. `--retry-frame-fraction 0` procesa todas las tramas con errores en cada pasada, y `--retry-beam-width 0` desactiva el reintento de una sola trama con haz más ancho.

Cuanto mayores son `--beam-width`, `--retry-beam-width` y `--batch-candidates`, más fácil es encontrar candidatos que sigan siendo válidos tras la realimentación ECC, pero el tiempo aumenta aproximadamente con el número de verificaciones de candidatos. Un `--batch-frames` mayor produce mejoras locales más fuertes por intento, pero es más fácil que la realimentación PO/PI provoque el rechazo del bloque completo; uno menor resulta más estable, aunque requiere más reconstrucciones completas. `--iterations`, o `--iterations-per-block` en modo de intervalo, indica el número de pasadas de bucle cerrado, con 3 por defecto. Sigue disponible el alias compatible `--algorithm control`; se rechazan los antiguos `--frame-candidates`, `--parallel-candidates`, `--parallelism`, `--word-candidates` y `--control-bytes`. `--mutation-bytes` sólo pertenece al modo `full`.

`--search-lanes 1..8` sirve para reducir los errores cercanos al 50% causados por mínimos locales. La vía 1 utiliza los datos útiles iniciales recibidos y mantiene una aceptación estrictamente monótona; las demás reinician con datos útiles independientes deterministas derivados de `--seed` y permiten aceptar temporalmente candidatos con peor puntuación exacta del bloque completo según la temperatura de recocido entre `--initial-temperature` y `--final-temperature`, para superar las barreras locales causadas por PO/PI. Cada vía conserva por separado su mejor resultado histórico tras la reproducción ECC exacta completa; todas se ejecutan en paralelo y al final se elige por mayoría controlada, gris y `|DSV|` final, en ese orden. Así, los pasos exploratorios no empeoran la salida final respecto a la vía 1. El valor predeterminado 1 no añade coste. Si se prioriza la calidad, se puede probar primero `--search-lanes 4 --iterations-per-block 10` en unos pocos sectores; los reinicios independientes modifican los datos útiles sin restricciones de imagen, por lo que debe mantenerse 1 si se necesita conservar contenido específico.

`full/exact` significa que no se restringen las posiciones de datos útiles modificables; no significa enumerar exhaustivamente el espacio `256^32768` ni garantizar un óptimo global. La imagen se binariza primero píxel a píxel mediante `--image-threshold`; state-control vota después la mayoría blanco/negro de las muestras de imagen de cada palabra de código de datos útiles de 16 bits, y el objetivo sólo puede ser negro puro 0 o blanco puro 16. Los empates 8:8 se deciden con la muestra central de la palabra. La resolución minimiza primero el número de palabras cuya mayoría real tiene la dirección incorrecta y después utiliza la distancia del número de land a 0/16 como error de gris. No ajusta el patrón de bits individual de las 16 posiciones; si se necesita una correspondencia exacta por posición de bit de canal, debe utilizarse `full`.

<a id="dispersion单字节纹理模式"></a>
### Modo de textura dispersion de un solo byte

`--algorithm dispersion` (alias `byte-pool`) genera directamente los datos útiles en el dominio aleatorizado, sin búsqueda aleatoria:

```text
Luminancia de imagen   0..127 -> Q = 0xA5 -> 5 transiciones fijas -> Vista previa 0
Luminancia de imagen 128..255 -> Q = 0x92 -> 2 transiciones fijas -> Vista previa 255
ISO payload P = Q XOR sector scrambler byte
```

Cada palabra de código de datos útiles toma la muestra de imagen en el centro físico de sus 16 bits de canal y se clasifica según los intervalos fijos anteriores; por ello, este modo no depende de `--constraint-step` ni de `--image-threshold`; `--image-threshold` sólo controla el objetivo binario de `full/state-control`. ID, IED, RSV a cero, EDC, PI, PO y SYNC siguen generándose según la norma; el programa no despliega los 619008 bits NRZI, pero reproduce palabra a palabra todas las tramas de grabación para transmitir con exactitud el estado EFMPlus, el DSV y el pit/land finales al siguiente bloque ECC.

<a id="影调抖动"></a>
#### Tramado tonal

Las dos texturas sólo representan blanco y negro, pero los tonos intermedios de la imagen pueden reproducirse mezclándolas proporcionalmente en cada byte de datos útiles:

```powershell
--tone-dither 1 --tone-levels 2 --tone-brightness 0
```

- `--tone-dither 0..1`: 0 conserva el umbral fijo anterior `--image-threshold`; 1 aplica un umbral tramado por byte para reconstruir el gris mediante la proporción espacial de palabras de código blancas y negras.
- `--tone-levels`: DVD sólo tiene dos texturas y únicamente acepta 2.
- `--tone-brightness -64..64`: desplaza toda la escala de grises.

La fase del tramado depende del índice lógico del byte de datos útiles y no del muestreo de vista previa; al leer la ISO, `simulate` sólo clasifica según `0xA5/0x92`, por lo que la simulación de relectura refleja naturalmente el resultado del tramado. Si no se proporcionan estos parámetros, la salida coincide byte a byte con la versión anterior.

Si sólo se necesita generar una imagen grabable lo antes posible y no hacen falta los límites EFMPlus finales, se puede añadir `--fast-output true`. La salida rápida sigue tomando muestras en los mismos centros de palabras de código de datos útiles y produce los mismos sectores de 2048 bytes que dispersion normal; la diferencia es que utiliza una máscara de aleatorización precalculada de 32 KiB para obtener directamente los bytes ISO `P` a partir de `Q`, sin construir tramas de datos, EDC, PI, PO ni tramas de grabación, ni reproducir EFMPlus, DSV o estados pit/land. En el JSON, `exactEccEfmReplay` será `false` y `finalBoundary`, `null`. Esto no omite datos necesarios para grabar: el grabador sigue generando esos campos derivados a partir de los sectores de 2048 bytes.

```powershell
& ".\src\DvdImageSolver\bin\Release\net10.0-windows\DvdImageSolver.exe" solve `
  --image target.png `
  --total-sectors 2297888 `
  --iso-output "R:\full-disc-fast.iso" `
  --fill-sectors 2297888 `
  --lba 0 `
  --psn-offset 0x30000 `
  --inner-radius-mm 24 `
  --outer-radius-mm 58 `
  --channel-bit-nm 133.3 `
  --algorithm dispersion `
  --fast-output true `
  --fast-parallelism 0 `
  --initial zero
```

`--fast-parallelism 0` genera en paralelo los datos útiles de bloques ECC independientes según el número de procesadores lógicos y después los escribe en la imagen por orden de LBA; también puede fijarse en `1..64` para limitar el uso de CPU. El paralelismo no cambia la elección de bytes ni el orden de salida.

La salida rápida a archivo y por flujo activa de forma predeterminada el muestreo SIMD de cuatro puntos en doble precisión si hay aceleración por hardware. Cada grupo sigue utilizando cuatro centros reales de palabras de código de datos útiles, sin tratar posiciones que cruzan SYNC/ECC como muestras consecutivas equidistantes. La inversión del paso cúbico dentro de un mismo segmento comparte una búsqueda de segmento y vectoriza los cuatro cálculos Newton/Hermite originales; al cruzar segmentos o límites se vuelve a la geometría escalar. Las imágenes opacas en blanco y negro utilizan seno y coseno vectoriales; los puntos próximos a medios píxeles o bordes de imagen se recalculan por la ruta escalar original. Las imágenes de grises normales, color y transparencia conservan el seno, coseno e interpolación escalares originales, acelerando sólo la geometría de paso cúbico. El paso constante, las funciones de patrones analíticos y las máquinas sin hardware SIMD siguen utilizando muestreo escalar. La ruta de cuatro puntos para paso constante presenta una regresión de rendimiento en las mediciones actuales, por lo que permanece desactivada. Se conserva `double` durante todo el proceso, sin coordenadas de precisión simple ni fórmulas de espiral aproximadas.

Al llamar a la API de generación se puede establecer `MultiBlockSolveOptions.UseSimdSampling = false` para utilizar la ruta escalar de referencia y comparar rendimiento y salida; el valor predeterminado es `true`, sin necesidad de cambiar los parámetros GUI o de línea de comandos existentes. Las regresiones abarcan la coincidencia de datos útiles/recuentos entre ambas rutas de muestreo, los límites de segmentos de paso cúbico, límites de medio píxel, umbrales de transparencia, sentidos horario y antihorario y orden de salida paralelo. El rendimiento debe medirse por los bytes reales de datos útiles con la misma entrada e intervalo LBA; la aceleración aislada de funciones trigonométricas no representa la aceleración del disco completo.

La inversión rápida del paso constante guarda en caché el valor integral en el radio interior, el cuadrado del radio y los coeficientes de paso, que no cambian una vez resueltos, y reutiliza la misma raíz cuadrada dentro de una corrección Newton. Esta ruta conserva el orden original de operaciones `double`, la división y el comportamiento en los límites, sin integrales aproximadas ni sustituir divisiones por multiplicaciones por el inverso. Una regresión independiente de la fórmula antigua compara bit a bit el radio, el ángulo y los límites de espirales cortas; las comparaciones de rendimiento deben utilizar motores independientes anteriores y posteriores a la optimización, porque ambas posiciones del interruptor `UseSimdSampling` utilizan esta optimización escalar.

`payload-transition-class-error=0` significa que todos los datos útiles controlados se asignaron correctamente a las dos categorías `0xA5/0x92`, no que los campos fijos, ECC y SYNC tengan la misma densidad de transiciones. `dispersionPool.levels` del JSON de salida registra los dos intervalos de entrada, los bytes Q, los números de transiciones y las luminancias nominales de vista previa; estos valores de vista previa no equivalen a la reflectancia del disco físico.

La salida incluye:

- `payload.bin`: concatenación secuencial de 16 sectores de 2048 bytes;
- `payload.bin.json`: PSN, estados de los límites inicial/final, errores, número de iteraciones y tiempo de ejecución;
- `channel.bin` opcional: niveles NRZI empaquetados en 77376 bytes.

<a id="输出可刻录-iso"></a>
## Salida ISO grabable

`solve` puede producir sólo una ISO o conservar también los datos útiles de 32768 bytes:

```powershell
dotnet run --project src/DvdImageSolver --configuration Release -- solve `
  --image picture.png `
  --total-sectors 2295104 `
  --iso-output solved-disc.iso `
  --lba 0 `
  --iterations 10000
```

El modo de imagen reutiliza directamente `--total-sectors` como número de sectores lógicos ISO. Con restricciones de texto debe proporcionarse además `--iso-sectors`:

```powershell
--target target.txt --iso-output solved-disc.iso --iso-sectors 2295104
```

La imagen nueva contiene un sistema de archivos ISO9660 mínimo y un archivo `SOLVED.BIN`; los 16 sectores de 2048 bytes resueltos se escriben en `--lba ... --lba+15`. Si LBA es 0, esos datos se encuentran en la zona de sistema ISO9660 y el programa coloca otra copia visible de `SOLVED.BIN` en la zona de datos del sistema de archivos. En NTFS con compatibilidad con archivos dispersos, la imagen nueva intenta utilizar almacenamiento disperso; su longitud lógica sigue siendo `total-sectors × 2048` y el software de grabación lee los huecos como ceros.

También se pueden sustituir 16 sectores consecutivos de una ISO existente:

```powershell
dotnet run --project src/DvdImageSolver --configuration Release -- solve `
  --image picture.png `
  --total-sectors 2295104 `
  --iso-template original.iso `
  --iso-output solved-disc.iso `
  --lba 32 `
  --iterations 10000
```

El tamaño de la plantilla debe ser un múltiplo de 2048; en modo de imagen, su número de sectores también debe coincidir con `--total-sectors`. Sustituir sectores asignados a directorios, archivos o metadatos UDF/ISO destruye el sistema de archivos original, por lo que el modo de plantilla debe seleccionar una zona reservada o un extent ya ocupado por el archivo objetivo. Una ISO mínima nueva rechaza sobrescribir los sectores fijos 16 y 17 de descriptores de volumen ISO9660.

La ISO contiene datos de usuario de 2048 bytes, no un flujo de canal sin procesar de 619008 bits. Un grabador DVD estándar vuelve a generar ID, EDC, ECC, EFMPlus y NRZI; por ello, el patrón real también depende de que la unidad siga las mismas elecciones de codificación y de que el estado, DSV y pit/land iniciales proporcionados al solucionador coincidan con los límites de grabación reales.

<a id="连续铺满盘片或指定-sector-数"></a>
### Rellenar el disco completo o un número de sectores consecutivos

Si `--fill-sectors` es mayor que 16, el programa resuelve bloques ECC consecutivos uno a uno desde `--lba` y transmite el estado, DSV y pit/land finales de cada bloque al siguiente. El número de sectores debe ser múltiplo de 16. Para un disco con capacidad de 2297888 sectores, el comando de disco completo es:

```powershell
dotnet run --project src/DvdImageSolver --configuration Release -- solve `
  --image picture.png `
  --total-sectors 2297888 `
  --iso-output full-disc.iso `
  --fill-sectors 2297888 `
  --lba 0 `
  --psn-offset 0x30000 `
  --inner-radius-mm 24 `
  --outer-radius-mm 58 `
  --channel-bit-nm 133.3 `
  --constraint-step 16 `
  --algorithm state-control `
  --bridge-bytes 2 `
  --beam-width 8 `
  --retry-beam-width 32 `
  --retry-frame-fraction 0.25 `
  --batch-frames 8 `
  --batch-candidates 4 `
  --search-lanes 1 `
  --iterations-per-block 3 `
  --initial-state 1 `
  --initial-dsv 0 `
  --initial-level land
```

Para generar sólo los primeros 1600 sectores, cambiarlo por:

```powershell
--fill-sectors 1600
```

En ese caso, el archivo de salida contiene los 1600 sectores de LBA 0 a 1599; `--total-sectors` sigue representando la capacidad del disco completo para calcular la geometría de pista. Si `--lba` no es 0, la salida también conserva los sectores anteriores a cero, por lo que la longitud de imagen es `(lba + fill-sectors) × 2048`.

El modo de intervalo sobrescribe todos los sectores especificados, incluidos los descriptores ISO9660, por lo que produce una imagen sin procesar de 2048 bytes/sector grabable directamente, no un sistema de archivos ISO9660 montable. `--constraint-step` indica el intervalo de muestreo de imagen de la búsqueda de datos útiles libres; state-control vota la mayoría de las muestras binarias que caen dentro de una misma palabra de código de datos útiles. La puntuación final de visibilidad no está limitada por ese paso disperso: vuelve a muestrear la imagen en el centro de cada palabra de código de canal de 16 bits e incluye palabras derivadas, fijas y SYNC. Para aumentar la diferencia de brillo del disco con una imagen binaria, se recomienda `--constraint-step 16`, que asigna un objetivo a cada palabra de datos útiles; `32` es un compromiso más rápido con la mitad de densidad, mientras que `256` sólo restringe aproximadamente 1/16 de las palabras de datos útiles, por lo que el error global de mayoría suele acercarse al nivel aleatorio. El disco completo tiene 143618 bloques ECC; antes de una ejecución definitiva se deben medir la velocidad y el resultado con 160 a 1600 sectores.

El modo dispersion siempre toma muestras en el centro de cada palabra de código de datos útiles e ignora el significado de búsqueda dispersa de `--constraint-step`; por ello puede omitirse ese parámetro.

<a id="文件与图案混合模式"></a>
## Modo mixto de archivos y patrón

Proporcionar `--data-dir` activa el modo mixto. El programa escribe ese directorio y sus subdirectorios recursivamente como un sistema de archivos ISO9660 + Joliet estándar. `--file-position inner` (predeterminado) coloca los archivos en la zona interior y el patrón fuera de ellos; `--file-position outer` coloca el contenido de los archivos al final del disco y el patrón después de los metadatos iniciales y antes de los archivos. Los límites inicial y final del patrón se alinean a bloques ECC de 16 sectores, conservando siempre la geometría del disco completo sin estirar la zona de dibujo restante para ocuparlo. La ISO final puede abrirse como un disco de datos normal en Windows y otros sistemas; el directorio Joliet conserva los nombres de archivo chinos.

```powershell
dotnet run --project src/DvdImageSolver --configuration Release -- solve `
  --image picture.png `
  --data-dir ".\disc-files" `
  --file-position outer `
  --iso-output hybrid-disc.iso `
  --total-sectors 2297888 `
  --volume-label MY_DISC `
  --inner-radius-mm 24 `
  --outer-radius-mm 58
```

El modo mixto utiliza `--algorithm dispersion --fast-output true` de forma predeterminada, adecuado para generar directamente toda la zona exterior. También se puede añadir expresamente `--fast-output false`: el programa codifica y reproduce primero todos los bloques de datos interiores para determinar el estado EFMPlus, el DSV y el pit/land al entrar en la zona exterior y después dibuja con límites exactos. El tiempo de reproducción exacta aumenta con el volumen de datos interiores.

La zona «Archivos del disco» de la página de diseño GUI permite elegir una carpeta y alternar «Posición de archivos: interior/exterior»; comparte ajustes con la página de exportación. Esta opción se guarda en el proyecto y se utiliza también para la grabación por flujo. La ruta por flujo siempre utiliza dispersion rápido: primero examina el directorio y fija la distribución, después produce metadatos, zona de archivos y zona de patrón en orden físico, sin crear una ISO temporal completa; en modo exterior escribe primero el patrón y después los archivos. Si el tamaño de un archivo de origen cambia tras planificar la distribución, se rechaza continuar la escritura por flujo. La vista previa marca en amarillo la zona ocupada por el sistema de archivos; ese color no forma parte de los datos grabados.

Este modo no acepta `--lba` ni `--fill-sectors`, que se calculan automáticamente a partir de la distribución de archivos y la capacidad del disco. `--drawing-start-lba N` permite dejar espacio adicional después del inicio predeterminado del dibujo, pero debe estar dentro de la zona dibujable y alinearse con `--psn-offset` a 16 sectores. `--file-position outer` debe utilizarse junto con `--data-dir`.

El escritor ISO9660 actual admite directorios recursivos y nombres Unicode Joliet; los archivos individuales no pueden superar 4 GiB, y para evitar ciclos de enlaces y referencias accidentales a datos externos, `--data-dir` no acepta puntos de reanálisis ni enlaces simbólicos.

<a id="半径标定预览"></a>
## Vista previa de calibración de radios

La calibración adopta la proyección directa de la versión CD: toma muestras según la secuencia de posiciones globales de canal; para una misma posición, primero lee la imagen objetivo en la «espiral supuesta durante la generación» y después la proyecta en la «espiral real estimada». Si varias muestras de pista caen en el mismo píxel de salida, se promedian, conservando la fase real acumulada por la diferencia de paso y evitando el muaré de burbujas que aparece al invertir cada píxel de salida para buscar la pista más cercana.

```powershell
dotnet run --project src/DvdImageSolver --configuration Release -- calibrate `
  --image picture.png `
  --output calibration-preview.png `
  --total-sectors 2297888 `
  --generated-inner-radius-mm 24 `
  --generated-outer-radius-mm 58 `
  --actual-inner-radius-mm 24.2 `
  --actual-outer-radius-mm 57.8 `
  --channel-bit-nm 133.3 `
  --spiral-direction ccw `
  --image-threshold 128 `
  --alpha-threshold 1 `
  --preview-size 2048 `
  --samples-per-sector 16
```

`--samples-per-sector` controla la densidad de proyección directa, entre 1–4096 y con 16 por defecto; cada sector DVD corresponde a unos 5.16 mm de pista, y 16 equivale a una muestra cada 0.32 mm aproximadamente. Subir a 32 o 64 reduce píxeles vacíos y mejora los detalles, con un aumento de tiempo aproximadamente lineal. La vista previa no ofrece un parámetro de giro arbitrario; las espirales de generación y real comparten el origen 0° y un giro global no afecta a la calibración de radios. Además de la vista previa, produce `calibration-preview.png.json`, con `mappingMode` igual a `forward-channel-splat-average`, y registra la densidad de muestreo, ambos pasos, el número total de vueltas y la longitud de pista. No incluye el error residual del solucionador ni las diferencias de implementación del grabador.

La calibración muestra el disco completo por defecto. Si sólo se generó una imagen parcial, se pueden proporcionar los mismos `--lba` y `--fill-sectors` del comando de resolución; las zonas no escritas permanecen transparentes.

<a id="生成-iso-的读回模拟"></a>
## Simulación de relectura de la ISO generada

La entrada de `calibrate` es la imagen de origen y describe el efecto ideal de «escribir con la geometría de generación y releer con la geometría medida»; utiliza el mismo umbral de dos niveles 128 que la generación, y conserva `--image-threshold` por compatibilidad sin que cambie esta vista previa. `simulate`, en cambio, lee la propia ISO ya generada: restaura cada byte de datos útiles muestreado mediante la aleatorización DVD, lo clasifica como `0xA5/0x92` y lo muestra respectivamente como 0/255; después proyecta el centro de la palabra de código sobre el disco con la espiral de radios medidos. Para ver imágenes antiguas de varios niveles, la relectura sigue mostrando `0x66` como 0 y `0xB9` como 85; estos valores no se utilizan para generar imágenes nuevas. Es un bucle cerrado de la misma estructura que el diálogo de calibración de cdimage: la geometría de generación ya está fijada en la ISO y la simulación sólo necesita un conjunto de parámetros geométricos medidos.

```powershell
dotnet run --project src/DvdImageSolver --configuration Release -- simulate `
  --iso drawing.iso `
  --output readback-preview.png `
  --inner-radius-mm 24.2 `
  --outer-radius-mm 57.8 `
  --preview-size 2048 `
  --samples-per-sector 16
```

Si se omiten `--total-sectors`, `--lba` y `--fill-sectors`, se leen de `drawing.iso.json`, escrito por el comando de resolución (LBA inicial y número de sectores de dibujo); sin archivo auxiliar, se utiliza toda la ISO desde LBA 0. `--samples-per-sector` controla el número de bytes de datos útiles muestreados por sector (1–2048, 16 por defecto). Los bytes que después de la aleatorización no pertenecen a la tabla de dos niveles ni a los valores antiguos compatibles 66/B9 se omiten. El prefijo del sistema de archivos puede coincidir accidentalmente con la tabla y debe excluirse con el intervalo de dibujo del archivo auxiliar; la propia ISO no almacena códigos de corrección PI/PO. Junto a la salida se escribe también `readback-preview.png.json`, con `mappingMode` igual a `payload-scramble-classify-splat`, que registra el número de coincidencias de clasificación, el paso y las vueltas totales.

La posición de muestreo es el centro de la palabra de código: en la generación, `FastDispersionImageWriter` coloca el objetivo de imagen en el centro mediante `PayloadDirectChannelOffset + 8` (la mitad de una palabra de 16 bits), y la relectura utiliza la misma posición; así, ambas proyecciones no difieren en 8 bits de canal (aproximadamente 1 µm de longitud de pista). Además, la relectura y la generación comparten la espiral, el sentido y la máscara de aleatorización, de modo que, cuando los «parámetros de generación» coinciden con los «parámetros de vista previa», la imagen releída reproduce la generada muestra a muestra.

Al cambiar de ISO, el espacio de vista previa lee `imageMapping.totalSectors`, `innerRadiusMm` y `outerRadiusMm` del archivo auxiliar y carga la geometría de calibración: una imagen importada no se comprime en un anillo estrecho por reutilizar las dimensiones del disco anterior, y los ajustes posteriores del usuario parten de esos parámetros.

<a id="预览工作区的两种模式"></a>
## Los dos modos del espacio de vista previa

El selector de entrada de la página de vista previa ofrece las dos rutas anteriores, que comparten agrupación de cambios, cancelación y exportación:

- **Relectura del resultado** (simulación de relectura, `simulate`): la entrada es una ISO ya generada y los parámetros son un único conjunto de geometría medida; muestra los bytes que se escribieron realmente en la imagen.
- **Proyección del diseño** (vista previa de calibración, predeterminada, `calibrate`): la entrada es la imagen de origen y los parámetros incluyen las geometrías de generación y medida. No lee la ISO, por lo que se actualiza mucho más rápido después de cambiar parámetros; a cambio, muestra la proyección del origen con la geometría medida, no las palabras de código realmente presentes en la imagen: no aparecen los errores del solucionador ni el prefijo del sistema de archivos.

La vista previa de calibración utiliza directamente la composición de la página de diseño, el mismo origen que la generación de la imagen, por lo que ambos patrones coinciden.

<a id="仅执行前向编码"></a>
## Ejecutar sólo la codificación directa

```powershell
dotnet run --project src/DvdImageSolver --configuration Release -- encode `
  --input payload.bin `
  --output channel.bin `
  --lba 0 `
  --psn-offset 0x30000 `
  --initial-state 1 `
  --initial-dsv 0 `
  --initial-level land
```

El primer PSN debe ser múltiplo de 16; en caso contrario, el programa rechaza la codificación porque la entrada no comienza en un límite de bloque ECC.

<a id="调制表再生成"></a>
## Regenerar la tabla de modulación

`Encoding/EfmPlusTables.Generated.cs` se genera mediante extracción mecánica del anexo G de ECMA-267. Para regenerarla a partir del PDF de la norma:

```powershell
python tools/generate_efm_tables.py ECMA-267.pdf src/DvdImageSolver/Encoding/EfmPlusTables.Generated.cs
```
