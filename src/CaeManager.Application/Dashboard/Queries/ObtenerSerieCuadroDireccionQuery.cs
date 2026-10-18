using CaeManager.Application.Common;
using CaeManager.Application.Facturacion;
using CaeManager.Application.Facturacion.Queries.ObtenerResumenFacturacion;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Domain.Common;
using CaeManager.Domain.Facturacion;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Dashboard.Queries;

/// <summary>
/// La serie mensual del Cuadro de dirección: los <paramref name="Meses"/> meses naturales que terminan en
/// (<paramref name="Anyo"/>, <paramref name="Mes"/>), del más antiguo al más reciente. Alimenta «Facturación y
/// gestiones por mes», la pestaña Facturación, el mapa de calor de los últimos seis meses y la Comparativa anual.
/// </summary>
public record ObtenerSerieCuadroDireccionQuery(int Anyo, int Mes, int Meses, bool ConFacturacion = false)
    : IRequest<SerieCuadroDireccionDto>
{
    public const int MaximoMeses = 36;
}

/// <summary>Lo que un concepto con tarifa suma en un mes: unidades y su importe estimado, en una moneda.</summary>
public record LineaFacturacionCuadroDto(
    ConceptoFacturable Concepto, string ConceptoNombre, string MonedaIso, int Unidades, decimal Importe);

/// <param name="Cifras">Las cifras del mes con las mismas definiciones que el Cuadro (el cumplimiento no se mide aquí).</param>
/// <param name="Facturacion">
/// Facturación estimada del mes: unidades de cada concepto con tarifa por la tarifa de hoy
/// (<see cref="ObtenerResumenFacturacionQueryHandler"/>); null si la serie no se pidió con facturación. Sin
/// tarifas, lista vacía: no hay nada que valorar y nada se inventa.
/// </param>
public record PuntoSerieCuadroDto(
    int Anyo, int Mes, EstadoCifraMensual Estado, CifrasCuadroDireccionDto Cifras,
    IReadOnlyList<LineaFacturacionCuadroDto>? Facturacion)
{
    /// <summary>Importe estimado total del mes en una moneda; null si la serie no lleva facturación.</summary>
    public decimal? ImporteEn(string monedaIso) => Facturacion?
        .Where(l => string.Equals(l.MonedaIso, monedaIso, StringComparison.OrdinalIgnoreCase))
        .Sum(l => l.Importe);

    /// <summary>Unidades de un concepto con tarifa en el mes (0 si no tiene tarifa o la serie no lleva facturación).</summary>
    public int UnidadesDe(ConceptoFacturable concepto) => Facturacion?.Where(l => l.Concepto == concepto).Sum(l => l.Unidades) ?? 0;
}

public record SerieTenantCuadroDto(Guid TenantId, string TenantNombre, IReadOnlyList<PuntoSerieCuadroDto> Meses);

public record SerieCuadroDireccionDto(
    IReadOnlyList<PuntoSerieCuadroDto> Total, IReadOnlyList<SerieTenantCuadroDto> Tenants, int TenantsSinAlcanceCompleto);

/// <summary>Lo medido en un Tenant beneficiario un mes de la serie.</summary>
public record MesMedidoCuadro(MedicionCuadroDireccion Medicion, IReadOnlyList<LineaFacturacionCuadroDto>? Facturacion);

