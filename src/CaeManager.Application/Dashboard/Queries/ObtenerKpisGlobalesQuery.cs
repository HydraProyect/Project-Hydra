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
    int TasaCumplimientoDocumental,
    /// <summary>
    /// Igual que <see cref="KpisDashboardDto.SinCarteraAsignada"/> para este
    /// Cliente Delegante concreto — un Gestor CAE sin ninguna Asignación de
    /// Cartera ahí. La UI no debe pintar <see cref="TasaCumplimientoDocumental"/>
    /// como si fuera un SLA al día: 100% aquí es "no hay nada que evaluar",
    /// no "está al día" (ver <see cref="ObtenerKpisGlobalesQueryHandler.Fusionar"/>).
    /// </summary>
    bool SinCarteraAsignada = false);

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

        return Fusionar(porCliente);
    }

    /// <summary>
    /// Fórmulas de merge, pura y sin dependencias de infraestructura — mismo
    /// motivo y mismo patrón que <c>ObtenerDashboardEjecutivoQueryHandler.Fusionar</c>.
    ///
    /// <para>
    /// La media de SLA documental pondera cada Cliente Delegante por su
    /// volumen de documentos con vigencia (<c>DocumentosVigentes + Próximos +
    /// Urgentes + Vencidos</c>) y excluye a los que no tienen ninguna cartera
    /// asignada: su 100% es "no hay nada que evaluar para este usuario", no
    /// "está al día", y promediarlo igual que un Cliente con cartera completa
    /// inflaba el SLA agregado con alcance que en realidad no existe (defecto
    /// de fuga de alcance, hallazgo Codex 2026-09-11). Un Cliente CON cartera
    /// pero sin ningún documento con vigencia (volumen cero) queda excluido
    /// por el mismo mecanismo de ponderación, sin necesitar una regla aparte:
    /// pesa cero en la suma igual que si no estuviera.
    /// </para>
    /// </summary>
    public static KpisGlobalesDto Fusionar(IReadOnlyList<(ClienteAutorizadoDto Cliente, KpisDashboardDto Kpis)> porCliente)
    {
        var vencidos = porCliente.Sum(p => p.Kpis.DocumentosVencidos);
        var urgentes = porCliente.Sum(p => p.Kpis.DocumentosUrgentes);
        var proximos = porCliente.Sum(p => p.Kpis.DocumentosProximos);
        var trabajadores = porCliente.Sum(p => p.Kpis.TrabajadoresActivos);
        var centros = porCliente.Sum(p => p.Kpis.Centros);

        var conPeso = porCliente
            .Where(p => !p.Kpis.SinCarteraAsignada)
            .Select(p => (Tasa: p.Kpis.TasaCumplimientoDocumental, Peso: (double)TotalConVigencia(p.Kpis)))
            .Where(p => p.Peso > 0)
            .ToList();
        var tasaPromedio = conPeso.Count == 0 ? 100 : (int)(conPeso.Sum(p => p.Tasa * p.Peso) / conPeso.Sum(p => p.Peso));

        var clientesConMasRiesgo = porCliente
            .Select(p => new ClienteRiesgoDto(
                p.Cliente.TenantId, p.Cliente.Nombre, p.Kpis.DocumentosVencidos, p.Kpis.DocumentosUrgentes,
                p.Kpis.TasaCumplimientoDocumental, p.Kpis.SinCarteraAsignada))
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

    private static int TotalConVigencia(KpisDashboardDto kpis) =>
        kpis.DocumentosVigentes + kpis.DocumentosProximos + kpis.DocumentosUrgentes + kpis.DocumentosVencidos;
}
