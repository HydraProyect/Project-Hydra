using CaeManager.Application.Comercial.LineaOutbound;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.RelacionesEmpresariales;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Subcontratas.Commands.RestaurarSubcontrata;

/// <summary>
/// «Deshacer» de <c>EliminarSubcontrataCommand</c> y de su lote — ver RestaurarClienteCommand para el
/// razonamiento completo del IgnoreQueryFilters()+TenantId.
///
/// Revierte solo el borrado lógico de la Empresa: la baja no toca sus Relaciones Empresariales, sus
/// credenciales de portal ni sus documentos, y exige que no tenga trabajadores, así que no hay nada
/// más que restaurar. Precisamente por eso, restaurarla devuelve a Outbound las Relaciones Empresariales
/// vigentes con Clientes empresariales que conservaba, y eso se cierra al Tenant TEC participante sin
/// línea Outbound (D3, DN-7), como en <c>RestaurarEmpresaCommand</c> sobre la misma fila.
/// </summary>
[AreaDelEncargo("Subcontratas")]
public record RestaurarSubcontrataCommand(Guid Id) : ICommand;

public class RestaurarSubcontrataCommandHandler(
    IEmpresasQueryContext empresasContext, ITenantActual tenantActual,
    IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork, ILimiteEmpresaPropia limiteEmpresaPropia,
    IRelacionEmpresarialRepository relaciones, IOutboundCerradoTenantTec outboundCerrado,
    IInvalidadorAlcance? invalidadorAlcance = null)
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

        // D-P11: una Subcontrata nace con EsPropia = false, pero el comando no lo exige; si la fila fuera propia,
        // restaurarla pasa por el mismo límite que RestaurarEmpresaCommand, bajo el mismo candado.
        return await limiteEmpresaPropia.SerializarAsync(async ct =>
        {
            // La Asignación de Cartera se vuelve a decidir después de esperar el candado, sin el alcance memoizado de
            // antes de la espera (issue #2118).
            invalidadorAlcance?.Invalidar();
            if (!await alcanceDatos.SubcontrataEliminadaParaGestionVisibleAsync(subcontrata.Id, ct))
                return Result.Fallo(Error.Crear("Subcontrata.NoEncontrada", "No encontramos esta subcontrata eliminada."));

            if (subcontrata.EsPropia
                && await limiteEmpresaPropia.ComprobarAltaAsync(0, esIntento: true, ct) is { } limite)
                return Result.Fallo(limite);

            // EP-4 del diseño D3 y DN-7: D5 abre las Subcontratas al Tenant TEC, pero no las Relaciones Empresariales con
            // Clientes empresariales. Restaurar una Subcontrata que las tenga vigentes las devuelve a Outbound, y las
            // restauraciones se deniegan al Tenant TEC participante sin línea Outbound; sin esta guarda, este comando sería
            // un rodeo de la de RestaurarEmpresaCommand sobre la misma fila. La Subcontrata sin Clientes empresariales sigue
            // abierta. El Tenant beneficiario es el Tenant activo, dueño de la fila; sin él, falla cerrado.
            if ((await relaciones.ObtenerContrapartesVigentesAsync(subcontrata.Id, ct)).ClienteIds.Any()
                && (tenantActual.TenantId is not { } tenantId || await outboundCerrado.EstaCerradoAsync(tenantId, ct)))
                return Result.Fallo(ErroresLimiteTenantTec.OutboundCerrado);

            subcontrata.Restaurar();
            await unitOfWork.SaveChangesAsync(ct);
            return Result.Exito();
        }, cancellationToken);
    }
}
