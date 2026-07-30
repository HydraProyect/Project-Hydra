using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using MediatR;

namespace CaeManager.Application.Dashboard.Queries;

public record ObtenerKpisGlobalesQuery : IRequest<KpisGlobalesDto>;

public record ClienteRiesgoDto(
    Guid TenantId,
    string Nombre,
    int DocumentosVencidos,
    int DocumentosUrgentes,
    int TasaCumplimientoDocumental);

public record KpisGlobalesDto(
    int TotalClientes,
    int DocumentosVencidos,
    int DocumentosUrgentes,
    int DocumentosProximos,
    int TrabajadoresActivos,
    int Centros,
    int TasaCumplimientoDocumentalPromedio,
    IReadOnlyList<ClienteRiesgoDto> ClientesConMasRiesgo);

/// <summary>
/// Visión de cartera: agrega los KPI de <see cref="ObtenerKpisDashboardQuery"/>
/// entre todos los Clientes autorizados del usuario actual (su tenant de
/// origen más cualquier Delegated Workspace activo, ver ADR-004). No cruza
/// nunca el filtro global de tenant con una query propia — reutiliza la
/// misma Query de un solo tenant N veces, una por tenant, "sellada" con
/// <see cref="AmbitoTenantExplicito"/> (mismo patrón que usan los seeders de
/// demo), y suma/ordena en memoria. Para un usuario sin ningún Delegated
/// Workspace, el resultado tiene un único Cliente — su propio tenant — y esta
/// vista coincide con el Dashboard de siempre salvo por el envoltorio de
/// "cartera" (ver ROADMAP.md, Fase 51, Capa de Reporting).
/// </summary>
public class ObtenerKpisGlobalesQueryHandler(IMediator mediator)
    : IRequestHandler<ObtenerKpisGlobalesQuery, KpisGlobalesDto>
{
    public async Task<KpisGlobalesDto> Handle(ObtenerKpisGlobalesQuery request, CancellationToken cancellationToken)
    {
        var clientes = await mediator.Send(new ObtenerClientesAutorizadosQuery(), cancellationToken);

        var porCliente = new List<(ClienteAutorizadoDto Cliente, KpisDashboardDto Kpis)>();

        foreach (var cliente in clientes)
        {
            using (AmbitoTenantExplicito.Establecer(cliente.TenantId))
            {
                var kpis = await mediator.Send(new ObtenerKpisDashboardQuery(), cancellationToken);
                porCliente.Add((cliente, kpis));
            }
        }

        var vencidos = porCliente.Sum(p => p.Kpis.DocumentosVencidos);
        var urgentes = porCliente.Sum(p => p.Kpis.DocumentosUrgentes);
        var proximos = porCliente.Sum(p => p.Kpis.DocumentosProximos);
        var trabajadores = porCliente.Sum(p => p.Kpis.TrabajadoresActivos);
        var centros = porCliente.Sum(p => p.Kpis.Centros);
        var tasaPromedio = porCliente.Count == 0 ? 100 : (int)porCliente.Average(p => p.Kpis.TasaCumplimientoDocumental);

        var clientesConMasRiesgo = porCliente
            .Select(p => new ClienteRiesgoDto(
                p.Cliente.TenantId, p.Cliente.Nombre, p.Kpis.DocumentosVencidos, p.Kpis.DocumentosUrgentes, p.Kpis.TasaCumplimientoDocumental))
            .OrderByDescending(c => c.DocumentosVencidos)
            .ThenByDescending(c => c.DocumentosUrgentes)
            .ToList();

        return new KpisGlobalesDto(
            TotalClientes: porCliente.Count,
            DocumentosVencidos: vencidos,
            DocumentosUrgentes: urgentes,
            DocumentosProximos: proximos,
            TrabajadoresActivos: trabajadores,
            Centros: centros,
            TasaCumplimientoDocumentalPromedio: tasaPromedio,
            ClientesConMasRiesgo: clientesConMasRiesgo);
    }
}
