using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Facturacion;
using CaeManager.Application.Proyectos;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Visitas;
using CaeManager.Application.Facturacion.Queries.ObtenerTarifasCliente;
using CaeManager.Domain.Common;
using CaeManager.Domain.Facturacion;
using CaeManager.Domain.Proyectos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Facturacion.Queries.ObtenerResumenFacturacion;

/// <param name="ExistenteHastaUtc">
/// Si se indica, solo cuentan los registros que ya existían en esa fecha (<c>CreadoEnUtc</c> menor o igual). La
/// pantalla «Facturación» no lo usa (valora el mes con lo que hay hoy). La serie histórica del Cuadro de dirección sí:
/// las fechas de negocio (alta de una asignación, inicio de una obra, fecha de una visita) pueden ser anteriores al
/// momento en que se registró el dato, y sin este corte un mes anterior a la existencia de los registros se facturaba
/// con unidades que el resto de la serie (que mide por fecha de creación) todavía no contaba.
/// </param>
public record ObtenerResumenFacturacionQuery(Guid ClienteId, int Anyo, int Mes, DateTime? ExistenteHastaUtc = null)
    : IRequest<ResumenFacturacionDto?>;

public record ResumenFacturacionDto(
    string ClienteNombre,
    IReadOnlyList<TotalPorMonedaDto> TotalesPorMoneda,
    IList<LineaFacturacionDto> Lineas)
{
    /// <summary>
    /// Un total por cada moneda presente en <paramref name="lineas"/>, en el
    /// orden en que aparece cada moneda por primera vez. Nunca suma subtotales
    /// de monedas distintas: con tarifas en EUR y USD a la vez, sumarlas sin
    /// mirar la moneda producía una cifra sin significado (100 EUR + 50 USD = "150").
    /// </summary>
    public static IReadOnlyList<TotalPorMonedaDto> CalcularTotalesPorMoneda(IEnumerable<LineaFacturacionDto> lineas) =>
        lineas
            .GroupBy(l => l.MonedaIso.Trim().ToUpperInvariant())
            .Select(g => new TotalPorMonedaDto(g.Key, g.Sum(l => l.Subtotal)))
            .ToList();
}

public record TotalPorMonedaDto(string MonedaIso, decimal Total);

public record LineaFacturacionDto(
    ConceptoFacturable Concepto,
    string ConceptoNombre,
    int Unidades,
    decimal PrecioUnitario,
    string MonedaIso,
    decimal Subtotal);

