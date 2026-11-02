Traducción al español · [Original](VALIDATION.md)

<a id="验证说明"></a>
# Notas de validación

Esta página enumera las comprobaciones reproducibles del código fuente actual. Las pruebas de regresión automatizadas validan los algoritmos, la distribución de archivos y los flujos de trabajo de la aplicación; el resultado de la grabación física sigue dependiendo del grabador, el firmware y el disco.

<a id="本地验证"></a>
## Validación local

Última regresión de arquitectura completa: 2026-10-28, con cobertura de las correcciones de interfaz, las optimizaciones de generación de calibración y el preajuste Verbatim BD-R (64099) (commits correspondientes: `3088802`, `49a0e4d` y `46e2c53`), en Windows con .NET SDK 10.0.400 (runtime 10.0.11), configuración Release. Se superaron la compilación de toda la solución (0 advertencias, 0 errores) y la regresión de arquitectura completa; el punto de entrada de las pruebas ejecuta directamente el arnés EXE propio, sin utilizar `dotnet test`.

Después de terminar la grabación de calibración se completaron la compilación de la optimización de generación, la regresión de arquitectura completa y la autoprueba del motor DVD. Tras añadir el preajuste Verbatim, se superaron de nuevo la compilación y la regresión de arquitectura completa, verificando la coincidencia de radios, número de sectores y polaridad entre GUI y CLI, así como la combinación de preajustes y las sobrescrituras de línea de comandos. Se superaron la validación de versión/registro de cambios de v1.3.3 y las 45 comprobaciones de las herramientas de publicación; no se añadieron mediciones de rendimiento ni conclusiones de grabación en disco físico.

Las nuevas regresiones superadas incluyen: activación y desactivación del paralelismo de calibración BD, funciones de muestreo secuenciales por defecto, polaridad e intervalo de dibujo, orden de lotes y cancelación; coincidencia bit a bit de las coordenadas del muestreo de cuatro puntos de calibración CD, entrelazado y orden de bytes, y coincidencia de bytes entre la implementación escalar y SIMD en las zonas interior y exterior del modo de archivos CD; coincidencia de coordenadas, índices y salida byte a byte entre el muestreo analítico y el de palabras de código de DVD, así como la coherencia del último lote paralelo.

Ejecutar desde la raíz del repositorio:

```powershell
dotnet restore DiscImageStudio.slnx
dotnet build DiscImageStudio.slnx --configuration Release --no-restore
dotnet run --project tests/DiscImageStudio.ArchitectureTests --configuration Release --no-build
dotnet run --project src/DvdImageSolver --configuration Release --no-build -- selftest
dotnet run --project src/DiscImageStudio.App --configuration Release --no-build -- selftest
./tests/Test-ReleaseTooling.ps1
```

La regresión de arquitectura abarca:

- CD/DVD: asignación de blanco y negro, tramado, gamma sRGB, coincidencia entre archivo y flujo, y coincidencia entre el muestreo SIMD y el escalar.
- Modo de archivos CD: lectura independiente de archivos con nombres chinos en ISO9660/Joliet, posiciones interior y exterior y guardado del proyecto, vista previa de la zona de archivos, geometría de sectores sin procesar y coincidencia entre salida a archivo y flujo de avance secuencial.
- DAO de datos CD: EDC Mode 1 y todas las ecuaciones de comprobación P/Q, orden de bytes del canal principal, direcciones del lead-in y del pregap, identificación Q de pista de datos y CRC, Seek hacia delante y lecturas cortas, y reconstrucción completa de ISO con archivos interiores y exteriores; conserva los 359,845 sectores del preajuste Ritek y utiliza un grabador simulado para validar los límites de capacidad compartidos con las pistas de audio. Las pruebas nativas de IMAPI sólo construyen imágenes en memoria y no crean un dispositivo de grabación.
- BD: 17PP/EDC/Reed-Solomon, asignación 3T/6T y polaridad, puesta a cero del intervalo de dibujo, UDF 2.01, archivos mayores de 4 GiB, ida y vuelta de archivos auxiliares y rechazo de límites no válidos.
- Posición de archivos DVD/BD: compatibilidad predeterminada de la zona interior, análisis independiente de directorios ISO9660/Joliet y UDF exteriores, contenido de archivos chinos, coordenadas del disco completo, coincidencia entre flujos sin Seek y salida a archivo, protecciones de capacidad y cancelación, e ida y vuelta de la opción de posición en el proyecto.
- Modelo de tres estados: ida y vuelta de proyectos schema 2 y actualización desde schema 1, combinación de preajustes schema 4 con esquemas anteriores y operaciones apply/capture de BD en `ProjectSession`.
- Grabación: exclusión mutua de operaciones, progreso, escritura secuencial, tiempos de espera, cancelación y liberación de recursos, siempre con interfaces simuladas en lugar de grabadores reales.
- Visualización de grabación por flujo: el progreso sólo avanza con el volumen de escritura confirmado por el dispositivo, conversión a multiplicadores de velocidad para pistas de audio/datos CD, DVD y BD, tasas desconocidas o no válidas, caducidad de 3 segundos, finalización del disco y limpieza al terminar, limitación de frecuencia de los registros de transferencia y protección frente a eventos tardíos.
- Origen de grabación de calibración: coincidencia entre etiquetas CD/DVD/BD y parámetros guardados, longitud de bit, polaridad e intervalo de dibujo BD, cambio entre tipos de disco, consulta de velocidad, solicitudes de grabación simuladas y textos de confirmación.
- Calibración: paso de pista cúbico, intervalo de dibujo y polaridad BD, corrección de fotografía, ajuste de curva, guardado y restauración de sesiones, sincronización de preajustes de tipo de disco y precisión de parámetros.
- Vista previa y ventanas: proyección y relectura CD/DVD/BD, límites de múltiples monitores y de la barra de tareas.
- Vista previa de la zona de archivos: resaltado amarillo de las proyecciones de diseño interiores y exteriores y de la relectura del resultado con archivo auxiliar, límites del paso de pista cúbico DVD, ausencia de resaltado amarillo en dibujos puros o con información de distribución no válida, avisos de directorio/capacidad incorrectos y limpieza al cambiar o restablecer. La superposición amarilla no modifica los datos ISO.

