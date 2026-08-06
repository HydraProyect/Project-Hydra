using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Documentos;

/// <summary>
/// Lectura centralizada de si un TipoDocumento aplica a un Centro concreto
/// (PLAN-EJECUCION-UX.md § 0.4) — antes duplicada en 4 sitios
/// (CalculoEstadoCentroService, ObtenerAsignacionesDocumentacionPorCentroQuery,
/// ObtenerDocumentacionVisitaQuery, IDocumentosFaltantesService) con la
/// semántica antigua de "allow-list global". Se extrae aquí porque los 4
/// sitios cambian a la vez a la semántica nueva y duplicarla otra vez
/// arriesgaba que se desincronizaran.
/// </summary>
public static class ResolucionTipoDocumentoCentro
{
    /// <param name="filaPorPar">
    /// Filas de <see cref="TipoDocumentoCentro"/> relevantes, indexadas por
    /// (TipoDocumentoId, CentroId) — normalmente el resultado de un
    /// <c>.ToDictionary(tc => (tc.TipoDocumentoId, tc.CentroId))</c> sobre
    /// las filas de los Centros/Tipos que interesan a la consulta.
    /// </param>
    /// <param name="esObligatorioPorDefecto">
    /// <see cref="TipoDocumento.EsObligatorio"/> del tipo — criterio con el
    /// que se resuelve el par cuando no hay fila explícita.
    /// </param>
    public static bool Aplica(
        IReadOnlyDictionary<(Guid TipoDocumentoId, Guid CentroId), TipoDocumentoCentro> filaPorPar,
        Guid tipoDocumentoId, Guid centroId, bool esObligatorioPorDefecto) =>
        filaPorPar.TryGetValue((tipoDocumentoId, centroId), out var fila) ? fila.Incluido : esObligatorioPorDefecto;
}
