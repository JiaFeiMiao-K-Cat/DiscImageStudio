<a id="蓝光字节与位周期密度分析"></a>
# Análisis de bytes y densidad de períodos de bit Blu-ray

> Traducción al español · [Original](README.md)

Analiza la cadena de codificación digital de BD-R de una capa para seleccionar patrones de entrada para pruebas físicas de contraste. Los resultados no definen la polaridad blanco/negro, no generan una imagen de disco completa y no graban. Consulta el [informe de análisis](../../docs/BLURAY_BYTE_DENSITY.es.md).

Requiere SDK .NET 10; el análisis independiente de tablas de la norma sólo requiere la biblioteca estándar de Python, y las gráficas necesitan matplotlib / numpy. Desde la raíz del repositorio:

```powershell
dotnet run --project tools/BluRayByteProbe --configuration Release -- artifacts/bluray-byte-density 4
dotnet run --project tools/BluRayByteProbe --configuration Release -- --fine artifacts/bluray-byte-density

python tools/BluRayByteProbe/reference/analyze.py --output-dir artifacts/bluray-byte-density/reference
python tools/BluRayByteProbe/reference/pattern_search.py --output-dir artifacts/bluray-byte-density/reference
python tools/BluRayByteProbe/reference/robust_pattern_search.py --output-dir artifacts/bluray-byte-density/reference

dotnet run --project tools/BluRayByteProbe --configuration Release -- --verify-reference artifacts/bluray-byte-density/reference/independent-finite-vectors.json
dotnet run --project tools/BluRayByteProbe --configuration Release -- --verify-patterns artifacts/bluray-byte-density/reference/independent-pattern-vectors.json
python tools/BluRayByteProbe/plot_results.py artifacts/bluray-byte-density
```

El comando predeterminado explora los 256 bytes Q repetidos: 17PP puro, cuatro estrategias DC de clúster completo, cuatro PSN candidatos, control de host sin compensar la aleatorización, vecinos aleatorios y patrones mixtos. El segundo argumento es el paralelismo (1–16). `--fine` explora todas las fases de cinco períodos y cuatro distribuciones, y vuelve a comprobar distintas direcciones; limita el paralelismo a 4.

Q son los **datos de usuario objetivo después de aleatorizar**. Los datos útiles reales del host son `Q XOR PSN mask`. Toda salida se escribe en el directorio indicado y puede sobrescribir archivos de análisis anteriores con el mismo nombre.

Salidas principales:

| Archivo | Contenido |
|---|---|
| `all-bytes.csv` / `.json` / `.md` | Los 256 bytes: densidad, sensibilidad DC y distribución de longitudes de racha |
| `all-bytes-density.png` | Mapa de densidad de 16 × 16 |
| `fine-patterns.json` / `.csv` | 176 combinaciones de cadenas de bits, distribución y fase, y comprobación por dirección |
| `fine-pattern-comparison.png` | Comparación de la base por bytes con el control a nivel de bit |
| `recommended-patterns.csv` | Parámetros de construcción y densidad de tres cadenas recomendadas |
| `context.json` / `periodic-context.json` | Tramas sintéticas sin ECC, vecinos y pruebas de límites DC |
| `reference/` | Tablas independientes de la norma, ciclos exactos y búsquedas |
| `method.json` / `validation.json` / `fine-validation.json` | Criterios estadísticos y registros de validación |

El denominador de estadísticas del clúster completo es `496 × 1932 = 958272` bits de canal; incluye sincronización, EDC, LDC/BIS, DC y 17PP. Excluye run-in/out/Guard3, pero conserva el nivel inicial y DSV de run-in. El DC automático usa el algoritmo greedy de **diagnóstico** del proyecto, con bits futuros puestos temporalmente a cero; no garantiza coincidir con las decisiones DC de una grabadora. DC todo ceros, todo unos o aleatorio sólo compara sensibilidad, no son configuraciones de grabación reales.

`FinePatternAnalysis` calcula la posición física ECC de cada bit controlable de datos útiles y coloca allí el valor periódico. Comprueba byte a byte los 65.536 bytes Q realmente codificados frente al objetivo. EDC, BIS, paridad y DC se generan en la cadena; no se consideran bits de usuario escribibles directamente.