La GUI permite generar capturas fuera de pantalla con los comandos existentes:

```powershell
dotnet run --project src/DiscImageStudio.App --configuration Release --no-build -- ui-snapshot --output artifacts/validation/home.png --tab 0
dotnet run --project src/DiscImageStudio.App --configuration Release --no-build -- ui-snapshot --output artifacts/validation/export-bd.png --tab 3 --project sample.discproj
dotnet run --project src/DiscImageStudio.App --configuration Release --no-build -- ui-snapshot --output artifacts/validation/preview-bd.png --tab 2 --project sample.discproj --live-input image.png --live-disc bd --live-mode calibrate
dotnet run --project src/DiscImageStudio.App --configuration Release --no-build -- ui-snapshot --output artifacts/validation/burn-bd.png --tab 4 --project sample.discproj --burn-source calibration --burn-progress write
```

`--burn-progress` acepta `write`, `buffer` y `finalize`, y genera la interfaz con progreso simulado, sin iniciar una grabación real. El 2026-10-28 se comprobaron la calibración BD en pantalla ancha y en una ventana estrecha de 800×650, y los estados de preparación y finalización; el volumen escrito, la tasa, el multiplicador de velocidad y la barra de progreso inferiores permanecieron visibles. En esa ronda también se superó la autoprueba del motor DVD y no se realizaron grabaciones en disco físico.

El flujo `build` ejecuta además la autoprueba DVD del directorio de módulos, una comprobación de salida CD de dos sectores y una captura de la página de inicio. Las comprobaciones adicionales del EXE publicado de un solo archivo se explican en la [guía de publicación](RELEASING.es.md).

En esa ronda también se revisaron las proyecciones de diseño con ambas posiciones de archivo para DVD/BD, la página de exportación exterior y la relectura del resultado exterior, con un total de 8 capturas fuera de pantalla; se superaron las autopruebas DVD/aplicación y las 45 regresiones de herramientas de publicación.

En el selector de archivos de la página de diseño también se comprobaron 7 estados de interfaz: DVD, BD, CD, sin carpeta seleccionada, 18 capas, ventana estrecha y ventana de poca altura; en pantalla ancha, la lista de capas se desplaza internamente y la zona de selección de archivos permanece accesible, con una compilación de 0 advertencias y 0 errores.

v1.3.0 también superó la validación de un solo archivo autocontenido .NET 10 de `Build-Release.ps1`: en un directorio aislado que sólo contenía el EXE, se superaron la información de compilación de interfaces de grabación, la autoprueba DVD, la generación RAW/WAV/CUE de dos sectores, la desactivación de CUE y la representación de la interfaz WPF. El resultado incluye .NET / Windows Desktop 10.0.11 y conserva sus avisos completos de licencia originales.

v1.3.2 superó la regresión de arquitectura completa, las autopruebas del motor DVD y de los módulos de la aplicación y las 45 comprobaciones de las herramientas de publicación. Las nuevas regresiones validan la coincidencia de polaridad entre «Guardar como» y la confirmación del proyecto tras cambiar de preajuste Blu-ray, así como la selección entre tipos de disco y la sincronización de geometría después de eliminar el último preajuste; el zoom del lienzo cubre los límites 25%–1200%, los botones de aumento y reducción y el restablecimiento de ajuste a ventana. También se revisaron 4 capturas fuera de pantalla: configuración de proyectos nuevos CD, Blu-ray y DVD y página de diseño de 18 capas.

<a id="实盘验证边界"></a>
## Límites de la validación en discos físicos

Los cambios de posición de archivos y vista previa de esta ronda no se probaron mediante grabación en disco físico. Las pruebas automatizadas no demuestran la estrategia de escritura del dispositivo, el brillo reflejado real ni la tasa de éxito de la grabación; la función Blu-ray sigue siendo experimental.

El DAO de discos de datos CD de v1.3.1 tampoco se ha probado aún con grabación en disco físico; sí superó la regresión de arquitectura completa, el arranque del EXE independiente de un solo archivo y la validación de pistas de audio/CUE, contenido de archivos ISO interiores y exteriores y relectura del resultado.

Las fotografías y los parámetros de paso de pista cúbico DVD ya registrados están en la [comparación de antes y después del README](../README.es.md#calibración-asistida). La auditoría histórica del modelo físico se encuentra en [PHYSICAL_MODEL_AUDIT.md](PHYSICAL_MODEL_AUDIT.es.md); sus conclusiones conservan las fechas y la referencia de código indicadas y no sustituyen la validación del código fuente actual.

Los futuros registros de discos físicos deben incluir la versión o commit, el grabador y su firmware, el modelo de disco, los parámetros del preajuste, la velocidad, el método de generación/grabación y la fotografía original, evitando generalizar el resultado de un dispositivo a todos los discos.
