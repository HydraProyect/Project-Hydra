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
/// </summary>
public enum TipoAccesoDocumentoSensible
{
    Apertura = 0,
    VersionAnterior = 1,
    EntregaExtension = 2
}
