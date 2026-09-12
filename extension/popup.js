// UI del popup. No habla con Hydra directamente: todo pasa por mensajes al
// service worker (background.js), que es quien guarda el token y hace fetch.

const seccionConectar = document.getElementById("seccion-conectar");
const seccionConectado = document.getElementById("seccion-conectado");
const textoUrl = document.getElementById("texto-url");
const textoExpira = document.getElementById("texto-expira");
const botonDesconectar = document.getElementById("boton-desconectar");
const botonActualizar = document.getElementById("boton-actualizar");
const errorLista = document.getElementById("error-lista");
const vacioLista = document.getElementById("vacio-lista");
const listaProveedores = document.getElementById("lista-proveedores");

function enviarMensaje(mensaje) {
  return chrome.runtime.sendMessage(mensaje);
}

function mostrarError(elemento, texto) {
  elemento.textContent = texto;
  elemento.hidden = !texto;
}

async function inicializar() {
  const conexion = await enviarMensaje({ accion: "obtenerConexion" });

  seccionConectar.hidden = conexion.conectado;
  seccionConectado.hidden = !conexion.conectado;

  if (conexion.conectado) {
    textoUrl.textContent = conexion.hydraUrl;
    textoExpira.textContent = new Date(conexion.expiraEnUtc).toLocaleTimeString("es-ES", {
      hour: "2-digit",
      minute: "2-digit",
    });
    await cargarPendientesAsync();
  }
}

botonDesconectar.addEventListener("click", async () => {
  await enviarMensaje({ accion: "desconectar" });
  await inicializar();
});

botonActualizar.addEventListener("click", cargarPendientesAsync);

async function cargarPendientesAsync() {
  mostrarError(errorLista, "");
  vacioLista.hidden = true;
  listaProveedores.innerHTML = "";
  botonActualizar.disabled = true;

  try {
    const resultado = await enviarMensaje({ accion: "listarPendientes" });

    if (!resultado.ok) {
      mostrarError(errorLista, resultado.error);
      return;
    }

    if (resultado.proveedores.length === 0) {
      vacioLista.hidden = false;
      return;
    }

    for (const proveedor of resultado.proveedores) renderizarProveedor(proveedor);
  } finally {
    botonActualizar.disabled = false;
  }
}

function renderizarProveedor(proveedor) {
  const detalle = document.createElement("details");
  detalle.open = true;

  const resumen = document.createElement("summary");
  const totalDocumentos = proveedor.clientes.reduce((suma, c) => suma + c.documentos.length, 0);
  resumen.textContent = `${proveedor.proveedorNombre} (${totalDocumentos})`;
  detalle.appendChild(resumen);

  // Kill switch remoto (MVP2 § 14.5): proveedorActivo viene de
  // ObtenerAcreditacionesPorProveedorQuery, que a su vez lo lee de
  // ProveedorPlataformaCae.Activo — un dato de configuración que TALVEG
  // puede apagar sin publicar una extensión nueva. Aquí es donde de verdad
  // se frena la subida: ni se ofrece el botón, así que ni la descarga del
  // PDF ni la inyección en el DOM llegan a intentarse para este proveedor.
  if (proveedor.proveedorActivo === false) {
    const aviso = document.createElement("p");
    aviso.className = "aviso-conector-inactivo";
    aviso.textContent = "Conector desactivado temporalmente. Contacta con soporte si lo necesitas.";
    detalle.appendChild(aviso);
  }

  for (const cliente of proveedor.clientes) {
    const grupoCliente = document.createElement("div");
    grupoCliente.className = "grupo-cliente";

    const tituloCliente = document.createElement("p");
    tituloCliente.className = "titulo-cliente";
    tituloCliente.textContent = cliente.clienteNombre;
    grupoCliente.appendChild(tituloCliente);

    for (const documento of cliente.documentos)
      grupoCliente.appendChild(renderizarDocumento(documento, proveedor.proveedorActivo !== false));

    detalle.appendChild(grupoCliente);
  }

  listaProveedores.appendChild(detalle);
}

function renderizarDocumento(documento, proveedorActivo) {
  const fila = document.createElement("div");
  fila.className = "fila-documento";

  const descripcion = document.createElement("span");
  descripcion.textContent = `${documento.propietarioNombre} — ${documento.tipoDocumentoNombre}`;
  // El backend serializa el enum por nombre (JsonStringEnumConverter, ver
  // Program.cs), nunca por su valor ordinal.
  if (documento.estado === "Rechazada") {
    descripcion.textContent += " (rechazada antes)";
  }
  fila.appendChild(descripcion);

  // Ritmo humano (MVP2 § 14.5): un botón por documento, un clic, una subida
  // — nunca una selección múltiple ni un "subir todos". No se toca este
  // invariante sin una decisión explícita nueva: cada llamada a
  // subirDocumento debe nacer de un clic real del gestor sobre UN documento
  // concreto (ver el mismo comentario en background.js, subirDocumento).
  const boton = document.createElement("button");
  boton.type = "button";
  boton.textContent = "Subir";
  boton.disabled = !proveedorActivo;
  boton.addEventListener("click", () => subirAsync(documento, boton, fila));
  fila.appendChild(boton);

  return fila;
}

async function subirAsync(documento, boton, fila) {
  boton.disabled = true;
  boton.textContent = "Subiendo…";

  const resultado = await enviarMensaje({
    accion: "subirDocumento",
    documentoId: documento.documentoId,
    acreditacionId: documento.acreditacionId,
    nombreArchivo: `${documento.tipoDocumentoNombre}.pdf`,
  });

  if (!resultado.ok) {
    boton.disabled = false;
    boton.textContent = "Subir";
    const aviso = document.createElement("p");
    aviso.className = "error";
    aviso.textContent = resultado.error;
    fila.appendChild(aviso);
    return;
  }

  fila.classList.add("fila-completada");
  boton.textContent = "Subido";
  await cargarPendientesAsync();
}

inicializar();
