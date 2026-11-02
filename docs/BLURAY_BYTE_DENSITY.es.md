Traducción al español · [Original](BLURAY_BYTE_DENSITY.md)

<a id="单层-bd-r字节与位周期的跳变密度分析"></a>
# BD-R de una capa: análisis de densidad de transiciones de bytes y períodos de bits

2026-10-24. Basado en el documento ISO/IEC 30190:2021 proporcionado, en especial 13.15–13.18 y las figuras 40/41, con experimentos numéricos que utilizan la cadena de codificación EDC, aleatorización, LDC/BIS, DC y 17PP de este proyecto. Sólo se consideran discos de una capa.

**Cuando el objetivo es la densidad total de transiciones, conviene utilizar patrones construidos bit a bit.** La mejor referencia de bytes repetidos alcanza densidades de aproximadamente **19.20% / 38.61%** en el clúster completo; al tener en cuenta el entrelazado físico y los límites de los segmentos DC, las estrategias de períodos de bits probadas alcanzan **15.95% / 43.37%**. La diferencia entre ambos extremos aumenta de 19.41 a 27.42 puntos porcentuales, alrededor de un 41%. Son resultados de codificación digital; el brillo reflejado del disco físico y la dirección de blanco y negro aún no se han determinado. El dibujo actual utiliza, por elección del usuario, la **preferencia por el número de intervalos 3T / 6T** descrita a continuación, en lugar de los extremos de densidad de este análisis.

<a id="按-3t--6t-数量接入绘图"></a>
## Integración en el dibujo según el número de intervalos 3T / 6T

Con clústeres completos de los mismos 958,272 bits de canal, se cuenta el **número absoluto** de intervalos completos de 3T o 6T entre unos de modulación (ONE) consecutivos. Se incluyen tanto marcas como espacios; no es el ciclo de trabajo del nivel NRZI ni una clasificación por `número de intervalos objetivo / número total de intervalos`.

| Extremo predeterminado de la imagen | Q en el dominio aleatorizado | Número de intervalos objetivo del clúster de referencia | Proporción de intervalos objetivo | Intervalo de recuentos objetivo en cuatro direcciones |
|---|---|---:|---:|---:|
| Zonas oscuras (0–127), preferencia por 3T | `AA` | 265,136 | 85.4164% | 264,946–265,186 |
| Zonas claras (128–255), preferencia por 6T | `44` | 116,575 | 62.6862% | 116,470–116,659 |

Ambos obtienen el máximo recuento objetivo respectivo entre los 256 bytes repetidos con PSN de referencia `0x100000`. `22` tiene la mayor proporción de 6T, un 62.7744%, pero sólo 116,402 intervalos 6T, menos que `44`; por ello, el modo por recuento elige `44`. Al comprobar las cuatro direcciones `0x100000`, `0x234560`, `0x6D3A00` y `0xCA73E0`, la media de 6T de `44` es 116,559.5, ligeramente superior a los 116,526.5 de `11` y los 116,457 de `22`; esto no implica que sea óptimo en cada dirección.

El `BluRayRunLengthMapping` de producción utiliza el umbral 128 para elegir `AA` / `44` y permite invertir la dirección de blanco y negro; `BluRayImagePayloadMapping` toma muestras según la posición de la imagen y el generador por flujo compensa la aleatorización antes de emitir los datos útiles del host. Los bordes de la imagen, EDC, ECC, BIS, DC y la sincronización introducen otras longitudes de intervalo, de modo que «preferencia por 3T / 6T» no equivale a 3T / 6T puros. La asignación predeterminada de oscuro y claro es una convención de imagen reversible, no una calibración del brillo reflejado de un disco físico.

Los valores anteriores utilizan el modelo de diagnóstico DC voraz actual; los recuentos originales están en `Cluster.RunCounts` de `all-bytes.json`. Las pruebas de regresión de arquitectura verifican la preferencia por recuento en ambos extremos del disco mediante codificación RUB completa y comprueban la aleatorización y las posiciones ECC de imágenes mixtas. La línea de comandos y el uso se explican en la [guía de Blu-ray](ADDING_BLURAY.es.md).

<a id="测量口径"></a>
## Criterios de medición

