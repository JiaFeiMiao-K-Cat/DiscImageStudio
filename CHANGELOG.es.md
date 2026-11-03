Traducción al español · [Original](CHANGELOG.md)

Se conservan y traducen por separado los bloques originales en chino e inglés, aunque describan cambios equivalentes. Sus subtítulos indican el idioma de origen; las fechas y las comprobaciones registradas corresponden al historial original.

<a id="changelog"></a>
# Registro de cambios

## Unreleased

<a id="中文"></a>
### Chino (traducción)

- Se añadió una interfaz en español: seleccionar «Español» en «Configuración → General → Idioma», guardar y reiniciar; los sistemas en español también pueden seguir el idioma del sistema. Las 734 cadenas de la aplicación incluyen traducciones `es`, conservando los argumentos de formato, la configuración regional numérica y las reglas de reserva existentes.
- Se añadieron copias completas de la documentación en español y un acceso de idioma en los README; los documentos originales, las atribuciones y las licencias se conservan sin cambios. Se añadieron regresiones de configuración de idioma y comprobaciones de capturas de la interfaz Windows en español. El README español incluye capturas reales de Windows en español.

<a id="english"></a>
### Inglés (traducción)

- Se añadió una interfaz en español. Seleccionar Español en Configuración → General → Idioma, guardar y reiniciar; los sistemas configurados en español también pueden seguir el idioma del sistema. Las 734 cadenas de la aplicación incluyen traducciones `es`, conservando los argumentos de formato, la configuración regional numérica y las reglas de reserva existentes.
- Se añadieron versiones en español de la documentación mantenida y enlaces al español en el selector de idioma de todos los README. Se conservan los documentos originales, las atribuciones y las licencias. Se ampliaron las regresiones de configuración de idioma y las comprobaciones de capturas de la interfaz Windows en español. El README español incluye capturas reales de Windows en español.


## [1.3.6] - 2026-11-02

<a id="中文-1"></a>
### Chino (traducción)

- **Función experimental: versión de línea de comandos sin conexión para Linux y macOS.** Permite generar desde una imagen pistas de audio CD (WAV+CUE o RAW), discos de datos CD e ISO de DVD y Blu-ray. Ambos sistemas ofrecen paquetes autocontenidos x64 y ARM64. Tras extraerlos, ejecutar `./disc-image-studio --help` para ver los comandos.
- La versión de escritorio Windows sigue leyendo imágenes con WIC. La CLI multiplataforma utiliza StbImageSharp para leer imágenes y StbImageWriteSharp para escribir PNG, reutilizando los mismos algoritmos de codificación del disco.
- Se añadió una interfaz en japonés: se puede seleccionar «日本語» en «Configuración → General → Idioma»; cuando el idioma del sistema es japonés (`ja`, `ja-JP`), seguir el sistema también utiliza japonés. Todos los recursos de traducción incorporan texto `ja`, y `ui-snapshot --language` admite `ja`.
- Se añadió un README en japonés (`README.ja.md`) y un enlace 日本語 al selector de idioma de la parte superior de todos los README.

<a id="english-1"></a>
### Inglés (traducción)

- **Función experimental: CLI sin conexión para Linux y macOS.** Genera desde una imagen pistas de audio CD (WAV+CUE o RAW), imágenes de datos CD e ISO de DVD o Blu-ray. Ambos sistemas disponen de archivos comprimidos autocontenidos x64 y ARM64. Extraer el archivo y ejecutar `./disc-image-studio --help` para ver los comandos.
- La aplicación de escritorio Windows sigue decodificando imágenes con WIC. La CLI multiplataforma utiliza StbImageSharp para leer imágenes y StbImageWriteSharp para escribir PNG, con los mismos algoritmos de codificación del disco.
- Se añadió una interfaz en japonés. Seleccionar 日本語 en Configuración → General → Idioma; los sistemas configurados en japonés (`ja`, `ja-JP`) ahora utilizan japonés al seguir el sistema. Todas las entradas de traducción incorporan texto `ja`, y `ui-snapshot --language` acepta `ja`.
- Se añadió un README en japonés (`README.ja.md`) y un enlace 日本語 al selector de idioma de la parte superior de todos los README.

## [1.3.4] - 2026-11-02

<a id="中文-2"></a>
### Chino (traducción)

