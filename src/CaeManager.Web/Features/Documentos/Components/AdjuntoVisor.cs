namespace CaeManager.Web.Features.Documentos.Components;

/// <summary>Cómo va un adjunto en la cola del visor: lo que falta por revisar, o ya resuelto (asignado o rechazado).</summary>
public enum EstadoAdjuntoVisor
{
    Pendiente,
    Asignado,
    Rechazado
}

/// <summary>
/// Un adjunto tal como lo pinta <c>VisorDocumentoPantallaCompleta</c>. El visor no lee nada del servidor: quien lo abre le da la
/// lista ya resuelta. <paramref name="Url"/> es la dirección desde la que se descarga el archivo; <paramref name="Peso"/> llega ya
/// formateado («312 KB») porque el visor solo lo rotula.
/// </summary>
public sealed record AdjuntoVisor(
    Guid Id,
    string NombreArchivo,
    string Url,
    int Paginas = 1,
    string? Peso = null,
    EstadoAdjuntoVisor Estado = EstadoAdjuntoVisor.Pendiente);

/// <summary>La hoja que el visor está pintando: el adjunto, su número (desde 1) y el total de páginas del adjunto.</summary>
public sealed record HojaVisor(AdjuntoVisor Adjunto, int Numero, int Total);
