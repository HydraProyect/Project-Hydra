using CaeManager.Application.AsistenteIa.Candidatos;
using CaeManager.Application.AsistenteIa.Preparacion;
using CaeManager.Application.Common;
using CaeManager.Application.Cumplimiento;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.Extensions.Options;

namespace CaeManager.Application.AsistenteIa.Queries.PreguntarAlAsistente;

/// <summary>
/// Recibe el historial visible del chat. Al proveedor solo se envía la ventana
/// reciente acotada por turnos y caracteres en <see cref="AsistenteConsultaOptions"/>.
///
/// Único de los cinco tratamientos de <c>Project-Hydra-Negocio/tecnico/docs/POLITICA-TECNICA-IA.md</c>
/// § 1 que no tenía, hasta este incremento, ningún control de cumplimiento:
/// el Nivel 0 (DEC-33, REC-035) es el primer gate que le aplica — lo que el
/// usuario escribe en el chat puede incluir datos personales, y viaja al
/// mismo proveedor externo que los demás tratamientos.
///
/// El Nivel 0 se exige a toda la cartera del Gestor CAE, no solo al Tenant de la
/// pantalla (<see cref="ComprobarInstruccionIaCarteraQuery"/>): el texto puede
/// nombrar a personas de cualquier Tenant de la cartera, así que falla cerrado si
/// alguno no tiene instrucción de tratamiento con IA vigente.
/// </summary>
public record PreguntarAlAsistenteQuery(IReadOnlyList<MensajeChatDto> Historial) : IRequest<Result<string>>;

public class PreguntarAlAsistenteQueryHandler(
    IAsistenteIaService asistenteIa,
    IInstruccionTratamientoIaService instruccionTratamientoIa,
    ITenantActual tenantActual,
    IMediator mediator,
    IActorAuditoria actorAuditoria,
    LimitadorConsultaIa limitador,
    IOptions<AsistenteConsultaOptions> opciones)
    : IRequestHandler<PreguntarAlAsistenteQuery, Result<string>>
{
    public async Task<Result<string>> Handle(PreguntarAlAsistenteQuery request, CancellationToken cancellationToken)
    {
        var actor = await actorAuditoria.ObtenerAsync();
        if (actor.ActorRealUsuarioId is not { } usuarioId || usuarioId == Guid.Empty
            || tenantActual.TenantId is not { } tenantId || tenantId == Guid.Empty)
            return Result.Fallo<string>(Error.Crear("AsistenteIa.SinIdentidad", TextosConsulta.Obtener("SinIdentidad")));

        // La validación de la pregunta (PreguntarAlAsistenteQueryValidator) la ejecuta ValidationBehavior
        // antes de llegar aquí; este handler solo se invoca a través del pipeline de MediatR.

        if (!await instruccionTratamientoIa.EstaHabilitadaAsync(tenantId, cancellationToken))
            return Result.Fallo<string>(Error.Crear(
                "AsistenteIa.SinInstruccion",
                "Esta organización todavía no tiene una instrucción de tratamiento con IA vigente — el asistente no puede procesar tu mensaje."));

        var cartera = await mediator.Send(new ComprobarInstruccionIaCarteraQuery(), cancellationToken);
        if (cartera.ErrorSiFalta() is { } sinInstruccion)
            return Result.Fallo<string>(sinInstruccion);

        if (limitador.IntentarReservar(usuarioId, tenantId) is { } errorCuota)
            return Result.Fallo<string>(errorCuota);

        var (enmascarado, tabla) = Enmascarar(request.Historial.TakeLast(opciones.Value.MaxTurnosHistorial).ToArray());
        var respuesta = await asistenteIa.PreguntarAsync(Recortar(enmascarado, opciones.Value), cancellationToken);

        // Como en el modo Operativo, lo que vuelve se restaura con la misma tabla: la persona ve
        // sus identificadores, y el turno del asistente se guarda en claro, así que al
        // enmascararlo otra vez en la pregunta siguiente lleva los mismos marcadores.
        return respuesta.EsExitoso ? Result.Exito(tabla.Restaurar(respuesta.Valor)) : respuesta;
    }

    /// <summary>
    /// Ventana que viaja al proveedor: turnos enteros, de los más recientes a los más antiguos, hasta
    /// <see cref="AsistenteConsultaOptions.MaxTurnosHistorial"/> turnos y
    /// <see cref="AsistenteConsultaOptions.MaxCaracteresHistorial"/> caracteres. Un turno antiguo se
    /// descarta entero, nunca se parte; solo la pregunta actual (el último mensaje) se conserva siempre.
    /// La API de Anthropic exige que el primer mensaje sea del usuario, así que se descartan los
    /// turnos del asistente con los que pudiera empezar la ventana.
    /// </summary>
    private static IReadOnlyList<MensajeChatDto> Recortar(IReadOnlyList<MensajeChatDto> historial, AsistenteConsultaOptions config)
    {
        if (historial.Count == 0)
            return historial;

        var ventana = new List<MensajeChatDto> { historial[^1] };
        var restantes = config.MaxCaracteresHistorial - historial[^1].Texto.Length;
        for (var i = historial.Count - 2; i >= 0 && ventana.Count < config.MaxTurnosHistorial; i--)
        {
            if (historial[i].Texto.Length > restantes)
                break;
            ventana.Add(historial[i]);
            restantes -= historial[i].Texto.Length;
        }

        ventana.Reverse();
        var primero = ventana.FindIndex(m => m.Rol != RolMensajeChat.Asistente);
        return primero > 0 ? ventana.GetRange(primero, ventana.Count - primero) : ventana;
    }

    /// <summary>
    /// Separador de turnos para enmascarar la ventana reciente de una vez: ningún patrón de
    /// <see cref="EnmascaradorIdentificadores"/> lo atraviesa, y nadie lo teclea.
    /// </summary>
    private const char SeparadorDeTurnos = '\u001E';

    /// <summary>
    /// Los DNI, NIE, pasaportes, CIF y correos no salen hacia el proveedor, igual que en la
    /// orden del modo Operativo (<c>CrearTareaAsistenteCommand</c> recibe el texto ya
    /// enmascarado). Se enmascara la ventana reciente antes de recortar caracteres, y no turno a turno, para
    /// que el mismo documento lleve el mismo marcador en todos los turnos y dos documentos
    /// distintos nunca compartan <c>[DOC_1]</c>. La persona sigue viendo su texto en claro: lo
    /// enmascarado es solo lo que viaja.
    /// </summary>
    private static (IReadOnlyList<MensajeChatDto> Historial, TextoEnmascarado Tabla) Enmascarar(IReadOnlyList<MensajeChatDto> historial)
    {
        if (historial.Count == 0)
            return (historial, EnmascaradorIdentificadores.Enmascarar(null));

        var unidos = string.Join(SeparadorDeTurnos, historial.Select(m => m.Texto.Replace(SeparadorDeTurnos, ' ')));
        var tabla = EnmascaradorIdentificadores.Enmascarar(unidos);
        var turnos = tabla.Texto.Split(SeparadorDeTurnos);
        return (historial.Select((m, i) => m with { Texto = turnos[i] }).ToList(), tabla);
    }
}
