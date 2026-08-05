using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Commands.RestaurarDocumento;

/// <summary>Fase D ("Deshacer al eliminar") — ver RestaurarClienteCommand para el razonamiento completo del IgnoreQueryFilters()+TenantId.</summary>
public record RestaurarDocumentoCommand(Guid Id) : ICommand;

public class RestaurarDocumentoCommandHandler(IDocumentosQueryContext documentosContext, ITenantActual tenantActual, IUnitOfWork unitOfWork)
    : IRequestHandler<RestaurarDocumentoCommand, Result>
{
    public async Task<Result> Handle(RestaurarDocumentoCommand request, CancellationToken cancellationToken)
    {
        var documento = await documentosContext.Documentos
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.Id == request.Id && d.TenantId == tenantActual.TenantId, cancellationToken);

        if (documento is null || !documento.EstaEliminado)
            return Result.Fallo(Error.Crear("Documento.NoEncontrado", "No encontramos este documento eliminado."));

        documento.Restaurar();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