- Se añadieron interfaces en chino simplificado, chino tradicional e inglés; en «Configuración → General → Idioma» se puede seguir el sistema o elegir un idioma. El cambio se aplica tras guardar y reiniciar; cancelar no modifica las preferencias. Los sistemas configurados para Taiwán, Hong Kong, Macao o `zh-Hant` utilizan chino tradicional; los demás sistemas chinos utilizan chino simplificado y el resto, inglés. La navegación, los espacios de trabajo, los diálogos y los avisos de estado comparten recursos de traducción integrados; los datos de proyecto y los formatos numéricos no cambian. Los diagnósticos de los módulos inferiores y del sistema operativo conservan su idioma original.
- El idioma y el tema siguen el sistema de forma predeterminada; las preferencias ausentes o no válidas vuelven a seguir el sistema, mientras que se conservan las elecciones válidas guardadas. Los ajustes se guardan en `%LOCALAPPDATA%\DiscImageStudio\settings.json`; los archivos de configuración dañados o de una versión superior utilizan los valores predeterminados, y no se escriben en `.discproj`.
- Se añadió un botón permanente de «engranaje + Configuración» a la derecha de la barra de título, para abrir los ajustes directamente desde el inicio y todos los espacios de trabajo, conservando el atajo Ctrl+,. Los nombres de proyecto largos se abrevian automáticamente; el menú «…» ofrece «Acerca de…», y se eliminaron la entrada duplicada de configuración y el antiguo acceso «Acerca de» de la barra lateral.
- Se añadieron las categorías de configuración «General» y «Almacenamiento». Los temas oscuro, claro y del sistema se aplican a todas las ventanas inmediatamente después de guardar, actualizando a la vez la barra lateral, los controles segmentados, el lienzo del disco y el panel de tareas.
- Se añadieron ajustes de carpeta predeterminada de proyectos y de caché; si se dejan vacíos, utilizan respectivamente las carpetas de documentos y temporal del sistema. Se puede ver el espacio de caché utilizado, vaciar la caché de la aplicación o abrir la carpeta de datos de la aplicación; el vaciado sólo elimina las imágenes en caché y los archivos auxiliares de la lista permitida, sin afectar a proyectos ni archivos exportados, y se desactiva durante las tareas.
- El límite de resolución de vista previa de CD, DVD y Blu-ray se unificó en 8192×8192 para las proyecciones de diseño, la relectura del resultado y las imágenes de vista previa exportadas; los tamaños predeterminados de cada tipo de disco no cambian.
- Se añadieron README completos en inglés y chino tradicional, con enlaces de idioma en la parte superior de las tres versiones, y documentación de configuración de idioma y mantenimiento de recursos de traducción.
- El comando de capturas de desarrollo `ui-snapshot` incorporó `--language zh-CN|zh-TW|en|system`, `--theme dark|light|system`, `--settings-dialog` y `--settings-view general|storage` para validar la distribución con distintos idiomas, temas y categorías de ajustes. Las capturas del diálogo de configuración utilizan 760×600 de forma predeterminada y un archivo de ajustes de un solo uso, sin modificar la configuración real.

<a id="english-2"></a>
### Inglés (traducción)

