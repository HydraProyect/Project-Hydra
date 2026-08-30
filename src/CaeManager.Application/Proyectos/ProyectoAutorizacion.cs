using CaeManager.Application.Common;

namespace CaeManager.Application.Proyectos;

/// <summary>
/// Autoridad de escritura sobre un Proyecto — auditoría Módulo 5, hallazgo
/// crítico 4/9. Los comandos de escritura (Actualizar/Cerrar/Eliminar,
/// Asignar/DesasignarTecnico) solo dependían del filtro de tenant del
/// repositorio: un gestor con cartera acotada podía modificar, cerrar,
/// eliminar o alterar los técnicos de un proyecto de la cartera de OTRO
/// gestor del mismo tenant, sin que la lectura de esos mismos proyectos
/// jamás lo hubiera revelado como visible (IDOR de escritura).
///
/// Reproduce exactamente el criterio de <c>ObtenerProyectoPorIdQuery</c>: la
/// autoridad va por el <c>ClienteId</c> del proyecto, igual que en lectura.
/// </summary>
public static class ProyectoAutorizacion
{
    public static async Task<bool> VisibleAsync(
        Guid clienteId, IAlcanceDatosService alcanceDatos, CancellationToken cancellationToken)
    {
        var clienteIdsVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);
        return clienteIdsVisibles is null || clienteIdsVisibles.Contains(clienteId);
    }
}
