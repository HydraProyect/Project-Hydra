using CaeManager.Application.Dashboard;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Domain.Dashboard;
using CaeManager.Web.Features.CuadroDireccion;

namespace CaeManager.Web.Features.Facturacion;

/// <summary>Cómo cobra el Operador CAE un concepto a una Empresa gestionada en el mes.</summary>
public enum EstadoTarifaConcepto
{
    /// <summary>Sin tarifa para el concepto (o ninguna en vigor cuando se valoró el mes): las unidades se cuentan y no hay importe.</summary>
    SinTarifa,

    /// <summary>La tarifa vigente tiene el cobro desactivado: se muestran las unidades y no se cobra.</summary>
    NoSeCobra,

    /// <summary>Se cobra con la tarifa con la que se valoró el mes.</summary>
    Cobra,

    /// <summary>
    /// La valoración no lleva el dato: la medición no tenía las unidades (un cierre anterior al campo) o la línea tiene
    /// cero unidades en un mes ya valorado, y de ahí no se deduce ni el precio ni si se cobra. No se inventa.
    /// </summary>
    SinDato,
}

public sealed record ImporteEnMoneda(string Moneda, decimal Importe);

/// <param name="Unidades">
/// Las de la línea de facturación con la que se valoró el mes (unidades × precio = importe). Sin línea, las que ve el
/// Cuadro o el conteo del Excel de extranjeros; null si la medición no las lleva: no se inventan.
/// </param>
/// <param name="Precio">El precio unitario con el que se valoró (importe entre unidades de la línea); null si no se cobra o no se sabe.</param>
/// <param name="Importe">Null si no hay importe que enseñar (no se cobra, sin tarifa o importes ocultos).</param>
/// <param name="Moneda">La de la línea con la que se valoró, no la de ninguna versión releída después.</param>
public sealed record ConceptoFacturado(
    ConceptoTarifaOperadorCae Concepto, int? Unidades, EstadoTarifaConcepto Estado, decimal? Precio, decimal? Importe, string? Moneda);

/// <summary>
/// Lo que el Operador CAE externo cobra a una Empresa gestionada (Tenant beneficiario) en un mes. «Ingreso por
/// gestión» y compañía son los del Cuadro de dirección: importe entre gestiones, sin dividir por cero.
/// </summary>
/// <param name="SinTarifa">Ninguna versión de tarifa para esta Empresa, nunca: se ofrece fijarla.</param>
/// <param name="SinTarifaEnElMes">
/// Hay tarifa, pero ninguna estaba en vigor en el instante en que se valoró el mes (se fijó después): no hay importe y
/// fijar otra no cambia ese mes si está cerrado.
/// </param>
/// <param name="ExtranjerosEnExcel">Las filas del Excel «Exportar extranjeros del mes» de esta Empresa: información complementaria.</param>
public sealed record EmpresaFacturada(
    Guid TenantId, string Nombre, EstadoCifraMensual Estado, IReadOnlyList<ConceptoFacturado> Conceptos,
    bool SinTarifa, bool SinTarifaEnElMes, bool ImportesOcultos, bool Congelada, IReadOnlyList<ImporteEnMoneda> Cuota,
    int? Gestiones, int? GestionesExtranjero, double? IngresoPorGestion, double? CostoPorGestion, double? CostoPorExtranjero, double? Relacion,
    int ExtranjerosEnExcel)
{
    public ConceptoFacturado Concepto(ConceptoTarifaOperadorCae concepto) => Conceptos.Single(c => c.Concepto == concepto);

    public int? Extranjeros => Concepto(ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero).Unidades;

    public int? CentrosNuevos => Concepto(ConceptoTarifaOperadorCae.CentroNuevo).Unidades;

    public int? ObrasNuevas => Concepto(ConceptoTarifaOperadorCae.ObraNueva).Unidades;
}

/// <param name="ExtranjerosDelMes">Las filas del Excel «Exportar extranjeros del mes» entero (todas las Empresas que alcanza quien mira).</param>
public sealed record ModeloFacturacion(
    int Anyo, int Mes, EstadoCifraMensual Estado, IReadOnlyList<EmpresaFacturada> Empresas, IReadOnlyList<ImporteEnMoneda> Total,
    int ExtranjerosDelMes, int TenantsSinAlcanceCompleto, bool PuedeEditarTarifas, bool ImportesOcultos)
{
    /// <summary>El total no incluye Empresas gestionadas que quien mira no alcanza por completo: hay que decirlo junto a la cifra.</summary>
    public bool AlcanceParcial => TenantsSinAlcanceCompleto > 0;
}

