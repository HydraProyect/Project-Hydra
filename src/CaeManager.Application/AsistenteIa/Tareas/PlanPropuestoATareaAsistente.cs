using System.Text.Json;
using CaeManager.Application.AsistenteIa.Candidatos;
using CaeManager.Application.AsistenteIa.Edicion;
using CaeManager.Application.AsistenteIa.Ordenes;
using CaeManager.Application.AsistenteIa.Queries.ProponerPlan;
using CaeManager.Application.AsistenteIa.Tareas.Commands;
using CaeManager.Domain.AsistenteIa;

namespace CaeManager.Application.AsistenteIa.Tareas;

/// <summary>
/// Traduce el plan que propone <see cref="ProponerPlanAsistenteQuery"/> a los pasos que
/// guarda la Tarea del asistente, de modo que <b>«el plan se puede confirmar»
/// signifique lo mismo en los dos lados</b>: <see cref="PlanPropuestoDto.Confirmable"/>
/// y <see cref="EstadoTareaAsistente.PlanListo"/>.
/// <para>
/// El dominio de la Tarea solo sabe si a un paso le falta un dato o si tiene un
/// aviso bloqueante; no conoce el Tenant destino ni si la orden es ejecutable. Por
/// eso ambas cosas se traducen aquí en un aviso bloqueante: sin él, un plan que
/// mezcla dos Tenants, o de una orden que el asistente todavía no ejecuta, con todos
/// sus datos, quedaría <c>PlanListo</c> y el Enter lo confirmaría.
/// </para>
/// <para>
/// Los datos se guardan como objeto JSON con el sello de Tenant de cada valor
/// elegido, los valores completos en <see cref="ClaveValores"/> y el Tenant destino en
/// <see cref="ClaveDestino"/>: es lo que hace falta para volver a comprobarlo todo al
/// ejecutar. La coordenada de Tenant no es autoridad: quien ejecuta
/// (<see cref="EjecutarPlanTareaAsistenteCommand"/>) vuelve a autorizar por los Commands.
/// </para>
/// </summary>
public static class PlanPropuestoATareaAsistente
{
    /// <summary>Clave del objeto de datos donde va el Tenant destino. Empieza por «_» para no chocar con ningún campo de orden.</summary>
    public const string ClaveDestino = "_destino";

    /// <summary>Clave con la lista de <see cref="ValorDatoPlanDto"/> completos del plan.</summary>
    public const string ClaveValores = "_valores";

    /// <summary>Clave con la operación del catálogo (nombre del Command) que ejecuta este paso.</summary>
    public const string ClaveOperacion = "_operacion";

    public const string TextoOrdenNoEjecutable = "Esta orden todavía no se puede ejecutar desde el asistente.";
    public const string TextoSinDestino = "No hay un Tenant destino único: elige en cuál se ejecuta.";
    public const string TextoDestinoNoEsLaPantalla =
        "Vega solo ejecuta en la Empresa gestionada que tienes abierta: cámbiala arriba, en «Trabajando en».";

    private static readonly JsonSerializerOptions OpcionesJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>
    /// Un solo paso con el plan entero (sin operación). Se conserva para el primer
    /// guardado de una propuesta; <see cref="APasos"/> es lo que se ejecuta.
    /// </summary>
    public static PasoPlanTareaAsistenteDto APaso(PlanPropuestoDto plan) => Construir(plan, operacion: null);

    /// <summary>
    /// Un paso por operación del catálogo cuando la orden es una secuencia (el alta de
    /// Trabajador y su Asignación son dos pasos); un solo paso en otro caso. Cada paso
    /// lleva los mismos datos y nombra su operación en <see cref="ClaveOperacion"/>.
    /// </summary>
    public static IReadOnlyList<PasoPlanTareaAsistenteDto> APasos(PlanPropuestoDto plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.OrdenId is not null
            && CatalogoOrdenesAsistente.PorId(plan.OrdenId) is { Modo: ModoDeEjecucion.Secuencia, Ejecucion.Count: > 0 } orden)
            return orden.Ejecucion.Select(p => Construir(plan, p.Operacion.Name)).ToList();

