Traducción al español · [Original](RELEASING.md)

<a id="发布指南"></a>
# Guía de publicación

<a id="分支与版本政策"></a>
## Política de ramas y versiones

El desarrollo habitual utiliza `main` y ramas de funcionalidad de corta duración, que se integran mediante Pull Request. Las versiones de corrección también se publican desde `main`; por ahora no se mantienen ramas de publicación a largo plazo.

La versión se mantiene de forma centralizada en `Directory.Build.props`, en la raíz del repositorio. Los responsables de mantenimiento eligen la siguiente versión según los cambios:

| Cambio | Incremento de versión | Ejemplo |
| --- | --- | --- |
| Corrección de problemas compatible con versiones anteriores | patch | `1.0.26` → `1.0.27` |
| Nueva funcionalidad compatible con versiones anteriores | minor | `1.0.26` → `1.1.0` |
| Cambios incompatibles de interfaces, comandos o formatos de archivo | major | `1.0.26` → `2.0.0` |

Para una versión estable, se escribe `A.B.C` en `VersionPrefix` y se deja `VersionSuffix` vacío; para una versión preliminar, se escribe `alpha.N`, `beta.N` o `rc.N` en `VersionSuffix`, donde `N` es un entero positivo sin ceros iniciales. Por ejemplo, `VersionPrefix=1.0.27` y `VersionSuffix=rc.1` corresponden a `1.0.27-rc.1`; el sufijo no debe incluirse en `VersionPrefix`. Al convertir una versión candidata en estable, se vacía `VersionSuffix`.

GitHub Actions lee la versión completa de la configuración y crea automáticamente la etiqueta correspondiente, como `vA.B.C` o `vA.B.C-rc.N`. La versión informativa del EXE publicado conserva el sufijo preliminar; las versiones de archivo y ensamblado utilizan cuatro componentes numéricos, `A.B.C.0`. Los campos numéricos del repositorio están limitados a `0..65534` para mantener la compatibilidad con las versiones de ensamblado de .NET; al enviar a Microsoft Store, el primer componente también debe ser mayor que `0`, y la versión MSIX es `A.B.C.0`.

<a id="github-actions-发布-windows-应用和跨平台-cli"></a>
## Publicación de la aplicación Windows y la CLI multiplataforma con GitHub Actions

<a id="准备发布"></a>
### Preparar la publicación

1. Comprobar que la licencia GPL-3.0-or-later, la política de privacidad y los datos de contacto de soporte satisfagan los requisitos de esta distribución. Al publicar, conservar una etiqueta que apunte al commit de compilación y proporcionar en la misma Release el código fuente completo correspondiente a esa versión y las instrucciones de compilación, de modo que el código fuente esté disponible junto con el EXE/MSIX; comprobar que el archivo de código fuente generado automáticamente por GitHub incluya los archivos necesarios para compilar. Al actualizar dependencias o el runtime .NET incluido, actualizar también los [avisos de terceros](../THIRD-PARTY-NOTICES.txt). La licencia del proyecto, los avisos de terceros y el acuerdo de la fuente están integrados en la aplicación y pueden consultarse sin conexión en «Acerca de»; los límites de las licencias se explican en [LICENSE-NOTICE.md](../LICENSE-NOTICE.es.md).
2. En una rama de funcionalidad, modificar `VersionPrefix` de `Directory.Build.props` (y establecer también `VersionSuffix` para una versión preliminar) y trasladar el contenido de esta publicación desde `Unreleased` de `CHANGELOG.md` a la sección de la versión correspondiente. Conservar `## Unreleased` para el desarrollo posterior.
3. El título de la sección de versión debe tener la forma `## [1.0.27] - YYYY-MM-DD` o `## [1.0.27-rc.1] - YYYY-MM-DD`, sustituyendo el marcador de fecha por la fecha real de publicación, y su contenido no puede estar vacío. Tanto las versiones preliminares como las estables necesitan su propia sección de versión, que debe coincidir exactamente con la etiqueta; a partir de esta publicación, las nuevas secciones de versión deben incluir los registros en chino e inglés según la convención que se explica más abajo.
4. Incluir la configuración de versión y el registro de cambios en el mismo PR de preparación de la publicación e integrarlo en `main` después de confirmar que el flujo `build` termina correctamente. El conjunto de cambios de ese push debe incluir ambos archivos y la versión completa de la configuración debe cambiar realmente para que se inicie la publicación automática.
5. GitHub Actions completa las comprobaciones, crea automáticamente la etiqueta de versión y genera un borrador de Release; los responsables de mantenimiento lo revisan y lo hacen público manualmente.

