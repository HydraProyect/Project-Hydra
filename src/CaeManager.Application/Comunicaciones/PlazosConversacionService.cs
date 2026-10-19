using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using Microsoft.Extensions.Options;

namespace CaeManager.Application.Comunicaciones;

public record OperacionDeConversacion(Guid OperadorCaeTenantId, Guid AsignacionOperacionId);

public interface IContextoOperativoConversacionService
{
    Task<OperacionDeConversacion?> ResolverAsync(Guid propietarioTenantId, Guid? clienteEmpresarialId, Guid? empresaId,
        CancellationToken cancellationToken);
    Task AvisarCriticaAsync(OperacionDeConversacion operacion, Guid eventoId, CancellationToken cancellationToken);
}

/// <summary>Fechas de Madrid configurables por Operador CAE mediante configuración de despliegue.</summary>
public class CalendariosOperadorCaeOptions
{
    public const string Seccion = "CalendariosOperadorCae";
    public Dictionary<Guid, DateOnly[]> Festivos { get; set; } = [];
}

public class PlazosConversacionService(IContextoOperativoConversacionService contexto, IOptions<CalendariosOperadorCaeOptions> opciones)
{
    public async Task<CalendarioLaboralOperadorCae?> ObtenerCalendarioAsync(Conversacion conversacion, CancellationToken cancellationToken)
    {
        var operacion = await contexto.ResolverAsync(conversacion.TenantId, conversacion.ClienteId, conversacion.EmpresaId, cancellationToken);
        return operacion is null ? null : new CalendarioLaboralOperadorCae(opciones.Value.Festivos.GetValueOrDefault(operacion.OperadorCaeTenantId, []));
    }

    public static DateTimeOffset Vencimiento(PrioridadConversacion prioridad, DateTimeOffset primerEntrante,
        CalendarioLaboralOperadorCae calendario) => prioridad switch
        {
            PrioridadConversacion.Critica => calendario.SumarTiempoLaborable(primerEntrante, TimeSpan.FromHours(1)),
            PrioridadConversacion.Urgente => calendario.SumarTiempoLaborable(primerEntrante, TimeSpan.FromHours(4)),
            PrioridadConversacion.Normal => calendario.SumarDiasLaborables(primerEntrante, 1),
            _ => throw new ArgumentOutOfRangeException(nameof(prioridad))
        };

    public static DateTime? PrimerEntranteSinRespuesta(IEnumerable<Mensaje> mensajes)
    {
        var ultimoSaliente = mensajes.Where(m => m.Direccion == DireccionMensaje.Saliente)
            .Select(m => (DateTime?)m.FechaUtc).Max();
        return mensajes.Where(m => m.Direccion == DireccionMensaje.Entrante && !m.EsAutomatico
                                   && (ultimoSaliente is null || m.FechaUtc > ultimoSaliente))
            .Select(m => (DateTime?)m.FechaUtc).Min();
    }
}
