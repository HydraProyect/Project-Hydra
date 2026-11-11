// Descarga del PDF unido por la herramienta «Unir PDF». El contenido lo genera el circuito de Blazor y no existe en ningún
// endpoint: llega por un DotNetStreamReference y se entrega como un fichero local del navegador, sin pasar por otra ruta.
// Solo presentación: ningún dato de negocio se guarda aquí.

export async function descargarPdf(nombre, referencia) {
    const buffer = await referencia.arrayBuffer();
    const blob = new Blob([buffer], { type: 'application/pdf' });
    const url = URL.createObjectURL(blob);
    const enlace = document.createElement('a');
    enlace.href = url;
    enlace.download = nombre || 'unido.pdf';
    enlace.style.display = 'none';
    document.body.appendChild(enlace);
    enlace.click();
    enlace.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}
