using CaeManager.Application.Common;
using CaeManager.Application.Dashboard.TarifasOperadorCae;
using CaeManager.Application.Operaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Dashboard;
using MediatR;

namespace CaeManager.Application.Dashboard.Queries;

/// <summary>
/// Lo que el Operador CAE externo cobra en un mes a cada Empresa gestionada (Tenant beneficiario): la pestaña
/// «Facturación» del Cuadro de dirección (decisión de Chris, 2026-10-20). La unidad de cobro es el Tenant beneficiario,
/// no el Cliente empresarial.
/// </summary>
public record ObtenerFacturacionOperadorCaeQuery(int Anyo, int Mes) : IRequest<FacturacionOperadorCaeDto>;

/// <summary>La tarifa vigente de un concepto para un Tenant beneficiario; lo que el drawer de tarifas edita.</summary>
public record TarifaVigenteOperadorCaeDto(ConceptoTarifaOperadorCae Concepto, decimal PrecioUnitario, string MonedaIso, bool Activa);

/// <param name="Unidades">Unidades del concepto en el mes; se muestran siempre, se cobren o no.</param>
/// <param name="PrecioUnitario">Null si el Tenant no tiene tarifa para el concepto en el mes.</param>
/// <param name="Importe">Cero si no hay tarifa o el cobro está desactivado.</param>
public record ConceptoFacturadoOperadorCaeDto(
    ConceptoTarifaOperadorCae Concepto, int Unidades, decimal? PrecioUnitario, bool CobroActivo, decimal Importe);

/// <summary>Ingreso por gestión de una Empresa gestionada, nunca de una persona (ver <see cref="CalculosFacturacionOperadorCae"/>).</summary>
/// <param name="PorGestion">Cuota del mes entre las gestiones totales.</param>
/// <param name="PorGestionNormal">(Cuota menos el importe de extranjeros) entre las gestiones que no son de extranjeros.</param>
/// <param name="PorGestionExtranjero">Importe de extranjeros entre las gestiones de extranjeros.</param>
/// <param name="Relacion">Ingreso por gestión de extranjero entre ingreso por gestión normal.</param>
public record IngresoPorGestionOperadorCaeDto(decimal? PorGestion, decimal? PorGestionNormal, decimal? PorGestionExtranjero, decimal? Relacion);

/// <param name="Extranjeros">
/// Trabajadores extranjeros cobrables por Visita del mes: sale de <see cref="ContarExtranjerosCobrablesDelMesQuery"/>, el
/// mismo criterio que la exportación a Excel.
/// </param>
/// <param name="Gestiones">Gestiones completadas del mes; null si la medición no las lleva (un cierre anterior).</param>
/// <param name="GestionesDeExtranjeros">Cada Trabajador extranjero por Visita es una gestión de extranjero.</param>
/// <param name="Cuota">Null si la Empresa no tiene tarifa en el mes, o si quien mira no puede ver importes.</param>
public record FilaFacturacionOperadorCaeDto(
    Guid TenantId, string TenantNombre, string? Moneda,
    ConceptoFacturadoOperadorCaeDto Extranjero, ConceptoFacturadoOperadorCaeDto Centro, ConceptoFacturadoOperadorCaeDto Obra,
    int? Gestiones, int GestionesDeExtranjeros, decimal? Cuota, bool SinTarifa,
    IngresoPorGestionOperadorCaeDto Ingreso,
    IReadOnlyList<CentrosNuevosPorClienteDto> CentrosPorCliente,
    IReadOnlyList<TarifaVigenteOperadorCaeDto> TarifaVigente)
{
    public int? GestionesNormales => Gestiones is { } g ? Math.Max(0, g - GestionesDeExtranjeros) : null;
}

/// <param name="Moneda">La moneda de los totales; las Empresas con tarifa en otra no se suman.</param>
public record TotalFacturacionOperadorCaeDto(
    string Moneda, int Extranjeros, int CentrosNuevos, int ObrasNuevas, decimal Cuota, int EmpresasConTarifa,
    int? GestionesNormales, IngresoPorGestionOperadorCaeDto Ingreso);