Por ejemplo, para una futura versión de corrección, establecer en `Directory.Build.props`:

```xml
<VersionPrefix>1.0.27</VersionPrefix>
<VersionSuffix></VersionSuffix>
```

Al mismo tiempo, añadir en `CHANGELOG.md` una sección de versión no vacía `## [1.0.27] - YYYY-MM-DD` con la fecha real; al integrarla en `main`, se genera automáticamente la etiqueta `v1.0.27`. Para una versión candidata, establecer `VersionSuffix` en `rc.1` y escribir `1.0.27-rc.1` como versión del registro.

<a id="双语发布日志"></a>
### Registro de cambios bilingüe

En las publicaciones posteriores, utilizar sucesivamente `### 中文` y `### English` dentro de la misma sección con fecha `## [versión] - fecha`. Ambos grupos de entradas deben describir los mismos hechos sobre cambios, límites y validación; el inglés debe traducir íntegramente el contenido chino correspondiente, sin omitir advertencias ni añadir afirmaciones de capacidad sin verificar. Después de organizar el contenido de la publicación, conservar `## Unreleased` en la parte superior para el siguiente ciclo de desarrollo.

Por ejemplo, la estructura durante la preparación de la publicación es la siguiente; la versión, la fecha y las entradas deben sustituirse por los datos reales de esta publicación:

```markdown
## Unreleased

## [1.3.4] - YYYY-MM-DD

### 中文

- 在设置中增加界面语言选择。

### English

- Add interface language selection to Settings.
```

`Get-ReleaseMetadata.ps1` extrae toda la sección de versión y sólo se detiene en el siguiente título `## ` o al final del archivo; por ello, tanto `### 中文` como `### English` se incluyen íntegramente en las notas de la Release. Los títulos de idioma no deben escribirse como `## 中文` o `## English`, porque cerrarían prematuramente la sección de versión. La comprobación actual de metadatos valida que la sección de versión sea única, que la fecha sea válida y que el contenido no esté vacío; la revisión de publicación sigue teniendo que confirmar la presencia de ambos idiomas y la coherencia de las entradas.

Tras el registro bilingüe, el flujo añade instrucciones de descarga y ejecución en chino e inglés, comandos de comprobación SHA-256 y el código fuente correspondiente con las instrucciones de compilación. Las instrucciones de descarga deben indicar expresamente en ambos idiomas que las CLI de Linux y macOS son experimentales, y explicar por separado el estado de firma del EXE de Windows y las limitaciones experimentales de la grabación al vuelo.

<a id="自动检查与草稿"></a>
### Comprobaciones automáticas y borrador

`.github/workflows/release.yml` detecta los cambios de `Directory.Build.props` o `CHANGELOG.md` en `main`, pero sólo entra en el proceso de publicación si ese push **modifica tanto la configuración de versión como el registro de cambios y cambia la versión completa**. Un push normal de código sólo ejecuta las comprobaciones habituales de compilación; añadir contenido únicamente a `Unreleased` o ajustar la configuración sin cambiar la versión no genera una Release.

El flujo lee la versión completa de la configuración, valida su formato y la sección correspondiente de Changelog, no vacía y con fecha, confirma que el commit de publicación pertenece a `origin/main` y reutiliza las comprobaciones de compilación y regresión de `build.yml`. Sólo crea la etiqueta de versión y el borrador correspondientes cuando todo se supera.

Windows x64 sigue ofreciendo un EXE autocontenido de un solo archivo. El script de empaquetado ejecuta el EXE final en un directorio aislado y realiza la autoprueba, las comprobaciones de generación RAW/WAV de dos sectores y la comprobación de capturas de la GUI.

Las CLI experimentales sin conexión de Linux y macOS se compilan en los runners x64 y Arm64 correspondientes, y sus paquetes incluyen el runtime .NET. Cada runner extrae el tar.gz que acaba de generar y, fuera del directorio del repositorio, ejecuta `--help`, la autoprueba de DVD y las pruebas de generación de pistas de CD, imágenes de DVD e imágenes de Blu-ray. El proceso de publicación recoge los archivos comprimidos originales que superan estas pruebas y los adjunta al borrador junto con el EXE de Windows. Por ejemplo:

