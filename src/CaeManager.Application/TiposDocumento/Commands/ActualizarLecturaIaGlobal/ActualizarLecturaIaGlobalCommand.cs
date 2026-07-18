using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;

namespace CaeManager.Application.TiposDocumento.Commands.ActualizarLecturaIaGlobal;

/// <summary>Nivel 1 (Administrador): activa/desactiva la lectura automática por IA de un TipoDocumento para todos los Clientes — ver TipoDocumento.LecturaIaActiva.</summary>
public record ActualizarLecturaIaGlobalCommand(Guid TipoDocumentoId, bool Activa) : IRequest<Result>;

public class ActualizarLecturaIaGlobalCommandHandler(
    ITipoDocumentoRepository repositorio, IUnitOfWork unitOfWork, ICurrentUserService currentUserService)
    : IRequestHandler<ActualizarLecturaIaGlobalCommand, Result>
{
    // Application no puede referenciar Infrastructure.Identity.Roles — mismo motivo que en AutorizacionEscrituraBehavior.
    private const string RolAdministrador = "Administrador";

    public async Task<Result> Handle(ActualizarLecturaIaGlobalCommand request, CancellationToken cancellationToken)
    {
        var rol = await currentUserService.ObtenerRolActualAsync();
        if (rol != RolAdministrador)
            return Result.Fallo(Error.Crear("LecturaIa.SoloAdministrador", "Solo Administrador puede cambiar este interruptor."));

        var tipoDocumento = await repositorio.ObtenerPorIdAsync(request.TipoDocumentoId, cancellationToken);
        if (tipoDocumento is null)
            return Result.Fallo(Error.Crear("TipoDocumento.NoEncontrado", "No encontramos este tipo de documento."));

        tipoDocumento.EstablecerLecturaIaActiva(request.Activa);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
