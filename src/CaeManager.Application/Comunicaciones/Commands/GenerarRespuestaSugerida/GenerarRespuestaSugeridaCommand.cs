using System.Data.Common;
using CaeManager.Application.Common;
using CaeManager.Application.Cumplimiento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.Commands.GenerarRespuestaSugerida;

/// <summary>Con <paramref name="MacroId"/>, la macro rellenada; sin él, redacta Vega. Devuelve el Id de la sugerencia.</summary>
public record GenerarRespuestaSugeridaCommand(Guid ConversacionId, Guid? MacroId = null) : ICommand<Guid>;

public class GenerarRespuestaSugeridaCommandValidator : AbstractValidator<GenerarRespuestaSugeridaCommand>
{
    public GenerarRespuestaSugeridaCommandValidator()
    {
        RuleFor(c => c.ConversacionId).NotEmpty().WithMessage("La respuesta sugerida debe pertenecer a una conversación.");
    }
}

/// <summary>
/// Genera bajo demanda la respuesta sugerida de una conversación, nunca sola ni en la ingesta. <b>No envía nada</b>: deja
/// una <see cref="RespuestaSugerida"/> en Propuesta y una persona decide. Solo si la conversación sigue viva y su último
/// mensaje de una persona es entrante: los automáticos no cuentan en ninguna dirección.
///
/// <para>
/// Una sugerencia SinRevisar está en el editor de alguien y no se toca: hay que confirmarla o descartarla antes. Una Propuesta
/// se sustituye, y queda Descartada en la misma transacción. Si ya hay Propuesta para ese mismo mensaje se devuelve esa y
/// Vega no redacta otra; solo otra macro distinta la sustituye. Sustituir una Propuesta que no es la que se vio al empezar
/// sería pisar a otra persona: es un conflicto.
/// </para>
///
/// <para>
/// Quién puede pedirla lo decide <see cref="AutorizacionEscrituraBehavior{TRequest,TResponse}"/>, que deja fuera a Cliente y
/// Consulta; este handler añade el alcance de la conversación. Vega solo redacta con Instrucción de Tratamiento vigente y
/// sobre un hilo con ancla, igual que la detección de intención; si no puede, no se guarda nada.
/// </para>
/// </summary>
public class GenerarRespuestaSugeridaCommandHandler(
    IComunicacionesQueryContext comunicaciones,
    IRespuestaSugeridaRepository respuestas,
    RellenoMacroRespuesta relleno,
    IRedaccionRespuestaIa redaccion,
    IInstruccionTratamientoIaService instruccionTratamientoIa,
    ITenantActual tenantActual,
    IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUserService,
    ITransaccionDeComando transaccion,
    IUnitOfWork unitOfWork,
    IDescarteCambiosPendientes descarteCambios)
    : IRequestHandler<GenerarRespuestaSugeridaCommand, Result<Guid>>
{
    private const string ViolacionDeUnicidad = "23505";

    public static readonly Error NoProcede = Error.Crear(
        "Sugerencia.NoProcede", "Solo se sugiere respuesta a una conversación sin resolver cuyo último mensaje es entrante.");

    public static readonly Error NoDisponible = Error.Crear(
        "Sugerencia.NoDisponible", "Vega no ha podido redactar una respuesta ahora mismo.");

    public static readonly Error PendienteDeRevisar = Error.Crear(
        "Sugerencia.PendienteDeRevisar", "Esta conversación tiene una respuesta sugerida sin revisar: confírmala o descártala antes.");

    public static readonly Error Conflicto = Error.Crear(
        "Sugerencia.Conflicto", "Otra persona acaba de generar una respuesta sugerida para esta conversación.");

    public static readonly Error SinTexto = Error.Crear(
        "Sugerencia.SinTexto", "Esta macro no deja ningún texto que sugerir.");

    public async Task<Result<Guid>> Handle(GenerarRespuestaSugeridaCommand request, CancellationToken cancellationToken)
    {
        var conversacion = await comunicaciones.Conversaciones.FirstOrDefaultAsync(c => c.Id == request.ConversacionId, cancellationToken);
        if (conversacion is null || !await alcanceDatos.ConversacionVisibleAsync(conversacion, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Conversacion.NoEncontrada", "No encontramos esta conversación."));

        if (await currentUserService.ObtenerUsuarioActualIdAsync() is not { } autorId)
            return Result.Fallo<Guid>(Error.Crear("Sugerencia.SinAutor", "No pudimos identificar quién pide la sugerencia."));

        if (conversacion.Estado is EstadoConversacion.Resuelta or EstadoConversacion.Cerrada)
            return Result.Fallo<Guid>(NoProcede);

        var ultimoMensaje = await comunicaciones.Mensajes
            .Where(m => m.ConversacionId == conversacion.Id && !m.EsAutomatico)
            .OrderByDescending(m => m.FechaUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (ultimoMensaje is null || ultimoMensaje.Direccion != DireccionMensaje.Entrante)
            return Result.Fallo<Guid>(NoProcede);

        var activa = await comunicaciones.RespuestasSugeridas
            .Where(r => r.ConversacionId == conversacion.Id &&
                        (r.Estado == EstadoRespuestaSugerida.Propuesta || r.Estado == EstadoRespuestaSugerida.SinRevisar))
            .Select(r => new { r.Id, r.Estado, r.MensajeOrigenId, r.MacroId })
            .FirstOrDefaultAsync(cancellationToken);
        if (activa?.Estado == EstadoRespuestaSugerida.SinRevisar)
            return Result.Fallo<Guid>(PendienteDeRevisar);
        if (activa is not null && activa.MensajeOrigenId == ultimoMensaje.Id && (request.MacroId is null || request.MacroId == activa.MacroId))
            return Result.Exito(activa.Id);

        var texto = request.MacroId is { } macroId
            ? await RellenarMacroAsync(conversacion, macroId, cancellationToken)
            : await RedactarConVegaAsync(conversacion, ultimoMensaje, cancellationToken);
        if (texto.EsFallido)
            return Result.Fallo<Guid>(texto.Error);
        if (texto.Valor.Length > RespuestaSugerida.LongitudMaximaTexto)
            return Result.Fallo<Guid>(NoDisponible);

        // El candado del índice único parcial pide descartar antes de insertar, y la operación puede repetirse entera.
        var id = Guid.Empty;
        try
        {
            var guardado = await transaccion.EjecutarAsync(async ct =>
            {
                if (await respuestas.ObtenerActivaPorConversacionAsync(conversacion.Id, ct) is { } anterior)
                {
                    // Vega tarda: lo que se comprobó arriba puede haber cambiado mientras redactaba.
                    if (anterior.Estado == EstadoRespuestaSugerida.SinRevisar)
                        return Result.Fallo(PendienteDeRevisar);
                    if (anterior.Id != activa?.Id)
                        return Result.Fallo(Conflicto);

                    anterior.Descartar();
                    await unitOfWork.SaveChangesAsync(ct);
                }

                var ahora = DateTime.UtcNow;
                var nueva = request.MacroId is { } macro
                    ? RespuestaSugerida.DesdeMacro(conversacion.Id, ultimoMensaje.Id, macro, texto.Valor, autorId, ahora)
                    : RespuestaSugerida.DeVega(conversacion.Id, ultimoMensaje.Id, texto.Valor, autorId, ahora);
                respuestas.Agregar(nueva);
                await unitOfWork.SaveChangesAsync(ct);

                id = nueva.Id;
                return Result.Exito();
            }, cancellationToken);

            return guardado.EsFallido ? Result.Fallo<Guid>(guardado.Error) : Result.Exito(id);
        }
        catch (DbUpdateException excepcion) when (EsConflictoConOtraGeneracion(excepcion))
        {
            // El índice único parcial impide dos activas, y el token de versión, sustituir una que ya cambió.
            descarteCambios.DescartarCambiosPendientes();
            return Result.Fallo<Guid>(Conflicto);
        }
    }

    // DbException y no PostgresException: Application no depende de Npgsql (FronterasDeCapaTests).
    private static bool EsConflictoConOtraGeneracion(DbUpdateException excepcion) =>
        excepcion is DbUpdateConcurrencyException || excepcion.InnerException is DbException { SqlState: ViolacionDeUnicidad };

    private async Task<Result<string>> RellenarMacroAsync(Conversacion conversacion, Guid macroId, CancellationToken cancellationToken)
    {
        var rellena = await relleno.RellenarAsync(conversacion, macroId, cancellationToken);
        if (rellena.EsFallido || conversacion.Canal != CanalConversacion.WhatsApp)
            return rellena;

        var plana = VariablesPlantillaRespuesta.HtmlATexto(rellena.Valor);
        return string.IsNullOrWhiteSpace(plana) ? Result.Fallo<string>(SinTexto) : Result.Exito(plana);
    }

    private async Task<Result<string>> RedactarConVegaAsync(
        Conversacion conversacion, Mensaje ultimoEntrante, CancellationToken cancellationToken)
    {
        // Fallo cerrado: sin Tenant, sin ancla o sin Nivel 0 el correo no sale al proveedor.
        if (tenantActual.TenantId is not { } tenantId || !conversacion.TieneAncla()
            || !await instruccionTratamientoIa.EstaHabilitadaAsync(tenantId, cancellationToken))
            return Result.Fallo<string>(NoDisponible);

        var redactado = await redaccion.RedactarAsync(conversacion.Asunto, ultimoEntrante.CuerpoHtml, cancellationToken);
        if (redactado.EsFallido)
            return Result.Fallo<string>(NoDisponible);

        return Result.Exito(conversacion.Canal == CanalConversacion.WhatsApp
            ? redactado.Valor
            : VariablesPlantillaRespuesta.TextoPlanoAHtml(redactado.Valor));
    }
}
