using System.Text.Json;
using CaeManager.Application.AsistenteIa.Candidatos;
using CaeManager.Application.AsistenteIa.Queries.ProponerPlan;
using CaeManager.Application.AsistenteIa.Tareas.Commands;
using CaeManager.Domain.AsistenteIa;

namespace CaeManager.Application.AsistenteIa.Tareas;

/// <summary>
/// Traduce el plan que propone <see cref="ProponerPlanAsistenteQuery"/> al paso que
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
/// elegido y el Tenant destino en <see cref="ClaveDestino"/>: es lo que hará falta
/// para volver a comprobarlo todo al retomar un borrador. La coordenada de Tenant
/// no es autoridad: quien ejecute vuelve a autorizar por los Commands.
/// </para>
/// </summary>
public static class PlanPropuestoATareaAsistente
{
    /// <summary>Clave del objeto de datos donde va el Tenant destino. Empieza por «_» para no chocar con ningún campo de orden.</summary>
    public const string ClaveDestino = "_destino";

    public const string TextoOrdenNoEjecutable = "Esta orden todavía no se puede ejecutar desde el asistente.";
    public const string TextoSinDestino = "No hay un Tenant destino único: elige en cuál se ejecuta.";

    public static PasoPlanTareaAsistenteDto APaso(PlanPropuestoDto plan)
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

        var pendientes = plan.Datos
            .Where(d => d.Obligatorio && d.CandidatoId is null)
            .Select(d => d.Campo)
            .ToList();

        var avisos = new List<AvisoPasoTareaAsistente>();
        if (!plan.Ejecutable)
            avisos.Add(new(GravedadAvisoPasoTareaAsistente.Bloqueante, TextoOrdenNoEjecutable));
        if (plan.Destino is not { Situacion: SituacionTenantDestino.Unico })
        {
            var motivo = string.IsNullOrWhiteSpace(plan.Destino?.Motivo) ? TextoSinDestino : plan.Destino.Motivo;
            if (motivo.Length > AvisoPasoTareaAsistente.LongitudMaximaTexto)
                motivo = motivo[..AvisoPasoTareaAsistente.LongitudMaximaTexto];
            avisos.Add(new(GravedadAvisoPasoTareaAsistente.Bloqueante, motivo, ObtenerCandidatosAsistenteQueryHandler.CampoTenant));
        }

        return new PasoPlanTareaAsistenteDto(
            plan.OrdenId!, JsonSerializer.Serialize(datos), Resumen: null, pendientes, avisos);
    }
}
