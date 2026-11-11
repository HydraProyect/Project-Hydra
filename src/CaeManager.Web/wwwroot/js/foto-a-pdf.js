// Foto a PDF: entrega el PDF generado como descarga del navegador. El PDF llega como flujo desde el circuito, se
// convierte en un Blob local y no se guarda en ningún sitio: la dirección temporal se revoca en cuanto empieza la descarga.
export async function descargar(nombre, flujo) {
    const contenido = await flujo.arrayBuffer();
    const blob = new Blob([contenido], { type: 'application/pdf' });
    const url = URL.createObjectURL(blob);
    const enlace = document.createElement('a');
    enlace.href = url;
    enlace.download = nombre;
    document.body.appendChild(enlace);
    enlace.click();
    enlace.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}
