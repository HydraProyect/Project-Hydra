using CaeManager.Application.Common;
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
public class ObtenerResumenFacturacionQueryHandler(IApplicationDbContext dbContext)
    : IRequestHandler<ObtenerResumenFacturacionQuery, ResumenFacturacionDto?>
{
    public async Task<ResumenFacturacionDto?> Handle(ObtenerResumenFacturacionQuery request, CancellationToken cancellationToken)
    {
        var periodoInicio = new DateTime(request.Anyo, request.Mes, 1, 0, 0, 0, DateTimeKind.Utc);
        var periodoFin = periodoInicio.AddMonths(1);
        var periodoInicioFecha = DateOnly.FromDateTime(periodoInicio);
        var periodoFinFecha = DateOnly.FromDateTime(periodoFin.AddDays(-1));

        var clienteNombre = await dbContext.Clientes
            .Where(c => c.Id == request.ClienteId)
            .Select(c => c.RazonSocial)
            .FirstOrDefaultAsync(cancellationToken);

        if (clienteNombre is null)
            return null;

        var tarifas = await dbContext.TarifasCliente
            .Where(t => t.ClienteId == request.ClienteId)
            .OrderBy(t => t.Concepto)
            .ToListAsync(cancellationToken);

        if (tarifas.Count == 0)
            return new ResumenFacturacionDto(clienteNombre, "EUR", 0, []);

        var centroIds = await dbContext.Centros
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

        return await dbContext.Asignaciones
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
        return await dbContext.Centros
            .Where(c => c.ClienteId == clienteId && c.CreadoEnUtc >= inicio && c.CreadoEnUtc < fin)
            .CountAsync(ct);
    }

    private async Task<int> ContarVisitasExtranjeroAsync(
        IList<Guid> centroIds, DateOnly inicio, DateOnly fin, CancellationToken ct)
    {
        if (centroIds.Count == 0) return 0;

        var visitaIds = await dbContext.Visitas
            .Where(v => centroIds.Contains(v.CentroId) && v.FechaInicio >= inicio && v.FechaInicio <= fin)
            .Select(v => v.Id)
            .ToListAsync(ct);

        if (visitaIds.Count == 0) return 0;

        var dnis = await dbContext.VisitasTrabajadores
            .Where(vt => visitaIds.Contains(vt.VisitaId))
            .Join(dbContext.Trabajadores, vt => vt.TrabajadorId, t => t.Id, (vt, t) => t.Dni)
            .Distinct()
            .ToListAsync(ct);

        // Trabajadores con NIE, TIE o documento extranjero (no DNI español)
        return dnis.Count(dni =>
            ValidadorIdentificacion.Analizar(dni).Tipo != TipoIdentificacion.Dni);
    }

    private async Task<int> ContarDocumentosGestionadosAsync(
        IList<Guid> centroIds, DateOnly inicioFecha, DateOnly finFecha,
        DateTime inicio, DateTime fin, CancellationToken ct)
    {
        if (centroIds.Count == 0) return 0;

        var trabajadorIds = await dbContext.Asignaciones
            .Where(a => centroIds.Contains(a.CentroId)
                     && a.FechaAlta <= finFecha
                     && (a.FechaBaja == null || a.FechaBaja >= inicioFecha))
            .Select(a => a.TrabajadorId)
            .Distinct()
            .ToListAsync(ct);

        if (trabajadorIds.Count == 0) return 0;

        return await dbContext.Documentos
            .Where(d => d.TrabajadorId != null
                     && trabajadorIds.Contains(d.TrabajadorId!.Value)
                     && d.CreadoEnUtc >= inicio
                     && d.CreadoEnUtc < fin)
            .CountAsync(ct);
    }
}
