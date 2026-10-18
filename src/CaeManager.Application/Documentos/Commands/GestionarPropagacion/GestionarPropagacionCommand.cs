using System.Net;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.EnviarMensajeNuevo;
using CaeManager.Application.Documentos.Queries.ObtenerPropagacionesPendientes;
using CaeManager.Application.Integraciones.Queries.ObtenerConexionesIntegracion;
using CaeManager.Application.Proyectos;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Integraciones;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Commands.GestionarPropagacion;

public interface IDisponibilidadComunicacionesPropagacion
{
    bool Activa { get; }
}

public record ComprobarBuzonAvisoPropagacionQuery(Guid ClienteEmpresarialId) : IRequest<Guid?>;

public sealed class ComprobarBuzonAvisoPropagacionQueryHandler(
    IDisponibilidadComunicacionesPropagacion disponibilidad, IMediator mediator)
    : IRequestHandler<ComprobarBuzonAvisoPropagacionQuery, Guid?>
{
    public async Task<Guid?> Handle(ComprobarBuzonAvisoPropagacionQuery request, CancellationToken ct) =>
        await ObtenerBuzonConectadoParaAvisoAsync(request.ClienteEmpresarialId, ct);

    // Única puerta de disponibilidad: sin Comunicaciones o sin buzón no existe envío alternativo.
    private async Task<Guid?> ObtenerBuzonConectadoParaAvisoAsync(Guid clienteEmpresarialId, CancellationToken ct)
    {
        if (!disponibilidad.Activa) return null;
        var conexiones = await mediator.Send(new ObtenerConexionesIntegracionQuery(SoloPropiasYGenerales: true), ct);
        return conexiones.Where(c => c.Estado == EstadoConexionIntegracion.Habilitada
                && (c.ClienteId == null || c.ClienteId == clienteEmpresarialId))
            .OrderByDescending(c => c.ClienteId == clienteEmpresarialId).ThenBy(c => c.Id)
            .Select(c => (Guid?)c.Id).FirstOrDefault();
    }
}

/// <summary>Se ejecuta solo después de la ventana de deshacer; vuelve a comprobar el acceso y el destino.</summary>
public record GestionarPropagacionCommand(Guid DocumentoId, Guid CanalId, bool EnviarAviso = false) : ICommand<Guid>;

public sealed class GestionarPropagacionCommandValidator : AbstractValidator<GestionarPropagacionCommand>
{
    public GestionarPropagacionCommandValidator()
    {
        RuleFor(c => c.DocumentoId).NotEmpty();
        RuleFor(c => c.CanalId).NotEmpty();
    }
}

