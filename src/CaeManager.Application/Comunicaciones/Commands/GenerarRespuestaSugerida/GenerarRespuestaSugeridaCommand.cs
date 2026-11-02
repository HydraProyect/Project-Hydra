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
/// una <see cref="RespuestaSugerida"/> en Propuesta y una persona decide. Si ya había otra activa, queda Descartada en la
/// misma transacción. Solo si la conversación sigue viva y su último mensaje es entrante.
///
/// Quién puede pedirla lo decide <see cref="AutorizacionEscrituraBehavior{TRequest,TResponse}"/>, que deja fuera a Cliente y
/// Consulta; este handler añade el alcance de la conversación. Vega solo redacta con Instrucción de Tratamiento vigente y
/// sobre un hilo con ancla, igual que la detección de intención; si no puede, no se guarda nada.
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
    public static readonly Error NoProcede = Error.Crear(
        "Sugerencia.NoProcede", "Solo se sugiere respuesta a una conversación sin resolver cuyo último mensaje es entrante.");

    public static readonly Error NoDisponible = Error.Crear(
        "Sugerencia.NoDisponible", "Vega no ha podido redactar una respuesta ahora mismo.");

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
            .Where(m => m.ConversacionId == conversacion.Id)
            .OrderByDescending(m => m.FechaUtc)
            .FirstOrDefaultAsync(cancellationToken);
        if (ultimoMensaje is null || ultimoMensaje.Direccion != DireccionMensaje.Entrante)
            return Result.Fallo<Guid>(NoProcede);

        var texto = request.MacroId is { } macroId
            ? await relleno.RellenarAsync(conversacion, macroId, cancellationToken)
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
        catch (DbUpdateException)
        {
            // Otra generación sobre la misma conversación terminó antes: el índice único parcial impide dos activas.
            descarteCambios.DescartarCambiosPendientes();
            return Result.Fallo<Guid>(Error.Crear(
                "Sugerencia.Conflicto", "Otra persona acaba de generar una respuesta sugerida para esta conversación."));
        }
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
