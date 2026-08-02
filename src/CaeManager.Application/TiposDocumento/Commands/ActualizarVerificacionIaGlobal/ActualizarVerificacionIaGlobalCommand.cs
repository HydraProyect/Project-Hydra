using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.TiposDocumento.Commands.ActualizarVerificacionIaGlobal;

/// <summary>Solo Administrador: activa/desactiva la verificación IA con confidence score para un TipoDocumento de Trabajador — ver TipoDocumento.VerificacionIaActiva.</summary>
public record ActualizarVerificacionIaGlobalCommand(Guid TipoDocumentoId, bool Activa) : ICommand;

public class ActualizarVerificacionIaGlobalCommandValidator : AbstractValidator<ActualizarVerificacionIaGlobalCommand>
{
    public ActualizarVerificacionIaGlobalCommandValidator()
    {
        RuleFor(c => c.TipoDocumentoId).NotEmpty();
    }
}

public class ActualizarVerificacionIaGlobalCommandHandler(
    ITipoDocumentoRepository repositorio, IUnitOfWork unitOfWork, ICurrentUserService currentUserService)
    : IRequestHandler<ActualizarVerificacionIaGlobalCommand, Result>
{
    // Application no puede referenciar Infrastructure.Identity.Roles — mismo motivo que en AutorizacionEscrituraBehavior.
    private const string RolAdministrador = "Administrador";

    public async Task<Result> Handle(ActualizarVerificacionIaGlobalCommand request, CancellationToken cancellationToken)
    {
        var rol = await currentUserService.ObtenerRolActualAsync();
        if (rol != RolAdministrador)
            return Result.Fallo(Error.Crear("VerificacionIa.SoloAdministrador", "Solo Administrador puede cambiar este interruptor."));

        var tipoDocumento = await repositorio.ObtenerPorIdAsync(request.TipoDocumentoId, cancellationToken);
        if (tipoDocumento is null)
            return Result.Fallo(Error.Crear("TipoDocumento.NoEncontrado", "No encontramos este tipo de documento."));

        if (tipoDocumento.AmbitoAplicacion != AmbitoAplicacion.Trabajador)
            return Result.Fallo(Error.Crear(
                "VerificacionIa.AmbitoIncorrecto", "La verificación IA solo aplica a tipos de documento de Trabajador."));

        tipoDocumento.EstablecerVerificacionIaActiva(request.Activa);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
