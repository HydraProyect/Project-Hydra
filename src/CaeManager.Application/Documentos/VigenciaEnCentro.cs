using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos;

/// <summary>
/// Los datos con que se evalúa un Documento <b>visto desde un Centro</b>: la tolerancia y la periodicidad especial de ese Centro
/// y la última presentación del Documento en él. El estado de contexto (Vigente, Próximo, Urgente, «Vencido · en tolerancia hasta
/// dd/MM» o Vencido en ese Centro) lo decide <see cref="ReglaBloqueoDeAcceso.EstadoEnElCentro"/>; solo lo usan las vistas con
/// contexto de Centro (Centro 360 y Trabajador 360 por Centro); las vistas generales, sin contexto, no lo aplican y dicen «Vencido».
///
/// <para>
/// No decide nada que no decida ya la regla de acceso: las condiciones salen de la misma resolución (periodicidad y tolerancia
/// del Centro, si no la tolerancia del Cliente empresarial, si no 0; <see cref="ReglaBloqueoDeAcceso.ResolverToleranciaDias"/>).
/// Tampoco toca el porcentaje de cumplimiento: ese sigue midiendo el estado de vigencia real del Documento.
/// </para>
/// </summary>
public static class VigenciaEnCentro
{
    /// <summary>
    /// La tolerancia por defecto de cada Cliente empresarial dado para cada Tipo dado. Sin fila, no hay entrada (tolerancia 0).
    /// El aislamiento entre Tenants lo da RLS.
    /// </summary>
    public static async Task<IReadOnlyDictionary<(Guid ClienteEmpresarialId, Guid TipoDocumentoId), int>> CargarToleranciasDeClientesAsync(
        ITiposDocumentoQueryContext tiposDocumentoContext,
        IReadOnlyCollection<Guid> clienteEmpresarialIds,
        IReadOnlyCollection<Guid> tipoDocumentoIds,
        CancellationToken cancellationToken)
    {
        if (clienteEmpresarialIds.Count == 0 || tipoDocumentoIds.Count == 0)
            return new Dictionary<(Guid, Guid), int>();

        return (await tiposDocumentoContext.ToleranciasDocumentoClienteEmpresarial
            .Where(t => clienteEmpresarialIds.Contains(t.ClienteEmpresarialId) && tipoDocumentoIds.Contains(t.TipoDocumentoId))
            .Select(t => new { t.ClienteEmpresarialId, t.TipoDocumentoId, t.ToleranciaDias })
            .ToListAsync(cancellationToken))
            .ToDictionary(t => (t.ClienteEmpresarialId, t.TipoDocumentoId), t => t.ToleranciaDias);
    }

    /// <summary>
    /// Las condiciones de un Centro para un Tipo: la periodicidad especial y la tolerancia de la fila del Centro (solo si el Tipo
    /// está incluido en él) y, sin personalización, la del Cliente empresarial titular.
    /// </summary>
    public static CondicionesDeAccesoDelCentro Condiciones(TipoDocumentoCentro? filaDelCentro, int? toleranciaDelClienteEmpresarial)
    {
        var fila = filaDelCentro is { Incluido: true } ? filaDelCentro : null;
        return new CondicionesDeAccesoDelCentro(
            fila?.PeriodicidadEspecialMeses,
            ReglaBloqueoDeAcceso.ResolverToleranciaDias(fila?.ToleranciaDias, toleranciaDelClienteEmpresarial));
    }

    /// <summary>
    /// La fecha de la última presentación de cada Documento en cada Centro (<see cref="PresentacionDocumentoEnCentro"/>): el
    /// ancla de la periodicidad especial de un Centro. Sin entrada para un par, nunca se presentó allí y el ancla es la emisión.
    /// El único cargador: el bloqueo, Centro 360 y Trabajador 360 por Centro lo usan para pasar
    /// <see cref="DocumentoParaAcceso.UltimaPresentacionEnElCentro"/> a <see cref="ReglaBloqueoDeAcceso"/>. El aislamiento entre
    /// Tenants lo da RLS.
    /// </summary>
    public static async Task<IReadOnlyDictionary<(Guid DocumentoId, Guid CentroId), DateOnly>> CargarUltimasPresentacionesAsync(
        IDocumentosQueryContext documentosContext,
        IReadOnlyCollection<Guid> documentoIds,
        IReadOnlyCollection<Guid> centroIds,
        CancellationToken cancellationToken)
    {
        if (documentoIds.Count == 0 || centroIds.Count == 0)
            return new Dictionary<(Guid, Guid), DateOnly>();

        return (await documentosContext.PresentacionesDocumentoEnCentro
            .Where(p => documentoIds.Contains(p.DocumentoId) && centroIds.Contains(p.CentroId))
            .GroupBy(p => new { p.DocumentoId, p.CentroId })
            .Select(g => new { g.Key.DocumentoId, g.Key.CentroId, Ultima = g.Max(p => p.FechaPresentacion) })
            .ToListAsync(cancellationToken))
            .ToDictionary(p => (p.DocumentoId, p.CentroId), p => p.Ultima);
    }

    /// <summary>
    /// El Documento tal como lo evalúa la regla en un Centro: su vigencia, su emisión y la última presentación allí. Sin
    /// presentación registrada en ese Centro, <paramref name="presentaciones"/> no tiene el par y el ancla es la emisión.
    /// </summary>
    public static DocumentoParaAcceso DocumentoEnElCentro(
        Guid documentoId, Guid centroId, EstadoVigenciaDocumento estadoVigencia, DateOnly? fechaVencimiento, DateOnly fechaEmision,
        IReadOnlyDictionary<(Guid DocumentoId, Guid CentroId), DateOnly> presentaciones) =>
        new(VigenciaDocumento.Rehidratar(estadoVigencia, fechaVencimiento), fechaEmision,
            presentaciones.TryGetValue((documentoId, centroId), out var ultima) ? ultima : null);
}
