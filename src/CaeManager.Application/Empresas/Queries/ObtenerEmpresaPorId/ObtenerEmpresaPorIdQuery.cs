using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontrataPorId;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;

public record ObtenerEmpresaPorIdQuery(Guid Id) : IRequest<EmpresaDetalleDto?>;

/// <param name="Notas">
/// La «Nota interna» de la ficha 360 (<c>Empresa.Notas</c>, una sola por Empresa, compartida entre sus papeles). Solo
/// viaja para los roles de <see cref="ObtenerSubcontrataPorIdQueryHandler.RolesQueVenLaNotaInterna"/>; para cualquier
/// otro es <c>null</c>.
/// </param>
/// <param name="NotaInternaVisible">
/// Si quien pregunta es del equipo y por tanto la ficha debe pintar la tarjeta «Nota interna» (aunque esté vacía).
/// Con <c>false</c> la ficha no la pinta: <c>Notas</c> nulo no distingue «sin nota» de «no te la enseño».
/// </param>
public record EmpresaDetalleDto(
    Guid Id,
    string RazonSocial,
    string? Cif,
    DateTime CreadoEnUtc,
    IReadOnlyList<Guid> ClienteIds,
    Guid Version,
    string? Cnae = null,
    string? ConvenioAplicable = null,
    bool EsActividadAnexoI = false,
    string? Notas = null,
    bool NotaInternaVisible = false);

/// <summary>
/// F4.2c: <c>ClienteIds</c> sale de la arista, con el MISMO criterio de
/// clasificación que usa el diff de escritura de <c>EditarEmpresaCommand</c>
/// (eje Cliente = <c>EsCritico != null</c>) — los dos lados leen la misma
/// fuente, que es la condición para que este DTO pueda realimentar la
/// edición sin cerrar nada por desalineamiento. Dos invariantes deliberados:
/// una contraparte soft-deleted no aparece aquí NI entra en "actuales" del
/// diff (opaca en ambos lados → la relación sobrevive intacta a una edición
/// que no la menciona); y <c>ClienteIds</c> NO se acota por cartera — un
/// Cliente vivo pero fuera del alcance del usuario debe seguir viajando en
/// el DTO, porque hoy el diff de bajas se define sobre este contenido y
/// acotarlo reabriría el cierre silencioso por la puerta del alcance.
///
/// <para>
/// <b>Nota interna: el corte es de aquí, no de la página.</b> Esta consulta la lee también el rol Cliente (portal,
/// por <c>EmpresaWorkspacePanel</c>) y la nota es del equipo del Tenant propietario. Solo se entrega a los roles de
/// <see cref="ObtenerSubcontrataPorIdQueryHandler.RolesQueVenLaNotaInterna"/> —la misma lista blanca que Subcontrata
/// 360 y Vehículo 360, una sola definición de quién ve una «Nota interna»—; a cualquier otro le responde
/// <c>Notas = null</c> y <c>NotaInternaVisible = false</c>.
/// </para>
/// </summary>
public class ObtenerEmpresaPorIdQueryHandler(
    IEmpresasQueryContext dbContext, IAlcanceDatosService alcanceDatos, ICurrentUserService currentUserService)
    : IRequestHandler<ObtenerEmpresaPorIdQuery, EmpresaDetalleDto?>
{
    public async Task<EmpresaDetalleDto?> Handle(ObtenerEmpresaPorIdQuery request, CancellationToken cancellationToken)
    {
        // Alcance de LECTURA es correcto aquí (REC-149, se queda): esta
        // consulta respalda la pestaña "Información" del Context Workspace
        // de Empresa, reutilizada por el rol Cliente (portal) desde /empresas
        // — la ficha básica de una contratista (razón social, CIF, CNAE,
        // convenio) es exactamente lo que ese portal existe para mostrar, y
        // ClienteId != Version también alimenta EditarEmpresaCommand
        // (read-modify-write), que ya exige alcance de gestión en su propio
        // punto de escritura. Moverla a gestión cerraría esa pantalla para
        // el usuario al que sirve.
        //
        // HALLAZGO SECUNDARIO, elevado y no corregido aquí: EmpresaDetalleDto
        // incluye ClienteIds — la cartera COMERCIAL de la contratista (qué
        // otros Clientes tiene), la misma categoría de dato que REC-153 ya
        // decidió sacar de la cartera de lectura en ObtenerClientesDeEmpresaQuery.
        // Hoy el panel solo muestra un recuento (EmpresaWorkspacePanel.razor,
        // "Clientes con los que trabaja"), pero el DTO viaja completo. No lo
        // toco porque decidir qué debe ver el portal en esta pantalla
        // concreta no es una decisión mía (§14 del handoff) y el DTO
        // alimenta un comando de escritura con una invariante propia — ver
        // RETURN PACKAGE de HO-149-01.
        if (!await alcanceDatos.EmpresaVisibleAsync(request.Id, cancellationToken)) return null;

        var empresa = await dbContext.Empresas
            .Where(e => e.Id == request.Id)
            .Select(e => new { e.Id, e.RazonSocial, e.Cif, e.CreadoEnUtc, e.Version, e.Cnae, e.ConvenioAplicable, e.EsActividadAnexoI, e.Notas })
            .FirstOrDefaultAsync(cancellationToken);

        if (empresa is null) return null;

        var clienteIds = await dbContext.RelacionesEmpresariales
            .Where(r => r.ProveedoraId == request.Id && r.VigenciaHasta == null)
            .Join(dbContext.Empresas.Where(e => e.EsCritico != null), r => r.ClienteId, e => e.Id, (r, e) => e.Id)
            .ToListAsync(cancellationToken);

        var rol = await currentUserService.ObtenerRolEfectivoAsync();
        var esDelEquipo = rol is not null && ObtenerSubcontrataPorIdQueryHandler.RolesQueVenLaNotaInterna.Contains(rol);

        return new EmpresaDetalleDto(
            empresa.Id, empresa.RazonSocial, empresa.Cif, empresa.CreadoEnUtc, clienteIds, empresa.Version,
            empresa.Cnae, empresa.ConvenioAplicable, empresa.EsActividadAnexoI,
            Notas: esDelEquipo ? empresa.Notas : null, NotaInternaVisible: esDelEquipo);
    }
}
