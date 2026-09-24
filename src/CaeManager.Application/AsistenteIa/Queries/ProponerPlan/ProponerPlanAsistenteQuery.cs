using CaeManager.Application.AsistenteIa.Candidatos;
using CaeManager.Application.AsistenteIa.Decisiones;
using CaeManager.Application.AsistenteIa.Ordenes;
using CaeManager.Application.Common;
using CaeManager.Application.Cumplimiento;
using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.AsistenteIa.Queries.ProponerPlan;

/// <summary>
/// Convierte una orden escrita por un Gestor CAE en un <b>plan propuesto</b>: qué
/// orden del catálogo pide, qué valor de la cartera nombra para cada dato y en qué
/// Tenant beneficiario se ejecutaría. No ejecuta nada: el plan se confirma con un
/// Enter (<see cref="OrdenAsistida.RequiereConfirmacion"/>) y lo ejecutan después
/// los Commands de <see cref="OrdenAsistida.Ejecucion"/>.
/// <para>
/// El Tenant de la pantalla es el <see cref="ITenantActual"/>: es el de serie,
/// pero mandan los datos de la orden (<see cref="ResolucionTenantDestino"/>).
/// <paramref name="TenantElegido"/> es el que el Gestor CAE haya fijado a mano en
/// el chip del chat; tiene que ser de su cartera.
/// </para>
/// <para>
/// Nivel 0 (DEC-33), el mismo control que <c>PreguntarAlAsistenteQuery</c>, pero
/// aplicado a cada Tenant cuyos datos viajan al proveedor: el texto exige la
/// instrucción de tratamiento con IA del Tenant de la pantalla, y los candidatos
/// solo salen de Tenants de la cartera que la tengan vigente. Los que no, se
/// devuelven en <see cref="PlanPropuestoDto.TenantsSinInstruccion"/> para que el
/// plan lo diga en vez de dar por hecho que la orden no los nombra.
/// </para>
/// </summary>
public record ProponerPlanAsistenteQuery(string Texto, Guid? TenantElegido = null) : IRequest<Result<PlanPropuestoDto>>;

public enum SituacionPlan
{
    /// <summary>La orden es del catálogo: hay plan que revisar y confirmar.</summary>
    Propuesto,

    /// <summary>El modelo se abstuvo: el texto no pide ninguna orden del catálogo.</summary>
    NoEntendido,
}

/// <summary>
/// Un dato de la orden. Con <paramref name="CandidatoId"/> el asistente ha elegido
/// un valor de la cartera; sin él, o el modelo se abstuvo o el dato todavía no se
/// resuelve aquí (<paramref name="Pendiente"/> dice por qué).
/// </summary>
public record DatoPropuestoDto(
    string Campo,
    string Descripcion,
    bool Obligatorio,
    Guid? CandidatoId,
    string? Nombre,
    Guid? TenantId,
    int Confianza,
    string? Pendiente);

public record PlanPropuestoDto(
    SituacionPlan Situacion,
    string? OrdenId,
    int ConfianzaOrden,
    IReadOnlyList<DatoPropuestoDto> Datos,
    TenantDestinoDto? Destino,
    IReadOnlyList<TenantDeCarteraDto> TenantsSinInstruccion,
    bool Ejecutable,
    string? Limitacion)
{
    /// <summary>
    /// Se puede confirmar con Enter: la orden es ejecutable, el Tenant no bloquea
    /// ni está por elegir y no falta ningún dato obligatorio.
    /// </summary>
    public bool Confirmable =>
        Situacion == SituacionPlan.Propuesto
        && Ejecutable
        && Destino is { Situacion: SituacionTenantDestino.Unico }
        && Datos.Where(d => d.Obligatorio).All(d => d.CandidatoId is not null);
}

