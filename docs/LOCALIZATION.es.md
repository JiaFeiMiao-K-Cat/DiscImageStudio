<a id="界面国际化"></a>
# Internacionalización de la interfaz

> Traducción al español · [Original](LOCALIZATION.md)

Haz clic en el botón permanente de engranaje y «Configuración» de la esquina superior derecha. En «General → Idioma», elige «Sistema», «简体中文», «繁體中文», «English», «日本語» o «Español». Guarda y reinicia para aplicar el cambio. El botón aparece en Inicio y en todos los espacios de trabajo; también puedes abrirlo con Ctrl+,. El menú «…» sólo conserva Acerca de. Cancelar, pulsar Escape o cerrar la ventana de configuración no guarda el idioma. Cambiarlo no reinicia automáticamente la app ni cierra proyectos o interrumpe tareas.

El campo `language` de `settings.json` usa `system`, `zh-CN`, `zh-TW`, `en`, `ja` o `es`. El idioma y el tema siguen el sistema de forma predeterminada; valores ausentes, vacíos o no admitidos también vuelven al sistema, y se conservan las preferencias válidas existentes. Se usa el idioma de UI del sistema capturado al iniciar: `zh-TW`, `zh-HK`, `zh-MO`, `zh-Hant` y los `zh-Hant-*` explícitos usan chino tradicional; `zh-CN`, `zh-SG`, `zh-Hans` y las demás variantes chinas usan chino simplificado; `ja` y `ja-JP` usan japonés; `es`, `es-ES`, `es-MX`, `es-419` y otras variantes españolas usan español. El inglés, francés y los demás idiomas usan inglés. Los cambios del idioma del sistema durante la ejecución requieren reiniciar. El tema elige claro u oscuro según el modo de color de las apps de Windows. El idioma es independiente de `.discproj`; no se traducen los nombres de proyectos, preajustes del usuario, capas, rutas ni reglas de lectura numérica.

<a id="添加和维护文案"></a>
## Añadir y mantener textos

Las traducciones están en `src/DiscImageStudio.App/Localization/Catalogs/*.json`, incrustadas en el ensamblado al compilar y sin dependencias adicionales en ejecución. Cada clave contiene cinco textos: `zh-CN`, `zh-TW`, `en`, `ja` y `es`. El chino tradicional tiene recursos independientes y términos como «設定», «專案», «檔案» y «燒錄»; no se convierten datos del usuario durante la ejecución:

```json
{
  "示例 {0}": { "zh-CN": "示例 {0}", "zh-TW": "範例 {0}", "en": "Example {0}", "ja": "例 {0}", "es": "Ejemplo {0}" }
}
```

En C# se usa `L.T("源文案")`; para interpolación, `L.F($"示例 {value}")`, cuya clave es el formato compuesto .NET `示例 {0}`. Si necesitas una clave explícita, usa `L.Format(key, arguments)`. La traducción puede reordenar argumentos, pero debe conservar los índices y los especificadores de formato. XAML usa `xmlns:loc="clr-namespace:DiscImageStudio.Localization"` y `{loc:Text ResourceKey}`, resueltos al crear ventanas y plantillas.

Define una sola vez las frases compartidas; las claves deben ser únicas entre todos los recursos. Si falta el idioma actual, se usa chino; si falta la clave, se muestra la propia clave. Inicializa el idioma antes de crear ventanas y ViewModels. Cambia sólo la cultura de UI y conserva la cultura numérica; no traduzcas identificadores de protocolos, comandos, rutas o datos del usuario ni uses textos traducidos en decisiones lógicas.

El alcance es la interfaz de escritorio y los mensajes de la capa de aplicación. La ayuda de la CLI independiente, los diagnósticos de los codificadores subyacentes y los errores de terceros/sistema operativo conservan su idioma; los textos legales originales se mantienen.

<a id="验证"></a>
## Validación

`LocalizationTests` comprueba la integridad de los idiomas, los argumentos de formato, la resolución del idioma del sistema, el guardado y la cancelación de preferencias, y que un fallo al guardar no aplique el idioma.

```powershell
dotnet run --project src/DiscImageStudio.App -c Release -- ui-snapshot --settings-dialog --language en --output artifacts/settings-en.png
dotnet run --project src/DiscImageStudio.App -c Release -- ui-snapshot --settings-dialog --language zh-CN --output artifacts/settings-zh.png
dotnet run --project src/DiscImageStudio.App -c Release -- ui-snapshot --settings-dialog --language zh-TW --output artifacts/settings-tw.png
dotnet run --project src/DiscImageStudio.App -c Release -- ui-snapshot --settings-dialog --language ja --output artifacts/settings-ja.png
dotnet run --project src/DiscImageStudio.App -c Release -- ui-snapshot --settings-dialog --language es --output artifacts/settings-es.png
```

Las capturas usan un archivo de configuración de un solo uso y no modifican las preferencias reales. Al añadir un idioma, amplía también las reglas de normalización y resolución, el selector, todos los recursos y las pruebas.
