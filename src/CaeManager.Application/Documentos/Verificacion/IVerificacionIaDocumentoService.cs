using CaeManager.Domain.DocumentosIa;

namespace CaeManager.Application.Documentos.Verificacion;

/// <summary>
/// Qué se encoló: el Documento, cuándo y en qué versión. Sin la hora y la
/// versión del encolado, un análisis que llega tarde no puede saber si
/// alguien decidió el Documento a mano en el intervalo.
/// </summary>
public sealed record EncargoVerificacionIa(Guid DocumentoId, DateTime EncoladoEnUtc, Guid? VersionDocumentoEncolada)
{
    public static EncargoVerificacionIa De(TrabajoAnalisisDocumento trabajo) =>
        new(trabajo.DocumentoId, trabajo.CreadoEnUtc, trabajo.VersionDocumentoEncolada);
}

public interface IVerificacionIaDocumentoService
{
    /// <summary>
    /// Devuelve el motivo de descarte cuando el resultado llegó tarde (ver
    /// <see cref="TrabajoAnalisisDocumento.MarcarDescartado"/>) y no escribió
    /// nada; <c>null</c> cuando se aplicó o no había nada que verificar.
    /// </summary>
    Task<string?> ProcesarDocumentoAsync(EncargoVerificacionIa encargo, CancellationToken cancellationToken = default);
}
