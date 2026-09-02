using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Integraciones;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Comunicaciones.Commands.EnviarMensajeNuevo;

/// <summary>
/// "Redactar" — a diferencia de <c>ResponderConversacionCommand</c>, no
/// contesta un hilo existente: abre uno nuevo (borrador + envío, sin
/// <c>conversationId</c> previo que preservar) y crea la
/// <see cref="Conversacion"/> que lo representa en Hydra. Exige un
/// buzón conectado — a diferencia de responder, no tiene sentido "simular"
/// el primer envío de una conversación nueva (sí tiene sentido simular una
/// respuesta dentro de un hilo ya sembrado como dato de prueba).
///
/// El <c>conversationId</c> que devuelve Graph puede ser uno YA conocido: si se
/// redacta con el mismo asunto al mismo destinatario, Outlook puede agrupar el
/// mensaje en un hilo existente. Como <c>{TenantId, HiloExternoId}</c> es único
/// (ver ConversacionConfiguration), crear una Conversacion nueva a ciegas
/// reventaría el guardado — así que se busca antes y, si el hilo ya existe, el
/// mensaje se suma a él y se devuelve su Id. Es además lo correcto de negocio:
/// para Graph es la misma conversación, y para el gestor también.
/// </summary>
/// <param name="EmpresaId">
/// Ancla de Empresa contraparte para el hilo que se abre, excluyente con
/// <paramref name="ClienteId"/> (ver <see cref="Conversacion.EmpresaId"/>).
/// Hoy solo la usa la reclamación de documentos de empresa
/// (<c>EnviarReclamacionEmpresaCommand</c>), que no tiene Cliente al que
/// anclar el hilo y no puede dejarlo caer en la cola de triage compartida.
/// </param>
public record EnviarMensajeNuevoCommand(
    Guid ConexionIntegracionId, IReadOnlyList<string> Destinatarios, string Asunto, string CuerpoHtml,
    Guid? ClienteId = null, IReadOnlyList<AdjuntoParaEnviarDto>? Adjuntos = null, Guid? EmpresaId = null) : ICommand<Guid>;

public class EnviarMensajeNuevoCommandValidator : AbstractValidator<EnviarMensajeNuevoCommand>
{
    public EnviarMensajeNuevoCommandValidator()
    {
        RuleFor(c => c.ConexionIntegracionId).NotEmpty().WithMessage("Selecciona el buzón desde el que enviar.");
        RuleFor(c => c.Destinatarios).NotEmpty().WithMessage("Indica al menos un destinatario.");
        RuleForEach(c => c.Destinatarios).EmailAddress().WithMessage("Alguno de los destinatarios no es un correo válido.");
        RuleFor(c => c.Asunto).NotEmpty().WithMessage("El asunto es obligatorio.").MaximumLength(Conversacion.LongitudMaximaAsunto);
        RuleFor(c => c.CuerpoHtml).NotEmpty().WithMessage("El mensaje no puede estar vacío.");
        RuleFor(c => c.Adjuntos)
            .Must(a => a is null || a.Sum(x => x.Contenido.LongLength) <= LimitesAdjuntosCorreo.TamanoMaximoTotalAdjuntosBytes)
            .WithMessage("Los adjuntos no pueden superar 3 MB en total.");
        RuleFor(c => c)
            .Must(c => c.ClienteId is null || c.EmpresaId is null)
            .WithMessage("Un hilo se ancla a un Cliente o a una Empresa, nunca a los dos.");
    }
}

