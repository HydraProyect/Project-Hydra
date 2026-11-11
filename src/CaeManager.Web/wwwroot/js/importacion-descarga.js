// Descarga un texto como fichero (los CSV de errores, valores previos e informe del Paso 1 del Importador). El BOM hace
// que Excel abra el CSV en UTF-8. El contenido sale del servidor ya construido: aquí solo se entrega al navegador.
export function descargarTexto(nombre, texto) {
    const blob = new Blob(['﻿', texto], { type: 'text/csv;charset=utf-8' });
    const url = URL.createObjectURL(blob);
    const enlace = document.createElement('a');
    enlace.href = url;
    enlace.download = nombre;
    document.body.appendChild(enlace);
    enlace.click();
    enlace.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}