- Se añadieron interfaces en chino simplificado, chino tradicional e inglés. Seleccionar un idioma o seguir el sistema en Configuración → General → Idioma; guardar y reiniciar para aplicarlo, o cancelar para conservar las preferencias. Los sistemas configurados para Taiwán, Hong Kong, Macao o `zh-Hant` utilizan chino tradicional; los demás sistemas chinos utilizan chino simplificado y el resto, inglés. La navegación, los espacios de trabajo, los diálogos y los mensajes de estado comparten recursos de traducción integrados. Los datos del proyecto y el formato numérico no cambian; los diagnósticos devueltos por los módulos inferiores y el sistema operativo conservan su idioma original.
- El idioma y el tema ahora siguen el sistema de forma predeterminada. Las preferencias ausentes o no válidas vuelven al sistema, mientras que se conservan las elecciones válidas ya guardadas. Los ajustes se guardan en `%LOCALAPPDATA%\DiscImageStudio\settings.json`; los archivos dañados o con un esquema más reciente utilizan los valores predeterminados. Los ajustes no se escriben en los archivos `.discproj`.
- Se añadió un botón permanente de engranaje y Configuración a la derecha de la barra de título, disponible en la página de inicio y en todos los espacios de trabajo, conservando el atajo Ctrl+,. Los nombres de proyecto largos se abrevian. El menú «…» ofrece Acerca de; se eliminaron la entrada duplicada Configuración del menú y la antigua entrada Acerca de de la barra lateral.
- Se añadieron las categorías General y Almacenamiento. Los temas oscuro, claro y del sistema se aplican inmediatamente después de guardar a todas las ventanas, incluidas la barra lateral, los controles segmentados, el lienzo del disco y el panel de tareas.
- Se añadieron ajustes de carpeta predeterminada de proyectos y de caché, que utilizan las carpetas Documentos y temporal del sistema, respectivamente, si se dejan vacíos. Se puede ver el espacio de caché utilizado, vaciar la caché o abrir la carpeta de datos de la aplicación. El vaciado sólo elimina las imágenes en caché y los archivos auxiliares de la lista permitida, conserva los proyectos y archivos exportados y se desactiva mientras se ejecuta una tarea.
- Se unificó la resolución máxima de vista previa de CD, DVD y Blu-ray en 8192×8192 para proyecciones de diseño, relectura de resultados y vistas previas exportadas. Los tamaños de vista previa predeterminados de cada medio no cambian.
- Se añadieron README completos en inglés y chino tradicional, con enlaces de idioma en la parte superior de las tres versiones, y documentación de configuración de idioma y mantenimiento de recursos de traducción.
- Se amplió el comando de desarrollo `ui-snapshot` con `--language zh-CN|zh-TW|en|system`, `--theme dark|light|system`, `--settings-dialog` y `--settings-view general|storage` para verificar las distribuciones con distintos idiomas, temas y categorías de ajustes. Las capturas del diálogo de configuración utilizan 760×600 de forma predeterminada y un archivo de ajustes temporal, sin modificar las preferencias reales.

## [1.3.3] - 2026-10-28

- Se añadió el preajuste integrado Verbatim BD-R (64099), con los radios, el número de sectores y la polaridad de asignación medidos y guardados por el usuario, disponible tanto en la GUI como en la línea de comandos.
- Se corrigió el resumen de origen de la grabación por flujo de calibración Blu-ray, que mostraba CD por error, y se muestran la longitud de bit Blu-ray, la polaridad 3T/6T y el intervalo de dibujo según el registro de calibración.
- La parte inferior de la grabación por flujo muestra de forma permanente el volumen escrito por el dispositivo/total, la tasa en tiempo real y el multiplicador de velocidad correspondiente; la preparación, transferencia, finalización y los diagnósticos se registran. El volumen transferido ya no sobrescribe el progreso real de escritura, y la tasa anterior se borra durante la finalización o tras un período prolongado sin nuevos datos escritos.
- La asignación del intervalo de calibración BD se integró en los lotes paralelos acotados del generador normal de imágenes y reutiliza el muestreo geométrico de cuatro puntos; conserva la función de calibración original, el intervalo de dibujo, la polaridad y la salida secuencial. Las asignaciones personalizadas siguen teniendo que permitir el paralelismo expresamente.
- La calibración CD reutiliza el muestreo geométrico de cuatro puntos de la generación de imágenes; la calibración DVD y las funciones de palabras de código reutilizan la ruta SIMD existente de paso de pista variable. Se conservan la trigonometría escalar punto a punto, el orden de llamadas y el entrelazado de la pista; el paso constante DVD sigue utilizando la alternativa escalar existente.
- Se completó el muestreo de cuatro puntos del modo de archivos CD, calculando la posición física independiente de cada byte después del intercambio y la asignación CIRC, y conservando la zona de archivos, los índices de paleta, la aleatorización y el tratamiento del límite final del disco.

## [1.3.2] - 2026-10-27

