using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.ResponderConversacion;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.Commands.AprobarRecordatorioConversacion;

/// <param name="CuerpoHtml">El borrador tal como lo dejó el Gestor CAE: es lo que se envía.</param>
public record AprobarRecordatorioConversacionCommand(Guid ConversacionId, Guid PropuestaId, string CuerpoHtml) : ICommand;

public class AprobarRecordatorioConversacionCommandValidator : AbstractValidator<AprobarRecordatorioConversacionCommand>
{
    public AprobarRecordatorioConversacionCommandValidator()
    {
        RuleFor(c => c.ConversacionId).NotEmpty().WithMessage("El recordatorio debe pertenecer a una conversación.");
        RuleFor(c => c.PropuestaId).NotEmpty().WithMessage("Falta el recordatorio que se aprueba.");
        RuleFor(c => c.CuerpoHtml).NotEmpty().WithMessage("El recordatorio no puede estar vacío.");
        // Mismas reglas que ResponderConversacionCommandValidator: el envío no pasa por su pipeline (ver el handler).
        RuleFor(c => c.CuerpoHtml)
            .Must(texto => texto is null || VariablesPlantillaRespuesta.Pendientes(texto).Count == 0)
            .WithMessage("Completa las variables de la plantilla antes de enviar.");
    }
}

/// <summary>
/// El Gestor CAE aprueba el recordatorio que TALVEG propuso y se envía (decisión del 2026-10-17: se propone, se aprueba,
/// nada se envía solo). Quién puede lo decide <see cref="AutorizacionEscrituraBehavior{TRequest,TResponse}"/>; aquí va el
/// escalón de <b>alcance</b>, el mismo que responder el hilo (<see cref="AlcanceDatosServiceExtensions.ConversacionVisibleAsync"/>).
///
/// <para>
/// <b>El envío es el de la respuesta normal</b>: se llama al handler de <see cref="ResponderConversacionCommand"/>
/// directamente, no por MediatR. Volver a entrar en el pipeline desde un handler bloquearía la puerta de acceso a datos
/// (<c>SerializacionAccesoDatosBehavior</c>) y repetiría comprobaciones que este comando ya pasó; por eso este validador
/// copia las reglas del de la respuesta.
/// </para>
///
/// <para>
/// <b>Como mucho un envío.</b> La propuesta se marca Aprobada y se guarda <i>antes</i> de enviar: si otra persona la
/// aprueba a la vez, su guardado choca con el token de concurrencia y no envía nada. Si el envío falla, la propuesta vuelve
/// a Pendiente para poder reintentarlo. Si la conversación ya salió de la espera (llegó la respuesta, cambió el estado),
/// la propuesta se descarta y no se envía.
/// </para>
/// </summary>
public class AprobarRecordatorioConversacionCommandHandler(
    IConversacionRepository conversaciones,
    IPropuestaRecordatorioConversacionRepository propuestas,
    IAlcanceDatosService alcanceDatos,
    IRequestHandler<ResponderConversacionCommand, Result> responder,
    IUnitOfWork unitOfWork,
    IDescarteCambiosPendientes descarteCambios)
    : IRequestHandler<AprobarRecordatorioConversacionCommand, Result>
{
    public static readonly Error ErrorYaNoAplica = Error.Crear(
        "Recordatorio.YaNoAplica", "La conversación ya no está esperando respuesta: el recordatorio se ha descartado.");

    public async Task<Result> Handle(AprobarRecordatorioConversacionCommand request, CancellationToken cancellationToken)
    {
        var conversacion = await conversaciones.ObtenerPorIdAsync(request.ConversacionId, cancellationToken);
        if (conversacion is null || !await alcanceDatos.ConversacionVisibleAsync(
                conversacion.ClienteId, conversacion.EmpresaId, conversacion.ConexionIntegracionId, cancellationToken))
            return Result.Fallo(Error.Crear("Conversacion.NoEncontrada", "No encontramos esta conversación."));

        var propuesta = await propuestas.ObtenerPorIdAsync(request.PropuestaId, cancellationToken);
        if (propuesta is null || propuesta.ConversacionId != conversacion.Id)
            return Result.Fallo(Error.Crear("Recordatorio.NoEncontrado", "No encontramos este recordatorio."));

        var ahora = DateTime.UtcNow;
        if (propuesta.Estado == EstadoPropuestaRecordatorio.Pendiente && !propuesta.SigueVigentePara(conversacion))
        {
            propuesta.Descartar(ahora);
            await GuardarAsync(cancellationToken);
            return Result.Fallo(ErrorYaNoAplica);
        }

        var aprobacion = propuesta.Aprobar(ahora);
        if (aprobacion.EsFallido)
            return aprobacion;
        await GuardarAsync(cancellationToken);

        Result envio;
        try
        {
            envio = await responder.Handle(
                new ResponderConversacionCommand(conversacion.Id, request.CuerpoHtml), cancellationToken);
        }
        catch
        {
            await LiberarAsync(propuesta.Id);
            throw;
        }

        if (envio.EsFallido)
            await LiberarAsync(propuesta.Id);

        return envio;
    }

    /// <summary>
    /// Un guardado fallido no puede dejar la propuesta modificada en el contexto: en Blazor Server vive lo que el circuito y
    /// el siguiente comando la guardaría (ver <see cref="IDescarteCambiosPendientes"/>).
    /// </summary>
    private async Task GuardarAsync(CancellationToken cancellationToken)
    {
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            descarteCambios.DescartarCambiosPendientes();
            throw;
        }
    }

    /// <summary>
    /// El envío no salió: se sueltan los cambios a medias del envío y la propuesta vuelve a Pendiente. Sin el token del
    /// comando a propósito: si el Gestor CAE cerró la pantalla, la propuesta no puede quedarse Aprobada sin correo enviado.
    /// </summary>
    private async Task LiberarAsync(Guid propuestaId)
    {
        descarteCambios.DescartarCambiosPendientes();
        var recargada = await propuestas.ObtenerPorIdAsync(propuestaId, CancellationToken.None);
        if (recargada is null) return;
        recargada.LiberarTrasEnvioFallido();
        await GuardarAsync(CancellationToken.None);
    }
}
