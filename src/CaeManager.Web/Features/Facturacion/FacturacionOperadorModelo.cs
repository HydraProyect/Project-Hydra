using CaeManager.Application.Dashboard;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Domain.Dashboard;
using CaeManager.Web.Features.CuadroDireccion;

namespace CaeManager.Web.Features.Facturacion;

/// <summary>Cómo cobra el Operador CAE un concepto a una Empresa gestionada en el mes.</summary>
public enum EstadoTarifaConcepto
{
    /// <summary>Ninguna versión de tarifa para el concepto: las unidades se cuentan y no hay importe.</summary>
    SinTarifa,

    /// <summary>La tarifa vigente tiene el cobro desactivado: se muestran las unidades y no se cobra.</summary>
    NoSeCobra,

    /// <summary>Se cobra con la tarifa vigente.</summary>
    Cobra,
}

public sealed record ImporteEnMoneda(string Moneda, decimal Importe);

/// <param name="Unidades">Null si la medición no las lleva (un cierre anterior al campo): no se inventan.</param>
/// <param name="Precio">El precio unitario con el que se valoró; null si no se cobra o no hay tarifa.</param>
/// <param name="Importe">Null si no hay importe que enseñar (no se cobra, sin tarifa, sin vigencia o importes ocultos).</param>
public sealed record ConceptoFacturado(
    ConceptoTarifaOperadorCae Concepto, int? Unidades, EstadoTarifaConcepto Estado, decimal? Precio, decimal? Importe, string? Moneda);

/// <summary>
/// Lo que el Operador CAE externo cobra a una Empresa gestionada (Tenant beneficiario) en un mes. «Ingreso por
/// gestión» y compañía son los del Cuadro de dirección: importe entre gestiones, sin dividir por cero.
/// </summary>
public sealed record EmpresaFacturada(
    Guid TenantId, string Nombre, EstadoCifraMensual Estado, IReadOnlyList<ConceptoFacturado> Conceptos,
    bool SinTarifa, bool SinVigencia, bool ImportesOcultos, bool Congelada, IReadOnlyList<ImporteEnMoneda> Cuota,
    int? Gestiones, int? GestionesExtranjero, double? IngresoPorGestion, double? CostoPorGestion, double? CostoPorExtranjero, double? Relacion)
{
    public ConceptoFacturado Concepto(ConceptoTarifaOperadorCae concepto) => Conceptos.Single(c => c.Concepto == concepto);

    public int? Extranjeros => Concepto(ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero).Unidades;

    public int? CentrosNuevos => Concepto(ConceptoTarifaOperadorCae.CentroNuevo).Unidades;

    public int? ObrasNuevas => Concepto(ConceptoTarifaOperadorCae.ObraNueva).Unidades;
}

public sealed record ModeloFacturacion(
    int Anyo, int Mes, EstadoCifraMensual Estado, IReadOnlyList<EmpresaFacturada> Empresas, IReadOnlyList<ImporteEnMoneda> Total,
    int ExtranjerosDelMes, int TenantsSinAlcanceCompleto, bool PuedeEditarTarifas, bool ImportesOcultos);

/// <summary>
/// Compone «Facturación» (cuota del Operador CAE a cada Empresa gestionada, decisión de Chris, 2026-10-20) con lo que ya
/// miden y valoran las consultas del Cuadro de dirección; aquí no se calcula ningún importe:
/// <list type="bullet">
/// <item>Los importes y la tarifa con la que se valoró salen de <see cref="PuntoSerieCuadroDto.Facturacion"/>
/// (<see cref="ValoracionOperadorCae"/>), la misma cifra del Cuadro. Nulo si quien mira no pasa
/// <c>AutorizacionTarifasOperadorCae</c> (Coordinador CAE): entonces solo se enseñan unidades.</item>
/// <item>Los extranjeros son los de <see cref="ContarExtranjerosCobrablesDelMesQuery"/>, las mismas filas del Excel
/// «Exportar extranjeros del mes».</item>
/// <item>Los Centros y las Obras nuevas son los de las cifras del Cuadro; con el cobro desactivado se muestran igual.</item>
/// </list>
/// El Cliente empresarial nuevo no se cobra ni sale aquí. Pura: la prueban los tests sin base de datos.
/// </summary>
public static class FacturacionOperadorModelo
{
    private static readonly ConceptoTarifaOperadorCae[] Conceptos =
        [ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero, ConceptoTarifaOperadorCae.CentroNuevo, ConceptoTarifaOperadorCae.ObraNueva];

    public static ModeloFacturacion Construir(
        int anyo, int mes, SerieCuadroDireccionDto serie, ConteoExtranjerosCobrablesDelMesDto extranjeros,
        IReadOnlyList<TarifaOperadorCaeDto>? tarifas, DateTime ahoraUtc)
    {
        var empresas = new List<EmpresaFacturada>();
        var estadoMes = EstadoCifraMensual.Parcial;
        if (MesCuadroDireccion.EsValido(anyo, mes))
        {
            var finDelMes = new MesCuadroDireccion(anyo, mes).FinUtc;
            foreach (var tenant in serie.Tenants)
            {
                if (tenant.Meses.LastOrDefault(p => p.Anyo == anyo && p.Mes == mes) is not { } punto)
                    continue;

                var asignaciones = extranjeros.PorTenant.FirstOrDefault(t => t.TenantId == tenant.TenantId)?.Asignaciones ?? 0;
                // Una tarifa de un mes ya cerrado es la vigente al cerrarse; mientras el mes corre, la de ahora.
                var instante = punto.Estado == EstadoCifraMensual.Parcial || finDelMes > ahoraUtc ? ahoraUtc : finDelMes;
                empresas.Add(Empresa(tenant, punto, asignaciones, tarifas, instante));
                estadoMes = punto.Estado;
            }
        }

        var total = SumarPorMoneda(empresas.SelectMany(e => e.Cuota));
        return new ModeloFacturacion(
            anyo, mes, empresas.Count > 0 ? estadoMes : EstadoDelMes(anyo, mes, ahoraUtc), empresas, total,
            empresas.Sum(e => e.Extranjeros ?? 0), serie.TenantsSinAlcanceCompleto,
            PuedeEditarTarifas: tarifas is not null, ImportesOcultos: tarifas is null);
    }