- El diálogo de configuración del proyecto permite cambiar directamente a otros preajustes del tipo de disco actual; antes de cambiar se confirma la sustitución, y cancelar conserva los parámetros originales y la selección.
- El azul del tema se unificó en `#3385FF` para oscuro y `#74C0FF` para el acento claro; la opción seleccionada de los selectores deslizantes utiliza el color del tema, su altura se alinea con los campos de entrada y la flecha desplegable a la derecha de los selectores se redujo a un tamaño compacto.
- En Nuevo proyecto y Configuración del proyecto se pueden eliminar los preajustes «Personalizado» con el botón derecho; la eliminación también limpia el registro de plantillas recientes.
- La barra de zoom del lienzo de diseño permanece en la esquina inferior derecha; se añadieron botones «−» y «+» y se amplió el intervalo a 25%–1200% (antes sólo se podía ampliar); «Ajustar a ventana» utiliza un icono de actualización. Se mantiene el zoom con Ctrl/Alt+rueda y el desplazamiento arrastrando con el botón central.
- Las descripciones grises de las tarjetas de plantilla del inicio muestran el tipo de disco («CD», «DVD», «Blu-ray»).
- Se corrigieron la respuesta incorrecta del diálogo de confirmación de cambio de preajuste a teclas normales, la conservación de la polaridad anterior al guardar como preajuste tras cambiar de Blu-ray y la falta de coincidencia entre el tipo de disco y la siguiente selección después de eliminar el último preajuste.

## [1.3.1] - 2026-10-26

- Se añadió un modo de archivos del disco para CD: sin carpeta seleccionada conserva la pista WAV; al seleccionar una carpeta genera un disco de datos ISO9660/Joliet Mode 1. Los archivos se pueden situar en la zona interior o exterior, y el guardado del proyecto, la exportación ISO y la grabación por flujo comparten la distribución.
- La proyección de diseño CD y la relectura del resultado con información de distribución muestran en amarillo la zona de archivos, conservando las coordenadas del disco completo; se admiten directorios y archivos chinos y el contenido del disco conserva sus bytes originales.
- La grabación por flujo de discos de datos CD pasó a compartir con las pistas de audio la sesión DAO y el tratamiento de los límites de capacidad, corrigiendo el aviso erróneo de «Capacidad restante insuficiente» del preajuste Ritek Medical Aqua; se conservan los 359,845 sectores calibrados por ATIP y los límites finales existentes.
- Se añadieron regresiones de códigos de comprobación Mode 1, identificación de pistas de datos DAO, flujos de avance secuencial y reconstrucción completa de ISO interiores y exteriores. El DAO de discos de datos CD ha superado la validación por software y de imágenes nativas en memoria, pero aún no la validación de grabación en disco físico; la grabación al vuelo sigue siendo experimental.

## [1.3.0] - 2026-10-26

- Toda la aplicación, los módulos de disco y las herramientas se actualizaron a .NET 10; la CI y el empaquetado EXE y MSIX utilizan .NET 10 SDK. El EXE autocontenido publicado sigue sin requerir una instalación adicional del runtime.
- Se añadió la zona «Archivos del disco» a la página de diseño DVD/Blu-ray para elegir directamente una carpeta y su posición interior/exterior, compartiendo ajustes con la página de exportación.
- El modo mixto de archivos y patrón de DVD y Blu-ray incorporó «Posición de archivos: interior/exterior», con interior como valor predeterminado; el guardado del proyecto, la exportación ISO y la grabación por flujo utilizan la misma distribución. El modo exterior conserva la información necesaria del sistema de archivos y el intervalo de dibujo evita automáticamente la zona de archivos.
- Las proyecciones de diseño DVD/Blu-ray y la relectura del resultado con información de distribución muestran en amarillo la zona de archivos (incluido el sistema de archivos) y una leyenda; el color sólo se utiliza en la vista previa. Las imágenes externas sin información de distribución asociada no presuponen el espacio ocupado por archivos.

## [1.2.0] - 2026-10-26

<a id="工程与设计"></a>
### Proyecto y diseño

- Se reorganizó la aplicación en cinco espacios de trabajo: diseño, vista previa, exportación, grabación y calibración asistida; CD, DVD y BD comparten el proyecto actual y los parámetros del disco.
- Se añadieron el guardado y apertura de proyectos `.discproj`, miniaturas de proyectos recientes, comprobación de cambios sin guardar y configuración del proyecto; los parámetros actuales del disco se pueden guardar como preajuste de usuario.
- Se añadió un lienzo de diseño multicapa que permite importar varias imágenes, moverlas, escalarlas, girarlas y ajustar su orden y visibilidad; la exportación y la grabación utilizan la misma composición de alta resolución. Se eliminó el acceso anterior a la duplicación circular automática y la distribución pasa a gestionarse desde el lienzo.
- Se añadió un panel de tareas que reúne los registros de ejecución, el progreso, la cancelación y la ubicación de salida; las páginas de exportación y grabación muestran de forma uniforme el origen del proyecto actual.

