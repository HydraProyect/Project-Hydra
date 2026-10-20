using CaeManager.Application.Dashboard.Queries;
using CaeManager.Domain.Dashboard;

namespace CaeManager.Application.Dashboard;

/// <summary>Una versión de la tarifa del Operador CAE externo para una Empresa gestionada (ver <see cref="TarifaOperadorCae"/>).</summary>
public record TarifaOperadorCaeDto(
    Guid PropietarioTenantId, ConceptoTarifaOperadorCae Concepto, decimal PrecioUnitario, string MonedaIso,
    bool Activa, DateTime VigenteDesdeUtc);

/// <summary>
/// Valoración pura de lo que el Operador CAE externo cobra a una Empresa gestionada en un mes: las unidades salen
/// de la medición del Cuadro de dirección (en vivo o de la foto del cierre) y el precio de la versión de
/// <see cref="TarifaOperadorCae"/> vigente en el instante de valoración. No usa <c>TarifaCliente</c> ni el resumen
/// de «Facturación»: aquello es lo que cada Tenant cobra a sus Clientes empresariales (decisión de Chris, 2026-10-20).
/// </summary>
public static class ValoracionOperadorCae
{
    public static IReadOnlyList<ConceptoTarifaOperadorCae> Conceptos { get; } = Enum.GetValues<ConceptoTarifaOperadorCae>();

    /// <summary>
    /// Las unidades de un concepto en una medición; null si la medición no las lleva (una foto de cierre anterior al
    /// campo), y entonces el concepto no se valora: no se reconstruye ni se inventa. Es el único sitio que decide qué
    /// cifra se cobra por cada concepto.
    /// </summary>
    public static int? UnidadesDe(MedicionCuadroDireccion medicion, ConceptoTarifaOperadorCae concepto) => concepto switch
    {
        ConceptoTarifaOperadorCae.TrabajadorExtranjeroGestionado => medicion.ExtranjerosActivos,
        ConceptoTarifaOperadorCae.CentroNuevo => medicion.AltasCentros,
        ConceptoTarifaOperadorCae.ObraNueva => medicion.ObrasNuevas,
        _ => null,
    };

    public static string NombreDe(ConceptoTarifaOperadorCae concepto) => concepto switch
    {
        ConceptoTarifaOperadorCae.TrabajadorExtranjeroGestionado => "Trabajador extranjero gestionado",
        ConceptoTarifaOperadorCae.CentroNuevo => "Centro nuevo",
        ConceptoTarifaOperadorCae.ObraNueva => "Obra nueva",
        _ => concepto.ToString(),
    };

    /// <summary>La versión vigente en <paramref name="instanteUtc"/>: la más reciente que empezó antes o en ese instante.</summary>
    public static TarifaOperadorCaeDto? VigenteEn(
        IEnumerable<TarifaOperadorCaeDto> versiones, Guid propietarioTenantId, ConceptoTarifaOperadorCae concepto, DateTime instanteUtc) =>
        versiones
            .Where(v => v.PropietarioTenantId == propietarioTenantId && v.Concepto == concepto && v.VigenteDesdeUtc <= instanteUtc)
            .MaxBy(v => v.VigenteDesdeUtc);

    /// <summary>
    /// Las líneas valoradas de una Empresa gestionada en un mes: un renglón por concepto con tarifa activa en
    /// <paramref name="instanteUtc"/> y unidades medidas. Un concepto desactivado, sin tarifa o sin unidades en la
    /// foto no aparece; sus unidades se siguen viendo en las cifras del Cuadro.
    /// </summary>
    public static IReadOnlyList<LineaFacturacionCuadroDto> Valorar(
        MedicionCuadroDireccion medicion, Guid propietarioTenantId, IEnumerable<TarifaOperadorCaeDto> versiones, DateTime instanteUtc)
    {
        var lista = versiones as IReadOnlyCollection<TarifaOperadorCaeDto> ?? versiones.ToList();
        var lineas = new List<LineaFacturacionCuadroDto>();
        foreach (var concepto in Conceptos)
        {
            var tarifa = VigenteEn(lista, propietarioTenantId, concepto, instanteUtc);
            if (tarifa is not { Activa: true } || UnidadesDe(medicion, concepto) is not { } unidades)
                continue;
            lineas.Add(new LineaFacturacionCuadroDto(
                concepto, NombreDe(concepto), tarifa.MonedaIso, unidades, unidades * tarifa.PrecioUnitario));
        }

        return lineas;
    }
}