public class ProponerPlanAsistenteQueryHandler(
    IMediator mediator,
    IDecisionesCerradasAsistenteService decisiones,
    IInstruccionTratamientoIaService instruccionTratamientoIa,
    ITenantActual tenantActual)
    : IRequestHandler<ProponerPlanAsistenteQuery, Result<PlanPropuestoDto>>
{
    private const string PendienteSinConstructor = "Este dato todavía no lo resuelve el asistente: complétalo en el plan.";
    private const string PendienteNoEsSeleccion = "Este dato se completa en el plan.";

    private static readonly CampoDeOrden CampoTenantImplicito = new(
        ObtenerCandidatosAsistenteQueryHandler.CampoTenant, FormaDeExtraccion.SeleccionDeCatalogo, Obligatorio: false,
        "Tenant en el que se trabaja, solo si la orden lo nombra.");

    public async Task<Result<PlanPropuestoDto>> Handle(ProponerPlanAsistenteQuery request, CancellationToken cancellationToken)
    {
        if (tenantActual.TenantId is not { } tenantPantalla
            || !await instruccionTratamientoIa.EstaHabilitadaAsync(tenantPantalla, cancellationToken))
            return Result.Fallo<PlanPropuestoDto>(Error.Crear(
                "AsistenteIa.SinInstruccion",
                "Este tenant todavía no tiene una instrucción de tratamiento con IA vigente — el asistente no puede procesar tu mensaje."));

        var clasificacion = await decisiones.ClasificarOrdenAsync(request.Texto, cancellationToken);
        if (clasificacion.EsFallido)
            return Result.Fallo<PlanPropuestoDto>(clasificacion.Error);

        if (clasificacion.Valor.OrdenId is not { } ordenId || CatalogoOrdenesAsistente.PorId(ordenId) is not { } orden)
            return new PlanPropuestoDto(SituacionPlan.NoEntendido, null, clasificacion.Valor.Confianza, [], null, [], false, null);

        var campos = orden.Campos.ToList();
        var seleccionables = campos
            .Where(c => c.Forma == FormaDeExtraccion.SeleccionDeCatalogo
                && ObtenerCandidatosAsistenteQueryHandler.CamposConCandidatos.Contains(c.Nombre))
            .ToList();
        // Lo que el Gestor CAE escribe manda: si nombra un Tenant, cuenta aunque
        // la orden del catálogo no declare el campo.
        if (seleccionables.All(c => c.Nombre != CampoTenantImplicito.Nombre))
            seleccionables.Add(CampoTenantImplicito);

        var leidos = await mediator.Send(
            new ObtenerCandidatosAsistenteQuery(seleccionables.Select(c => c.Nombre).ToList()), cancellationToken);

        var conInstruccion = new HashSet<Guid>();
        var sinInstruccion = new List<TenantDeCarteraDto>();
        foreach (var tenant in leidos.Tenants)
        {
            if (await instruccionTratamientoIa.EstaHabilitadaAsync(tenant.TenantId, cancellationToken))
                conInstruccion.Add(tenant.TenantId);
            else
                sinInstruccion.Add(tenant);
        }
        var candidatos = leidos.SoloDeLosTenants(conInstruccion);

        if (request.TenantElegido is { } elegido && candidatos.Tenants.All(t => t.TenantId != elegido))
            return Result.Fallo<PlanPropuestoDto>(Error.Crear(
                "AsistenteIa.TenantFueraDeCartera",
                "El Tenant elegido no es de tu cartera o no tiene instrucción de tratamiento con IA vigente."));

        IReadOnlyList<SeleccionCandidatoDto> elegidos = [];
        if (candidatos.Tenants.Count > 0)
        {
            var solicitadas = seleccionables
                .Select(c => new SeleccionSolicitadaDto(c, candidatos.PorCampo[c.Nombre].Select(x => x.ParaDecision()).ToList()))
                .Where(s => s.Candidatos.Count > 0)
                .ToList();
            if (solicitadas.Count > 0)
            {
                var seleccion = await decisiones.SeleccionarCandidatosAsync(request.Texto, solicitadas, cancellationToken);
                if (seleccion.EsFallido)
                    return Result.Fallo<PlanPropuestoDto>(seleccion.Error);
                // Un Id que no estaba entre los candidatos enviados no es de la
                // cartera: incumple el contrato del servicio y se trata como
                // abstención, nunca como dato del plan ni como pista del Tenant.
                elegidos = seleccion.Valor
                    .Select(s => s.CandidatoId is { } id
                        && (!candidatos.PorCampo.TryGetValue(s.Campo, out var enviados) || enviados.All(c => c.Id != id))
                            ? s with { CandidatoId = null, Confianza = 0 }
                            : s)
                    .ToList();
            }
        }

        var destino = ResolucionTenantDestino.Resolver(candidatos, elegidos, request.TenantElegido, tenantPantalla);

        var porCampo = elegidos.ToDictionary(s => s.Campo, StringComparer.Ordinal);
        var datos = campos.Select(c => Dato(c, porCampo, candidatos)).ToList();
        return new PlanPropuestoDto(
            SituacionPlan.Propuesto, orden.Id, clasificacion.Valor.Confianza, datos, destino, sinInstruccion,
            orden.Ejecutable, string.IsNullOrWhiteSpace(orden.Limitacion) ? null : orden.Limitacion);
    }

    private static DatoPropuestoDto Dato(
        CampoDeOrden campo, IReadOnlyDictionary<string, SeleccionCandidatoDto> porCampo, CandidatosAsistenteDto candidatos)
    {
        if (campo.Forma != FormaDeExtraccion.SeleccionDeCatalogo)
            return new(campo.Nombre, campo.Descripcion, campo.Obligatorio, null, null, null, 0, PendienteNoEsSeleccion);
        if (!ObtenerCandidatosAsistenteQueryHandler.CamposConCandidatos.Contains(campo.Nombre))
            return new(campo.Nombre, campo.Descripcion, campo.Obligatorio, null, null, null, 0, PendienteSinConstructor);
        if (!porCampo.TryGetValue(campo.Nombre, out var eleccion) || eleccion.CandidatoId is not { } id)
            return new(campo.Nombre, campo.Descripcion, campo.Obligatorio, null, null, null, eleccion?.Confianza ?? 0, null);

        var candidato = candidatos.PorCampo[campo.Nombre].First(c => c.Id == id);
        return new(campo.Nombre, campo.Descripcion, campo.Obligatorio, id, candidato.Nombre, candidato.TenantId, eleccion.Confianza, null);
    }
}