<a id="影调蓝光与标定"></a>
### Tono, Blu-ray y calibración

- El tono se centralizó en la página de diseño, con intensidad de tramado 0–100%, ajuste de brillo, linealización sRGB y escala de grises CD de dos/cuatro niveles. Los proyectos nuevos utilizan un tramado del 75% de forma predeterminada; los proyectos anteriores sin ajustes tonales conservan la asignación binaria original.
- Se añadieron parámetros tonales opcionales a los comandos de producción CD, DVD y BD; CD/DVD utilizan tramado determinista de bytes lógicos, mientras que BD procesa a la resolución de la imagen de origen. La generación, la grabación por flujo y la proyección del diseño utilizan ajustes coherentes.
- Las ISO BD incorporaron metadatos JSON asociados que registran la geometría del disco, el intervalo de dibujo, la polaridad de asignación, el tono y las marcas de tiempo UDF; al abrir la ISO en la vista previa se pueden restaurar automáticamente los parámetros correspondientes.
- Se mejoró el ajuste de calibración asistida: el intervalo de búsqueda se amplía automáticamente y Blu-ray sigue ampliándolo si el residuo continúa siendo elevado; se añadieron desviación máxima, marcas de puntos atípicos y umbrales de error para evitar aplicar resultados con desviación excesiva.

<a id="兼容性与迁移"></a>
### Compatibilidad y migración

- Los proyectos y preajustes de usuario admiten CD/DVD/BD de forma uniforme; se conserva la compatibilidad de lectura de proyectos schema 1, preajustes schema 2/3 y pistas CD existentes. Los comandos de producción conservan los parámetros existentes y todos los nuevos parámetros tonales son opcionales.
- La numeración de páginas del comando de capturas de desarrollo `ui-snapshot` es ahora: 0 inicio, 1 diseño, 2 vista previa, 3 exportación, 4 grabación y 5 calibración; la ventana Acerca de utiliza `--about-dialog` y se eliminó `--live-processing`.
- Blu-ray y la grabación directa siguen siendo experimentales; el alcance de las pruebas de hardware se explica en `docs/VALIDATION.md`, sin equiparar las pruebas automatizadas a nuevas validaciones en disco físico.

## [1.1.0] - 2026-10-25

<a id="新增"></a>
### Novedades

- Modo experimental BD-R de una capa y 25 GB: distribución de imagen de origen, asignación 3T/6T e inversión, parámetros y preajustes de disco, vista previa en tiempo real, generación ISO y calibración asistida, disponibles en la GUI y la línea de comandos. Por ahora no se admite doble capa y el brillo de los discos físicos sigue pendiente de validación.
- Los archivos de la zona interior Blu-ray admiten UDF 2.01 y archivos individuales mayores de 4 GiB; la generación y la grabación por flujo comparten la geometría del disco completo, la distribución segmentada de archivos y el flujo de datos acotado.
- Grabación secuencial sin zona de reserva en BD-R nuevos sin formatear, conservando los 12,219,392 sectores completos. Antes de grabar se validan la capacidad, el estado en blanco y la dirección inicial; no se reduce automáticamente la capacidad ni se reescribe automáticamente tras un fallo, y se añaden tiempos de espera por bloque y diagnósticos de limpieza.
- La creación y la vista previa en tiempo real de CD, DVD y BD permiten activar tramado en blanco y negro y corrección gamma sRGB independiente, desactivados por defecto. La generación de archivos, la grabación por flujo y la vista previa de origen utilizan opciones de procesamiento coherentes.
- La calibración asistida admite corrección de perspectiva fotográfica, ajuste de curvas mediante puntos, instantáneas de parámetros y aplicación de éstos; DVD utiliza un modelo de paso cúbico e incluye el preajuste validado Verbatim DVD-R AZO (43533), con fotografías y registros de antes y después de la calibración.
- Se añadieron una herramienta independiente de tramado en blanco y negro, una herramienta experimental de escala de grises DVD y una herramienta de análisis de bytes Blu-ray, con instrucciones de uso y pasos de validación reproducibles.

