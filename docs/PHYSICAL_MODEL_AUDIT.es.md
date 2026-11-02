Traducción al español · [Original](PHYSICAL_MODEL_AUDIT.md)

<a id="cd--dvd-生成物理模型复核"></a>
# Revisión del modelo físico de generación de CD / DVD

Fecha de revisión: 2026-09-21. Se examinó el espacio de trabajo de `DiscImageStudio-GitHub` de ese momento, incluida la implementación de calibración aún sin confirmar, tomando como referencia el commit `6c55a18`. Esta revisión se limitó a contrastar las normas, auditar el código y realizar comprobaciones numéricas independientes; no modificó el generador, los ajustes predefinidos ni el comportamiento de grabación.

<a id="结论"></a>
## Conclusiones

No se encontraron errores de cálculo en las fórmulas básicas de la espiral ni en la longitud de los sectores de DVD. Sin embargo, el modelo actual no puede predecir con precisión los ángulos de un disco real únicamente a partir de los parámetros nominales de las normas: la combinación de parámetros predeterminados de CD presenta un problema de coherencia geométrica; la calibración con una sola línea y una densidad lineal fija puede absorber un error de densidad en los radios; y el modelo geométrico no incorpora los límites del flujo finito del entrelazado de CD ni su referencia temporal. También falta comprobar que el tipo de disco grabable coincida con el modelo de generación.

Esta auditoría por sí sola no permite determinar la causa única de la curvatura de un disco concreto. Tampoco justifica sustituir directamente la longitud de bit de DVD por otro valor nominal como corrección.

<a id="标准依据与适用范围"></a>
## Normas de referencia y ámbito de aplicación

