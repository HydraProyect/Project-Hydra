using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Subcontratas.Commands.RestaurarVerificacionExterna;

/// <summary>
/// «Deshacer» de <c>EliminarVerificacionExternaSubcontrataCommand</c> — ver RestaurarClienteCommand para el
/// razonamiento completo del IgnoreQueryFilters()+TenantId. Revierte solo el borrado lógico: la evidencia
/// nunca salió del almacenamiento. Misma autoridad que la baja: la Subcontrata y el Centro de la verificación
/// tienen que estar al alcance de gestión.
/// </summary>
public record RestaurarVerificacionExternaSubcontrataCommand(Guid Id) : ICommand;

public class RestaurarVerificacionExternaSubcontrataCommandHandler(
    ISubcontratasQueryContext subcontratasContext,
    ITenantActual tenantActual,
    IAlcanceDatosService alcanceDatos,
    IUnitOfWork unitOfWork)
    : IRequestHandler<RestaurarVerificacionExternaSubcontrataCommand, Result>
{
    public async Task<Result> Handle(
        RestaurarVerificacionExternaSubcontrataCommand request, CancellationToken cancellationToken)
    {
        var verificacion = await subcontratasContext.VerificacionesExternaSubcontrata
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(v => v.Id == request.Id && v.TenantId == tenantActual.TenantId, cancellationToken);

        if (verificacion is null || !verificacion.EstaEliminado
            || !await alcanceDatos.SubcontrataParaGestionVisibleAsync(verificacion.SubcontrataId, cancellationToken)
            || !await alcanceDatos.CentroParaGestionVisibleAsync(verificacion.CentroId, cancellationToken))
            return Result.Fallo(Error.Crear("VerificacionExterna.NoEncontrada", "No encontramos esta verificación eliminada."));

        verificacion.Restaurar();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