/// <summary>
/// Compone «Facturación» (cuota del Operador CAE a cada Empresa gestionada, decisión de Chris, 2026-10-20) con lo que ya
/// miden y valoran las consultas del Cuadro de dirección; aquí no se calcula ningún importe ni se vuelve a leer la tarifa
/// en otro instante: lo que se enseña es lo que dice la propia línea de facturación.
/// <list type="bullet">
/// <item>Las unidades, el importe, el precio (importe entre unidades) y la moneda salen de la línea de
/// <see cref="PuntoSerieCuadroDto.Facturacion"/> (<see cref="ValoracionOperadorCae"/>), la misma cifra del Cuadro, así que
/// unidades × precio = importe. Nulo si quien mira no pasa <c>AutorizacionTarifasOperadorCae</c> (Coordinador CAE):
/// entonces solo se enseñan unidades.</item>
/// <item>Sin línea, las unidades de extranjeros son las del conteo del Excel «Exportar extranjeros del mes»
/// (<see cref="ContarExtranjerosCobrablesDelMesQuery"/>); con línea ese conteo es solo información complementaria.</item>
/// <item>Solo salen las Empresas con una Asignación de Operación externa del Operador en vigor en el mes
/// (<see cref="CaeManager.Application.Dashboard.TarifasOperadorCae.ObtenerEmpresasOperadasEnElMesQuery"/>): nunca el
/// Tenant del propio Operador ni uno que opera otra organización.</item>
/// </list>
/// El Cliente empresarial nuevo no se cobra ni sale aquí. Pura: la prueban los tests sin base de datos.
/// </summary>
public static class FacturacionOperadorModelo
{
    private static readonly ConceptoTarifaOperadorCae[] Conceptos =
        [ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero, ConceptoTarifaOperadorCae.CentroNuevo, ConceptoTarifaOperadorCae.ObraNueva];

    public static ModeloFacturacion Construir(
        int anyo, int mes, SerieCuadroDireccionDto serie, ConteoExtranjerosCobrablesDelMesDto extranjeros,
        IReadOnlyList<TarifaOperadorCaeDto>? tarifas, IReadOnlyCollection<Guid> empresasOperadas, DateTime ahoraUtc)
    {
        var empresas = new List<EmpresaFacturada>();
        if (MesCuadroDireccion.EsValido(anyo, mes))
        {
            foreach (var tenant in serie.Tenants.Where(t => empresasOperadas.Contains(t.TenantId)))
            {
                if (tenant.Meses.LastOrDefault(p => p.Anyo == anyo && p.Mes == mes) is not { } punto)
                    continue;

                var asignaciones = extranjeros.PorTenant.FirstOrDefault(t => t.TenantId == tenant.TenantId)?.Asignaciones ?? 0;
                empresas.Add(Empresa(tenant, punto, asignaciones, tarifas, ahoraUtc));
            }
        }

        // El estado del mes es el del total de la serie, no el de la última Empresa que se recorrió.
        var estadoMes = serie.Total.Count > 0 ? serie.Total[0].Estado : EstadoDelMes(anyo, mes, ahoraUtc);
        return new ModeloFacturacion(
            anyo, mes, estadoMes, empresas, SumarPorMoneda(empresas.SelectMany(e => e.Cuota)),
            extranjeros.PorTenant.Sum(t => t.Asignaciones), serie.TenantsSinAlcanceCompleto,
            PuedeEditarTarifas: tarifas is not null, ImportesOcultos: tarifas is null);
    }

    /// <summary>Sin cifras de la serie, el estado del mes sale del calendario.</summary>
    private static EstadoCifraMensual EstadoDelMes(int anyo, int mes, DateTime ahoraUtc) =>
        MesCuadroDireccion.EsValido(anyo, mes) ? new MesCuadroDireccion(anyo, mes).EstadoA(ahoraUtc) : EstadoCifraMensual.Parcial;

