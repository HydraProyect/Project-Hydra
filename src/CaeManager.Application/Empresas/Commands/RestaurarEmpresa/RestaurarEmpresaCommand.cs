using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Empresas.Commands.RestaurarEmpresa;

/// <summary>Fase D ("Deshacer al eliminar") — ver RestaurarClienteCommand para el razonamiento completo del IgnoreQueryFilters()+TenantId.</summary>
public record RestaurarEmpresaCommand(Guid Id) : ICommand;

public class RestaurarEmpresaCommandHandler(IEmpresasQueryContext empresasContext, ITenantActual tenantActual, IUnitOfWork unitOfWork)
    : IRequestHandler<RestaurarEmpresaCommand, Result>
{
    public async Task<Result> Handle(RestaurarEmpresaCommand request, CancellationToken cancellationToken)
    {
        var empresa = await empresasContext.Empresas
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.Id == request.Id && e.TenantId == tenantActual.TenantId, cancellationToken);

        if (empresa is null || !empresa.EstaEliminado)
            return Result.Fallo(Error.Crear("Empresa.NoEncontrada", "No encontramos esta empresa eliminada."));

        empresa.Restaurar();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
