// Arnés de prueba compartido por las cuatro páginas de este portal.
//
// No forma parte de la extensión — es solo el lado "página" del puente de
// solo-QA que documenta el README (window.postMessage -> content script ->
// background.js). Sin la extensión conectada (o sin el puente añadido a una
// copia de trabajo de content.js), el botón simplemente agota el tiempo de
// espera: eso es correcto, no un fallo de este arnés.
function llamarExtension(mensaje, timeoutMs = 8000) {
  return new Promise((resolve, reject) => {
    const id = Math.random().toString(36);
    const timer = setTimeout(() => {
      window.removeEventListener("message", handler);
      reject(new Error("timeout — ¿está la extensión conectada y el puente de QA cargado?"));
    }, timeoutMs);
    function handler(event) {
      if (event.source !== window) return;
      if (event.data?.qaHydraRespuesta && event.data.id === id) {
        clearTimeout(timer);
        window.removeEventListener("message", handler);
        resolve(event.data.respuesta);
      }
    }
    window.addEventListener("message", handler);
    window.postMessage({ qaHydra: true, id, mensaje }, "*");
  });
}

// PDF mínimo de una página, el mismo de la QA del 2026-09-07.
const PDF_DE_PRUEBA_BASE64 =
  "JVBERi0xLjQKMSAwIG9iajw8L1R5cGUvQ2F0YWxvZy9QYWdlcyAyIDAgUj4+ZW5kb2JqCjIgMCBvYmo8PC9UeXBlL1BhZ2VzL0tpZHNbMyAwIFJdL0NvdW50IDE+PmVuZG9iagozIDAgb2JqPDwvVHlwZS9QYWdlL1BhcmVudCAyIDAgUi9NZWRpYUJveFswIDAgMjAwIDIwMF0vUmVzb3VyY2VzPDw+Pj4+ZW5kb2JqCnRyYWlsZXI8PC9TaXplIDQvUm9vdCAxIDAgUj4+CiUlRU9GCg==";

function conectarBotonInyectar(idBoton, idSalida) {
  const boton = document.getElementById(idBoton);
  const salida = document.getElementById(idSalida);
  boton.addEventListener("click", async () => {
    salida.textContent = "Inyectando…";
    try {
      const resultado = await llamarExtension({
        accion: "inyectarArchivoLocal",
        base64: PDF_DE_PRUEBA_BASE64,
        nombreArchivo: "Certificado de prueba.pdf",
        tipoMime: "application/pdf",
      });
      salida.textContent = JSON.stringify(resultado);
      salida.style.color = resultado.ok ? "green" : "crimson";
    } catch (error) {
      salida.textContent = `ERROR: ${error.message}`;
      salida.style.color = "crimson";
    }
  });
}