- [ECMA-359, primera edición, 2004](https://ecma-international.org/wp-content/uploads/ECMA-359_1st_edition_december_2004.pdf), publicación oficial: DVD-R de una capa. Se revisaron principalmente §§10.7–10.9, 18–23 y 24.2, y se contrastaron las páginas originales 28, 29, 52 y 56 del PDF.
- [ECMA-267, tercera edición, 2001](https://ecma-international.org/wp-content/uploads/ECMA-267_3rd_edition_april_2001.pdf), publicación oficial: DVD-ROM. Se revisaron principalmente §§10.6 y 18–21. No sustituye todas las reglas de los soportes grabables.
- [ECMA-130, segunda edición, 1996](https://ecma-international.org/wp-content/uploads/ECMA-130_2nd_edition_june_1996.pdf), publicación oficial: referencia para la geometría de CD-ROM y la codificación CIRC común; §§8.6, 11.3–11.4, 19.4 y anexo C. Una pista de audio no equivale a una pista de datos de CD-ROM.
- [ECMA-394, primera edición, 2010](https://ecma-international.org/wp-content/uploads/ECMA-394_1st_edition_december_2010.pdf), publicación oficial: CD-R; grabación a varias velocidades, surco pregrabado y relación de compatibilidad con Red Book; §§1.2 y 2.4–2.5. Este documento establece parte de los requisitos geométricos mediante referencias a Red Book; no vuelve a incluir directamente una tabla completa de geometría de CD.

Las restricciones principales de ECMA-359 se resumen a continuación. Todas son requisitos de la norma, no mediciones de un disco concreto:

| Parámetro | Restricción | Apartado |
|---|---|---|
| Radio inicial de los datos | 23.9–24.0 mm | 10.7.1.2; el original especifica un diámetro de 48.0 mm con tolerancia −0.2/+0.0 mm |
| Radio final de los datos en un disco de 12 cm | No superior a 58.0 mm | 10.7.1.2 |
| Paso medio de la espiral | 0.74 ± 0.01 μm | 10.8 |
| Desviación local del paso | Máximo de ±0.03 μm respecto a 0.74 μm | 10.8 |
| Longitud media del bit de canal | 133.3 ± 1.4 nm | 10.9 |
| Primer PSN del área de datos | 0x030000; sectores físicos continuos, sin huecos | 24.2 |

El límite inferior del diámetro inicial de los datos de ECMA-267 difiere del de DVD-R: §10.6.1 especifica 48.0 mm con tolerancia −0.4/+0.0 mm. No deben mezclarse las tolerancias iniciales de ambas normas.

<a id="已确认正确的部分"></a>
## Aspectos cuya corrección se ha confirmado

El número de bits de canal por sector de DVD es `13 × 2 × (32 + 1456) = 38688`; cada bloque ECC de 16 sectores contiene 619008 bits. El valor empleado en `DvdEccBlockEncoder.cs:8` es correcto. No se detectó ninguna omisión general en el cálculo de las longitudes de los desplazamientos de sector, las tramas de sincronización ni el entrelazado de las filas PO.

La longitud de pista actual es:

```text
CD:  L = (N / 75) × v                         [mm]
DVD: L = N × 38688 × channelBitLengthNm × 10^-6 [mm]
```

`CdDiscParameters.cs:59–75` utiliza una aproximación por área con paso constante; `ArchimedeanSpiral.cs:33–70,115–120` utiliza la longitud de arco exacta. Las relaciones aproximadas son `pitch = π(ro²−ri²)/L` y `θ = 2L(r−ri)/(ro²−ri²)`. La diferencia angular acumulada en todo el CD entre la aproximación y la espiral exacta es de unos 0.000135°, insuficiente para explicar una curvatura claramente visible. Tampoco se encontraron errores en la inversión de la longitud de arco exacta de DVD, la conversión de nanómetros a milímetros ni la orientación predeterminada de las coordenadas de imagen.

El *linking* de DVD-R se aplica a determinadas situaciones de grabación incremental. §24.2 establece expresamente la continuidad de los sectores. No hay fundamento para añadir sistemáticamente un supuesto hueco oculto a cada bloque ECC.

<a id="发现一默认-cd-80-分钟预设的参数组合不合理"></a>
## Hallazgo 1: la combinación de parámetros del ajuste predeterminado de CD de 80 minutos no es coherente

`src/DiscImageStudio.Core/Presets/built-in-disc-presets.json:6–12` combina 359849 sectores, radios de 24.5–56.8 mm y una velocidad de 1200 mm/s. Al invertir las fórmulas utilizadas realmente para generar el disco se obtienen estos valores:

| Ajuste predefinido | Paso implícito de la espiral (μm) |
|---|---:|
| CD de 80 minutos predeterminado | 1.432856 |
| CD de 74 minutos predeterminado | 1.548384 |
| CD Ritek azul claro, radios medidos | 1.492598 |
| CD Verbatim, radios medidos | 1.500257 |
| DVD de 12 cm predeterminado | 0.739837 |
| DVD-R Verbatim, radios medidos | 0.738304 |

ECMA-130 §11.3 establece un intervalo de paso de 1.5–1.7 μm; §11.4 establece un intervalo de velocidad de exploración de referencia de 1200–1400 mm/s. La combinación predeterminada de CD80 no sirve como ajuste genérico preciso que cumpla ese intervalo geométrico. Su radio inicial de 24.5 mm tampoco puede identificarse directamente con el inicio del área de datos de usuario de §8.6, situado entre 24.8 y 25.0 mm.

Esta comprobación se refiere a la combinación de parámetros del software; no determina que los CD-R de una persona incumplan la norma. Los ajustes marcados como medidos siguen utilizando una velocidad fija de 1200 mm/s: esa etiqueta no significa que también se haya medido la densidad lineal. No deben alterarse los radios medidos por el usuario únicamente para hacerlos encajar en el intervalo de la norma.

<a id="发现二标准容差远大于绘图所需的累计角度精度"></a>
## Hallazgo 2: las tolerancias de la norma superan ampliamente la precisión angular acumulada necesaria para dibujar

`ImageTargetMapper.cs:58–77` y `FastDispersionImageWriter.cs:345–365` calculan un paso constante a partir de la capacidad, los dos radios y una longitud de bit fija. Los parámetros predeterminados de DVD producen unas 45956.06 vueltas. La norma permite variaciones locales del paso y 58 mm es un límite superior; por tanto, esos datos sólo definen un modelo inicial.

Sensibilidad numérica, calculada en cada caso modificando una sola entrada y manteniendo constantes las demás:

- Un cambio de 10 ppm en la longitud de bit de DVD altera el ángulo acumulado de todo el disco en unos 165.44°.
- Cambiar la longitud de bit de DVD de 133.33 a 133.3 nm altera el ángulo acumulado en unas 10.34037 vueltas.
- Aumentar en 0.001 mm el radio interior o exterior de DVD altera el ángulo acumulado en unos −201.76°.

Estos valores son diferencias de fase del modelo, no errores medidos en un disco real. Una longitud de 133.33 nm está dentro de la tolerancia de la norma; 133.3 nm tampoco es la longitud exacta real de cada disco. Sustituir valores nominales sin más comprobaciones puede cambiar considerablemente el resultado generado.

<a id="发现三单条径向线不能独立确认半径和线密度"></a>
## Hallazgo 3: una sola línea radial no permite identificar de forma independiente los radios y la densidad lineal

`CalibrationTarget.cs:468–473` fija la velocidad lineal o la longitud de bit, y `CalibrationCrossFitter.cs:73` ajusta únicamente los dos radios y una rotación global. El procedimiento actual no utiliza la correspondencia con los radios originales de los círculos concéntricos.

Contraejemplo independiente: el modelo de generación es un DVD `(ri=24, ro=58, bit=133.33)` y se supone que el modelo real es `(24,58,133.3)`. Si la longitud de bit del ajuste permanece fijada en 133.33, basta cambiar los radios candidatos a aproximadamente `(24.00540257,58.01305620)` y ajustar la rotación global para reproducir casi por completo la misma línea curva entre 25 y 57 mm. El residuo máximo de fase, calculado con longitudes de arco exactas en 2049 puntos, es de unos 2.04×10^-10 rad. El candidato está dentro del intervalo de búsqueda predeterminado de ±0.02 mm. Se trata de una comprobación directa de un candidato construido analíticamente; no se afirma que el optimizador actual vaya a devolver necesariamente esos mismos valores.

Por tanto, un residuo bajo puede limitarse a interpretar un error en la longitud de bit como un error en los radios. La función actual puede proporcionar parámetros de compensación útiles, pero deben entenderse como «parámetros equivalentes bajo la hipótesis de una densidad lineal fija». Para identificar los radios y la densidad físicos reales hacen falta anillos cuyo radio de origen sea conocido, posiciones de datos conocidas o mediciones independientes de los límites. Añadir sin más otro parámetro al ajuste no resuelve la falta de información.

<a id="发现四cd-交织补偿有有限流边界效应物理时序原点待验证"></a>
## Hallazgo 4: la compensación del entrelazado de CD tiene efectos de borde en flujos finitos; falta validar el origen temporal físico

La tabla de retardos de `CddaInterleaver.cs:8–16` coincide con las rutas de datos de la figura C.4 de ECMA-130; no es un entrelazado duplicado incorrectamente. Una simulación independiente mediante etiquetas volvió a introducir el PCM ya compensado en las 24 rutas de datos del CIRC estándar: las 20856 posiciones de datos en régimen estacionario de una prueba de 10 sectores coincidieron, pero el resultado combinado retrasa 111 tramas el patrón objetivo completo.

Deben distinguirse rigurosamente dos aspectos:

- **Efecto confirmado de flujo finito:** la inicialización introduce 1332 bytes de relleno a cero; no se emiten 1332 bytes del patrón objetivo distribuidos entre las últimas 108 tramas. El `Flush` de `CddaInterleaver.cs:46–55` sólo procesa el final del sector y, cuando la entrada contiene sectores completos, no vacía el búfer circular. Esto afecta a los extremos del patrón.
- **Desplazamiento físico condicionado:** las 111 tramas son el retardo combinado con la referencia temporal de la figura C.4: unos 15.102 ms, equivalentes a unos 18.122 mm de arco a 1200 mm/s. Si el origen temporal de la cadena de grabación no lo compensa, la diferencia angular varía con el radio y no puede eliminarse únicamente mediante el ángulo inicial. Todavía no se ha medido su relación con ATIP, INDEX 01 ni con el inicio real de los datos del disco, por lo que no debe presentarse como un desplazamiento físico ya demostrado.

`CdTrackGenerator.cs:229–246` toma muestras según `globalByte` sin incorporar esa referencia temporal, y la calibración geométrica tampoco incluye esta etapa de codificación. La documentación de compatibilidad ya indica que la vista previa no simula el CIRC/EFM completo. Hace falta validar la referencia temporal física de extremo a extremo; la coincidencia entre una generación y una vista previa basadas en el mismo modelo no demuestra por sí sola que la posición sobre el disco sea correcta.

<a id="发现五刻录介质类型没有与模型匹配"></a>
## Hallazgo 5: el tipo de soporte de grabación no se contrasta con el modelo

`WindowsImapiBurner.cs:151–166,377–390` sólo comprueba la compatibilidad con IMAPI, el estado de disco vacío y la capacidad; no consulta el tipo físico real de soporte. Sin embargo, el generador utiliza un ID de una sola capa (`DvdDataFrameBuilder.cs:41`) y una espiral unidireccional de una sola capa.

Falta, por tanto, un rechazo preventivo de formatos físicos distintos, como los DVD de doble capa o DVD-RAM. Que IMAPI pueda escribir en un soporte no significa que el modelo de dibujo actual sea aplicable; la posibilidad de iniciar realmente la escritura sigue dependiendo del dispositivo y de su estado. No están modelados el cambio de capa de un disco de doble capa, el sentido de la segunda capa ni su longitud de bit.

Se pueden utilizar [CurrentPhysicalMediaType](https://learn.microsoft.com/en-us/windows/win32/api/imapi2/nf-imapi2-idiscformat2data-get_currentphysicalmediatype) y la [enumeración de soportes físicos](https://learn.microsoft.com/en-us/windows/win32/api/imapi2/ne-imapi2-imapi_media_physical_type) de Microsoft para comprobar esa correspondencia.

<a id="建议的修正顺序"></a>
## Orden de corrección recomendado

1. Añadir una comprobación del paso implícito de la espiral a los ajustes predefinidos, distinguiendo valores iniciales de la norma, valores medidos y valores obtenidos por compensación; dar prioridad a la combinación predeterminada de CD80. Precisar a qué posición de datos corresponden los radios inicial y final, y separar el área de recorte del dibujo de los puntos de referencia de la pista.
2. Verificar conjuntamente los tiempos del entrelazado de CD, el inicio de la pista de audio y la cadena de grabación; después corregir el desplazamiento de muestreo y la estrategia de los extremos sin cambiar el número final de sectores.
3. Añadir a la calibración restricciones independientes de posición radial o de datos. Si los residuos medidos siguen mostrando una curvatura local sistemática, evaluar entonces un modelo de paso variable o de corrección de fase.
4. Antes de escribir, comprobar que el tipo físico de soporte y su capacidad coincidan con el modelo seleccionado.

No se realizaron grabaciones en discos reales ni mediciones de brillo óptico. La validación incluyó los textos y páginas originales de las normas, la repetición de los cálculos, comparaciones numéricas con la espiral exacta, un contraejemplo de parámetros no identificables y una simulación con etiquetas de las rutas de datos CIRC. Estos resultados no se presentan como una simulación óptica completa ni como una certificación de precisión en discos reales.

El material para reproducir la auditoría se encuentra en `tmp/standards-audit/`, en la raíz del espacio de trabajo: `circ-audit.py`, `circ-audit-result.json`, `geometry-audit.py`, `geometry-audit-result.json`, además de los textos extraídos y las páginas originales clave de dos PDF facilitados por el usuario.