<a id="改进与修复"></a>
### Mejoras y correcciones

- La vista previa en tiempo real admite proyecciones de origen CD/DVD/BD y relectura de resultados existentes, resolución configurable, ajuste a ventana, píxeles 1:1, zoom con ratón y arrastre; se corrigieron la sincronización de preajustes, los comandos de exploración y las posiciones de muestreo de relectura DVD.
- CD/DVD utilizan una asignación uniforme de blanco y negro de dos niveles; la entrada en blanco y negro conserva los píxeles discretos y vuelve a aplicar tramado después de escalar la duplicación circular. La relectura de resultados antiguos de varios niveles sigue reconociendo sus grises originales.
- El muestreo CD y DVD incorporó SIMD y una alternativa para los límites, mientras que Blu-ray utiliza generación paralela acotada y SIMD entero; las pruebas de regresión validan la coincidencia de las salidas escalar, paralela y por flujo.
- La página de grabación muestra una tasa calculada con los datos escritos confirmados por el dispositivo y serializa las consultas al dispositivo y las grabaciones; durante la preparación y la finalización no se muestran tasas obsoletas. La grabación real sigue siendo experimental.
- Se corrigieron los límites de maximización con varios monitores, distintos DPI y posiciones de la barra de tareas, y se unificó la precisión decimal de los parámetros; el inicio y las tareas recientes admiten Blu-ray y la salida WAV de CD permite elegir si se genera CUE.
- Los registros de calibración utilizan schema 6 y no aceptan registros de patrones anteriores; la compatibilidad de preajustes y pistas/imágenes existentes se explica en la documentación correspondiente. Todas las conclusiones sobre geometría y brillo reales siguen requiriendo validación con el dispositivo y medio concretos.

<a id="开源与文档"></a>
### Código abierto y documentación

- Se adoptó GPL-3.0-or-later para mantener la compatibilidad con la implementación de entrelazado CD derivada de CDImage; se completaron el copyright y los agradecimientos del autor original.
- Se conservan las licencias completas de los componentes de terceros y la fuente HarmonyOS Sans; pueden consultarse sin conexión en «Acerca de», y el EXE de un solo archivo también incluye los textos de licencia.
- El README incorporó normas como ECMA-267, ECMA-359 e ISO/IEC 30190:2021 y sus enlaces oficiales; se organizaron los accesos a herramientas, los documentos de validación, las reglas para ignorar la caché y los ejemplos de rutas personales.

## [1.0.26] - 2026-09-12

- Primera GitHub Release: EXE de un solo archivo Windows x64 con .NET incluido, listo para ejecutarse tras la descarga.
- Incluye generación de pistas CD WAV + CUE / RAW, ISO DVD de dibujo y discos mixtos de datos, además de GUI, línea de comandos, vista previa en tiempo real, duplicación circular y preajustes de disco editables.
- Incluye la corrección del orden de bytes de WAV CD y pistas por flujo, conservando la compatibilidad con RAW anteriores; los comentarios de un usuario tras una prueba física confirmaron la desaparición de la imagen doble después de la corrección.
- Incluye grabación directa experimental de CD/DVD. Ya se dispone de algunos resultados de discos físicos, pero la cobertura de validación entre distintos dispositivos, discos y métodos de grabación sigue siendo limitada.
- GitHub Actions compila el programa publicado y ejecuta regresiones de módulos, la autoprueba DVD y comprobaciones de generación RAW/WAV y capturas GUI del archivo único final; las notas de publicación ofrecen sumas SHA-256 para verificar las descargas.
- Se unificó el origen de versión; los paquetes sólo se generan después de modificar el número de versión y el registro de cambios correspondiente. Se admiten versiones preliminares y reintentos manuales, manteniendo un proceso independiente de empaquetado MSIX.

<a id="首次-github-发布前的开发记录"></a>
## Registro de desarrollo anterior a la primera publicación en GitHub

A continuación se conserva el registro acumulado del desarrollo; el estado de validación refleja la etapa de cada entrada. El alcance de validación actual se define en `docs/VALIDATION.md`.