public class EnviarMensajeNuevoCommandHandler(
    IConexionIntegracionRepository conexionRepositorio,
    IConversacionRepository conversacionRepositorio,
    IAlcanceDatosService alcanceDatos,
    IMicrosoft365GraphClient graphClient,
    AccesoGraphService accesoGraph,
    IFileStorageService almacenamiento,
    IResolucionParticipanteConversacionService resolucionParticipante,
    IUnitOfWork unitOfWork)
    : IRequestHandler<EnviarMensajeNuevoCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(EnviarMensajeNuevoCommand request, CancellationToken cancellationToken)
    {
        var conexion = await conexionRepositorio.ObtenerPorIdAsync(request.ConexionIntegracionId, cancellationToken);
        if (conexion is null || !await alcanceDatos.ClienteOpcionalVisibleAsync(conexion.ClienteId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("ConexionIntegracion.NoEncontrada", "No encontramos esta conexión."));

        // Un buzón personal de OTRO gestor no se resuelve por cartera de
        // Cliente (tiene ClienteId null, igual que el genérico del tenant) —
        // sin este check, cualquiera con acceso a Comunicaciones podía enviar
        // correo desde el buzón personal de un colega pasando su Id a mano.
        if (!await alcanceDatos.ConexionIntegracionVisibleAsync(conexion.Id, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("ConexionIntegracion.NoEncontrada", "No encontramos esta conexión."));

        if (conexion.Estado != EstadoConexionIntegracion.Habilitada)
            return Result.Fallo<Guid>(Error.Crear("ConexionIntegracion.NoDisponible", "Este buzón no está disponible."));

        // El Cliente del mensaje es el que pida quien redacta si se indica
        // (p. ej. desde una pantalla de Cliente concreta); si no, el que ya
        // tenga la propia conexión (buzón dedicado a un Cliente Delegante).
        //
        // Con ancla de Empresa NO se hereda el Cliente de la conexión: sería
        // anclar el hilo a los dos planos a la vez, que es justo lo que
        // Conversacion prohíbe.
        var clienteId = request.EmpresaId is null ? request.ClienteId ?? conexion.ClienteId : null;
        if (clienteId is not null && !await alcanceDatos.ClienteVisibleAsync(clienteId.Value, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Cliente.NoEncontrado", "No encontramos este cliente."));

        // Mismo criterio que el Cliente: quien redacta no puede abrir un hilo
        // anclado a una Empresa que no tiene en cartera.
        if (request.EmpresaId is { } empresaId && !await alcanceDatos.EmpresaVisibleAsync(empresaId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Empresa.NoEncontrada", "No encontramos esta empresa."));

        var tokenResultado = await accesoGraph.ObtenerAccessTokenVigenteAsync(conexion.Id, cancellationToken);
        if (tokenResultado.EsFallido)
            return Result.Fallo<Guid>(tokenResultado.Error);

        var envioResultado = await graphClient.EnviarNuevoMensajeAsync(
            tokenResultado.Valor, conexion.BuzonEmail, request.Destinatarios, request.Asunto, request.CuerpoHtml, request.Adjuntos, cancellationToken);
        if (envioResultado.EsFallido)
            return Result.Fallo<Guid>(envioResultado.Error);

        var conversacion = await conversacionRepositorio.ObtenerPorHiloExternoAsync(conexion.Id, envioResultado.Valor.HiloExternoId, cancellationToken);
        if (conversacion is null)
        {
            conversacion = new Conversacion(request.Asunto, clienteId, empresaId: request.EmpresaId);
            conversacion.AsociarConexion(conexion.Id, envioResultado.Valor.HiloExternoId);
            conversacionRepositorio.Agregar(conversacion);
        }

        var mensaje = conversacion.AgregarMensaje(
            DireccionMensaje.Saliente, conversacion.Canal, conexion.BuzonEmail, request.CuerpoHtml,
            mensajeExternoId: envioResultado.Valor.MensajeExternoId);
        foreach (var destinatario in request.Destinatarios)
        {
            // Al sumarse a un hilo ya existente el destinatario suele estar ya
            // registrado — no se duplica la fila (no hay índice único que lo
            // impida, así que la guarda va aquí).
            if (conversacion.Participantes.Any(p => p.Rol == RolParticipante.Para &&
                                                   string.Equals(p.Email, destinatario, StringComparison.OrdinalIgnoreCase)))
                continue;

            var (tipoOrigen, entidadRelacionadaId) = await resolucionParticipante.ResolverAsync(destinatario, clienteId, cancellationToken);
            conversacion.AgregarParticipante(destinatario, RolParticipante.Para, tipoOrigen, entidadRelacionadaId);
        }

        if (request.Adjuntos is { Count: > 0 })
        {
            foreach (var adjunto in request.Adjuntos)
            {
                using var flujo = new MemoryStream(adjunto.Contenido);
                var archivoUrl = await almacenamiento.GuardarAsync(flujo, adjunto.NombreArchivo, cancellationToken);
                mensaje.AgregarAdjunto(adjunto.NombreArchivo, adjunto.TipoContenido, adjunto.Contenido.LongLength, archivoUrl);
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito(conversacion.Id);
    }
}
