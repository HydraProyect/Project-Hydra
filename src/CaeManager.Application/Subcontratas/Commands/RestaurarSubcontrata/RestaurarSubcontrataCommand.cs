using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Subcontratas.Commands.RestaurarSubcontrata;

/// <summary>
/// «Deshacer» de <c>EliminarSubcontrataCommand</c> y de su lote — ver RestaurarClienteCommand para el
/// razonamiento completo del IgnoreQueryFilters()+TenantId.
///
/// Revierte solo el borrado lógico de la Empresa: la baja no toca sus Relaciones Empresariales, sus
/// credenciales de portal ni sus documentos, y exige que no tenga trabajadores, así que no hay nada
/// más que restaurar.
/// </summary>
public record RestaurarSubcontrataCommand(Guid Id) : ICommand;

public class RestaurarSubcontrataCommandHandler(
    IEmpresasQueryContext empresasContext, ITenantActual tenantActual,
    IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<RestaurarSubcontrataCommand, Result>
{
    public async Task<Result> Handle(RestaurarSubcontrataCommand request, CancellationToken cancellationToken)
    {
        var subcontrata = await empresasContext.Empresas
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.Id == request.Id && e.TenantId == tenantActual.TenantId, cancellationToken);

        // NivelServicio es lo que hace de una Empresa una Subcontrata: sin comprobarlo aquí, este
        // comando restauraría cualquier Empresa eliminada del Tenant con el alcance de Subcontratas.
        if (subcontrata is null || !subcontrata.EstaEliminado || subcontrata.NivelServicio is null)
            return Result.Fallo(Error.Crear("Subcontrata.NoEncontrada", "No encontramos esta subcontrata eliminada."));

        // Misma autoridad que la baja (alcance de gestión), pero SubcontrataParaGestionVisibleAsync no
        // sirve aquí: su lista se deriva de las Empresas con el filtro global de soft delete, que
        // excluye justamente la fila que se está restaurando.
        if (!await alcanceDatos.SubcontrataEliminadaParaGestionVisibleAsync(subcontrata.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Subcontrata.NoEncontrada", "No encontramos esta subcontrata eliminada."));

        subcontrata.Restaurar();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