/// <param name="PuedeFijarTarifas">Solo el Administrador o la Dirección CAE del Operador (<see cref="AutorizacionTarifasOperadorCae"/>).</param>
/// <param name="CentrosPorCliente">Los Centros nuevos de todas las Empresas repartidos por Cliente empresarial: indicador de crecimiento.</param>
public record FacturacionOperadorCaeDto(
    int Anyo, int Mes, bool PuedeFijarTarifas, IReadOnlyList<FilaFacturacionOperadorCaeDto> Filas, TotalFacturacionOperadorCaeDto Total,
    IReadOnlyList<CentrosNuevosPorClienteTotalDto> CentrosPorCliente, int TenantsSinAlcanceCompleto,
    CuadroDireccionSinCifras? SinCifras = null);

public record CentrosNuevosPorClienteTotalDto(string ClienteNombre, int Centros);

/// <summary>
/// Cálculo puro de la cuota del Operador CAE por Empresa gestionada y de su ingreso por gestión. Se muestra por Empresa
/// gestionada y nunca por persona: suma las gestiones de todos sus Gestores CAE. Sin tarifa o sin gestiones, «sin dato»
/// (null), nunca cero.
/// </summary>
public static class CalculosFacturacionOperadorCae
{
    public static ConceptoFacturadoOperadorCaeDto Concepto(
        ConceptoTarifaOperadorCae concepto, int unidades, LineaFacturacionCuadroDto? linea)
    {
        if (linea is null)
            return new ConceptoFacturadoOperadorCaeDto(concepto, unidades, null, false, 0m);

        var activo = linea.CobroActivo ?? linea.Importe > 0m;
        var precio = linea.PrecioUnitario ?? (linea.Unidades > 0 ? linea.Importe / linea.Unidades : 0m);
        return new ConceptoFacturadoOperadorCaeDto(concepto, unidades, precio, activo, activo ? unidades * precio : linea.Importe);
    }

    public static IngresoPorGestionOperadorCaeDto Ingreso(decimal cuota, decimal importeExtranjeros, int? gestiones, int gestionesDeExtranjeros)
    {
        if (gestiones is not > 0)
            return new IngresoPorGestionOperadorCaeDto(null, null, null, null);

        var normales = Math.Max(0, gestiones.Value - gestionesDeExtranjeros);
        decimal? porNormal = normales > 0 ? (cuota - importeExtranjeros) / normales : null;
        decimal? porExtranjero = gestionesDeExtranjeros > 0 ? importeExtranjeros / gestionesDeExtranjeros : null;
        decimal? relacion = porNormal is > 0m && porExtranjero is { } e ? e / porNormal : null;
        return new IngresoPorGestionOperadorCaeDto(cuota / gestiones.Value, porNormal, porExtranjero, relacion);
    }

    /// <summary>
    /// La fila de una Empresa gestionada. <paramref name="lineas"/> es null si quien mira no puede ver importes; vacía si la
    /// Empresa no tiene tarifa (o ninguna Asignación de Operación vigente en el mes): entonces las unidades se cuentan pero
    /// no hay cuota.
    /// </summary>
    public static FilaFacturacionOperadorCaeDto Fila(
        Guid tenantId, string tenantNombre, int extranjeros, int centrosNuevos, int obrasNuevas, int? gestiones,
        IReadOnlyList<CentrosNuevosPorClienteDto> centrosPorCliente, IReadOnlyList<LineaFacturacionCuadroDto>? lineas,
        IReadOnlyList<TarifaVigenteOperadorCaeDto> tarifaVigente)
    {
        var moneda = lineas is { Count: > 0 }
            ? (lineas.FirstOrDefault(l => l.Concepto == ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero) ?? lineas[0]).MonedaIso.Trim().ToUpperInvariant()
            : null;
        var delConcepto = (ConceptoTarifaOperadorCae c) => lineas?.FirstOrDefault(l =>
            l.Concepto == c && string.Equals(l.MonedaIso.Trim(), moneda, StringComparison.OrdinalIgnoreCase));

        var ext = Concepto(ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero, extranjeros, delConcepto(ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero));
        var centroLinea = delConcepto(ConceptoTarifaOperadorCae.CentroNuevo);
        var obraLinea = delConcepto(ConceptoTarifaOperadorCae.ObraNueva);
        var centro = Concepto(ConceptoTarifaOperadorCae.CentroNuevo, centroLinea?.Unidades ?? centrosNuevos, centroLinea);
        var obra = Concepto(ConceptoTarifaOperadorCae.ObraNueva, obraLinea?.Unidades ?? obrasNuevas, obraLinea);

        var sinTarifa = lineas is not { Count: > 0 };
        decimal? cuota = sinTarifa ? null : ext.Importe + centro.Importe + obra.Importe;
        var ingreso = cuota is { } c
            ? Ingreso(c, ext.Importe, gestiones, extranjeros)
            : new IngresoPorGestionOperadorCaeDto(null, null, null, null);

        return new FilaFacturacionOperadorCaeDto(
            tenantId, tenantNombre, moneda, ext, centro, obra, gestiones, extranjeros, cuota, sinTarifa, ingreso,
            centrosPorCliente, tarifaVigente);
    }

