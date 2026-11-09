using CaeManager.Application.AsistenteIa.Candidatos;
using CaeManager.Application.AsistenteIa.Edicion;
using CaeManager.Application.AsistenteIa.Queries.ProponerPlan;

namespace CaeManager.Web.Features.AsistenteIa;

public enum EstadoVistaPlan
{
    /// <summary>Esperando la confirmación de la persona (o bloqueado, si no es confirmable).</summary>
    Propuesto,

    /// <summary>La persona confirmó el plan (Enter); Vega lo ejecuta a continuación (<see cref="VistaPlanAsistente.Ejecucion"/>).</summary>
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

    /// <summary>
    /// Confirmar ya recibió el foco una vez. Vive en la vista (en el hilo) y no en el componente:
    /// al volver a montar el panel, un plan viejo no roba el foco a la pantalla.
    /// </summary>
    public bool FocoDado { get; set; }

    public bool PuedeConfirmar =>
        Estado == EstadoVistaPlan.Propuesto && Plan.Confirmable && TareaId is not null && Version is not null;

    /// <summary>El Tenant efectivo de la pantalla cuando se pintó el plan: el único donde Vega ejecuta.</summary>
    public Guid? TenantPantalla { get; set; }

    /// <summary>Las opciones de la cartera para completar el plan a mano; null si no se pueden ofrecer aquí.</summary>
    public OpcionesPlanAsistenteDto? Opciones { get; set; }

    /// <summary>Los valores del plan tal como están ahora (lo que eligió Vega y lo que completó la persona).</summary>
    public Dictionary<string, ValorDatoPlanDto> Valores { get; } = new(StringComparer.Ordinal);

    /// <summary>Por qué el plan no se puede completar aquí, si es el caso (p. ej. sin Empresa gestionada abierta).</summary>
    public string? AvisoEdicion { get; set; }

    /// <summary>El resultado de ejecutar el plan confirmado; null mientras no se haya ejecutado.</summary>
    public EjecucionPlanAsistenteDto? Ejecucion { get; set; }

    /// <summary>
    /// Los datos se completan a mano: la orden la ejecuta Vega, el destino es el Tenant de la
    /// pantalla y hay opciones de la cartera en él. Un destino por elegir, mezclado o
    /// distinto de la pantalla se resuelve antes con el selector de Tenant.
    /// </summary>
    public bool Editable =>
        Estado == EstadoVistaPlan.Propuesto && TareaId is not null && Version is not null
        && Opciones?.Tenant is not null && Plan.Ejecutable
        && Plan.Destino is { Situacion: SituacionTenantDestino.Unico } && Plan.DestinoEsLaPantalla;
}
