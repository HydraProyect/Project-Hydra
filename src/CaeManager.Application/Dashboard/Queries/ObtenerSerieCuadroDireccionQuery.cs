using CaeManager.Application.Common;
using CaeManager.Application.Dashboard.TarifasOperadorCae;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Domain.Common;
using CaeManager.Domain.Dashboard;
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

/// <summary>
/// Lo que el Operador CAE externo cobra por un concepto en un mes: unidades medidas y su importe con la tarifa del
/// Operador para ese Tenant beneficiario (<see cref="Dashboard.ValoracionOperadorCae"/>), en una moneda. Con el cobro
/// desactivado la línea sale con sus unidades e importe cero.
/// </summary>
public record LineaFacturacionCuadroDto(
    ConceptoTarifaOperadorCae Concepto, string ConceptoNombre, string MonedaIso, int Unidades, decimal Importe);

/// <param name="Cifras">Las cifras del mes con las mismas definiciones que el Cuadro (el cumplimiento no se mide aquí).</param>
/// <param name="Facturacion">
/// Lo que el Operador CAE externo cobra en el mes: unidades de cada concepto con tarifa por la tarifa del Operador
/// (<see cref="Dashboard.ValoracionOperadorCae"/>); un concepto con el cobro desactivado sale con sus unidades e
/// importe cero. Null si la serie no se pidió con facturación o quien mira no gestiona las tarifas del Operador.
/// Lista vacía sin tarifas o sin vigencia de la Asignación de Operación en el mes: no hay nada que valorar y nada se
/// inventa.
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
    public int UnidadesDe(ConceptoTarifaOperadorCae concepto) => Facturacion?.Where(l => l.Concepto == concepto).Sum(l => l.Unidades) ?? 0;
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
/// <see cref="MedicionMensualCuadroDireccion"/>. La fusión es pura (<see cref="Construir"/>).
///
/// <para>
/// <b>Facturación</b> (decisión de Chris, 2026-10-20): lo que el Operador CAE externo cobra a cada Tenant
/// beneficiario, no lo que cada Tenant cobra a sus Clientes empresariales (<c>TarifaCliente</c>, pantalla
/// «Facturación» por Cliente empresarial). Solo la ve quien pasa <see cref="AutorizacionTarifasOperadorCae"/>
/// (Administrador o Dirección CAE del Operador; para Coordinador CAE es null). Las unidades salen de la medición
/// del mes (la foto en un mes cerrado) y el precio de la versión de la tarifa vigente en el instante del cierre; un
/// mes sin cierre vigente se valora con la de ahora. Así el cierre congela unidades y tarifas sin copiar los precios
/// del Operador a una fila del Tenant beneficiario. La valoración se acota a la vigencia de la Asignación de
/// Operación del Operador en cada mes (<see cref="ValorarEnVigenciaAsync"/>).
/// </para>
/// </summary>
public class ObtenerSerieCuadroDireccionQueryHandler(
    IMediator mediator,
    IAlcanceDatosService alcanceDatos,
    MedicionMensualCuadroDireccion medicion,
    ITarifasOperadorCaeContext tarifasContext,
    CaeManager.Application.Operaciones.IOperacionesQueryContext operacionesContext,
    ICurrentUserService currentUserService,
    TimeProvider reloj,
    ICierresCuadroDireccion? cierresContext = null)
    : IRequestHandler<ObtenerSerieCuadroDireccionQuery, SerieCuadroDireccionDto>
{
    public async Task<SerieCuadroDireccionDto> Handle(ObtenerSerieCuadroDireccionQuery request, CancellationToken cancellationToken)
    {
        await AutorizacionCuadroDireccion.ExigirLecturaAsync(currentUserService);

        // Leer el Cuadro no da derecho a ver importes: las tarifas son plano Comercial del Operador CAE y las guarda
        // AutorizacionTarifasOperadorCae. Sin ese rol efectivo (Coordinador CAE) la facturación sale a null.
        IReadOnlyList<TarifaOperadorCaeDto>? tarifas = null;
        ILookup<Guid, VigenciaAsignacionOperacion> vigencias = Array.Empty<VigenciaAsignacionOperacion>().ToLookup(_ => Guid.Empty);
        if (request.ConFacturacion && await AutorizacionTarifasOperadorCae.OperadorQueGestionaAsync(currentUserService) is { } operador)
        {
            using (AmbitoTenantExplicito.Establecer(operador))
            {
                tarifas = await ObtenerTarifasOperadorCaeQueryHandler.LeerAsync(tarifasContext, operador, cancellationToken);
                vigencias = await AutorizacionTarifasOperadorCae.VigenciasPorTenantBeneficiarioAsync(operacionesContext, operador, cancellationToken);
            }
        }

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

                    var facturacion = tarifas is null
                        ? null
                        : await ValorarEnVigenciaAsync(medida, mes, corteUtc, cliente.TenantId, tarifas, vigencias[cliente.TenantId], cierre?.CerradoEnUtc ?? ahoraUtc, cancellationToken);

                    medidos.Add(new MesMedidoCuadro(medida, facturacion));
                }

                porTenant.Add((cliente, medidos));
            }
        }

        return Construir(meses, ahoraUtc, porTenant, sinAlcance);
    }

    /// <summary>
    /// La valoración del Operador CAE acotada a la vigencia de su Asignación de Operación en el mes: sin vigencia en
    /// el mes no hay importe (lista vacía); con el mes entero cubierto vale la medición del mes (la foto si está
    /// cerrado); si la Asignación empieza o termina dentro del mes, solo cuentan las unidades dentro de su vigencia,
    /// recontadas en vivo aunque el mes esté cerrado (la foto guarda el mes entero, no los trozos).
    /// </summary>
    private async Task<IReadOnlyList<LineaFacturacionCuadroDto>> ValorarEnVigenciaAsync(
        MedicionCuadroDireccion medida, MesCuadroDireccion mes, DateTime corteUtc, Guid tenantBeneficiarioId,
        IReadOnlyList<TarifaOperadorCaeDto> tarifas, IEnumerable<VigenciaAsignacionOperacion> vigencias, DateTime instanteUtc,
        CancellationToken cancellationToken)
    {
        var tramos = ValoracionOperadorCae.TramosEnElMes(vigencias, mes.InicioUtc, mes.FinUtc);
        if (tramos.Count == 0)
            return [];

        var valorada = ValoracionOperadorCae.CubreElMes(tramos, mes.InicioUtc, mes.FinUtc)
            ? medida
            : ValoracionOperadorCae.AcotadaA(medida, await medicion.MedirUnidadesOperadorAsync(tramos, corteUtc, cancellationToken));
        return ValoracionOperadorCae.Valorar(valorada, tenantBeneficiarioId, tarifas, instanteUtc);
    }

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

    /// <summary>Suma las líneas de varios Tenants beneficiarios por concepto y moneda: un renglón por cada par.</summary>
    public static IReadOnlyList<LineaFacturacionCuadroDto> SumarLineas(IEnumerable<LineaFacturacionCuadroDto> lineas) =>
        lineas
            .GroupBy(l => (l.Concepto, Moneda: l.MonedaIso.Trim().ToUpperInvariant()))
            .Select(g => new LineaFacturacionCuadroDto(
                g.Key.Concepto, g.First().ConceptoNombre, g.Key.Moneda, g.Sum(l => l.Unidades), g.Sum(l => l.Importe)))
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
            ? SumarLineas(medidos.SelectMany(m => m.Facturacion!))
            : null;

        return new PuntoSerieCuadroDto(
            mes.Anyo, mes.Mes, estado,
            ObtenerCuadroDireccionQueryHandler.Cifras(medidos.Select(m => m.Medicion).ToList(), estado, ahoraUtc),
            facturacion);
    }
}