- Densidad de transiciones = número de unos de modulación / número de bits de canal. Un uno representa una inversión NRZI y no puede sustituirse por la proporción de unos en el nivel NRZI.
- Q son los **datos útiles objetivo después de la aleatorización**. Los datos útiles del host deben escribirse como `Q XOR máscara de aleatorización`; repetir directamente los bytes originales del host elimina el patrón objetivo.
- Los resultados de 17PP puro no incluyen DC, ECC, BIS ni sincronización; se calculan con una tabla independiente de la norma y un cálculo exacto de ciclos de estados finitos.
- El clúster completo se genera a partir de 65,536 bytes del host y se contabilizan `496 × 1932 = 958272` bits de canal, incluidos EDC, paridad LDC, BIS, DC y sincronización; no incluye run-in/out/Guard3, pero utiliza el nivel y el DSV al final del run-in.
- El PSN predeterminado es `0x100000`, con los indicadores de dirección y los datos de control de usuario a cero. Los candidatos también se validan en `0x234560`, `0x6D3A00` y `0xCA73E0`.
- Los resultados principales utilizan el algoritmo de diagnóstico DC voraz actual: los bits de control futuros se fijan temporalmente a cero. No se garantiza que coincida con las decisiones del grabador; DC todo a 0, todo a 1 y aleatorio con semilla fija son pruebas de sensibilidad adicionales, no instrucciones para una grabación real.

<a id="全-256-字节基线"></a>
## Referencia de los 256 bytes

Repetir continuamente un byte Q sólo produce cuatro niveles de densidad en régimen estable sin DC/ECC:

| Densidad de 17PP puro | Número de bytes |
|---|---:|
| 1/6 = 16.6667% | 28 |
| 1/4 = 25% | 92 |
| 1/3 = 33.3333% | 100 |
| 5/12 = 41.6667% | 36 |

De ellos, **80 bytes cambian de nivel tras un desplazamiento impar de bits**. La inserción de bits DC modifica la fase de análisis de pares de bits, por lo que no se puede clasificar únicamente a partir de esta tabla de codificación sin otros elementos.

| Q | 17PP puro | Clúster completo con DC voraz | Intervalo del clúster completo en cuatro PSN | Uso en las pruebas |
|---|---:|---:|---:|---|
| `09` | 16.6667% | 19.1997% | 19.1962–19.2080% | Byte repetido de menor densidad en el barrido |
| `11` | 16.6667% | 19.4820% | 19.4447–19.4820% | La codificación pura sólo contiene 6T; sirve de referencia de intervalos largos |
| `22` | 16.6667% | 19.3505% | 19.3242–19.3510% | También pertenece al grupo de baja densidad robusta |
| `3D` | 41.6667% | 38.4602% | 38.4428–38.4739% | Byte de alta densidad robusta |
| `7A` | 41.6667% | 38.5486% | 38.5409–38.5525% | Byte de alta densidad robusta |
| `9E` | 41.6667% | 38.6143% | 38.6014–38.6260% | Byte repetido de mayor densidad en el barrido |
| `00` | 33.3333% | 32.8615% | 32.8412–32.8668% | Referencia de entrada habitual |
| `FF` | 33.3333% | 32.5332% | 32.5332–32.5639% | No produce una gran diferencia de densidad respecto a `00` |

Los clústeres completos de `55/AA` rondan el 32.30% / 32.39%, por lo que tampoco sirven como extremos de densidad. Sin compensar la aleatorización, los 14 candidatos de bytes constantes del host probados quedan aproximadamente entre 29.37–29.67%, y las diferencias originales prácticamente desaparecen.

Resultados completos: [CSV](../artifacts/bluray-byte-density/all-bytes.csv), [tabla legible](../artifacts/bluray-byte-density/all-bytes.md), [gráfico de densidad de los 256 bytes](../artifacts/bluray-byte-density/all-bytes-density.png).

<a id="更细粒度的可达模式"></a>
## Patrones alcanzables con mayor precisión

17PP codifica con reglas de longitud variable de 2/4/6/8 bits de entrada y tiene en cuenta el bit de modulación anterior y reglas de sustitución especiales. Los bytes son el contenedor y no pueden tratarse como palabras de código independientes.

