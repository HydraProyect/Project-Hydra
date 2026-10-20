using CaeManager.Application.Dashboard.Queries;
using CaeManager.Domain.Dashboard;

namespace CaeManager.Application.Dashboard;

/// <summary>Una versión de la tarifa del Operador CAE externo para un Tenant beneficiario (ver <see cref="TarifaOperadorCae"/>).</summary>
public record TarifaOperadorCaeDto(
    Guid PropietarioTenantId, ConceptoTarifaOperadorCae Concepto, decimal PrecioUnitario, string MonedaIso,
    bool Activa, DateTime VigenteDesdeUtc);

/// <summary>Un trozo de mes [DesdeUtc, HastaUtc) en que la Asignación de Operación del Operador CAE estuvo en vigor.</summary>
public record TramoDeVigencia(DateTime DesdeUtc, DateTime HastaUtc);

/// <summary>Una vigencia de Asignación de Operación: desde, y hasta (exclusivo) o null si sigue abierta.</summary>
public record VigenciaAsignacionOperacion(DateTime DesdeUtc, DateTime? HastaUtc);

/// <summary>Las unidades que valora el Operador CAE externo, contadas solo dentro de los tramos de vigencia.</summary>
public record UnidadesOperadorCae(int AltasCentros, int? ObrasNuevas, int? VisitasTrabajadorExtranjero);

/// <summary>
/// Valoración pura de lo que el Operador CAE externo cobra a un Tenant beneficiario en un mes: las unidades salen
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
    /// cifra se cobra por cada concepto: la visita de Trabajador extranjero cuenta Trabajador por Visita
    /// (<see cref="MedicionCuadroDireccion.VisitasTrabajadorExtranjero"/>), no las personas de la cifra «Extranjeros».
    /// </summary>
    public static int? UnidadesDe(MedicionCuadroDireccion medicion, ConceptoTarifaOperadorCae concepto) => concepto switch
    {
        ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero => medicion.VisitasTrabajadorExtranjero,
        ConceptoTarifaOperadorCae.CentroNuevo => medicion.AltasCentros,
        ConceptoTarifaOperadorCae.ObraNueva => medicion.ObrasNuevas,
        _ => null,
    };

    public static string NombreDe(ConceptoTarifaOperadorCae concepto) => concepto switch
    {
        ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero => "Visita de Trabajador extranjero",
        ConceptoTarifaOperadorCae.CentroNuevo => "Centro nuevo",
        ConceptoTarifaOperadorCae.ObraNueva => "Obra nueva",
        _ => concepto.ToString(),
    };

    /// <summary>
    /// Los trozos de [<paramref name="inicioMesUtc"/>, <paramref name="finMesUtc"/>) cubiertos por alguna vigencia de
    /// Asignación de Operación, recortados al mes, ordenados y unidos si se solapan o se tocan. Vacío si ninguna
    /// estuvo en vigor en el mes: entonces el Operador no cobra ese mes (decisión de la coordinadora, 2026-10-20).
    /// </summary>
    public static IReadOnlyList<TramoDeVigencia> TramosEnElMes(
        IEnumerable<VigenciaAsignacionOperacion> vigencias, DateTime inicioMesUtc, DateTime finMesUtc)
    {
        var recortados = vigencias
            .Select(v => new TramoDeVigencia(
                v.DesdeUtc > inicioMesUtc ? v.DesdeUtc : inicioMesUtc,
                v.HastaUtc is { } hasta && hasta < finMesUtc ? hasta : finMesUtc))
            .Where(t => t.DesdeUtc < t.HastaUtc)
            .OrderBy(t => t.DesdeUtc);

        var tramos = new List<TramoDeVigencia>();
        foreach (var tramo in recortados)
        {
            if (tramos.Count > 0 && tramo.DesdeUtc <= tramos[^1].HastaUtc)
            {
                if (tramo.HastaUtc > tramos[^1].HastaUtc)
                    tramos[^1] = tramos[^1] with { HastaUtc = tramo.HastaUtc };
            }
            else
            {
                tramos.Add(tramo);
            }
        }

        return tramos;
    }

    /// <summary>Si los tramos cubren el mes entero: entonces vale la medición del mes tal cual.</summary>
    public static bool CubreElMes(IReadOnlyList<TramoDeVigencia> tramos, DateTime inicioMesUtc, DateTime finMesUtc) =>
        tramos.Count == 1 && tramos[0].DesdeUtc <= inicioMesUtc && tramos[0].HastaUtc >= finMesUtc;

    /// <summary>La medición con las unidades del Operador sustituidas por las contadas dentro de su vigencia.</summary>
    public static MedicionCuadroDireccion AcotadaA(MedicionCuadroDireccion medicion, UnidadesOperadorCae unidades) =>
        medicion with
        {
            AltasCentros = unidades.AltasCentros,
            ObrasNuevas = unidades.ObrasNuevas,
            VisitasTrabajadorExtranjero = unidades.VisitasTrabajadorExtranjero,
        };

    /// <summary>La versión vigente en <paramref name="instanteUtc"/>: la más reciente que empezó antes o en ese instante.</summary>
    public static TarifaOperadorCaeDto? VigenteEn(
        IEnumerable<TarifaOperadorCaeDto> versiones, Guid propietarioTenantId, ConceptoTarifaOperadorCae concepto, DateTime instanteUtc) =>
        versiones
            .Where(v => v.PropietarioTenantId == propietarioTenantId && v.Concepto == concepto && v.VigenteDesdeUtc <= instanteUtc)
            .MaxBy(v => v.VigenteDesdeUtc);

    /// <summary>
    /// Las líneas valoradas de un Tenant beneficiario en un mes: un renglón por concepto con tarifa vigente en
    /// <paramref name="instanteUtc"/> y unidades medidas. Con el cobro desactivado el renglón sigue saliendo con sus
    /// unidades y el importe a cero: las unidades se muestran siempre (regla de Chris). Un concepto sin tarifa o sin
    /// unidades en la foto no aparece.
    /// </summary>
    public static IReadOnlyList<LineaFacturacionCuadroDto> Valorar(
        MedicionCuadroDireccion medicion, Guid propietarioTenantId, IEnumerable<TarifaOperadorCaeDto> versiones, DateTime instanteUtc)
    {
        var lista = versiones as IReadOnlyCollection<TarifaOperadorCaeDto> ?? versiones.ToList();
        var lineas = new List<LineaFacturacionCuadroDto>();
        foreach (var concepto in Conceptos)
        {
            var tarifa = VigenteEn(lista, propietarioTenantId, concepto, instanteUtc);
            if (tarifa is null || UnidadesDe(medicion, concepto) is not { } unidades)
                continue;
            lineas.Add(new LineaFacturacionCuadroDto(
                concepto, NombreDe(concepto), tarifa.MonedaIso, unidades, tarifa.Activa ? unidades * tarifa.PrecioUnitario : 0m));
        }

        return lineas;
    }
}