/// <summary>
/// Misma disciplina que <see cref="ObtenerCuadroDireccionQueryHandler"/>: por cada Tenant beneficiario autorizado,
/// con <see cref="AmbitoTenantExplicito"/> fijado a ese Tenant (filtro global y RLS de ese Tenant), solo si quien
/// mira alcanza el Tenant entero; un mes con cierre mensual vigente sale de su foto y el resto se mide en vivo con
/// <see cref="MedicionMensualCuadroDireccion"/>. La fusión es pura (<see cref="Construir"/>). La facturación es
/// estimada por naturaleza y solo se devuelve a quien pasa <see cref="AutorizacionTarifasCliente"/> (en el Cuadro,
/// Dirección CAE; para Coordinador CAE es null): valora con las tarifas de hoy las unidades de cada mes, igual que la pantalla
/// «Facturación», y por eso no usa las tarifas congeladas de la foto (la foto no guarda las unidades por concepto).
/// </summary>
public class ObtenerSerieCuadroDireccionQueryHandler(
    IMediator mediator,
    IAlcanceDatosService alcanceDatos,
    MedicionMensualCuadroDireccion medicion,
    IFacturacionQueryContext facturacionContext,
    ICurrentUserService currentUserService,
    TimeProvider reloj,
    ICierresCuadroDireccion? cierresContext = null)
    : IRequestHandler<ObtenerSerieCuadroDireccionQuery, SerieCuadroDireccionDto>
{
    public async Task<SerieCuadroDireccionDto> Handle(ObtenerSerieCuadroDireccionQuery request, CancellationToken cancellationToken)
    {
        await AutorizacionCuadroDireccion.ExigirLecturaAsync(currentUserService);

        // Leer el Cuadro no da derecho a ver importes: las tarifas son plano Comercial y las guarda
        // AutorizacionTarifasCliente. Sin ese rol efectivo (Coordinador CAE) la facturación sale a null.
        var conFacturacion = await DevuelveFacturacionAsync(request.ConFacturacion, currentUserService);
        var ahoraUtc = reloj.GetUtcNow().UtcDateTime;
        var meses = MesesDeLaSerie(request.Anyo, request.Mes, request.Meses, ahoraUtc);
        if (meses.Count == 0)
            return Construir(meses, ahoraUtc, [], 0);

        var clientes = await mediator.Send(new ObtenerClientesAutorizadosQuery(), cancellationToken);
        var porTenant = new List<(ClienteAutorizadoDto Cliente, IReadOnlyList<MesMedidoCuadro> Meses)>();
        var sinAlcance = 0;

        foreach (var cliente in clientes)
        {
            using (AmbitoTenantExplicito.Establecer(cliente.TenantId))
            {
                if (!await alcanceDatos.AlcanzaElTenantEnteroAsync(cancellationToken))
                {
                    sinAlcance++;
                    continue;
                }

                var cierres = cierresContext is null
                    ? []
                    : (await cierresContext.CierresMensuales.Where(c => c.ReabiertoEnUtc == null).ToListAsync(cancellationToken))
                        .GroupBy(c => (c.Anyo, c.Mes))
                        .ToDictionary(g => g.Key, g => g.First());

                var empresasConTarifa = conFacturacion
                    ? await facturacionContext.TarifasCliente.Select(t => t.ClienteId).Distinct().ToListAsync(cancellationToken)
                    : [];

                var medidos = new List<MesMedidoCuadro>();
                foreach (var mes in meses)
                {
                    var corteUtc = mes.CorteA(ahoraUtc);
                    var cierre = mes.FinUtc <= ahoraUtc && cierres.TryGetValue((mes.Anyo, mes.Mes), out var c) ? c : null;
                    var medida = cierre is null
                        ? await medicion.MedirAsync(mes, corteUtc, cancellationToken, medirDocumental: false)
                        : FotoCierreMensual.Leer(cierre.FotoJson).Medicion with
                        {
                            EstadoPersistido = cierre.Estimado ? EstadoCifraMensual.Estimado : EstadoCifraMensual.Existe,
                        };

                    IReadOnlyList<LineaFacturacionCuadroDto>? facturacion = null;
                    if (conFacturacion)
                    {
                        var lineas = new List<LineaFacturacionDto>();
                        foreach (var empresaId in empresasConTarifa)
                        {
                            var resumen = await mediator.Send(new ObtenerResumenFacturacionQuery(empresaId, mes.Anyo, mes.Mes), cancellationToken);
                            if (resumen is not null)
                                lineas.AddRange(resumen.Lineas);
                        }

                        facturacion = SumarLineas(lineas);
                    }

                    medidos.Add(new MesMedidoCuadro(medida, facturacion));
                }

                porTenant.Add((cliente, medidos));
            }
        }

        return Construir(meses, ahoraUtc, porTenant, sinAlcance);
    }

    /// <summary>Hay importes solo si se pidieron y el rol efectivo pasa el guard de tarifas (Administrador o Dirección CAE).</summary>
    public static async Task<bool> DevuelveFacturacionAsync(bool pedida, ICurrentUserService usuario) =>
        pedida && await usuario.PuedeGestionarTarifasAsync();

    /// <summary>
    /// Los meses de la serie, del más antiguo al más reciente: hasta <paramref name="cantidad"/> (1 a
    /// <see cref="ObtenerSerieCuadroDireccionQuery.MaximoMeses"/>) que terminan en el pedido, sin los
    /// anteriores al año mínimo ni los que todavía no han empezado. Un mes final no válido da una serie vacía.
    /// </summary>
    public static IReadOnlyList<MesCuadroDireccion> MesesDeLaSerie(int anyo, int mes, int cantidad, DateTime ahoraUtc)
    {
        if (!MesCuadroDireccion.EsValido(anyo, mes))
            return [];

        var ultimo = new DateOnly(anyo, mes, 1);
        var n = Math.Clamp(cantidad, 1, ObtenerSerieCuadroDireccionQuery.MaximoMeses);
        return Enumerable.Range(0, n)
            .Select(i => ultimo.AddMonths(-(n - 1 - i)))
            .Where(d => MesCuadroDireccion.EsValido(d.Year, d.Month))
            .Select(d => new MesCuadroDireccion(d.Year, d.Month))
            .Where(m => m.InicioUtc <= ahoraUtc)
            .ToList();
    }

    /// <summary>Suma las líneas de varias Empresas por concepto y moneda: un renglón por cada par.</summary>
    public static IReadOnlyList<LineaFacturacionCuadroDto> SumarLineas(IEnumerable<LineaFacturacionDto> lineas) =>
        lineas
            .GroupBy(l => (l.Concepto, Moneda: l.MonedaIso.Trim().ToUpperInvariant()))
            .Select(g => new LineaFacturacionCuadroDto(
                g.Key.Concepto, g.First().ConceptoNombre, g.Key.Moneda, g.Sum(l => l.Unidades), g.Sum(l => l.Subtotal)))
            .OrderBy(l => l.Concepto)
            .ThenBy(l => l.MonedaIso, StringComparer.Ordinal)
            .ToList();

    /// <summary>Fusión pura: las cifras de cada mes con el mismo cálculo que el Cuadro, por Tenant y en total.</summary>
    public static SerieCuadroDireccionDto Construir(
        IReadOnlyList<MesCuadroDireccion> meses,
        DateTime ahoraUtc,
        IReadOnlyList<(ClienteAutorizadoDto Cliente, IReadOnlyList<MesMedidoCuadro> Meses)> porTenant,
        int tenantsSinAlcanceCompleto)
    {
        var tenants = porTenant
            .Select(p => new SerieTenantCuadroDto(
                p.Cliente.TenantId, p.Cliente.Nombre,
                meses.Select((mes, i) => Punto(mes, ahoraUtc, [p.Meses[i]])).ToList()))
            .OrderBy(t => t.TenantNombre, StringComparer.CurrentCulture)
            .ToList();

        var total = meses
            .Select((mes, i) => Punto(mes, ahoraUtc, porTenant.Select(p => p.Meses[i]).ToList()))
            .ToList();

        return new SerieCuadroDireccionDto(total, tenants, tenantsSinAlcanceCompleto);
    }

    private static PuntoSerieCuadroDto Punto(MesCuadroDireccion mes, DateTime ahoraUtc, IReadOnlyList<MesMedidoCuadro> medidos)
    {
        var estado = mes.EstadoA(ahoraUtc);
        if (estado != EstadoCifraMensual.Parcial && medidos.Count > 0 && medidos.All(m => m.Medicion.EstadoPersistido == EstadoCifraMensual.Existe))
            estado = EstadoCifraMensual.Existe;
        else if (medidos.Count == 1 && medidos[0].Medicion.EstadoPersistido is { } persistido)
            estado = persistido;

        var facturacion = medidos.Count > 0 && medidos.All(m => m.Facturacion is not null)
            ? SumarLineas(medidos.SelectMany(m => m.Facturacion!).Select(l =>
                new LineaFacturacionDto(l.Concepto, l.ConceptoNombre, l.Unidades, 0, l.MonedaIso, l.Importe)))
            : null;

        return new PuntoSerieCuadroDto(
            mes.Anyo, mes.Mes, estado,
            ObtenerCuadroDireccionQueryHandler.Cifras(medidos.Select(m => m.Medicion).ToList(), estado, ahoraUtc),
            facturacion);
    }
}
