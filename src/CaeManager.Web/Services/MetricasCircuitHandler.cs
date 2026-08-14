using CaeManager.Application.Common;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace CaeManager.Web.Services;

/// <summary>
/// Alimenta el gauge de "circuitos activos" (<see cref="Observabilidad.CircuitosActivos"/>,
/// Horizonte 2.3) desde el único punto de extensión estándar del ciclo de
/// vida de un circuito de Blazor Server. No repite las CircuitOptions
/// explícitas de Horizonte 2.1 (Program.cs): aquellas acotan cuánta memoria
/// se retiene por circuito desconectado/persistido; esto mide cuántos
/// circuitos hay vivos ahora mismo, la señal que decide cuándo ese ajuste
/// deja de bastar (ADR-008-capacidad-blazor-server.md § "Umbral concreto
/// para la multi-réplica").
///
/// Registrado como singleton en Program.cs, no scoped: no guarda estado por
/// circuito, solo incrementa/decrementa un contador compartido — una
/// instancia por circuito sería trabajo de asignación sin ningún beneficio
/// (mismo patrón que recomienda la documentación de Blazor para un
/// CircuitHandler que solo cuenta).
/// </summary>
public class MetricasCircuitHandler : CircuitHandler
{
    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Observabilidad.CircuitoAbierto();
        return Task.CompletedTask;
    }

    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        Observabilidad.CircuitoCerrado();
        return Task.CompletedTask;
    }
}