| Cadena periódica de bits original | Longitud del período | Ejemplo de empaquetado en bytes | Densidad de 17PP puro | Intervalos en régimen estable |
|---|---:|---|---:|---|
| `0001` | 4 | `11` | 16.6667% | Todos de 6T |
| `00111101` | 8 | `3D` | 41.6667% | 3 de 2T y 2 de 3T |
| `001111` | 6 | `3C F3 CF` | 44.4444% | 3 de 2T y 1 de 3T |
| `0000101101110010` | 16 | `0B 72` | **12.5%** | Todos de 8T |
| `0111011110` | 10 | `77 9D E7 79 DE` | **46.6667%** | 6 de 2T y 1 de 3T |

Los intervalos de un segmento de datos 17PP normal van de 2T a 8T, con un máximo de seis intervalos 2T consecutivos. Por tanto, el límite inferior de densidad para segmentos de datos largos es `1/8` y el superior es `7/(6×2+3)=7/15`; los patrones de 16 y 10 bits anteriores alcanzan esos límites, respectivamente. La sincronización contiene 9T, por lo que estos límites no pueden aplicarse directamente a cualquier ventana corta que incluya sincronización.

Los dos períodos extremos se degradan a 29.1667% / 33.3333% en fases de bits impares. El patrón de 6 bits `001111` mantiene el 44.4444% en ambas fases, pero su ciclo de trabajo NRZI sin control es 4/9 o su complemento, y necesita equilibrarse mediante control DC. En la búsqueda de períodos de hasta 16 bits, es el patrón de mayor densidad robusto en fases pares e impares.

Los ejemplos de empaquetado sólo muestran cómo representar el período. **No se deben repetir directamente estos bytes del host, ni limitarse a repetir Q en el orden original de los datos útiles ignorando el entrelazado.** Por ejemplo, al rellenar Q con el período de 6 bits en el orden de los datos útiles, el clúster completo sólo alcanza aproximadamente 33.43–34.39%; ordenarlo según el ECC físico permite alcanzar aproximadamente 40.64–40.75%.

<a id="dc-补偿与完整簇结果"></a>
## Compensación DC y resultados del clúster completo

Cada trama contiene 155 bytes ECC, un total de 1240 bits sin procesar; primero hay 25 bits de datos y después se inserta un bit DC cada 45 bits de datos, hasta un total de 28. Sea `j=8×EccColumn+bitWithinByte`, donde el bit 0 es el MSB:

```text
DCbefore(j) = j < 25 ? 0 : 1 + floor((j - 25) / 45)

Posiciones DC reservadas:
index = RecordingFrame × 1268 + j + DCbefore(j)
Qbit = pattern[(index + phase) % period]

Reinicio por segmento de datos DC:
index = j < 25 ? j : (j - 25) % 45
Qbit = pattern[(index + phase) % period]
```

Después de insertar DC, cada segmento comienza en un bit de entrada par, por lo que reiniciar el período conserva la fase de análisis de pares de bits. El método de posiciones DC reservadas sólo reserva las posiciones; los valores de los bits DC los sigue eligiendo el codificador/grabador. EDC, BIS y la paridad tampoco pueden controlarse arbitrariamente y se generan con normalidad.

Tras construir Q según estas posiciones físicas, se redistribuye de forma inversa a los bytes de datos útiles correspondientes y después se compensa la aleatorización. El `Map(luminance, position)` existente del proyecto ya proporciona RecordingFrame/EccColumn; basta con generar los 8 bits uno a uno en cada llamada, sin limitar el valor devuelto a un byte fijo.

| Patrón de bits recomendado | Construcción | phase | Clúster completo con DC voraz | Intervalo en cuatro PSN | Intervalo con otras tres estrategias DC |
|---|---|---:|---:|---:|---:|
| 16 bits `0000101101110010` | Reinicio por segmento DC | 2 | **15.9500%** | 15.9405–15.9500% | 15.9278–17.0720% |
| 6 bits `001111` | Posiciones DC reservadas | 3 | **40.8172%** | 40.8158–40.8315% | 41.0010–41.4603% |
| 10 bits `0111011110` | Reinicio por segmento DC | 4 | **43.3670%** | 43.3662–43.3713% | 42.1870–43.3904% |

