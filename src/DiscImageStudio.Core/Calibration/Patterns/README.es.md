<a id="固定标定参考图"></a>
# Imagen de referencia fija para calibración

> Traducción al español · [Original](README.md)

`target_slim.png` es una copia sin cambios de la imagen indicada por la persona que la aportó: RGB de 2400×2400. SHA-256:

```text
c98ffe87db7cf9c3f132352814e1ce611c44f704de5b7212da4083188257c829
```

Los tres canales del original son idénticos píxel a píxel. `target_slim.gray.gz` guarda sus píxeles de gris de 8 bits sin binarizar, ordenados por filas y comprimidos con GZip; descomprimido ocupa 5.760.000 bytes, con SHA-256:

```text
3d351bdfb817b943c1d1b4aedc97fc4f0a111e7e1537748f70cb5b180ba96852
```

La imagen se escala al radio exterior de generación con las mismas coordenadas de píxel del codificador normal: el centro de píxel de referencia es `1199.5` para DVD y `1200` para CD. Las coordenadas centradas en el disco son
`((x-centre)*radioExterior/centre, (y-centre)*radioExterior/centre)` mm, con Y hacia abajo. Los nueve círculos tienen radios de referencia de
490, 570, 650, 730, 810, 890, 970, 1050 y 1130 píxeles; el trazo tiene unos 10 píxeles de anchura y las dimensiones en milímetros varían proporcionalmente al radio exterior de generación.

La codificación, la vista previa y la exportación PNG comparten esta imagen: primero se escala al radio exterior y después se recorta la zona de dibujo entre ambos radios. El fondo de disco de 120 mm de la vista previa no determina la escala de codificación. La imagen no se ha interpretado y vuelto a dibujar. `CalibrationEncoderTests` comprueba el hash PNG original, cada píxel de gris, la vista previa nativa y la exportación, y la coherencia de archivos y flujos CD/DVD.
