using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Empresas.Commands.RestaurarEmpresa;

/// <summary>Fase D ("Deshacer al eliminar") — ver RestaurarClienteCommand para el razonamiento completo del IgnoreQueryFilters()+TenantId.</summary>
public record RestaurarEmpresaCommand(Guid Id) : ICommand;

public class RestaurarEmpresaCommandHandler(
    IEmpresasQueryContext empresasContext, ITenantActual tenantActual,
    IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork, ILimiteEmpresaPropia limiteEmpresaPropia)
    : IRequestHandler<RestaurarEmpresaCommand, Result>
{
    public async Task<Result> Handle(RestaurarEmpresaCommand request, CancellationToken cancellationToken)
    {
        var empresa = await empresasContext.Empresas
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.Id == request.Id && e.TenantId == tenantActual.TenantId, cancellationToken);

        if (empresa is null || !empresa.EstaEliminado)
            return Result.Fallo(Error.Crear("Empresa.NoEncontrada", "No encontramos esta empresa eliminada."));

        // Autoridad de cartera, no solo tenant (auditoría Módulo 5, hallazgo
        // crítico 8/9, mismo patrón que RestaurarCentro/RestaurarTrabajador).
        // Defensa en profundidad (REC-149): inalcanzable para el rol Cliente
        // vía AutorizacionEscrituraBehavior; alcance de gestión como segunda
        // barrera independiente.
        if (!await alcanceDatos.EmpresaParaGestionVisibleAsync(empresa.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Empresa.NoEncontrada", "No encontramos esta empresa eliminada."));

        // D-P11: devolver a activa una Empresa propia es tener una más; la de la Papelera no contaba.
        if (empresa.EsPropia
            && await limiteEmpresaPropia.ComprobarAltaAsync(0, esIntento: true, cancellationToken) is { } limite)
            return Result.Fallo(limite);

        empresa.Restaurar();
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}
