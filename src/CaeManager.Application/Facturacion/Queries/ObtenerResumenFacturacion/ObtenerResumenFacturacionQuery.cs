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
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Facturacion.Queries.ObtenerResumenFacturacion;

public record ObtenerResumenFacturacionQuery(Guid ClienteId, int Anyo, int Mes)
    : IRequest<ResumenFacturacionDto?>;

public record ResumenFacturacionDto(
    string ClienteNombre,
    string MonedaIso,
    decimal TotalEstimado,
    IList<LineaFacturacionDto> Lineas);

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
            return new ResumenFacturacionDto(clienteNombre, "EUR", 0, []);

        var centroIds = await centrosContext.Centros
            .Where(c => c.ClienteId == request.ClienteId)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        var moneda = tarifas[0].MonedaIso;
        var lineas = new List<LineaFacturacionDto>();

        foreach (var tarifa in tarifas)
        {
            var unidades = tarifa.Concepto switch
            {
                ConceptoFacturable.TrabajadorActivo =>
                    await ContarTrabajadoresActivosAsync(centroIds, periodoInicioFecha, periodoFinFecha, cancellationToken),
                ConceptoFacturable.AltaCentro =>
                    await ContarAltasCentroAsync(request.ClienteId, periodoInicio, periodoFin, cancellationToken),
                ConceptoFacturable.VisitaTrabajadorExtranjero =>
                    await ContarVisitasExtranjeroAsync(centroIds, periodoInicioFecha, periodoFinFecha, cancellationToken),
                ConceptoFacturable.DocumentoGestionado =>
                    await ContarDocumentosGestionadosAsync(centroIds, periodoInicioFecha, periodoFinFecha, periodoInicio, periodoFin, cancellationToken),
                ConceptoFacturable.TecnicoAsignadoProyecto =>
                    await ContarTecnicosAsignadosProyectoAsync(request.ClienteId, periodoInicioFecha, periodoFinFecha, cancellationToken),
                ConceptoFacturable.GestionProyectoRealizada =>
                    await ContarGestionesProyectoAsync(request.ClienteId, periodoInicio, periodoFin, cancellationToken),
                ConceptoFacturable.DiaProyectoAbierto =>
                    await ContarDiasProyectoAbiertoAsync(request.ClienteId, periodoInicioFecha, periodoFinFecha, cancellationToken),
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

        return new ResumenFacturacionDto(clienteNombre, moneda, lineas.Sum(l => l.Subtotal), lineas);
    }

    private async Task<int> ContarTrabajadoresActivosAsync(
        IList<Guid> centroIds, DateOnly inicio, DateOnly fin, CancellationToken ct)
    {
        if (centroIds.Count == 0) return 0;

        return await asignacionesContext.Asignaciones
            .Where(a => centroIds.Contains(a.CentroId)
                     && a.FechaAlta <= fin
                     && (a.FechaBaja == null || a.FechaBaja >= inicio))
            .Select(a => a.TrabajadorId)
            .Distinct()
            .CountAsync(ct);
    }

    private async Task<int> ContarAltasCentroAsync(
        Guid clienteId, DateTime inicio, DateTime fin, CancellationToken ct)
    {
        return await centrosContext.Centros
            .Where(c => c.ClienteId == clienteId && c.CreadoEnUtc >= inicio && c.CreadoEnUtc < fin)
            .CountAsync(ct);
    }

    private async Task<int> ContarVisitasExtranjeroAsync(
        IList<Guid> centroIds, DateOnly inicio, DateOnly fin, CancellationToken ct)
    {
        if (centroIds.Count == 0) return 0;

        var visitaIds = await visitasContext.Visitas
            .Where(v => centroIds.Contains(v.CentroId) && v.FechaInicio >= inicio && v.FechaInicio <= fin)
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
        DateTime inicio, DateTime fin, CancellationToken ct)
    {
        if (centroIds.Count == 0) return 0;

        var trabajadorIds = await asignacionesContext.Asignaciones
            .Where(a => centroIds.Contains(a.CentroId)
                     && a.FechaAlta <= finFecha
                     && (a.FechaBaja == null || a.FechaBaja >= inicioFecha))
            .Select(a => a.TrabajadorId)
            .Distinct()
            .ToListAsync(ct);

        if (trabajadorIds.Count == 0) return 0;

        return await documentosContext.Documentos
            .Where(d => d.TrabajadorId != null
                     && trabajadorIds.Contains(d.TrabajadorId!.Value)
                     && d.CreadoEnUtc >= inicio
                     && d.CreadoEnUtc < fin)
            .CountAsync(ct);
    }

    /// <summary>Técnicos únicos con alta en algún Proyecto del cliente, activa en algún punto del período — factura la obra, no la gestión CAE tradicional.</summary>
    private async Task<int> ContarTecnicosAsignadosProyectoAsync(Guid clienteId, DateOnly inicio, DateOnly fin, CancellationToken ct)
    {
        var proyectoIds = await proyectosContext.Proyectos
            .Where(p => p.ClienteId == clienteId)
            .Select(p => p.Id)
            .ToListAsync(ct);

        if (proyectoIds.Count == 0) return 0;

        return await proyectosContext.ProyectosTecnicos
            .Where(pt => proyectoIds.Contains(pt.ProyectoId)
                      && pt.FechaAlta <= fin
                      && (pt.FechaBaja == null || pt.FechaBaja >= inicio))
            .Select(pt => pt.TrabajadorId)
            .Distinct()
            .CountAsync(ct);
    }

    private async Task<int> ContarGestionesProyectoAsync(Guid clienteId, DateTime inicio, DateTime fin, CancellationToken ct)
    {
        var proyectoIds = await proyectosContext.Proyectos
            .Where(p => p.ClienteId == clienteId)
            .Select(p => p.Id)
            .ToListAsync(ct);

        if (proyectoIds.Count == 0) return 0;

        return await documentosContext.Documentos
            .Where(d => d.ProyectoId != null
                     && proyectoIds.Contains(d.ProyectoId!.Value)
                     && d.CreadoEnUtc >= inicio
                     && d.CreadoEnUtc < fin)
            .CountAsync(ct);
    }

    /// <summary>
    /// Suma, para cada Proyecto del cliente, los días de intersección entre
    /// su periodo abierto (FechaInicio–FechaCierreReal, o "hoy" si sigue
    /// abierto) y el período de facturación consultado.
    /// </summary>
    private async Task<int> ContarDiasProyectoAbiertoAsync(Guid clienteId, DateOnly inicio, DateOnly fin, CancellationToken ct)
    {
        var proyectos = await proyectosContext.Proyectos
            .Where(p => p.ClienteId == clienteId)
            .Select(p => new { p.FechaInicio, p.FechaCierreReal })
            .ToListAsync(ct);

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
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
