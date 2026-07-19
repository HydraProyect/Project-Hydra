using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.TiposDocumento.Commands.ActualizarDeteccionTrabajadoresGlobal;

/// <summary>Solo Administrador: activa/desactiva la detección automática de altas/bajas de personal para un TipoDocumento de Empresa — ver TipoDocumento.DeteccionTrabajadoresActiva.</summary>
public record ActualizarDeteccionTrabajadoresGlobalCommand(Guid TipoDocumentoId, bool Activa) : IRequest<Result>;

public class ActualizarDeteccionTrabajadoresGlobalCommandValidator : AbstractValidator<ActualizarDeteccionTrabajadoresGlobalCommand>
{
    public ActualizarDeteccionTrabajadoresGlobalCommandValidator()
    {
        RuleFor(c => c.TipoDocumentoId).NotEmpty();
    }
}

public class ActualizarDeteccionTrabajadoresGlobalCommandHandler(
    ITipoDocumentoRepository repositorio, IUnitOfWork unitOfWork, ICurrentUserService currentUserService)
    : IRequestHandler<ActualizarDeteccionTrabajadoresGlobalCommand, Result>
{
    // Application no puede referenciar Infrastructure.Identity.Roles — mismo motivo que en AutorizacionEscrituraBehavior.
    private const string RolAdministrador = "Administrador";

    public async Task<Result> Handle(ActualizarDeteccionTrabajadoresGlobalCommand request, CancellationToken cancellationToken)
    {
        var rol = await currentUserService.ObtenerRolActualAsync();
        if (rol != RolAdministrador)
            return Result.Fallo(Error.Crear("DeteccionTrabajadores.SoloAdministrador", "Solo Administrador puede cambiar este interruptor."));

        var tipoDocumento = await repositorio.ObtenerPorIdAsync(request.TipoDocumentoId, cancellationToken);
        if (tipoDocumento is null)
            return Result.Fallo(Error.Crear("TipoDocumento.NoEncontrado", "No encontramos este tipo de documento."));

        if (tipoDocumento.AmbitoAplicacion != AmbitoAplicacion.Empresa)
            return Result.Fallo(Error.Crear(
                "DeteccionTrabajadores.AmbitoIncorrecto", "La detección de trabajadores solo aplica a tipos de documento de Empresa."));

        tipoDocumento.EstablecerDeteccionTrabajadoresActiva(request.Activa);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
