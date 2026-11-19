using CaeManager.Application.Comercial.LineaOutbound;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.RelacionesEmpresariales;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Empresas.Commands.RestaurarEmpresa;

/// <summary>Fase D ("Deshacer al eliminar") — ver RestaurarClienteCommand para el razonamiento completo del IgnoreQueryFilters()+TenantId.</summary>
[AreaDelEncargo("Empresas")]
public record RestaurarEmpresaCommand(Guid Id) : ICommand;

public class RestaurarEmpresaCommandHandler(
    IEmpresasQueryContext empresasContext, ITenantActual tenantActual,
    IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork, ILimiteEmpresaPropia limiteEmpresaPropia,
    IRelacionEmpresarialRepository relaciones, IOutboundCerradoTenantTec outboundCerrado,
    IInvalidadorAlcance? invalidadorAlcance = null)
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

        // D-P11: devolver a activa una Empresa propia es tener una más; la de la Papelera no contaba. Recuento y guardado
        // bajo el candado del Tenant, como el alta.
        return await limiteEmpresaPropia.SerializarAsync(async ct =>
        {
            // La Asignación de Cartera se vuelve a decidir después de esperar el candado, sin el alcance memoizado de
            // antes de la espera (issue #2118).
            invalidadorAlcance?.Invalidar();
            if (!await alcanceDatos.EmpresaParaGestionVisibleAsync(empresa.Id, ct))
                return Result.Fallo(Error.Crear("Empresa.NoEncontrada", "No encontramos esta empresa eliminada."));

            if (empresa.EsPropia
                && await limiteEmpresaPropia.ComprobarAltaAsync(0, esIntento: true, ct) is { } limite)
                return Result.Fallo(limite);

            // D3 (DN-4, DN-7): restaurar una Empresa con Relaciones Empresariales vigentes con Clientes empresariales devuelve
            // a Outbound esas relaciones, y las restauraciones se deniegan al Tenant TEC participante sin línea Outbound. La
            // Empresa sin Clientes empresariales sigue abierta. El Tenant beneficiario es el Tenant activo, dueño de la Empresa;
            // sin él, falla cerrado.
            if ((await relaciones.ObtenerContrapartesVigentesAsync(empresa.Id, ct)).ClienteIds.Any()
                && (tenantActual.TenantId is not { } tenantId || await outboundCerrado.EstaCerradoAsync(tenantId, ct)))
                return Result.Fallo(ErroresLimiteTenantTec.OutboundCerrado);

            empresa.Restaurar();
            await unitOfWork.SaveChangesAsync(ct);
            return Result.Exito();
        }, cancellationToken);
    }
}