        return [Construir(plan, operacion: null)];
    }

    /// <summary>Los valores completos guardados en un paso; vacío si el paso no los tiene.</summary>
    public static IReadOnlyList<ValorDatoPlanDto> LeerValores(string datosJson)
    {
        using var doc = JsonDocument.Parse(datosJson);
        return doc.RootElement.TryGetProperty(ClaveValores, out var valores)
            ? valores.Deserialize<List<ValorDatoPlanDto>>(OpcionesJson) ?? []
            : [];
    }

    public static string? LeerOperacion(string datosJson)
    {
        using var doc = JsonDocument.Parse(datosJson);
        return doc.RootElement.TryGetProperty(ClaveOperacion, out var operacion) && operacion.ValueKind == JsonValueKind.String
            ? operacion.GetString()
            : null;
    }

    public static Guid? LeerTenantDestino(string datosJson)
    {
        using var doc = JsonDocument.Parse(datosJson);
        return doc.RootElement.TryGetProperty(ClaveDestino, out var destino)
            && destino.TryGetProperty("tenantId", out var tenant) && tenant.ValueKind == JsonValueKind.String
            && Guid.TryParse(tenant.GetString(), out var id)
                ? id
                : null;
    }

    /// <summary>Los valores del plan: lo completado a mano y, si no, el valor elegido por el asistente.</summary>
    public static IReadOnlyList<ValorDatoPlanDto> Valores(PlanPropuestoDto plan) =>
        plan.Datos
            .Select(d => d.Valor is { Completo: true } valor ? valor
                : d.CandidatoId is { } id ? new ValorDatoPlanDto(d.Campo, Id: id)
                : null)
            .Where(v => v is not null)
            .Select(v => v!)
            .ToList();

    private static PasoPlanTareaAsistenteDto Construir(PlanPropuestoDto plan, string? operacion)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Situacion != SituacionPlan.Propuesto || string.IsNullOrWhiteSpace(plan.OrdenId))
            throw new ArgumentException("Solo un plan propuesto con una orden del catálogo se guarda como paso.", nameof(plan));

        var datos = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var dato in plan.Datos.Where(d => d.CandidatoId is not null))
            datos[dato.Campo] = new { id = dato.CandidatoId, nombre = dato.Nombre, tenantId = dato.TenantId, confianza = dato.Confianza };

        datos[ClaveDestino] = new
        {
            situacion = plan.Destino?.Situacion.ToString(),
            tenantId = plan.Destino?.Tenant?.TenantId,
            nombre = plan.Destino?.Tenant?.Nombre,
        };
        datos[ClaveValores] = Valores(plan);
        if (operacion is not null)
            datos[ClaveOperacion] = operacion;

        var pendientes = plan.Datos
            .Where(d => d.Obligatorio && !d.Completo)
            .Select(d => d.Campo)
            .ToList();

        var avisos = new List<AvisoPasoTareaAsistente>(plan.Avisos ?? []);
        if (!plan.Ejecutable)
            avisos.Add(new(GravedadAvisoPasoTareaAsistente.Bloqueante, TextoOrdenNoEjecutable));
        if (plan.Destino is not { Situacion: SituacionTenantDestino.Unico })
        {
            var motivo = string.IsNullOrWhiteSpace(plan.Destino?.Motivo) ? TextoSinDestino : plan.Destino.Motivo;
            if (motivo.Length > AvisoPasoTareaAsistente.LongitudMaximaTexto)
                motivo = motivo[..AvisoPasoTareaAsistente.LongitudMaximaTexto];
            avisos.Add(new(GravedadAvisoPasoTareaAsistente.Bloqueante, motivo, ObtenerCandidatosAsistenteQueryHandler.CampoTenant));
        }
        else if (!plan.DestinoEsLaPantalla)
        {
            avisos.Add(new(GravedadAvisoPasoTareaAsistente.Bloqueante, TextoDestinoNoEsLaPantalla,
                ObtenerCandidatosAsistenteQueryHandler.CampoTenant));
        }

        return new PasoPlanTareaAsistenteDto(
            plan.OrdenId!, JsonSerializer.Serialize(datos, OpcionesJson), Resumen: null, pendientes, avisos);
    }
}
