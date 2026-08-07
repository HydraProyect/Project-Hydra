using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.TiposDocumento.Commands.ActualizarPerfilDocumentoOficialGlobal;

/// <summary>
/// Solo Administrador: asigna (o quita, con <see cref="PerfilDocumentoOficial.Ninguno"/>)
/// el perfil de documento oficial de un TipoDocumento — el interruptor de la
/// validación automática por firma digital (ver TipoDocumento.PerfilDocumentoOficial).
/// </summary>
public record ActualizarPerfilDocumentoOficialGlobalCommand(Guid TipoDocumentoId, PerfilDocumentoOficial Perfil) : ICommand;

public class ActualizarPerfilDocumentoOficialGlobalCommandValidator : AbstractValidator<ActualizarPerfilDocumentoOficialGlobalCommand>
{
    public ActualizarPerfilDocumentoOficialGlobalCommandValidator()
    {
        RuleFor(c => c.TipoDocumentoId).NotEmpty();
        RuleFor(c => c.Perfil).IsInEnum();
    }
}

public class ActualizarPerfilDocumentoOficialGlobalCommandHandler(
    ITipoDocumentoRepository repositorio, IUnitOfWork unitOfWork, ICurrentUserService currentUserService)
    : IRequestHandler<ActualizarPerfilDocumentoOficialGlobalCommand, Result>
{
    // Application no puede referenciar Infrastructure.Identity.Roles — mismo motivo que en AutorizacionEscrituraBehavior.
    private const string RolAdministrador = "Administrador";

    public async Task<Result> Handle(ActualizarPerfilDocumentoOficialGlobalCommand request, CancellationToken cancellationToken)
    {
        var rol = await currentUserService.ObtenerRolActualAsync();
        if (rol != RolAdministrador)
            return Result.Fallo(Error.Crear("PerfilDocumentoOficial.SoloAdministrador", "Solo Administrador puede cambiar el perfil oficial."));

        var tipoDocumento = await repositorio.ObtenerPorIdAsync(request.TipoDocumentoId, cancellationToken);
        if (tipoDocumento is null)
            return Result.Fallo(Error.Crear("TipoDocumento.NoEncontrado", "No encontramos este tipo de documento."));

        if (request.Perfil != PerfilDocumentoOficial.Ninguno
            && tipoDocumento.AmbitoAplicacion is not (AmbitoAplicacion.Empresa or AmbitoAplicacion.Cliente))
        {
            return Result.Fallo(Error.Crear(
                "PerfilDocumentoOficial.AmbitoIncorrecto",
                "El perfil de documento oficial solo aplica a tipos de documento de Empresa o Cliente."));
        }

        tipoDocumento.EstablecerPerfilDocumentoOficial(request.Perfil);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
