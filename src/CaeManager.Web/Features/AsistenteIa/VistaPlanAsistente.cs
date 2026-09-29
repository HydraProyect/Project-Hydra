using CaeManager.Application.AsistenteIa.Candidatos;
using CaeManager.Application.AsistenteIa.Queries.ProponerPlan;

namespace CaeManager.Web.Features.AsistenteIa;

public enum EstadoVistaPlan
{
    /// <summary>Esperando la confirmación de la persona (o bloqueado, si no es confirmable).</summary>
    Propuesto,

    /// <summary>La persona confirmó el plan (Enter). No implica ejecución: ver <see cref="AsistenteIa"/>.</summary>
    Confirmado,

    Descartado,
}

/// <summary>
/// Lo que el panel sabe de un plan propuesto: el plan del motor, la Tarea del
/// asistente donde está guardado y los Tenants entre los que se puede cambiar el
/// destino. El Tenant de la <see cref="Cartera"/> es una coordenada de contexto,
/// nunca una autorización: el motor vuelve a comprobar que sea de la cartera.
/// </summary>
public sealed class VistaPlanAsistente
{
    public required string TextoOrden { get; init; }

    public required PlanPropuestoDto Plan { get; set; }

    /// <summary>Tarea del asistente que guarda el plan; null si no se pudo guardar.</summary>
    public Guid? TareaId { get; set; }

    /// <summary>Versión de la Tarea que la persona tiene delante (concurrencia optimista al confirmar).</summary>
    public Guid? Version { get; set; }

    public EstadoVistaPlan Estado { get; set; } = EstadoVistaPlan.Propuesto;

    public IReadOnlyList<TenantDeCarteraDto> Cartera { get; set; } = [];

    /// <summary>Por qué el plan no está guardado (y, por tanto, no se puede confirmar), si es el caso.</summary>
    public string? ErrorGuardado { get; set; }

    public bool PuedeConfirmar =>
        Estado == EstadoVistaPlan.Propuesto && Plan.Confirmable && TareaId is not null && Version is not null;
}