public sealed class GestionarPropagacionCommandHandler(
    IMediator mediator, ICentrosQueryContext centros, IPropagacionDocumentoRepository repositorio,
    ICurrentUserService usuario, IUnitOfWork unitOfWork)
    : IRequestHandler<GestionarPropagacionCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(GestionarPropagacionCommand request, CancellationToken ct)
    {
        var pendientes = await mediator.Send(new ObtenerPropagacionesPendientesQuery(), ct);
        var destino = pendientes.FirstOrDefault(p => p.DocumentoId == request.DocumentoId && p.CanalId == request.CanalId);
        var usuarioId = await usuario.ObtenerUsuarioActualIdAsync();
        if (destino is null || usuarioId is null)
            return Result.Fallo<Guid>(Error.Crear("Propagacion.NoEncontrada", "No encontramos esta propagación pendiente."));
        if (request.EnviarAviso != (destino.Via == TipoCanalGestion.Email))
            return Result.Fallo<Guid>(Error.Crear("Propagacion.ViaIncorrecta", "Utiliza la vía de entrega de este Centro."));

        var fila = await repositorio.ObtenerAsync(request.DocumentoId, request.CanalId, ct);
        Guid? conversacionId = null;
        if (request.EnviarAviso)
        {
            var buzon = await mediator.Send(new ComprobarBuzonAvisoPropagacionQuery(destino.ClienteEmpresarialId), ct);
            if (buzon is null)
                return Result.Fallo<Guid>(Error.Crear("Propagacion.ConectarBuzon", "Conecta el buzón para enviar el aviso. La propagación sigue pendiente."));
            var canal = await centros.CanalesGestionDocumental.AsNoTracking().SingleAsync(c => c.Id == request.CanalId, ct);
            var destinatarios = (canal.EmailsDestinatarios ?? "").Split([';', ','], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (destinatarios.Length == 0)
                return Result.Fallo<Guid>(Error.Crear("Propagacion.SinDestinatarios", "Añade un destinatario al canal de correo de este Centro."));
            var envio = await mediator.Send(new EnviarMensajeNuevoCommand(buzon.Value, destinatarios,
                "Documento aprobado · " + destino.Documento,
                "<p>Documento aprobado: " + WebUtility.HtmlEncode(destino.Documento) + ".</p><p>Centro: "
                    + WebUtility.HtmlEncode(destino.Centro) + ".</p>", ClienteId: destino.ClienteEmpresarialId), ct);
            if (envio.EsFallido) return Result.Fallo<Guid>(envio.Error);
            conversacionId = envio.Valor;
        }
        if (fila is null)
        {
            fila = new PropagacionDocumento(request.DocumentoId, request.CanalId);
            repositorio.Agregar(fila);
        }
        fila.MarcarHecha(usuarioId.Value, DateTime.UtcNow, conversacionId);
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Exito(fila.Version);
    }
}

public record DeshacerPropagacionCommand(Guid DocumentoId, Guid CanalId, Guid VersionEsperada) : ICommand;

public sealed class DeshacerPropagacionCommandValidator : AbstractValidator<DeshacerPropagacionCommand>
{
    public DeshacerPropagacionCommandValidator()
    {
        RuleFor(c => c.DocumentoId).NotEmpty();
        RuleFor(c => c.CanalId).NotEmpty();
        RuleFor(c => c.VersionEsperada).NotEmpty();
    }
}

public sealed class DeshacerPropagacionCommandHandler(
    IPropagacionDocumentoRepository repositorio, IDocumentoRepository documentos,
    ICentrosQueryContext centros, IAlcanceDatosService alcance, IProyectosQueryContext proyectos,
    ICurrentUserService usuario, IUnitOfWork unitOfWork)
    : IRequestHandler<DeshacerPropagacionCommand, Result>
{
    public async Task<Result> Handle(DeshacerPropagacionCommand request, CancellationToken ct)
    {
        var fila = await repositorio.ObtenerAsync(request.DocumentoId, request.CanalId, ct);
        var documento = await documentos.ObtenerPorIdAsync(request.DocumentoId, ct);
        var centroId = await centros.CanalesGestionDocumental.Where(c => c.Id == request.CanalId).Select(c => (Guid?)c.CentroId).SingleOrDefaultAsync(ct);
        var centrosVisibles = await alcance.ObtenerCentroIdsParaGestionAsync(ct);
        var usuarioId = await usuario.ObtenerUsuarioActualIdAsync();
        if (fila is null || documento is null || centroId is null || usuarioId != fila.HechoPorUsuarioId
            || (centrosVisibles is not null && !centrosVisibles.Contains(centroId.Value))
            || !await alcance.DocumentoVisibleAsync(documento, proyectos, ct))
            return Result.Fallo(Error.Crear("Propagacion.NoEncontrada", "No encontramos esta propagación."));
        if (ConcurrenciaOptimista.Verificar(fila, request.VersionEsperada, "esta propagación") is { } conflicto)
            return Result.Fallo(conflicto);
        if (fila.ConversacionId is not null)
            return Result.Fallo(Error.Crear("Propagacion.CorreoEnviado", "El aviso ya se envió y no se puede deshacer."));
        fila.Deshacer(usuarioId!.Value);
        await unitOfWork.SaveChangesAsync(ct);
        return Result.Exito();
    }
}
