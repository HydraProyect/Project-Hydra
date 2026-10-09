using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
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
/// F3b — reemplaza <c>IClientesQueryContext</c> por <c>IEmpresasQueryContext</c>:
/// una Empresa (ex-Cliente) ya eliminada no la encuentra
/// <c>IEmpresaRepository.ObtenerPorIdAsync</c> (el filtro global lo
/// excluye), así que hace falta <c>IgnoreQueryFilters()</c> — y como ese
/// filtro combina tenant + soft-delete en una sola condición (ver
/// <c>CaeManagerDbContext</c>), ignorarlo también deja de filtrar por
/// tenant. Por eso el <c>TenantId</c> se compara aquí a mano: sin esa
/// comprobación, el Id de una Empresa eliminada de **otro** tenant se
/// restauraría igual — el mismo tipo de fallo que la frontera de aislamiento
/// de CLAUDE.md pide revisar explícitamente en cualquier
/// <c>IgnoreQueryFilters()</c> nuevo.
/// </summary>
public record RestaurarClienteCommand(Guid Id) : ICommand;

public class RestaurarClienteCommandHandler(
    IEmpresasQueryContext empresasContext, ITenantActual tenantActual,
    IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<RestaurarClienteCommand, Result>
{
    public async Task<Result> Handle(RestaurarClienteCommand request, CancellationToken cancellationToken)
    {
        var empresa = await empresasContext.Empresas
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.Id == request.Id && e.TenantId == tenantActual.TenantId, cancellationToken);

        // EsCritico es lo que hace de una Empresa un Cliente empresarial: sin comprobarlo aquí, este
        // comando restauraría cualquier Empresa eliminada del Tenant con el alcance de Clientes.
        if (empresa is null || !empresa.EstaEliminado || empresa.EsCritico is null)
            return Result.Fallo(Error.Crear("Cliente.NoEncontrado", "No encontramos este Cliente empresarial eliminado."));

        // Autoridad de cartera, no solo tenant (auditoría Módulo 5, hallazgo crítico 8/9). La misma
        // que la baja, pero ClienteVisibleAsync no sirve aquí: bajo una cartera de ámbito universal
        // su lista se materializa desde las Empresas con el filtro global de soft delete, que excluye
        // justamente la fila que se está restaurando (LV-9: «Deshacer» respondía «No encontramos…»).
        if (!await alcanceDatos.ClienteEliminadoVisibleAsync(empresa.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Cliente.NoEncontrado", "No encontramos este Cliente empresarial eliminado."));

        empresa.Restaurar();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
