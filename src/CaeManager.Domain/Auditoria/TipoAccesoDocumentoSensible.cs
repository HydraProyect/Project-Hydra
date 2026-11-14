namespace CaeManager.Domain.Auditoria;

/// <summary>
/// Cómo se accedió al contenido — DEC-36 (REC-099): «abrir/visualizar;
/// descargar; previsualizar si entrega contenido; exportar». El PDF que sirve
/// <c>IFileStorageService.AbrirAsync</c> no distingue en el servidor entre
/// abrir y descargar (el navegador decide cómo mostrarlo), así que ambos caen
/// en <see cref="Apertura"/>; <see cref="VersionAnterior"/> es el único caso
/// donde el propio punto de servicio ya sabe que el contenido es una versión
/// pasada del archivo, no la vigente. <see cref="EntregaExtension"/> es el archivo
/// que la extensión de navegador recibe para ponerlo en el formulario de una
/// plataforma CAE de terceros: el contenido sale hacia un tercero si el Gestor CAE
/// lo envía, y el registro tiene que poder distinguirlo de una apertura en TALVEG.
/// <see cref="RevisionIa"/> es el documento que la pantalla Revisión IA carga sola al seleccionar una revisión pendiente
/// (D-49, opción C): lo sirve una ruta propia, ligada a la revisión, y la fila se deduplica por persona (Usuario, Actor
/// real y vía) y Documento, no una por petición: como mucho una fila por persona y revisión pendiente: con una sola
/// revisión pendiente del Documento se deduplica por persona y Documento desde que se creó; con varias, cada acceso deja
/// fila, porque la fila no identifica la revisión; peticiones simultáneas pueden dejar más, nunca menos; cada acceso
/// cuenta para la señal de extracción masiva, se escriba la fila o no. Una revisión creada después de la última fila de
/// esa persona deja otra fila (es un acto de revisión nuevo). Lo decide el servidor por la ruta, nunca un dato
/// del cliente; «Descargar» y cualquier otra vía siguen siendo <see cref="Apertura"/>.
/// </summary>
public enum TipoAccesoDocumentoSensible
{
    Apertura = 0,
    VersionAnterior = 1,
    EntregaExtension = 2,
    RevisionIa = 3
}
