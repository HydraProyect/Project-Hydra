using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.ContratosAcceso;
using MediatR;

namespace CaeManager.Application.ContratosAcceso.Commands.EliminarContratoAcceso;

/// <summary>Baja lógica (soft delete): el runtime no tiene DELETE sobre <c>ContratosAcceso</c>.</summary>
public record EliminarContratoAccesoCommand(Guid Id) : ICommand;

public class EliminarContratoAccesoCommandHandler(
    IContratoAccesoRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork, ICurrentUserService currentUserService)
    : IRequestHandler<EliminarContratoAccesoCommand, Result>
{
    public async Task<Result> Handle(EliminarContratoAccesoCommand request, CancellationToken cancellationToken)
    {
        var contrato = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (contrato is null || !await ContratoAccesoAutorizacion.VisibleAsync(contrato.ClienteEmpresarialId, alcanceDatos, cancellationToken))
            return Result.Fallo(Error.Crear("ContratoAcceso.NoEncontrado", "El contrato de acceso no existe o no tienes acceso."));

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        contrato.MarcarComoEliminado(usuarioId ?? Guid.Empty);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
