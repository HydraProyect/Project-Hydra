// Set up event handlers
const reconnectModal = document.getElementById("components-reconnect-modal");
reconnectModal.addEventListener("components-reconnect-state-changed", handleReconnectStateChanged);

const retryButton = document.getElementById("components-reconnect-button");
retryButton.addEventListener("click", retry);

const resumeButton = document.getElementById("components-resume-button");
resumeButton.addEventListener("click", resume);

const rejectedButton = document.getElementById("components-reconnect-rejected-button");
rejectedButton.addEventListener("click", () => location.reload());

function handleReconnectStateChanged(event) {
    if (event.detail.state === "show") {
        reconnectModal.showModal();
    } else if (event.detail.state === "hide") {
        reconnectModal.close();
    } else if (event.detail.state === "failed") {
        document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    } else if (event.detail.state === "rejected") {
        mostrarSesionPerdida();
    }
}

/**
 * El servidor está disponible pero no pudo recuperar la sesión (circuito
 * desconocido/expirado) — recargar es la única salida, pero hacerlo con
 * location.reload() sin avisar borra de golpe cualquier campo que el
 * usuario tuviera a medio escribir, sin que lo vea venir. En vez de eso, se
 * muestra el aviso y se espera un clic explícito en "Recargar la página".
 */
function mostrarSesionPerdida() {
    document.removeEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    reconnectModal.classList.remove(
        "components-reconnect-retrying",
        "components-reconnect-failed",
        "components-reconnect-paused",
        "components-reconnect-resume-failed");
    reconnectModal.classList.add("components-reconnect-rejected");
    if (!reconnectModal.open) reconnectModal.showModal();
}

async function retry() {
    document.removeEventListener("visibilitychange", retryWhenDocumentBecomesVisible);

    try {
        // Reconnect will asynchronously return:
        // - true to mean success
        // - false to mean we reached the server, but it rejected the connection (e.g., unknown circuit ID)
        // - exception to mean we didn't reach the server (this can be sync or async)
        const successful = await Blazor.reconnect();
        if (!successful) {
            // Se ha podido contactar al servidor, pero el circuito ya no
            // existe — ver mostrarSesionPerdida() para el motivo de no
            // recargar en silencio aquí.
            const resumeSuccessful = await Blazor.resumeCircuit();
            if (!resumeSuccessful) {
                mostrarSesionPerdida();
            } else {
                reconnectModal.close();
            }
        }
    } catch (err) {
        // We got an exception, server is currently unavailable
        document.addEventListener("visibilitychange", retryWhenDocumentBecomesVisible);
    }
}

async function resume() {
    try {
        const successful = await Blazor.resumeCircuit();
        if (!successful) {
            mostrarSesionPerdida();
        }
    } catch {
        reconnectModal.classList.replace("components-reconnect-paused", "components-reconnect-resume-failed");
    }
}

async function retryWhenDocumentBecomesVisible() {
    if (document.visibilityState === "visible") {
        await retry();
    }
}