- Se añadió una estrategia de publicación GitHub Actions: `Directory.Build.props` mantiene la versión de forma centralizada y sólo cuando los cambios integrados en `main` modifican tanto la versión como el registro de cambios y cambia la versión completa, tras la CI y la validación final del archivo único, se generan automáticamente la etiqueta de versión, el EXE autocontenido Windows x64 y el borrador de GitHub Release. Se admiten alpha/beta/rc, las notas incluyen SHA-256, los responsables lo hacen público tras revisarlo y los artefactos publicados no se sobrescriben. MSIX conserva el canal manual y reutiliza la CI, leyendo por defecto la misma configuración de versión.
- La salida CD a archivo pasó a WAV estándar de forma predeterminada (44.1 kHz, 16 bits, PCM de dos canales), generando también un CUE `FILE ... WAVE`: cdrecord puede leer directamente el WAV e ImgBurn abre el CUE asociado; los `.raw` anteriores mantienen el orden big-endian. WAV se convierte a little-endian después del entrelazado con retardo y la grabación por flujo CD utiliza explícitamente el mismo PCM little-endian sin cabecera, corrigiendo la discrepancia con la convención de entrada de audio IMAPI. La vista previa de pistas admite análisis de bloques RIFF de WAV y normalización de orden de bytes. Se añadieron regresiones de muestras de audio asimétricas y conservación de bytes de imágenes RAW de Windows.
- La grabación por flujo DVD incorporó compatibilidad con carpetas mixtas: calcula primero la distribución ISO9660/Joliet y después escribe secuencialmente archivos interiores, relleno alineado a ECC y patrón exterior mediante un flujo de avance sin retroceso, sin guardar una ISO temporal completa.
- La imagen intermedia de la generación definitiva de duplicación circular pasó de 2048×2048 a 8192×8192; el guardado de vista previa utiliza 4096×4096 y la vista previa en tiempo real, 2048×2048. El registro de ejecución incorporó el tamaño real en píxeles de cada copia para evitar perder detalles por imágenes intermedias de baja resolución.
- Se añadieron preajustes de disco: CD de 80/74 minutos, DVD de una capa de 12 cm y Mini DVD de una capa de 8 cm; las páginas de generación y vista previa en tiempo real se sincronizan en ambas direcciones y cambian automáticamente a «Parámetros personalizados» al modificar valores manualmente.
- Se añadió el preajuste CD «Ritek Medical Aqua (medido)»: 24.911275–57.931155 mm, 359845 sectores.
- Se añadió el preajuste CD «Verbatim CD-R AZO (43438) (medido)»: 24.837775–58.020875 mm, 359848 sectores.
- Se añadió el preajuste DVD «Verbatim DVD-R AZO (43533) (medido)»: 23.9968875–57.9779875 mm, 2,297,888 sectores.
- Los preajustes CD/DVD pasaron a JSON por capas: la capa integrada se actualiza con la aplicación; la de usuario genera inicialmente los parámetros CD/DVD personalizados y sólo guarda adiciones, sustituciones del mismo ID y elementos desactivados. Los preajustes sólo contienen capacidad y geometría de generación, sin mezclar el lienzo de imagen ni parámetros de calibración medida. El formato reserva el idioma predeterminado del texto y las claves de recursos de nombre/descripción para integrar i18n posteriormente sin romper el JSON del usuario. La interfaz permite abrir y recargar la capa de usuario; las ediciones no válidas muestran errores concretos y conservan los últimos preajustes válidos.
- Los parámetros decimales de los preajustes se muestran ahora completos según su precisión real, sin truncarse siempre a 3 o 6 decimales.
- Se corrigió la velocidad de exploración predeterminada de los preajustes integrados CD, la GUI y la línea de comandos: de los 1100 mm/s heredados de la herramienta anterior a 1200 mm/s, el límite inferior del intervalo ECMA-130, y se añadieron regresiones del intervalo estándar.
- Se añadió grabación directa experimental por flujo para CD y DVD sólo de dibujo: el generador suministra datos continuamente a Windows IMAPI2 mediante un búfer de memoria acotado de 64 MiB, sin guardar ISO/RAW completos; las pruebas automatizadas se superaron y la validación física seguía pendiente.
- Se corrigió la devolución de `0xC0AA0502` en algunos grabadores durante la grabación por flujo CD por establecer de forma redundante `DoNotFinalizeMedia`; el cierre de sesión y la protección contra vaciado del búfer pasan a utilizar los valores seguros predeterminados de IMAPI2, y se garantiza que un error de `ReleaseMedia` durante la limpieza no oculte el primer error real de escritura.
- La grabación por flujo CD pasó de Track-at-Once, que ocupaba 2 sectores adicionales y rechazaba obligatoriamente las pistas de audio, a Raw CD/DAO: el constructor de imágenes RAW de Windows añade por flujo lead-in, subcanales Cooked R-W y lead-out, de modo que los 359,845 sectores medidos encajen exactamente en el límite DAO válido del mismo disco. También se conserva un overburn final limitado a un máximo de 3 sectores DAO, rechazando antes de escribir cualquier exceso. Si el dispositivo no informa de velocidades, se utiliza una configuración estable de aproximadamente 16× confirmada como aceptable en hardware real.
- La interfaz de grabación por flujo incorporó lectura y selección de las velocidades de escritura admitidas por el disco actual; antes de grabar, el modo automático vuelve a leer el medio CD/DVD presente y elige la menor tasa disponible para reducir errores. La selección manual se conserva y se registra la velocidad que utiliza realmente el dispositivo.
- Según los resultados de discos físicos DVD, se invirtió la polaridad de bytes blanco y negro de dispersion: negro utiliza `0xA5` en el dominio aleatorizado y blanco, `0x92`; el cambio se aplica tanto al dibujo puro como a la grabación mixta por flujo.
- El búfer de memoria acotado de grabación por flujo pasó de 16 MiB a 64 MiB y el llenado previo al inicio, de 8 MiB a 32 MiB, para dar mayor margen a las variaciones de generación durante la escritura DVD a 16×.
- Antes de empaquetar MSIX, se limpia la caché de compilación RID, se comprueban cambios del código fuente durante la publicación y se verifica directamente que el componente de grabación incluido haya eliminado el ajuste antiguo `DoNotFinalizeMedia`, evitando que una versión actualizada del programa principal incluya una DLL de grabación antigua.
- Se añadieron una capa de abstracción independiente `DiscImageStudio.Burning`, enumeración de grabadores, comprobaciones de disco en blanco/capacidad, una segunda confirmación, progreso y cancelación; el flujo original de archivos de imagen no cambia.
- Se añadió el modo de procesamiento «Duplicación circular (número automático de copias)»: duplica automáticamente la imagen de origen y dispone las copias en círculo hacia fuera del disco, con márgenes de seguridad interior y exterior ajustables.
- Se corrigió la distribución de duplicación circular: el número de copias sigue calculándose automáticamente y, si falta espacio, todas se reducen uniformemente conservando sus proporciones para evitar deformaciones, recortes y solapamientos.
- Se añadió la capa independiente de preprocesamiento de imágenes `DiscImageStudio.Imaging`, compartida por CD y DVD y reutilizable por futuros módulos Blu-ray.
- La página independiente de vista previa en tiempo real admite CD y DVD; sus parámetros se sincronizan en ambas direcciones y los cambios actualizan la vista automáticamente tras un retardo de agrupación de 450ms.
- La vista previa geométrica CD pasó al mismo modo de escala de grises por promedio de muestras que DVD.
- La interfaz y el módulo DVD utilizan de forma fija el algoritmo rápido de textura, salida rápida y espiral horaria (CW), sin mostrar selectores de algoritmo ni dirección.
- Se añadió la carga y visualización de PNG en la interfaz y se corrigió la limitación de altura del cuadro de registro causada por el estilo global de los campos de entrada.
- Se corrigió el fallo de carga de vista previa al cargar PNG mediante `StreamSource` en WPF .NET 9, causado por activar erróneamente la opción de ignorar la caché URI.
- Se corrigió el cierre inesperado del MSIX firmado al escribir registros de generación/vista previa CD por un conflicto entre WPF y el modo de configuración regional invariable.
- Se añadió una GUI WPF unificada.
- Se integraron los flujos CD-DA y DVD, manteniendo aislado el motor DVD.
- Se añadió la capa de abstracción de módulos de disco `DiscImageStudio.Core`.
- Se añadieron adaptadores de módulos CD y DVD y puntos de extensión para un futuro módulo Blu-ray.
- Se añadieron empaquetado MSIX, recursos para la tienda, CI GitHub y documentación de publicación.
