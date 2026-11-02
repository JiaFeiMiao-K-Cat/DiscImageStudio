<a id="independent-17pp-reference"></a>
# Referencia 17PP independiente

> Traducción al español · [Original](README.md)

Herramientas de la biblioteca estándar de Python transcritas de ISO/IEC 30190:2021, §13.17 y Figura 41, proporcionada al proyecto original. Esta implementación no importa el codificador C# de producción. La referencia finita se contrastó con C# para las 256 entradas de cuatro bytes repetidos y 298 vectores finitos de patrones de bits: todos coincidieron. Los vectores esperados se incluyen en los resultados.

Desde la raíz del repositorio:

```powershell
python tools/BluRayByteProbe/reference/analyze.py --output-dir artifacts/bluray-byte-density/reference
python tools/BluRayByteProbe/reference/pattern_search.py --output-dir artifacts/bluray-byte-density/reference
python tools/BluRayByteProbe/reference/robust_pattern_search.py --output-dir artifacts/bluray-byte-density/reference
```

`--output-dir` usa por defecto el directorio actual. Ambas búsquedas aceptan `--max-period` (16 por defecto). No hacen falta paquetes Python externos.

<a id="measurement-domain"></a>
## Dominio de medida

Son **entradas posteriores a la aleatorización en la entrada 17PP**, antes de cualquier inserción DC modelada explícitamente. No son patrones de datos útiles del host sin procesar. La densidad es el número de UNOS de modulación dividido por los bits de canal: cada UNO de modulación conmuta el nivel grabado NRZI. Contar cambios *entre bits de modulación* mediría otra magnitud.

`analyze.py` calcula los ciclos recurrentes exactos para cada byte repetido y las ocho fases de bit de entrada, partiendo de cualquiera de los dos bits de modulación precedentes. Guarda resultados JSON/CSV y vectores finitos de cuatro bytes. La base excluye EDC, paridad RS, intercalado LDC, BIS, sincronización y DC. Los resultados de DC fijo insertan indefinidamente un control cada 45 bits de origen; son diagnósticos de sensibilidad, no tramas completas válidas ni salidas equilibradas en DC.

`pattern_search.py` evalúa explícitamente las 131.068 cadenas binarias de longitudes 2 a 16 con ambos bits de modulación previos posibles (262.136 evaluaciones exactas de ciclos). Verifica la invariancia de densidad bajo rotaciones de dos bits de entrada y la elección del bit previo, y agrupa las 14.694 clases de rotación coincidentes. Los períodos impares tienen una órbita; los pares conservan alineaciones par/impar separadas. Los informes de extremos por longitud mantienen patrones equivalentes y repeticiones de otros más cortos: son longitudes de período, no necesariamente períodos fundamentales.

`robust_pattern_search.py` agrupa rotaciones completas de bits y compara ambas fases de pares. Su mejor mínimo robusto a la fase es 1/6 y el máximo 4/9 **dentro de las longitudes exploradas**. No se afirman como límites globales de períodos más largos.

La ocupación del nivel uno NRZI parte del nivel cero; invertirlo complementa la ocupación. Un ciclo de trabajo del 50 % no predice un resultado óptico blanco o negro.

<a id="reachable-channel-density-bounds"></a>
## Límites alcanzables de densidad de canal

En un segmento de datos arbitrariamente largo modulado en 17PP, cada racha mide como máximo 8T y la densidad de transiciones es al menos 1/8. El ciclo de entrada de 16 bits `0000101101110010` (`0B 72` repetido) alcanza ese límite con el ciclo de salida `000100000001000000010000`: tres rachas 8T.

En el extremo contrario, puede haber como máximo seis rachas 2T consecutivas. Al agrupar cada secuencia de hasta seis rachas 2T con la siguiente de al menos 3T, la densidad máxima es 7/15. El ciclo de entrada de 10 bits `0111011110` (empaquetado como `77 9D E7 79 DE` repetido) lo alcanza con `101010101001010`: seis rachas 2T y una 3T. Son ciclos reales producidos por tablas, no meros límites RLL. Ambos extremos tienen ocupación NRZI del 50 % en sus ciclos de nivel completos.

Los límites son para segmentos de datos largos. La sincronización de trama tiene rachas especiales 9T fuera de las reglas 17PP ordinarias; la terminación finita y el resto de la estructura física se cuentan por separado.

| Patrón de entrada | Empaquetado en esta fase | Densidad | Rachas del ciclo de salida | Fase impar del bit de entrada |
|---|---|---:|---|---:|
| `0001` | `11` repetido | 1/6 = 16.6667% | 6T | 1/6 |
| `00111101` | `3D` repetido | 5/12 = 41.6667% | tres 2T + dos 3T | 5/12 |
| `001111` | `3C F3 CF` repetido | 4/9 = 44.4444% | tres 2T + una 3T | 4/9 |
| `0000101101110010` | `0B 72` repetido | 1/8 = 12.5% | tres 8T | 7/24 |
| `0111011110` | `77 9D E7 79 DE` repetido | 7/15 = 46.6667% | seis 2T + una 3T | 1/3 |

El candidato alto de seis bits tiene ciclo de trabajo NRZI sin equilibrar de 4/9 o 5/9 según la polaridad; el equilibrio DC lo modifica. Su robustez a la fase de pares lo hace un candidato útil más allá de bytes repetidos. Insertar DC a ciegas en los dos extremos destruye su alineación favorable.

<a id="maintaining-patterns-through-the-physical-layout"></a>
## Conservar patrones a través de la distribución física

Una correspondencia más fina elige cada bit de usuario controlable por su posición física final. Para el índice de bit de trama previo a DC `j`, el número de posiciones DC insertadas antes es `0` si `j < 25`, y `1 + floor((j - 25) / 45)` en caso contrario. Elige `P[(j + dcBefore(j) + phase) mod period]` reservando posiciones DC, en vez de dejar que desplacen el patrón. El valor DC real sigue siendo decisión de la unidad y altera códigos vecinos. Restablecer la fase en un bloque DC es otra estrategia que merece comparación experimental.

Construye la secuencia en orden de bytes de trama/ECC y usa la inversa de la permutación LDC existente para colocar los bytes de usuario controlables en los datos útiles posteriores a la aleatorización. Aplica la máscara de aleatorización correspondiente para recuperar los bytes del host. Un patrón repetido en orden del host o de sectores de origen normalmente no mantiene la periodicidad tras el intercalado. EDC, paridad LDC, BIS, sincronización y DC no son bits de datos útiles configurables independientemente: inclúyelos en medidas de toda la cadena y no consideres controlable todo bit de trama.

Ni la densidad ni el ciclo de trabajo NRZI asignan la polaridad final blanco/negro. La reflectancia y visibilidad de parches grabados siguen requiriendo mediciones empíricas.
