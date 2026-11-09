// Descarga de un archivo servido por un endpoint autenticado de la propia aplicación, por fetch en vez de por un enlace con
// «download»: si el servidor responde con un error (403, 409…), un enlace lo deja en la barra de descargas del navegador y
// la pantalla no se entera; así el componente recibe el estado y el texto y lo enseña. Solo rutas relativas de la misma
// aplicación (las cookies van con credentials: 'same-origin'); sin seguir redirecciones, que serían el inicio de sesión.

function nombreDeContentDisposition(cabecera) {
    if (!cabecera) return null;
    const extendido = /filename\*\s*=\s*UTF-8''([^;]+)/i.exec(cabecera);
    if (extendido) {
        try { return decodeURIComponent(extendido[1].trim()); } catch { /* cae al nombre simple */ }
    }
    const simple = /filename\s*=\s*("?)([^";]+)\1/i.exec(cabecera);
    return simple ? simple[2].trim() : null;
}

export async function descargar(ruta, nombrePorDefecto) {
    // Solo una ruta de la propia aplicación: «//host» y «/\host» (que el navegador normaliza a «//host») salen fuera.
    if (typeof ruta !== 'string' || !ruta.startsWith('/') || ruta.startsWith('//') || ruta.includes('\\')) {
        return { ok: false, estado: 0, mensaje: null };
    }

    let respuesta;
    try {
        respuesta = await fetch(ruta, { credentials: 'same-origin', redirect: 'manual' });
    } catch {
        return { ok: false, estado: 0, mensaje: null };
    }

    if (!respuesta.ok) {
        const tipo = respuesta.headers.get('content-type') || '';
        let mensaje = null;
        if (tipo.startsWith('text/plain')) {
            try { mensaje = (await respuesta.text()).slice(0, 500); } catch { mensaje = null; }
        }
        return { ok: false, estado: respuesta.status, mensaje };
    }

    const blob = await respuesta.blob();
    const nombre = nombreDeContentDisposition(respuesta.headers.get('content-disposition')) || nombrePorDefecto || 'descarga';
    const url = URL.createObjectURL(blob);
    const enlace = document.createElement('a');
    enlace.href = url;
    enlace.download = nombre;
    enlace.style.display = 'none';
    document.body.appendChild(enlace);
    enlace.click();
    enlace.remove();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
    return { ok: true, estado: respuesta.status, mensaje: null };
}
