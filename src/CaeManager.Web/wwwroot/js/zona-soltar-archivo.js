// Puente entre arrastrar/soltar (o pegar con Ctrl+V) y el <input type=file>
// que ya usa InputFile de Blazor — nunca leemos bytes en JS ni duplicamos el
// pipeline de subida en C#: solo rellenamos input.files con el FileList
// soltado/pegado y disparamos 'change', así InputFile dispara OnChange
// exactamente igual que si el usuario hubiera usado el selector nativo.
export function inicializar(zonaElement, inputElement) {
    const prevenir = e => {
        e.preventDefault();
        e.stopPropagation();
    };

    const marcarActiva = () => zonaElement.classList.add('zona-soltar-archivo-activa');
    const desmarcarActiva = () => zonaElement.classList.remove('zona-soltar-archivo-activa');

    const manejarDrop = e => {
        prevenir(e);
        desmarcarActiva();
        if (e.dataTransfer && e.dataTransfer.files && e.dataTransfer.files.length > 0) {
            inputElement.files = e.dataTransfer.files;
            inputElement.dispatchEvent(new Event('change', { bubbles: true }));
        }
    };

    const manejarPaste = e => {
        const archivos = e.clipboardData && e.clipboardData.files;
        if (archivos && archivos.length > 0) {
            inputElement.files = archivos;
            inputElement.dispatchEvent(new Event('change', { bubbles: true }));
        }
    };

    ['dragenter', 'dragover', 'dragleave', 'drop'].forEach(evento => zonaElement.addEventListener(evento, prevenir));
    zonaElement.addEventListener('dragenter', marcarActiva);
    zonaElement.addEventListener('dragover', marcarActiva);
    zonaElement.addEventListener('dragleave', desmarcarActiva);
    zonaElement.addEventListener('drop', manejarDrop);
    zonaElement.addEventListener('paste', manejarPaste);

    return {
        dispose() {
            ['dragenter', 'dragover', 'dragleave', 'drop'].forEach(evento => zonaElement.removeEventListener(evento, prevenir));
            zonaElement.removeEventListener('dragenter', marcarActiva);
            zonaElement.removeEventListener('dragover', marcarActiva);
            zonaElement.removeEventListener('dragleave', desmarcarActiva);
            zonaElement.removeEventListener('drop', manejarDrop);
            zonaElement.removeEventListener('paste', manejarPaste);
        }
    };
}
