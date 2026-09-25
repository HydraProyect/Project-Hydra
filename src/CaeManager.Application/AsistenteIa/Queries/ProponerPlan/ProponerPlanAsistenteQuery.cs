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
/// Nivel 0 (DEC-33), el mismo control que <c>PreguntarAlAsistenteQuery</c>: antes
/// de que el texto salga hacia el proveedor, el Tenant de la pantalla y todos los
/// Tenants de la cartera tienen que tener instrucción de tratamiento con IA
/// vigente (<see cref="ComprobarInstruccionIaCarteraQuery"/>); si falta en alguno,
/// falla cerrado y dice cuál. Los candidatos solo salen de los Tenants que esa
/// comprobación dio por buenos: uno que aparezca después sin haberse comprobado
/// también falla cerrado.
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

        var cartera = await mediator.Send(new ComprobarInstruccionIaCarteraQuery(), cancellationToken);
        if (cartera.ErrorSiFalta() is { } sinInstruccion)
            return Result.Fallo<PlanPropuestoDto>(sinInstruccion);

        var clasificacion = await decisiones.ClasificarOrdenAsync(request.Texto, cancellationToken);
        if (clasificacion.EsFallido)
            return Result.Fallo<PlanPropuestoDto>(clasificacion.Error);

        if (clasificacion.Valor.OrdenId is not { } ordenId || CatalogoOrdenesAsistente.PorId(ordenId) is not { } orden)
            return new PlanPropuestoDto(SituacionPlan.NoEntendido, null, clasificacion.Valor.Confianza, [], null, false, null);

        var campos = orden.Campos.ToList();
        var seleccionables = campos
            .Where(c => c.Forma == FormaDeExtraccion.SeleccionDeCatalogo
                && ObtenerCandidatosAsistenteQueryHandler.CamposConCandidatos.Contains(c.Nombre))
            .ToList();
        // Lo que el Gestor CAE escribe manda: si nombra un Tenant, cuenta aunque
        // la orden del catálogo no declare el campo.
        if (seleccionables.All(c => c.Nombre != CampoTenantImplicito.Nombre))
            seleccionables.Add(CampoTenantImplicito);

        var candidatos = await mediator.Send(
            new ObtenerCandidatosAsistenteQuery(seleccionables.Select(c => c.Nombre).ToList()), cancellationToken);

        // La cartera leída ahora tiene que ser la que se comprobó: un Tenant que
        // haya entrado entre las dos lecturas no tiene la instrucción verificada,
        // y sus candidatos no viajan. El texto ya salió para clasificar la orden
        // (con la cartera comprobada), así que el mensaje no puede decir que no.
        var conInstruccion = cartera.ConInstruccion.Select(t => t.TenantId).ToHashSet();
        var sinComprobar = candidatos.Tenants.Where(t => !conInstruccion.Contains(t.TenantId)).ToList();
        if (sinComprobar.Count > 0)
            return Result.Fallo<PlanPropuestoDto>(Error.Crear(
                InstruccionIaCarteraDto.CodigoError,
                $"Tu cartera ha cambiado mientras se preparaba el plan: {string.Join(", ", sinComprobar.Select(t => t.Nombre))} no se ha comprobado. El plan no sigue; vuelve a escribir la orden."));

        if (request.TenantElegido is { } elegido && candidatos.Tenants.All(t => t.TenantId != elegido))
            return Result.Fallo<PlanPropuestoDto>(Error.Crear(
                "AsistenteIa.TenantFueraDeCartera",
                "El Tenant elegido no es de tu cartera."));

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
            SituacionPlan.Propuesto, orden.Id, clasificacion.Valor.Confianza, datos, destino,
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
