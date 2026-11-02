<a id="黑白抖动工具"></a>
# Herramienta de tramado en blanco y negro

> Traducción al español · [Original](README.md)

Programa independiente para Windows que convierte una imagen a PNG en blanco y negro de dos niveles, sólo **0 y 255**. DVD y CD usan esos dos niveles y conservan los tonos mediante mezcla espacial de píxeles. El ejecutable sigue llamándose `FourLevelDither.exe` por compatibilidad.

<a id="窗口操作"></a>
## Uso de la ventana

1. Ejecuta `FourLevelDither.exe`, pulsa «打开图片» (Abrir imagen) o arrastra una imagen a la ventana.
2. Elige «DVD 二阶» (DVD de dos niveles) o «CD 二阶» (CD de dos niveles) y consulta la descripción de códigos; ambos generan la misma paleta blanco/negro. Marca «100% 像素查看» (Ver píxeles al 100 %) para examinar el tramado.
3. «sRGB 伽马校正» (Corrección gamma sRGB) está desactivado inicialmente para conservar la salida previa; al activarlo se vuelve a procesar y la ventana muestra el estado.
4. Pulsa «保存黑白 PNG» (Guardar PNG blanco y negro). Sin corrección, el nombre termina por defecto en `-黑白抖动.png`; activada, en `-黑白抖动-sRGB.png`, para comparar. No se permite sobrescribir la ruta original.

Cambiar el tipo de disco o gamma recalcula desde el original almacenado, sin volver a procesar el resultado tramado. Para pasar de los esquemas anteriores de tres/cuatro niveles a dos, abre el original sin tramar para conservar toda la información tonal.

Admite PNG, JPEG, BMP, TIFF, GIF y los formatos de los códecs del sistema. Exporta PNG Gray8 sin pérdida con dimensiones y DPI válidos del original; si los DPI son inválidos usa 96. El zoom de vista previa no cambia las dimensiones guardadas.

<a id="处理规则与光盘映射"></a>
## Procesado y correspondencia con el disco

Por defecto convierte el color a luminosidad en el espacio RGB codificado con pesos Rec.709, compone la transparencia sobre blanco y aplica **difusión de error Floyd–Steinberg serpenteante**: alterna el sentido por filas, elige blanco/negro según la luminosidad más el error acumulado y distribuye la diferencia a píxeles siguientes.

| Intervalo de dos niveles de la app principal | Gris PNG / vista previa | Byte Q DVD aleatorizado | Byte CD |
| --- | ---: | --- | --- |
| 0–127 | 0 | `0xA5` | `0x0D` |
| 128–255 | 255 | `0x92` | `0xAA` |

Sin gamma, una zona grande de gris 128 produce aproximadamente `128 / 255 = 50.2%` de píxeles blancos y el resto negros. Durante el tramado se acumula error difundido; con o sin gamma, el archivo sólo contiene 0 y 255.

<a id="srgb-伽马校正"></a>
### Corrección gamma sRGB

Al activarla, cada canal RGB se decodifica mediante la función sRGB estándar a luminosidad lineal; después se calcula luminosidad Rec.709, se compone sobre blanco y se difunde el error en espacio lineal. Cambia la proporción de píxeles blancos/negros, no los códigos ópticos ni los niveles de salida.

Por ejemplo, el gris 128 decodificado equivale aproximadamente al 21,6 % de luminosidad lineal y produce esa proporción de blanco; los tonos medios suelen ser más oscuros que con la opción desactivada. Guarda ambas versiones para comparar el efecto físico. Desactivada conserva el algoritmo y el comportamiento anteriores.

Las páginas CD, DVD y BD de la app principal y su vista previa también ofrecen «启用图像处理（黑白抖动）» (Activar procesado: tramado blanco/negro) y gamma sRGB independiente, ambas desactivadas por defecto según esta documentación histórica. La app vuelve al original y aplica las opciones a generación, streaming y proyección, sin exigir exportar antes un PNG con la herramienta independiente. La geometría y la distribución circular CD/DVD se configuran por separado.

Si la copia circular CD/DVD de la app reconoce entrada opaca de dos niveles, la escala y rota con calidad y vuelve a tramar el resultado a `0/255`, buscando conservar la luminosidad media de la mezcla. Las fotos normales conservan el procesado de alta calidad anterior.

El núcleo `DiscImageStudio.Imaging.GrayscaleDither` sigue admitiendo cualquier paleta de grises ordenada. La API antigua `FourLevelDither` mantiene compatibilidad para sondas de códigos y herramientas existentes; la ventana independiente y su CLI usan dos niveles.

<a id="命令行"></a>
## Línea de comandos

```powershell
# Convertir sin ventana; DVD de dos niveles por defecto; .png de salida no existente
.\FourLevelDither.exe --input original.jpg --output result.png

# Seleccionar DVD o CD; ambos generan blanco/negro 0/255
.\FourLevelDither.exe --input original.jpg --output dvd-result.png --mode dvd
.\FourLevelDither.exe --input original.jpg --output cd-result.png --mode cd

# Comparar gamma sRGB desactivado y activado; omitir --gamma equivale a none
.\FourLevelDither.exe --input original.jpg --output result-none.png --gamma none
.\FourLevelDither.exe --input original.jpg --output result-srgb.png --gamma srgb

# Abrir la ventana con una imagen
.\FourLevelDither.exe original.jpg

# Mostrar ayuda (también --help)
.\FourLevelDither.exe -h
```

<a id="从源码运行与发布"></a>
## Ejecutar y publicar desde el código

Con SDK .NET 10 desde la raíz del repositorio:

```powershell
dotnet run --project tools/FourLevelDither --configuration Release

dotnet publish tools/FourLevelDither/FourLevelDither.csproj --configuration Release --runtime win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --output artifacts/FourLevelDither/win-x64
```

El `FourLevelDither.exe` autocontenido incluye .NET 10. Para una compilación sin entorno incluido, necesitas .NET 10 Windows Desktop Runtime.
