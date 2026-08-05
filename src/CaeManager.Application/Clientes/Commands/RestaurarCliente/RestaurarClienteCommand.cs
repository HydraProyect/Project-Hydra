using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Clientes.Commands.RestaurarCliente;

/// <summary>
/// Fase D ("Deshacer al eliminar"): <c>EntidadBase.Restaurar()</c> ya
/// existía en el dominio, probado en <c>ClienteTests</c>, pero ningún
/// Command lo invocaba — la promesa "Podrás recuperarlo desde Auditoría" de
/// los diálogos de confirmación de eliminar era, hasta esta fase, un texto
/// sin nada detrás.
///
/// Un Cliente ya eliminado no lo encuentra <c>IClienteRepository.ObtenerPorIdAsync</c>
/// (el filtro global lo excluye), así que hace falta <c>IgnoreQueryFilters()</c>
/// — y como ese filtro combina tenant + soft-delete en una sola condición
/// (ver <c>CaeManagerDbContext</c>), ignorarlo también deja de filtrar por
/// tenant. Por eso el <c>TenantId</c> se compara aquí a mano: sin esa
/// comprobación, el Id de un Cliente eliminado de **otro** tenant se
/// restauraría igual — el mismo tipo de fallo que la frontera de aislamiento
/// de CLAUDE.md pide revisar explícitamente en cualquier
/// <c>IgnoreQueryFilters()</c> nuevo.
/// </summary>
public record RestaurarClienteCommand(Guid Id) : ICommand;

public class RestaurarClienteCommandHandler(IClientesQueryContext clientesContext, ITenantActual tenantActual, IUnitOfWork unitOfWork)
    : IRequestHandler<RestaurarClienteCommand, Result>
{
    public async Task<Result> Handle(RestaurarClienteCommand request, CancellationToken cancellationToken)
    {
        var cliente = await clientesContext.Clientes
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == request.Id && c.TenantId == tenantActual.TenantId, cancellationToken);

        if (cliente is null || !cliente.EstaEliminado)
            return Result.Fallo(Error.Crear("Cliente.NoEncontrado", "No encontramos este cliente eliminado."));

        cliente.Restaurar();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
