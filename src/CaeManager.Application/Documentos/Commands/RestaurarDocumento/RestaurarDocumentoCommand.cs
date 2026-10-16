using CaeManager.Application.Common;
using CaeManager.Application.Proyectos;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Commands.RestaurarDocumento;

/// <summary>Fase D ("Deshacer al eliminar") — ver RestaurarClienteCommand para el razonamiento completo del IgnoreQueryFilters()+TenantId.</summary>
public record RestaurarDocumentoCommand(Guid Id) : ICommand;

public class RestaurarDocumentoCommandHandler(
    IDocumentosQueryContext documentosContext, ITenantActual tenantActual, IAlcanceDatosService alcanceDatos,
    IProyectosQueryContext proyectosContext, IUnitOfWork unitOfWork)
    : IRequestHandler<RestaurarDocumentoCommand, Result>
{
    public async Task<Result> Handle(RestaurarDocumentoCommand request, CancellationToken cancellationToken)
    {
        var documento = await documentosContext.Documentos
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.Id == request.Id && d.TenantId == tenantActual.TenantId, cancellationToken);

        // Asignación de Cartera: fuera de ella el documento eliminado «no existe», igual que en
        // EliminarDocumentoCommand — deshacer una baja es tan escritura como hacerla.
        if (documento is null || !documento.EstaEliminado || !await VisibleAsync(documento, cancellationToken))
            return Result.Fallo(Error.Crear("Documento.NoEncontrado", "No encontramos este documento eliminado."));

        documento.Restaurar();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }

    /// <summary>
    /// Igual que <see cref="DocumentoAlcanceExtensions.DocumentoVisibleAsync"/>, salvo que el Proyecto
    /// dueño puede estar eliminado: EliminarProyecto no arrastra sus Documentos, y desde la Papelera se
    /// restaura un Documento de un Proyecto ya eliminado. Su Cliente empresarial se resuelve sin el filtro
    /// de borrado lógico, acotado al Tenant actual como la carga del propio Documento.
    /// </summary>
    private async Task<bool> VisibleAsync(Domain.Documentos.Documento documento, CancellationToken cancellationToken)
    {
        if (documento is not { TrabajadorId: null, ClienteId: null, VehiculoId: null, ProyectoId: { } proyectoId })
            return await alcanceDatos.DocumentoVisibleAsync(documento, proyectosContext, cancellationToken);

        var clienteEmpresarialDelProyecto = await proyectosContext.Proyectos
            .IgnoreQueryFilters()
            .Where(p => p.Id == proyectoId && p.TenantId == tenantActual.TenantId)
            .Select(p => (Guid?)p.ClienteId)
            .FirstOrDefaultAsync(cancellationToken);

        return clienteEmpresarialDelProyecto is { } clienteEmpresarial
               && await alcanceDatos.ClienteVisibleAsync(clienteEmpresarial, cancellationToken);
    }
}