    /// <summary>Suma las Empresas con tarifa en la moneda más usada; las de otra moneda no se mezclan.</summary>
    public static TotalFacturacionOperadorCaeDto Total(IReadOnlyList<FilaFacturacionOperadorCaeDto> filas)
    {
        var moneda = filas.Where(f => !f.SinTarifa && f.Moneda is not null)
            .GroupBy(f => f.Moneda!)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key)
            .FirstOrDefault() ?? "EUR";
        var conTarifa = filas.Where(f => !f.SinTarifa && f.Moneda == moneda).ToList();

        var cuota = conTarifa.Sum(f => f.Cuota ?? 0m);
        var importeExt = conTarifa.Sum(f => f.Extranjero.Importe);
        var gestiones = conTarifa.Any(f => f.Gestiones is null) ? (int?)null : conTarifa.Sum(f => f.Gestiones ?? 0);
        var gestionesExt = conTarifa.Sum(f => f.GestionesDeExtranjeros);

        return new TotalFacturacionOperadorCaeDto(
            moneda, filas.Sum(f => f.Extranjero.Unidades), filas.Sum(f => f.Centro.Unidades), filas.Sum(f => f.Obra.Unidades),
            cuota, conTarifa.Count,
            gestiones is { } g ? Math.Max(0, g - gestionesExt) : null,
            Ingreso(cuota, importeExt, gestiones, gestionesExt));
    }
}

