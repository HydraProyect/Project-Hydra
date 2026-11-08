using CaeManager.Domain.Documentos;
using CaeManager.Domain.Trabajadores;

namespace CaeManager.Application.Documentos;

/// <summary>
/// Lectura centralizada de si un TipoDocumento aplica a un Centro concreto
/// (Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md § 0.4) — antes duplicada en 4 sitios
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

    /// <summary>
    /// Igual que la sobrecarga general, pero para un Trabajador concreto: sin fila explícita, el tipo se pide si
    /// <paramref name="esObligatorioPorDefecto"/> y su serie por tipo de contratación
    /// (<see cref="TipoDocumento.TiposContratacionDeSerie"/>) incluye el del Trabajador. La fila explícita del
    /// Centro manda igual que siempre: la serie solo filtra lo que se pide de serie. Úsala en toda superficie que
    /// decide exigencias de Ámbito Trabajador para un Trabajador (estado del Centro, faltantes, avisos, Subcontrata,
    /// ficha); <paramref name="tiposContratacionDeSerie"/> debe ser <see cref="TiposContratacion.Todos"/> para tipos
    /// de otro ámbito, que es su valor persistido.
    /// </summary>
    public static bool Aplica(
        IReadOnlyDictionary<(Guid TipoDocumentoId, Guid CentroId), TipoDocumentoCentro> filaPorPar,
        Guid tipoDocumentoId, Guid centroId, bool esObligatorioPorDefecto,
        TiposContratacion tiposContratacionDeSerie, TipoContratacion tipoContratacion) =>
        Aplica(filaPorPar, tipoDocumentoId, centroId,
            esObligatorioPorDefecto && tiposContratacionDeSerie.Incluye(tipoContratacion));

    /// <summary>
    /// Igual que la sobrecarga general, pero sobre el <see cref="TipoDocumentoCentro.Incluido"/> de cada fila explícita
    /// y no sobre la entidad: para quien lee las filas con una proyección, que siempre trae lo confirmado en la base y
    /// no una instancia que el contexto rastreara antes (revisión Codex de la PR #2074, ronda 4, hallazgo 1).
    /// </summary>
    public static bool Aplica(
        IReadOnlyDictionary<(Guid TipoDocumentoId, Guid CentroId), bool> incluidoPorPar,
        Guid tipoDocumentoId, Guid centroId, bool esObligatorioPorDefecto) =>
        incluidoPorPar.TryGetValue((tipoDocumentoId, centroId), out var incluido) ? incluido : esObligatorioPorDefecto;

    /// <summary>Igual que la sobrecarga por Trabajador, sobre el <c>Incluido</c> de cada fila explícita.</summary>
    public static bool Aplica(
        IReadOnlyDictionary<(Guid TipoDocumentoId, Guid CentroId), bool> incluidoPorPar,
        Guid tipoDocumentoId, Guid centroId, bool esObligatorioPorDefecto,
        TiposContratacion tiposContratacionDeSerie, TipoContratacion tipoContratacion) =>
        Aplica(incluidoPorPar, tipoDocumentoId, centroId,
            esObligatorioPorDefecto && tiposContratacionDeSerie.Incluye(tipoContratacion));
}