```text
DiscImageStudio-v1.3.6-win-x64.exe
DiscImageStudio-CLI-v1.3.6-linux-x64.tar.gz
DiscImageStudio-CLI-v1.3.6-linux-arm64.tar.gz
DiscImageStudio-CLI-v1.3.6-osx-x64.tar.gz
DiscImageStudio-CLI-v1.3.6-osx-arm64.tar.gz
SHA256SUMS.txt
```

El flujo combina y valida las sumas de comprobación SHA-256 de cada plataforma y las escribe en las notas de la Release y en el adjunto `SHA256SUMS.txt`. Utiliza el `GITHUB_TOKEN` integrado para crear un **borrador de GitHub Release**, sin necesidad de configurar un token de acceso personal (PAT). Las etiquetas preliminares activan automáticamente el indicador prerelease; las etiquetas estables crean un borrador de versión estable.

Los responsables de mantenimiento revisan la versión, las notas de publicación, los adjuntos de cada plataforma y las sumas de comprobación del borrador en la [página de Releases](https://github.com/JiaFeiMiao-K-Cat/DiscImageStudio/releases) de GitHub, descargan los archivos y comprueban que arrancan antes de hacerlo público manualmente. El siguiente comando calcula el SHA-256 del archivo descargado para Windows y permite compararlo con el valor de las notas de la Release:

```powershell
Get-FileHash .\DiscImageStudio-v1.3.6-win-x64.exe -Algorithm SHA256
```

<a id="失败重试与更正"></a>
### Reintentos y correcciones

Se puede volver a ejecutar una ejecución fallida de Actions o iniciar manualmente el flujo `release` desde `main` en GitHub Actions. El inicio manual no tiene entrada de etiqueta: lee la configuración y el registro de cambios actuales de `main` y ejecuta todas las comprobaciones de publicación, sin tener que modificar otra vez el archivo de versión para reintentarlo.

Al reintentar la misma versión, la etiqueta existente debe apuntar al mismo commit que se compila en esta ejecución, y la Release existente debe seguir siendo un borrador. No se deben sobrescribir ni mover las Releases, etiquetas o artefactos ya publicados; si se detecta un problema, se corrige desde `main` y se publica una versión nueva; en una iteración preliminar se incrementa el número, por ejemplo `rc.1` → `rc.2`. Si `main` ha avanzado y la etiqueta de esa misma versión apunta a un commit anterior, se debe volver a ejecutar la ejecución original fallida o preparar una versión nueva para el nuevo commit. Incluso al modificar sólo las notas de publicación, deben conservarse los hechos y el historial de correcciones, sin sustituir los binarios ya publicados.

Se recomienda que los administradores del repositorio configuren en GitHub la protección de la rama `main`, exigiendo Pull Request y la comprobación obligatoria `build`; para las etiquetas `v*`, deben establecer un conjunto de reglas que permita al flujo de publicación crearlas y prohíba actualizarlas o eliminarlas. Los archivos del flujo no equivalen a la configuración de protección remota: los administradores deben configurar esas protecciones por separado.

<a id="本地-exe-演练"></a>
## Ensayo local del EXE

En la raíz del repositorio, ejecutar el mismo script de empaquetado que utiliza GitHub Actions:

```powershell
powershell -ExecutionPolicy Bypass -File packaging/Build-Release.ps1
```

El script utiliza automáticamente la versión de `Directory.Build.props`. Por ejemplo, con la configuración `1.0.26`, el directorio de salida es `artifacts/release/v1.0.26/`. El script compila y valida por separado el EXE autocontenido `win-x64`, y genera `DiscImageStudio-v1.0.26-win-x64.exe` y su archivo `SHA256SUMS.txt`. En la publicación oficial, el flujo añade a la lista las sumas de comprobación de los cuatro paquetes comprimidos de CLI para las plataformas.

<a id="microsoft-store-msix"></a>
## MSIX para Microsoft Store

MSIX es un canal de distribución manual independiente. Primero se reserva el nombre del producto en Partner Center y se copian los siguientes valores de la página Product identity:

- Package/Identity/Name
- Package/Identity/Publisher
- Package/Properties/PublisherDisplayName

Ejecutar localmente:

```powershell
powershell -ExecutionPolicy Bypass -File packaging/Build-MSIX.ps1 `
  -IdentityName "Partner Center Identity Name" `
  -Publisher "CN=Partner Center Publisher" `
  -PublisherDisplayName "Publisher display name" `
  -DisplayName "Reserved product name"
```

Si no se proporciona `-Version`, se lee la versión estable de `Directory.Build.props` y se convierte a `A.B.C.0`. Para especificar explícitamente la versión de la tienda, utilizar `-Version "1.0.26.0"`; este valor también se aplica a la versión del EXE incluido en el paquete para evitar discrepancias entre el manifiesto y el programa. El canal Microsoft Store no acepta sufijos preliminares como `-rc.1`; al preparar un paquete para Store, se debe utilizar una configuración de versión estable.

También se puede ejecutar manualmente el flujo `package-msix` en GitHub Actions e introducir los mismos valores; si `version` se deja vacío, también se lee la configuración del repositorio. Este flujo reutiliza primero la CI de `build.yml` y después genera el MSIX como adjunto de Actions; no lo envía automáticamente a la tienda ni lo adjunta a una GitHub Release. El MSIX generado para Store puede permanecer sin firmar; para probarlo mediante instalación directa, debe firmarse con un certificado cuyo Publisher coincida.

<a id="本机签名测试包"></a>
## Paquete de prueba firmado localmente

Para las pruebas locales, crear un certificado de firma de código cuyo sujeto coincida exactamente con el Publisher de prueba y colocar el certificado de clave pública en:

```text
artifacts/certificates/DiscImageStudio-Test.cer
```

Ejecutar `Build-MSIX.ps1 -CertificateThumbprint <thumbprint>` con la huella digital del certificado (e incluir los parámetros de identidad anteriores) para generar un paquete firmado. Después, hacer doble clic en `packaging/Install-TestPackage.cmd` o ejecutar:

```powershell
powershell -ExecutionPolicy Bypass -File packaging/Install-TestPackage.ps1
```

El script de instalación solicita una vez privilegios de administrador, importa la clave pública del certificado de prueba en «Equipo local/Personas de confianza» e instala el MSIX firmado. No se deben enviar claves privadas ni archivos PFX a GitHub, ni adjuntar el certificado de prueba local autofirmado a una Release oficial.

<a id="windows-11-未签名快速测试可选"></a>
## Prueba rápida sin firma en Windows 11 (opcional)

Los paquetes de prueba sin firmar deben utilizar un espacio de nombres OID de editor independiente y no pueden mezclarse con la identidad oficial de la tienda. Generar el paquete de prueba local:

```powershell
powershell -ExecutionPolicy Bypass -File packaging/Build-MSIX.ps1 `
  -IdentityName "12345.DiscImageStudioArchitectureTest" `
  -Publisher "CN=Disc Image Studio Test" `
  -PublisherDisplayName "Disc Image Studio Test" `
  -DisplayName "Disc Image Studio Test" `
  -Version "1.0.26.0" `
  -UnsignedDevelopmentPackage
```

Después, instalar con PowerShell como administrador:

```powershell
Add-AppxPackage -Path ".\artifacts\msix\DiscImageStudio_1.0.26.0_x64_unsigned-dev.msix" -AllowUnsigned
```

`-UnsignedDevelopmentPackage` sólo sirve para pruebas rápidas en sistemas Windows 11 que admitan esta función; algunas versiones de Windows pueden rechazar los paquetes ejecutables aunque ofrezcan `-AllowUnsigned`. En ese caso, utilizar el método anterior de prueba con firma. No se debe utilizar esta opción al preparar una distribución o un envío a Microsoft Store; los paquetes oficiales deben seguir utilizando la identidad real de Partner Center.

Antes del envío oficial:

- Volver a empaquetar con la identidad real; no se puede enviar un paquete con Identity de prueba.
- Probar la instalación, desinstalación y ejecución sin conexión en un dispositivo Windows limpio.
- Ejecutar Windows App Certification Kit sobre el paquete final.
- Proporcionar al menos una captura real del escritorio de 1366×768 o mayor.
- Publicar `PRIVACY.md` en una página HTTPS pública e indicar datos de contacto reales para soporte.
- En las notas de certificación, explicar que la aplicación genera archivos grandes, no requiere inicio de sesión, no se conecta a la red e incluye una función experimental de grabación directa de CD/DVD mediante Windows IMAPI2; enumerar con precisión los dispositivos aplicables, los requisitos de discos en blanco y el alcance de validación actual.
