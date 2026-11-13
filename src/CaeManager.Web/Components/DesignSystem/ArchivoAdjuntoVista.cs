namespace CaeManager.Web.Components.DesignSystem;

/// <summary>
/// Un archivo ya adjuntado tal como lo enseña <c>ZonaSoltarArchivo</c> cuando quien la usa le
/// pasa <c>ArchivosAdjuntos</c>: nombre, tamaño y tipo. <paramref name="Tipo"/> es la etiqueta
/// corta a mostrar (p. ej. «PDF»); si es null, la zona la deduce de la extensión del nombre.
/// </summary>
public sealed record ArchivoAdjuntoVista(string Nombre, long? TamanoBytes = null, string? Tipo = null);
