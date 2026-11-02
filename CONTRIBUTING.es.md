<a id="contributing-to-disc-image-studio"></a>
# Contribuir a Disc Image Studio

> Traducción al español · [Original](CONTRIBUTING.md)

Te damos la bienvenida a Disc Image Studio. Lee primero este documento completo: abarca informes de problemas, preparación del entorno, límites entre módulos, requisitos de validación y proceso de contribución.

<a id="报告问题"></a>
## Informar de problemas

Usa GitHub Issues para los problemas generales e incluye:

- Versión de la app y forma de obtenerla (compilación desde el código / paquete MSIX), además de la versión de Windows.
- Pasos que permitan reproducirlo de forma estable, comportamiento esperado y comportamiento observado.
- Registros o capturas relevantes.

Para problemas de grabación, añade el modelo de grabadora, su firmware y la marca/modelo de disco: el resultado visible y la calibración medida dependen mucho de este hardware; sin esos datos suele ser imposible localizar el problema.

**No** reveles vulnerabilidades explotables en incidencias públicas. Sigue la [política de seguridad](SECURITY.es.md) y usa los informes privados de GitHub.

Las propuestas de funciones también van en Issues: explica el problema, el resultado esperado y si existe alguna solución alternativa.

<a id="开发环境"></a>
## Entorno de desarrollo

- Windows 10 2004+ o Windows 11.
- SDK .NET 10; `global.json` fija `10.0.x` y `dotnet` selecciona automáticamente una versión instalada compatible.
- Las pruebas no dependen de frameworks externos: son programas de regresión de consola incluidos en el repositorio.

```powershell
git clone <URL de tu fork>
cd DiscImageStudio
dotnet build DiscImageStudio.slnx --configuration Release
dotnet run --project src/DiscImageStudio.App --configuration Release
```

<a id="模块边界"></a>
## Límites entre módulos

Lee la [arquitectura](docs/ARCHITECTURE.es.md) antes de modificar código. La decisión principal: el host no depende de ninguna codificación óptica; la lógica específica de cada medio permanece en su módulo. La revisión presta especial atención a estas reglas:

1. No coloques modulación física, corrección de errores, organización de sectores o detalles de sistemas de archivos de CD/DVD/futuros Blu-ray en `DiscImageStudio.Core` ni en las capas compartidas.
2. Los módulos no deben leer controles de la GUI; toda entrada procede de `DiscJobRequest`. Tampoco deben mostrar ventanas directamente.
3. Si el algoritmo subyacente no admite cancelación segura, `SupportsCancellation` debe ser `false`.
4. Los ID de módulos y los nombres de comandos son únicos globalmente; el catálogo rechaza conflictos al iniciar.
5. Todo comando nuevo debe registrarse en `DiscCommandDescriptor` y añadir pruebas de enrutamiento del catálogo y una prueba mínima de generación de salida.

<a id="编码规范"></a>
## Convenciones del código

- Sigue los nombres, la estructura y la densidad de comentarios del código circundante; mantén cero advertencias y cero errores de compilación.
- No elimines ni reduzcas funciones para lograr que compile; tampoco añadas por tu cuenta capas de compatibilidad o implementaciones duplicadas.
- Antes de eliminar funciones o código que parezcan intencionados, explica el objetivo en la incidencia o el PR.
- No incluyas resultados de compilación, recursos binarios ni claves privadas / archivos PFX.

<a id="测试与验证"></a>
## Pruebas y validación

Antes de un pull request deben pasar todas estas comprobaciones:

```powershell
dotnet build DiscImageStudio.slnx --configuration Release
dotnet run --project src/DvdImageSolver --configuration Release -- selftest
dotnet run --project tests/DiscImageStudio.ArchitectureTests --configuration Release
```

Amplía las comprobaciones según el cambio y describe sus resultados:

- Si cambias la composición de imágenes, la grabación en streaming o la lectura de preajustes, revisa las regresiones correspondientes de [VALIDATION.md](docs/VALIDATION.es.md).
- Para cambios de GUI, comprueba las páginas principales con capturas renderizadas fuera de pantalla y, si hace falta, actualiza `docs/images/`.
- Tras cambiar intercalado con retardo, EFMPlus, ECC u otras partes de codificación, explica por qué no varía la cobertura de `selftest`, si ese es el caso.

<a id="提交与-pull-request"></a>
## Commits y pull requests

1. Crea una rama de función desde tu fork (o desde `main`). Cada rama debe resolver una sola cuestión y mantener un diff revisable.
2. Escribe el título y el cuerpo de los commits en inglés. El título debe resumir el cambio; añade al cuerpo comportamiento, validación e impacto de archivos grandes cuando corresponda. Las notas de cada versión llevan secciones en chino e inglés.
3. El PR debe describir motivación, alcance y validación; si el cambio es visible para quienes usan la app, actualiza `Unreleased` de `CHANGELOG.md` y los documentos de `docs/` pertinentes.
4. Después de enviarlo, comprueba que el flujo `build` de GitHub Actions esté en verde.

Para cambios grandes (módulos, páginas de UI o contratos de grabación), se recomienda discutir primero el plan en una incidencia o un PR en borrador.

<a id="版本与发布"></a>
## Versiones y publicaciones

Se usa `main` con ramas breves de función y versiones semánticas que aumentan según correcciones, funciones o incompatibilidades. Anota las contribuciones habituales en `Unreleased` de `CHANGELOG.md`. En el mismo PR de preparación de versión, quienes mantienen el proyecto actualizan la versión de `Directory.Build.props` y su registro; tras integrar en `main`, GitHub Actions genera automáticamente la etiqueta y el borrador de versión. Un push de código habitual no genera una versión. Consulta las condiciones, sufijos de prelanzamiento, revisión de borradores y reintentos en la [guía de publicación](docs/RELEASING.es.md).

<a id="许可证"></a>
## Licencia

El proyecto usa [GNU GPL v3.0 o posterior](LICENSE) (`GPL-3.0-or-later`). Al contribuir confirmas que tienes derecho a enviar el contenido y aceptas licenciarlo en esos términos. Conserva la autoría y las licencias del código, fuentes o imágenes de terceros que añadas, confirma que sean compatibles con la distribución y amplía los [avisos de terceros](THIRD-PARTY-NOTICES.txt); los límites de estas autorizaciones se explican en [LICENSE-NOTICE.md](LICENSE-NOTICE.es.md).
