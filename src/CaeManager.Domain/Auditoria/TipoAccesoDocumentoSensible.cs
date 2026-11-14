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
/// (D-49, opción C): lo sirve una ruta propia, ligada a la revisión, y se deduplica por persona (Usuario, Actor real y
/// vía) y Documento, no una fila por petición: como mucho una fila por persona y Documento por cada revisión pendiente y
/// dentro de la ventana de detección de extracción masiva; peticiones simultáneas pueden dejar más, nunca menos. Una
/// revisión nueva del mismo Documento deja otra fila para la misma persona (es un acto de revisión nuevo), y pasada la
/// ventana la relectura vuelve a registrarse y a contar para la señal. Lo decide el servidor por la ruta, nunca un dato del
/// cliente; «Descargar» y cualquier otra vía siguen siendo <see cref="Apertura"/>.
/// </summary>
public enum TipoAccesoDocumentoSensible
{
    Apertura = 0,
    VersionAnterior = 1,
    EntregaExtension = 2,
    RevisionIa = 3
}