    /// <summary>Sin ninguna Empresa que enseñar, el estado del mes sale del calendario.</summary>
    private static EstadoCifraMensual EstadoDelMes(int anyo, int mes, DateTime ahoraUtc) =>
        MesCuadroDireccion.EsValido(anyo, mes) ? new MesCuadroDireccion(anyo, mes).EstadoA(ahoraUtc) : EstadoCifraMensual.Parcial;

    private static EmpresaFacturada Empresa(
        SerieTenantCuadroDto tenant, PuntoSerieCuadroDto punto, int extranjeros,
        IReadOnlyList<TarifaOperadorCaeDto>? tarifas, DateTime instanteUtc)
    {
        var importesOcultos = tarifas is null;
        var sinTarifa = tarifas is not null && tarifas.All(t => t.PropietarioTenantId != tenant.TenantId);
        var sinVigencia = tarifas is not null && !sinTarifa && punto.Facturacion is { Count: 0 };

        var conceptos = Conceptos
            .Select(c => Concepto(c, UnidadesDe(c, punto, extranjeros), tenant.TenantId, punto.Facturacion, tarifas, instanteUtc))
            .ToList();

        var cuota = sinTarifa || importesOcultos
            ? []
            : SumarPorMoneda(conceptos.Where(c => c.Importe is not null).Select(c => new ImporteEnMoneda(c.Moneda ?? string.Empty, c.Importe!.Value)));

        var gestiones = punto.Cifras.Gestiones?.Valor;
        var importeExtranjeros = conceptos[0].Importe ?? 0m;
        double? ingreso = null, costoGestion = null, costoExtranjero = null, relacion = null;
        if (cuota.Count == 1 && gestiones is > 0)
        {
            var importe = cuota[0].Importe;
            ingreso = CalculosCuadro.IngresoPorGestion(importe, gestiones);
            costoGestion = CalculosCuadro.IngresoPorGestion(importe - importeExtranjeros, gestiones - extranjeros);
            costoExtranjero = conceptos[0].Importe is not null ? CalculosCuadro.IngresoPorGestion(importeExtranjeros, extranjeros) : null;
            relacion = costoGestion is > 0 && costoExtranjero is not null ? costoExtranjero / costoGestion : null;
        }

        return new EmpresaFacturada(
            tenant.TenantId, tenant.TenantNombre, punto.Estado, conceptos, sinTarifa, sinVigencia, importesOcultos,
            Congelada: punto.Estado == EstadoCifraMensual.Existe, cuota, gestiones, extranjeros,
            ingreso, costoGestion, costoExtranjero, relacion);
    }

    private static int? UnidadesDe(ConceptoTarifaOperadorCae concepto, PuntoSerieCuadroDto punto, int extranjeros) => concepto switch
    {
        ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero => extranjeros,
        ConceptoTarifaOperadorCae.CentroNuevo => punto.Cifras.AltasCentros.Valor,
        ConceptoTarifaOperadorCae.ObraNueva => punto.Cifras.ObrasNuevas?.Valor,
        _ => null,
    };

    private static ConceptoFacturado Concepto(
        ConceptoTarifaOperadorCae concepto, int? unidades, Guid tenantId, IReadOnlyList<LineaFacturacionCuadroDto>? lineas,
        IReadOnlyList<TarifaOperadorCaeDto>? tarifas, DateTime instanteUtc)
    {
        if (tarifas is null)
            return new ConceptoFacturado(concepto, unidades, EstadoTarifaConcepto.SinTarifa, null, null, null);

        var version = ValoracionOperadorCae.VigenteEn(tarifas, tenantId, concepto, instanteUtc);
        if (version is null)
            return new ConceptoFacturado(concepto, unidades, EstadoTarifaConcepto.SinTarifa, null, null, null);
        if (!version.Activa)
            return new ConceptoFacturado(concepto, unidades, EstadoTarifaConcepto.NoSeCobra, null, null, version.MonedaIso);

        var linea = lineas?.FirstOrDefault(l => l.Concepto == concepto);
        // El precio con el que se valoró: el que sale de la propia línea (importe entre unidades) y, sin unidades, el de la versión vigente.
        var precio = linea is { Unidades: > 0, Importe: > 0 } ? linea.Importe / linea.Unidades : version.PrecioUnitario;
        return new ConceptoFacturado(concepto, unidades, EstadoTarifaConcepto.Cobra, precio, linea?.Importe, version.MonedaIso);
    }

    private static IReadOnlyList<ImporteEnMoneda> SumarPorMoneda(IEnumerable<ImporteEnMoneda> importes) =>
        importes
            .GroupBy(i => i.Moneda.Trim().ToUpperInvariant())
            .Select(g => new ImporteEnMoneda(g.Key, g.Sum(i => i.Importe)))
            .OrderBy(i => i.Moneda, StringComparer.Ordinal)
            .ToList();
}