    private static EmpresaFacturada Empresa(
        SerieTenantCuadroDto tenant, PuntoSerieCuadroDto punto, int extranjerosEnExcel,
        IReadOnlyList<TarifaOperadorCaeDto>? tarifas, DateTime ahoraUtc)
    {
        var importesOcultos = tarifas is null;
        var sinTarifa = tarifas is not null && tarifas.All(t => t.PropietarioTenantId != tenant.TenantId);
        var hayLineas = punto.Facturacion is { Count: > 0 };
        var sinTarifaEnElMes = tarifas is not null && !sinTarifa && !hayLineas;

        var conceptos = Conceptos
            .Select(c => Concepto(
                c, tenant.TenantId, punto, SinLinea(c, punto, extranjerosEnExcel), tarifas, sinTarifa, ahoraUtc))
            .ToList();

        // La cuota es la suma de las líneas con las que se valoró el mes, también las de importe cero: un mes con todos los
        // conceptos sin cobrar es «0 €», no un hueco.
        var cuota = sinTarifa || importesOcultos || punto.Facturacion is null
            ? []
            : SumarPorMoneda(punto.Facturacion.Select(l => new ImporteEnMoneda(l.MonedaIso, l.Importe)));

        var gestiones = punto.Cifras.Gestiones?.Valor;
        var extranjeros = conceptos[0].Unidades ?? 0;
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
            tenant.TenantId, tenant.TenantNombre, punto.Estado, conceptos, sinTarifa, sinTarifaEnElMes, importesOcultos,
            Congelada: punto.Estado == EstadoCifraMensual.Existe, cuota, gestiones, extranjeros,
            ingreso, costoGestion, costoExtranjero, relacion, extranjerosEnExcel);
    }

    /// <summary>Las unidades a mostrar cuando el concepto no tiene línea: las del Excel de extranjeros o las del Cuadro.</summary>
    private static int? SinLinea(ConceptoTarifaOperadorCae concepto, PuntoSerieCuadroDto punto, int extranjerosEnExcel) => concepto switch
    {
        ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero => extranjerosEnExcel,
        ConceptoTarifaOperadorCae.CentroNuevo => punto.Cifras.AltasCentros.Valor,
        ConceptoTarifaOperadorCae.ObraNueva => punto.Cifras.ObrasNuevas?.Valor,
        _ => null,
    };

    private static ConceptoFacturado Concepto(
        ConceptoTarifaOperadorCae concepto, Guid tenantId, PuntoSerieCuadroDto punto, int? unidadesSinLinea,
        IReadOnlyList<TarifaOperadorCaeDto>? tarifas, bool sinTarifa, DateTime ahoraUtc)
    {
        var linea = punto.Facturacion?.FirstOrDefault(l => l.Concepto == concepto);
        if (tarifas is null || sinTarifa)
            return new ConceptoFacturado(concepto, unidadesSinLinea, EstadoTarifaConcepto.SinTarifa, null, null, null);

        if (linea is null)
        {
            // Sin línea no hubo tarifa en vigor cuando se valoró, o la medición no llevaba las unidades.
            var estado = unidadesSinLinea is null ? EstadoTarifaConcepto.SinDato : EstadoTarifaConcepto.SinTarifa;
            return new ConceptoFacturado(concepto, unidadesSinLinea, estado, null, null, null);
        }

        var moneda = linea.MonedaIso.Trim().ToUpperInvariant();
        if (linea.Unidades > 0)
        {
            // Importe cero con unidades: el cobro estaba desactivado (la valoración pone el importe a cero y conserva las unidades).
            return linea.Importe > 0
                ? new ConceptoFacturado(concepto, linea.Unidades, EstadoTarifaConcepto.Cobra, linea.Importe / linea.Unidades, linea.Importe, moneda)
                : new ConceptoFacturado(concepto, linea.Unidades, EstadoTarifaConcepto.NoSeCobra, null, null, moneda);
        }

        // Sin unidades la línea no dice ni el precio ni si se cobra. Solo en un mes en curso se sabe con qué tarifa se
        // valoró (la de ahora, el mismo instante de la valoración); en uno ya valorado no se relee: no se sabe con qué
        // instante, y mirar otro cambiaría el precio o la moneda.
        if (punto.Estado == EstadoCifraMensual.Parcial
            && ValoracionOperadorCae.VigenteEn(tarifas, tenantId, concepto, ahoraUtc) is { } vigente)
        {
            return vigente.Activa
                ? new ConceptoFacturado(concepto, 0, EstadoTarifaConcepto.Cobra, vigente.PrecioUnitario, linea.Importe, moneda)
                : new ConceptoFacturado(concepto, 0, EstadoTarifaConcepto.NoSeCobra, null, null, moneda);
        }

        return new ConceptoFacturado(concepto, 0, EstadoTarifaConcepto.SinDato, null, linea.Importe, moneda);
    }

    private static IReadOnlyList<ImporteEnMoneda> SumarPorMoneda(IEnumerable<ImporteEnMoneda> importes) =>
        importes
            .GroupBy(i => i.Moneda.Trim().ToUpperInvariant())
            .Select(g => new ImporteEnMoneda(g.Key, g.Sum(i => i.Importe)))
            .OrderBy(i => i.Moneda, StringComparer.Ordinal)
            .ToList();
}
