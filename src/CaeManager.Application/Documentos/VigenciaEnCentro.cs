using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos;

/// <summary>
/// El estado de un Documento <b>visto desde un Centro</b>: el estado de vigencia propio del Documento
/// (<see cref="CalculadoraEstadoDocumento"/>) más, si el Centro (o su Cliente empresarial titular) concede tolerancia, la
/// distinción «Vencido · en tolerancia hasta dd/MM» (<see cref="EstadoDocumento.EnTolerancia"/>). Solo lo usan las vistas con
/// contexto de Centro (Centro 360 y Trabajador 360 por Centro); las vistas generales, sin contexto, no lo aplican y dicen «Vencido».
///
/// <para>
/// No decide nada que no decida ya la regla de acceso: la comparación es <see cref="ReglaBloqueoDeAcceso.EnToleranciaHasta"/>
/// y las condiciones salen de la misma resolución (tolerancia del Centro, si no la del Cliente empresarial, si no 0;
/// <see cref="ReglaBloqueoDeAcceso.ResolverToleranciaDias"/>). Tampoco toca el porcentaje de cumplimiento: ese sigue midiendo el
/// estado de vigencia real del Documento.
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
    /// Las condiciones de un Centro para un Tipo: la vigencia propia y la tolerancia de la fila del Centro (solo si el Tipo
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
    /// El estado de un Documento en un Centro. Solo un Documento <see cref="EstadoDocumento.Vencido"/> puede pasar a
    /// <see cref="EstadoDocumento.EnTolerancia"/> (con el último día en que aún vale); cualquier otro estado se queda como está.
    /// </summary>
    public static (EstadoDocumento Estado, DateOnly? EnToleranciaHasta) Aplicar(
        EstadoDocumento estado,
        EstadoVigenciaDocumento estadoVigencia,
        DateOnly? fechaVencimiento,
        DateOnly fechaEmision,
        CondicionesDeAccesoDelCentro condiciones,
        DateOnly hoy)
    {
        if (estado != EstadoDocumento.Vencido)
            return (estado, null);

        var documento = new DocumentoParaAcceso(VigenciaDocumento.Rehidratar(estadoVigencia, fechaVencimiento), fechaEmision);
        return ReglaBloqueoDeAcceso.EnToleranciaHasta(documento, condiciones, hoy) is { } hasta
            ? (EstadoDocumento.EnTolerancia, hasta)
            : (estado, null);
    }
}