phase comienza en 0 e indica el desplazamiento periódico que se suma a la coordenada de la fórmula anterior. Cada clúster vuelve a empezar desde el origen de coordenadas; estas fases sólo se refieren a ese método de construcción, no a desplazar arbitrariamente toda la señal física el mismo número de bits.

La densidad del clúster completo del método de 6 bits abarca 40.7929–40.8172% entre todas sus fases, con poca influencia del cambio de fase. Los métodos extremos de 16/10 bits siguen requiriendo la fase elegida; una fase incorrecta puede devolverlos aproximadamente al 30%–32%. El intervalo de sensibilidad DC sólo abarca las estrategias probadas y no es una garantía para cualquier grabador.

En el patrón de baja densidad recomendado, el **52.65% de los intervalos completos entre transiciones es de 8T**; en el de alta densidad recomendado, el **79.14% es de 2T**. Esto ofrece una comparación en disco físico más concreta que la densidad media por sí sola. En el modelo voraz, ambos tienen una proporción de niveles NRZI de aproximadamente el 50%, por lo que los valores de bit 0/1 no pueden interpretarse directamente como blanco y negro visuales.

![Comparación de densidad de clústeres completos](../artifacts/bluray-byte-density/fine-pattern-comparison.png)

<a id="首轮实盘候选"></a>
## Candidatos para la primera ronda en disco físico

Se recomienda comparar los siguientes bloques de color uniforme con los mismos discos, velocidad de grabación y condiciones de observación, repitiéndolos en los radios interior, medio y exterior:

1. Extremo disperso: período de 16 bits, reinicio por segmento DC, phase 2.
2. Extremo de alta densidad: período de 10 bits, reinicio por segmento DC, phase 4.
3. Referencia robusta de alta densidad: período de 6 bits, posiciones DC reservadas, phase 3.
4. Referencia de bytes: `09`, `11` y `9E`; `00` puede añadirse como referencia de entrada habitual.

Primero deben compararse bloques grandes para observar el brillo real y su estabilidad al girar el ángulo de observación; después se puede pasar a mezclas de zonas y asignación de imágenes. Alternar directamente los dos patrones byte a byte cambia el contexto de codificación; no debe suponerse que su densidad o brillo sea la media lineal de ambos extremos. Estos candidatos de densidades extremas todavía no se han grabado en discos físicos ni se utilizan en la asignación predeterminada actual de blanco y negro.

<a id="验证及复现"></a>
## Validación y reproducción

- La implementación independiente de Python transcribe la figura 41 de la norma; coincide bit a bit con los 256 vectores de cuatro bytes y los 298 vectores periódicos de C#.
- Se enumeran exhaustivamente las 131,068 cadenas originales de bits de longitud 2–16 y se comprueban los dos valores del bit anterior en cada una, con un total de 262,136 cálculos exactos de ciclos; se validan 14,694 clases de equivalencia por rotación de dos bits. La optimalidad robusta a la fase indicada sólo se aplica a ese espacio de búsqueda.
- La cadena completa barre los 256 bytes; las tramas sintéticas comprueban además, por cada byte, 8 máscaras DC × 31 posiciones de sincronización × 2 terminaciones de la trama anterior.
- Los patrones detallados suman 176 combinaciones de patrón/distribución/fase, además de 120 clústeres de comprobación de direcciones. En cada clúster se verifica que los 65,536 bytes Q controlables coincidan con el objetivo en las posiciones ECC reales.
- Se superan las comprobaciones de longitud de clúster, conservación de los recuentos de intervalos y separaciones RLL; el barrido rápido de clústeres y la ruta RUB completa coinciden en la comprobación cruzada. Los datos puros no contienen intervalos completos fuera de 2T–8T; los clústeres con sincronización permiten 9T.

Las herramientas y los comandos se encuentran en [BluRayByteProbe](../tools/BluRayByteProbe/README.es.md). Los resultados originales están en `artifacts/bluray-byte-density/`; el directorio artifacts se ignora según las reglas del repositorio, y los scripts permiten reconstruir los resultados. La versión de producción ya utiliza la asignación por número de intervalos 3T / 6T descrita por separado en esta página.
