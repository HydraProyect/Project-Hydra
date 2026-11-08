using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Application.Proyectos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Commands.RestaurarDocumento;

/// <summary>Fase D ("Deshacer al eliminar") — ver RestaurarClienteCommand para el razonamiento completo del IgnoreQueryFilters()+TenantId.</summary>
public record RestaurarDocumentoCommand(Guid Id) : ICommand;

public class RestaurarDocumentoCommandHandler(
    IDocumentosQueryContext documentosContext, ITenantActual tenantActual, IAlcanceDatosService alcanceDatos,
    IProyectosQueryContext proyectosContext, IUnitOfWork unitOfWork,
    IAltaAcreditacionesPlataformaService altaAcreditaciones, ISerializacionAltasAcreditacionEmpresa serializacionAltas)
    : IRequestHandler<RestaurarDocumentoCommand, Result>
{
    public async Task<Result> Handle(RestaurarDocumentoCommand request, CancellationToken cancellationToken)
    {
        // Lo leído aquí solo sirve para rechazar pronto y para saber el propietario, que no cambia.
        var documento = await CargarAsync(request.Id, cancellationToken);
        if (documento is null || !documento.EstaEliminado || !await VisibleAsync(documento, cancellationToken))
            return NoEncontrado();

        if (documento.EmpresaId is null)
        {
            documento.Restaurar();
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return Result.Exito();
        }

        // Un Documento de Empresa restaurado vuelve a acreditarse ante los accesos de plataforma de los Centros donde
        // su Empresa tiene actividad (regla A-1), también los dados de alta mientras estaba eliminado. Se serializa con
        // las altas que cambian a qué Centros llega (ver ISerializacionAltasAcreditacionEmpresa), y bajo el candado se
        // vuelve a decidir si sigue eliminado. Los Documentos de Trabajador no cambian.
        return await serializacionAltas.EjecutarAsync(async ct =>
        {
            var actual = await CargarAsync(request.Id, ct);
            if (actual is null || !actual.EstaEliminado)
                return NoEncontrado();

            actual.Restaurar();
            // Uno que ya estaba en el historial (sustituido) vuelve al historial: no se acredita, igual que en la
            // conciliación, que solo mira los operativos.
            if (DocumentoOperativo.Es(actual))
                await altaAcreditaciones.AgregarPendientesAsync(
                    new AltasConAcreditacion { Documentos = [actual], SoloDocumentosDeEmpresa = true }, ct);
            await unitOfWork.SaveChangesAsync(ct);
            return Result.Exito();
        }, cancellationToken);
    }

    // Asignación de Cartera: fuera de ella el documento eliminado «no existe», igual que en
    // EliminarDocumentoCommand — deshacer una baja es tan escritura como hacerla.
    private Task<Domain.Documentos.Documento?> CargarAsync(Guid id, CancellationToken cancellationToken) =>
        documentosContext.Documentos
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(d => d.Id == id && d.TenantId == tenantActual.TenantId, cancellationToken);

    private static Result NoEncontrado() =>
        Result.Fallo(Error.Crear("Documento.NoEncontrado", "No encontramos este documento eliminado."));

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
