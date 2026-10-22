using CaeManager.Application.Dashboard.Queries;

namespace CaeManager.Web.Features.CuadroDireccion;

/// <summary>
/// Suma de lo que el servidor ya midió por Tenant beneficiario, para el ámbito «Equipo de un Gestor CAE» del
/// Cuadro de dirección: las Empresas de su cartera principal, juntas. Solo suma volúmenes (trabajadores, centros,
/// documentos, solicitudes, importes): no mide a nadie. Las fórmulas son las del servidor
/// (<see cref="ObtenerCuadroDireccionQueryHandler.Cifras"/>): el porcentaje de cumplimiento se recalcula sobre los
/// recuentos sumados, nunca como promedio de porcentajes.
/// </summary>
public static class AgregadosCuadro
{
    /// <summary>El estado de un conjunto de cifras: parcial si alguna lo es, si no estimado si alguna lo es, si no cerrado.</summary>
    public static EstadoCifraMensual EstadoComun(IEnumerable<EstadoCifraMensual> estados)
    {
        var lista = estados.ToList();
        if (lista.Count == 0 || lista.Contains(EstadoCifraMensual.Parcial)) return EstadoCifraMensual.Parcial;
        return lista.Contains(EstadoCifraMensual.Estimado) ? EstadoCifraMensual.Estimado : EstadoCifraMensual.Existe;
    }

    public static CifrasCuadroDireccionDto SumarCifras(IReadOnlyList<CifrasCuadroDireccionDto> cifras, DateTime ahoraUtc)
    {
        var estado = EstadoComun(cifras.Select(c => c.TrabajadoresActivos.Estado));

        CifraMensual Suma(Func<CifrasCuadroDireccionDto, CifraMensual> campo) => new(cifras.Sum(c => campo(c).Valor), estado);

        var franjas = cifras
            .SelectMany(c => c.Solicitudes.PorFranja)
            .GroupBy(f => (f.Dia, f.Hora))
            .Select(g => new FranjaSolicitudesDto(g.Key.Dia, g.Key.Hora, g.Sum(f => f.Cantidad)))
            .OrderBy(f => f.Dia).ThenBy(f => f.Hora)
            .ToList();

        var vigentes = cifras.Sum(c => c.Cumplimiento.Vigentes);
        var proximos = cifras.Sum(c => c.Cumplimiento.Proximos);
        var urgentes = cifras.Sum(c => c.Cumplimiento.Urgentes);
        var vencidos = cifras.Sum(c => c.Cumplimiento.Vencidos);
        var sinConfirmar = cifras.Sum(c => c.Cumplimiento.SinConfirmar);
        var sinCaducidad = cifras.Sum(c => c.Cumplimiento.SinCaducidad);
        var paresExigidos = cifras.Sum(c => c.Cumplimiento.ParesExigidosSinDocumento);
        var (porcentaje, sinDatos) = KpisDashboardDto.SinDocumentos(
            KpisDashboardDto.FraccionDe(vigentes, proximos, urgentes, vencidos, sinConfirmar, sinCaducidad), paresExigidos);

        return new CifrasCuadroDireccionDto(
            TrabajadoresActivos: Suma(c => c.TrabajadoresActivos),
            AltasTrabajadores: Suma(c => c.AltasTrabajadores),
            BajasTrabajadores: Suma(c => c.BajasTrabajadores),
            CentrosActivos: Suma(c => c.CentrosActivos),
            AltasCentros: Suma(c => c.AltasCentros),
            DocumentosGestionados: Suma(c => c.DocumentosGestionados),
            Requerimientos: Suma(c => c.Requerimientos),
            Solicitudes: new SolicitudesDelMesDto(Suma(c => c.Solicitudes.Total), franjas),
            Cumplimiento: new CumplimientoActualDto(
                vigentes, proximos, urgentes, vencidos, sinConfirmar, sinCaducidad, paresExigidos, porcentaje, sinDatos,
                cifras.Count == 0 ? ahoraUtc : cifras.Max(c => c.Cumplimiento.MedidoEnUtc),
                ObtenerCuadroDireccionQueryHandler.SumarExigido(cifras.Select(c => c.Cumplimiento.Exigido))),
            Gestiones: cifras.Count > 0 && cifras.All(c => c.Gestiones is not null)
                ? new CifraMensual(cifras.Sum(c => c.Gestiones!.Valor), estado)
                : null,
            Extranjeros: cifras.Count > 0 && cifras.All(c => c.Extranjeros is not null)
                ? new CifraMensual(cifras.Sum(c => c.Extranjeros!.Valor), estado)
                : null);
    }

    /// <summary>La serie mensual del conjunto: cada mes suma los puntos de cada Tenant (y su facturación, por concepto y moneda).</summary>
    public static IReadOnlyList<PuntoSerieCuadroDto> SumarPuntos(IReadOnlyList<IReadOnlyList<PuntoSerieCuadroDto>> series, DateTime ahoraUtc) =>
        series
            .SelectMany(s => s)
            .GroupBy(p => (p.Anyo, p.Mes))
            .OrderBy(g => g.Key.Anyo).ThenBy(g => g.Key.Mes)
            .Select(g =>
            {
                var puntos = g.ToList();
                var cifras = SumarCifras(puntos.Select(p => p.Cifras).ToList(), ahoraUtc);
                IReadOnlyList<LineaFacturacionCuadroDto>? facturacion = puntos.All(p => p.Facturacion is not null)
                    ? puntos.SelectMany(p => p.Facturacion!)
                        .GroupBy(l => (l.Concepto, Moneda: l.MonedaIso.Trim().ToUpperInvariant()))
                        .Select(l => new LineaFacturacionCuadroDto(
                            l.Key.Concepto, l.First().ConceptoNombre, l.Key.Moneda, l.Sum(x => x.Unidades), l.Sum(x => x.Importe)))
                        .ToList()
                    : null;
                return new PuntoSerieCuadroDto(g.Key.Anyo, g.Key.Mes, EstadoComun(puntos.Select(p => p.Estado)), cifras, facturacion);
            })
            .ToList();

    public static IReadOnlyList<GestionesDelDiaDto> SumarDias(IEnumerable<IReadOnlyList<GestionesDelDiaDto>> dias) =>
        dias.SelectMany(d => d)
            .GroupBy(d => d.Dia)
            .OrderBy(g => g.Key)
            .Select(g => new GestionesDelDiaDto(g.Key, g.Sum(d => d.Cantidad)))
            .ToList();
}
