using CaeManager.Application.Common;

namespace CaeManager.Application.ContratosAcceso;

/// <summary>
/// Autoridad de lectura y escritura sobre un contrato de acceso: el mismo criterio que
/// <see cref="Proyectos.ProyectoAutorizacion"/>, por el <c>ClienteEmpresarialId</c> del contrato (el Cliente empresarial titular
/// del Centro). Lecturas y comandos lo comparten para que un contrato que no se ve tampoco se pueda modificar,
/// cerrar, reabrir ni eliminar (IDOR de escritura).
///
/// <para>
/// <b>La Empresa contratista no entra en la autoridad.</b> <see cref="IAlcanceDatosService.ObtenerEmpresaIdsVisiblesAsync"/>
/// y su gemelo «ParaGestion» describen la cartera Outbound: con una Asignación de Cartera acotada, las Empresas propias
/// del Tenant y las proveedoras propias relacionadas con un Cliente empresarial visible. La Empresa contraparte local
/// con la que el Tenant del Cliente empresarial representa a una contratista Inbound no está en esas listas, así que
/// usarlas dejaría sin contratos a todo Gestor CAE con cartera acotada. Hasta que exista un alcance Inbound de Empresas contratistas, el comando de alta solo exige que la
/// Empresa contratista exista en el Tenant (filtro de tenant y RLS) y no sea el propio Cliente empresarial.
/// </para>
/// </summary>
public static class ContratoAccesoAutorizacion
{
    public static async Task<bool> VisibleAsync(
        Guid clienteEmpresarialId, IAlcanceDatosService alcanceDatos, CancellationToken cancellationToken)
    {
        var clienteIdsVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);
        return clienteIdsVisibles is null || clienteIdsVisibles.Contains(clienteEmpresarialId);
    }
}