/// <summary>
/// Calcula el resumen de facturación mensual para un cliente a partir de los
/// datos ya existentes en el dominio — sin tabla de eventos separada. Las
/// unidades se calculan al momento de la consulta, por lo que un cambio de
/// tarifa a mitad del mes afecta al cálculo completo del mes. Para mayor
/// precisión histórica se pueden añadir fechas de vigencia a <see cref="TarifaCliente"/>
/// en una fase posterior.
///
/// Los Proyectos de tipo <see cref="TipoProyecto.Evento"/> no se facturan (decisión de producto de 2026-10-15): los
/// tres conceptos de Proyecto (técnicos, gestiones y días abiertos) solo cuentan obras. Los conceptos de Centro no
/// cambian: los participantes de un evento son <c>ProyectoTecnico</c>, no Asignaciones al Centro.
/// </summary>
public class ObtenerResumenFacturacionQueryHandler(IAsignacionesQueryContext asignacionesContext, ICentrosQueryContext centrosContext, IEmpresasQueryContext empresasContext, IDocumentosQueryContext documentosContext, IFacturacionQueryContext facturacionContext, IProyectosQueryContext proyectosContext, ITrabajadoresQueryContext trabajadoresContext, IVisitasQueryContext visitasContext)
    : IRequestHandler<ObtenerResumenFacturacionQuery, ResumenFacturacionDto?>
{
    public async Task<ResumenFacturacionDto?> Handle(ObtenerResumenFacturacionQuery request, CancellationToken cancellationToken)
    {
        var periodoInicio = new DateTime(request.Anyo, request.Mes, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodoFin = periodoInicio.AddMonths(1);
        var periodoInicioFecha = DateOnly.FromDateTime(periodoInicio);
        var periodoFinFecha = DateOnly.FromDateTime(periodoFin.AddDays(-1));

        var clienteNombre = await empresasContext.Empresas
            .Where(c => c.Id == request.ClienteId)
            .Select(c => c.RazonSocial)
            .FirstOrDefaultAsync(cancellationToken);

        if (clienteNombre is null)
            return null;

        var tarifas = await facturacionContext.TarifasCliente
            .Where(t => t.ClienteId == request.ClienteId)
            .OrderBy(t => t.Concepto)
            .ToListAsync(cancellationToken);

        if (tarifas.Count == 0)
            return new ResumenFacturacionDto(clienteNombre, [], []);

        // Sin corte, un instante lejano y en UTC (Npgsql rechaza un DateTime sin Kind=Utc para timestamptz).
        var corte = request.ExistenteHastaUtc is { } existente
            ? DateTime.SpecifyKind(existente, DateTimeKind.Utc)
            : new DateTime(9999, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        var centroIds = await centrosContext.Centros
            .Where(c => c.ClienteId == request.ClienteId && c.CreadoEnUtc <= corte)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        var lineas = new List<LineaFacturacionDto>();

        foreach (var tarifa in tarifas)
        {
            var unidades = tarifa.Concepto switch
            {
                ConceptoFacturable.TrabajadorActivo =>
                    await ContarTrabajadoresActivosAsync(centroIds, periodoInicioFecha, periodoFinFecha, corte, cancellationToken),
                ConceptoFacturable.AltaCentro =>
                    await ContarAltasCentroAsync(request.ClienteId, periodoInicio, periodoFin, corte, cancellationToken),
                ConceptoFacturable.VisitaTrabajadorExtranjero =>
                    await ContarVisitasExtranjeroAsync(centroIds, periodoInicioFecha, periodoFinFecha, corte, cancellationToken),
                ConceptoFacturable.DocumentoGestionado =>
                    await ContarDocumentosGestionadosAsync(centroIds, periodoInicioFecha, periodoFinFecha, periodoInicio, periodoFin, corte, cancellationToken),
                ConceptoFacturable.TecnicoAsignadoProyecto =>
                    await ContarTecnicosAsignadosProyectoAsync(request.ClienteId, periodoInicioFecha, periodoFinFecha, corte, cancellationToken),
                ConceptoFacturable.GestionProyectoRealizada =>
                    await ContarGestionesProyectoAsync(request.ClienteId, periodoInicio, periodoFin, corte, cancellationToken),
                ConceptoFacturable.DiaProyectoAbierto =>
                    await ContarDiasProyectoAbiertoAsync(request.ClienteId, periodoInicioFecha, periodoFinFecha, corte, cancellationToken),
                _ => 0
            };

            lineas.Add(new LineaFacturacionDto(
                tarifa.Concepto,
                ObtenerTarifasClienteQueryHandler.NombreConcepto(tarifa.Concepto),
                unidades,
                tarifa.PrecioUnitario,
                tarifa.MonedaIso,
                unidades * tarifa.PrecioUnitario));
        }

        return new ResumenFacturacionDto(clienteNombre, ResumenFacturacionDto.CalcularTotalesPorMoneda(lineas), lineas);
    }

    private async Task<int> ContarTrabajadoresActivosAsync(
        IList<Guid> centroIds, DateOnly inicio, DateOnly fin, DateTime corte, CancellationToken ct)
    {
        if (centroIds.Count == 0) return 0;

        var ids = await asignacionesContext.Asignaciones
            .Where(a => centroIds.Contains(a.CentroId)
                     && a.FechaAlta <= fin
                     && (a.FechaBaja == null || a.FechaBaja >= inicio))
            .Select(a => a.TrabajadorId)
            .Distinct()
            .ToListAsync(ct);

        return (await TrabajadoresExistentesAsync(ids, corte, ct)).Count;
    }

    /// <summary>Entre los trabajadores dados, los que ya existían (fecha de creación del registro) en el instante de corte.</summary>
    private async Task<List<Guid>> TrabajadoresExistentesAsync(List<Guid> ids, DateTime corte, CancellationToken ct)
    {
        // Sin corte (el resumen de Facturación de hoy) no hay nada que filtrar: se evita la consulta.
        if (ids.Count == 0 || corte.Year >= 9999) return ids;

        return await trabajadoresContext.Trabajadores
            .Where(t => ids.Contains(t.Id) && t.CreadoEnUtc <= corte)
            .Select(t => t.Id)
            .ToListAsync(ct);
    }

    private async Task<int> ContarAltasCentroAsync(
        Guid clienteId, DateTime inicio, DateTime fin, DateTime corte, CancellationToken ct)
    {
        return await centrosContext.Centros
            .Where(c => c.ClienteId == clienteId && c.CreadoEnUtc >= inicio && c.CreadoEnUtc < fin && c.CreadoEnUtc <= corte)
            .CountAsync(ct);
    }

    private async Task<int> ContarVisitasExtranjeroAsync(
        IList<Guid> centroIds, DateOnly inicio, DateOnly fin, DateTime corte, CancellationToken ct)
    {
        if (centroIds.Count == 0) return 0;

        var visitaIds = await visitasContext.Visitas
            // FS-11: una Visita cancelada no se factura, igual que cuando cancelar era borrarla.
            .Where(v => centroIds.Contains(v.CentroId) && !v.EstaCancelada && v.CreadoEnUtc <= corte
                     && v.FechaInicio >= inicio && v.FechaInicio <= fin)
            .Select(v => v.Id)
            .ToListAsync(ct);

        if (visitaIds.Count == 0) return 0;

        var dnis = await visitasContext.VisitasTrabajadores
            .Where(vt => visitaIds.Contains(vt.VisitaId))
            .Join(trabajadoresContext.Trabajadores, vt => vt.TrabajadorId, t => t.Id, (vt, t) => t.Dni)
            .Distinct()
            .ToListAsync(ct);

        // Trabajadores con NIE, TIE o documento extranjero (no DNI español).
        // Un Dni null (trabajador anonimizado) no tiene tipo que clasificar.
        return dnis.Count(dni =>
            dni is not null && ValidadorIdentificacion.Analizar(dni).Tipo != TipoIdentificacion.Dni);
    }

    private async Task<int> ContarDocumentosGestionadosAsync(
        IList<Guid> centroIds, DateOnly inicioFecha, DateOnly finFecha,
        DateTime inicio, DateTime fin, DateTime corte, CancellationToken ct)
    {
        if (centroIds.Count == 0) return 0;

        var trabajadorIds = await asignacionesContext.Asignaciones
            .Where(a => centroIds.Contains(a.CentroId)
                     && a.FechaAlta <= finFecha
                     && (a.FechaBaja == null || a.FechaBaja >= inicioFecha))
            .Select(a => a.TrabajadorId)
            .Distinct()
            .ToListAsync(ct);

        trabajadorIds = await TrabajadoresExistentesAsync(trabajadorIds, corte, ct);

        if (trabajadorIds.Count == 0) return 0;

        return await documentosContext.Documentos
            .Where(d => d.TrabajadorId != null
                     && trabajadorIds.Contains(d.TrabajadorId!.Value)
                     && d.CreadoEnUtc >= inicio
                     && d.CreadoEnUtc < fin
                     && d.CreadoEnUtc <= corte)
            .CountAsync(ct);
    }

    /// <summary>Técnicos únicos con alta en alguna obra del cliente (no en un evento), activa en algún punto del período — factura la obra, no la gestión CAE tradicional.</summary>
    private async Task<int> ContarTecnicosAsignadosProyectoAsync(Guid clienteId, DateOnly inicio, DateOnly fin, DateTime corte, CancellationToken ct)
    {
        var proyectoIds = await proyectosContext.Proyectos
            .Where(p => p.ClienteId == clienteId && p.Tipo != TipoProyecto.Evento && p.CreadoEnUtc <= corte)
            .Select(p => p.Id)
            .ToListAsync(ct);

        if (proyectoIds.Count == 0) return 0;

        var tecnicoIds = await proyectosContext.ProyectosTecnicos
            .Where(pt => proyectoIds.Contains(pt.ProyectoId)
                      && pt.FechaAlta <= fin
                      && (pt.FechaBaja == null || pt.FechaBaja >= inicio))
            .Select(pt => pt.TrabajadorId)
            .Distinct()
            .ToListAsync(ct);

        return (await TrabajadoresExistentesAsync(tecnicoIds, corte, ct)).Count;
    }

    private async Task<int> ContarGestionesProyectoAsync(Guid clienteId, DateTime inicio, DateTime fin, DateTime corte, CancellationToken ct)
    {
        var proyectoIds = await proyectosContext.Proyectos
            .Where(p => p.ClienteId == clienteId && p.Tipo != TipoProyecto.Evento && p.CreadoEnUtc <= corte)
            .Select(p => p.Id)
            .ToListAsync(ct);

        if (proyectoIds.Count == 0) return 0;

        return await documentosContext.Documentos
            .Where(d => d.ProyectoId != null
                     && proyectoIds.Contains(d.ProyectoId!.Value)
                     && d.CreadoEnUtc >= inicio
                     && d.CreadoEnUtc < fin
                     && d.CreadoEnUtc <= corte)
            .CountAsync(ct);
    }

    /// <summary>
    /// Suma, para cada obra del cliente (los eventos no se facturan), los días de intersección entre
    /// su periodo abierto (FechaInicio–FechaCierreReal, o "hoy" si sigue
    /// abierto) y el período de facturación consultado.
    /// </summary>
    private async Task<int> ContarDiasProyectoAbiertoAsync(Guid clienteId, DateOnly inicio, DateOnly fin, DateTime corte, CancellationToken ct)
    {
        var proyectos = await proyectosContext.Proyectos
            .Where(p => p.ClienteId == clienteId && p.Tipo != TipoProyecto.Evento && p.CreadoEnUtc <= corte)
            .Select(p => new { p.FechaInicio, p.FechaCierreReal })
            .ToListAsync(ct);

        var hoy = DiaDeNegocio.Hoy();
        var dias = 0;

        foreach (var proyecto in proyectos)
        {
            var aperturaFin = proyecto.FechaCierreReal ?? hoy;
            var desde = proyecto.FechaInicio > inicio ? proyecto.FechaInicio : inicio;
            var hasta = aperturaFin < fin ? aperturaFin : fin;

            if (hasta >= desde)
                dias += hasta.DayNumber - desde.DayNumber + 1;
        }

        return dias;
    }
}