public class ObtenerFacturacionOperadorCaeQueryHandler(
    IMediator mediator, ICurrentUserService usuario, IOperacionesQueryContext operaciones, IPoliticaNotaInterna politicaAcceso, TimeProvider reloj)
    : IRequestHandler<ObtenerFacturacionOperadorCaeQuery, FacturacionOperadorCaeDto>
{
    private static FacturacionOperadorCaeDto Vacia(int anyo, int mes, CuadroDireccionSinCifras? motivo, int sinAlcance = 0) =>
        new(anyo, mes, false, [], CalculosFacturacionOperadorCae.Total([]), [], sinAlcance, motivo);

    /// <summary>
    /// Autorización y alcance son los de las consultas que compone: <see cref="ObtenerSerieCuadroDireccionQuery"/> (las
    /// cifras, y los importes solo para quien pasa <see cref="AutorizacionTarifasOperadorCae"/>) y
    /// <see cref="ContarExtranjerosCobrablesDelMesQuery"/> (la cifra de extranjeros).
    ///
    /// <para>
    /// <b>Qué Empresas salen</b>: solo los Tenants beneficiarios que el Operador CAE de quien mira gestiona hoy o gestionó
    /// en el mes mediante una Asignación de Operación EXTERNA (<see cref="AutorizacionTarifasOperadorCae.VigenciasPorTenantBeneficiarioAsync"/>,
    /// la misma vigencia que usa la serie): nunca el Tenant propio del Operador (no se factura a sí mismo) ni, para quien
    /// no es de un Operador CAE, ninguna. El Operador es el Tenant de origen de quien mira, sea Administrador, Dirección
    /// CAE o Coordinador CAE: ver las Empresas no da derecho a ver importes. Sin vigencia en el mes, la Empresa no sale.
    /// </para>
    /// <para>
    /// Esta consulta solo lee las Asignaciones de Operación del Operador (con el ámbito en su Tenant de origen); las demás
    /// cifras las leen las consultas que compone.
    /// </para>
    /// </summary>
    public async Task<FacturacionOperadorCaeDto> Handle(ObtenerFacturacionOperadorCaeQuery request, CancellationToken cancellationToken)
    {
        await AutorizacionCuadroDireccion.ExigirLecturaAsync(usuario, politicaAcceso);
        var tramos = (await ObtenerEmpresasGestionadasDelMesQueryHandler.LeerAsync(
            usuario, operaciones, request.Anyo, request.Mes, cancellationToken)).Tramos;

        var extranjeros = await mediator.Send(new ContarExtranjerosCobrablesDelMesQuery(request.Anyo, request.Mes, tramos), cancellationToken);
        // El aviso de «Empresas sin alcance completo» cuenta solo las Empresas gestionadas: las demás no son de esta Facturación.
        var sinAlcance = (extranjeros.IdsSinAlcanceCompleto ?? []).Count(tramos.ContainsKey);
        if (extranjeros.SinCifras is { } motivo)
            return Vacia(request.Anyo, request.Mes, motivo, sinAlcance);

        var serie = await mediator.Send(new ObtenerSerieCuadroDireccionQuery(request.Anyo, request.Mes, 1, ConFacturacion: true), cancellationToken);
        var tarifas = await mediator.Send(new ObtenerTarifasOperadorCaeQuery(), cancellationToken);
        var ahora = reloj.GetUtcNow().UtcDateTime;

        var filas = serie.Tenants
            .Where(t => tramos.ContainsKey(t.TenantId))
            .Select(t =>
            {
                var punto = t.Meses.LastOrDefault(p => p.Anyo == request.Anyo && p.Mes == request.Mes);
                var cifras = punto?.Cifras;
                var vigentes = tarifas is null
                    ? []
                    : ValoracionOperadorCae.Conceptos
                        .Select(c => ValoracionOperadorCae.VigenteEn(tarifas, t.TenantId, c, ahora))
                        .OfType<TarifaOperadorCaeDto>()
                        .Select(v => new TarifaVigenteOperadorCaeDto(v.Concepto, v.PrecioUnitario, v.MonedaIso, v.Activa))
                        .ToList();
                return CalculosFacturacionOperadorCae.Fila(
                    t.TenantId, t.TenantNombre,
                    extranjeros.PorTenant.FirstOrDefault(e => e.TenantId == t.TenantId)?.Asignaciones ?? 0,
                    cifras?.AltasCentros.Valor ?? 0, cifras?.ObrasNuevas?.Valor ?? 0, cifras?.Gestiones?.Valor,
                    // Defensa en profundidad: sin permiso sobre las tarifas (tarifas es null) no sale ninguna línea valorada, aunque
                    // la serie la trajera: ni cuota ni importes ni precios viajan en el DTO.
                    cifras?.CentrosNuevosPorCliente ?? [], tarifas is null ? null : punto?.Facturacion, vigentes);
            })
            .ToList();

        var porCliente = filas
            .SelectMany(f => f.CentrosPorCliente)
            .GroupBy(c => c.ClienteNombre)
            .Select(g => new CentrosNuevosPorClienteTotalDto(g.Key, g.Sum(c => c.Centros)))
            .OrderByDescending(c => c.Centros)
            .ThenBy(c => c.ClienteNombre, StringComparer.CurrentCulture)
            .ToList();

        return new FacturacionOperadorCaeDto(
            request.Anyo, request.Mes, tarifas is not null, filas, CalculosFacturacionOperadorCae.Total(filas), porCliente,
            sinAlcance);
    }
}
