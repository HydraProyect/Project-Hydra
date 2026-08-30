using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Trabajadores.Commands.RestaurarTrabajador;

/// <summary>Fase D ("Deshacer al eliminar") — ver RestaurarClienteCommand para el razonamiento completo del IgnoreQueryFilters()+TenantId.</summary>
public record RestaurarTrabajadorCommand(Guid Id) : ICommand;

public class RestaurarTrabajadorCommandHandler(
    ITrabajadoresQueryContext trabajadoresContext, ITenantActual tenantActual,
    IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<RestaurarTrabajadorCommand, Result>
{
    public async Task<Result> Handle(RestaurarTrabajadorCommand request, CancellationToken cancellationToken)
    {
        var trabajador = await trabajadoresContext.Trabajadores
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == request.Id && t.TenantId == tenantActual.TenantId, cancellationToken);

        if (trabajador is null || !trabajador.EstaEliminado)
            return Result.Fallo(Error.Crear("Trabajador.NoEncontrado", "No encontramos este trabajador eliminado."));

        // Autoridad por el empleador, no solo tenant (auditoría Módulo 5,
        // hallazgo crítico 8/9): TrabajadorVisibleAsync no sirve aquí porque
        // su consulta pasa por el filtro global de soft delete, que excluye
        // justamente la fila que se está restaurando — el empleador
        // persistido (EmpresaId/SubcontrataId) es la coordenada estable.
        if (!await TrabajadorAutorizacion.EmpleadorVisibleAsync(
                trabajador.EmpresaId, trabajador.SubcontrataId, alcanceDatos, cancellationToken))
            return Result.Fallo(Error.Crear("Trabajador.NoEncontrado", "No encontramos este trabajador eliminado."));

        trabajador.Restaurar();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
