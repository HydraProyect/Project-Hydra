using CaeManager.Application.Common;

namespace CaeManager.Application.Trabajadores;

/// <summary>
/// Autoridad sobre el empleador de un Trabajador — auditoría Módulo 5,
/// hallazgo crítico 5/9. Un Trabajador pertenece a una Empresa o a una
/// Subcontrata, nunca ambas (ver <c>Trabajador.DeEmpresa</c>/<c>DeSubcontrata</c>);
/// antes solo se comprobaba que el empleador EXISTIERA en el tenant, no que
/// el actor tuviera autoridad sobre él, así que un gestor podía incorporar
/// trabajadores a una organización fuera de su cartera con solo conocer su
/// Id.
/// </summary>
public static class TrabajadorAutorizacion
{
    public static async Task<bool> EmpleadorVisibleAsync(
        Guid? empresaId, Guid? subcontrataId, IAlcanceDatosService alcanceDatos, CancellationToken cancellationToken)
    {
        if (empresaId is { } id) return await alcanceDatos.EmpresaVisibleAsync(id, cancellationToken);
        if (subcontrataId is { } sid) return await alcanceDatos.SubcontrataVisibleAsync(sid, cancellationToken);
        return false;
    }
}
