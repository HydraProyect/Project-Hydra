using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Visitas;

namespace CaeManager.Application.Visitas.Commands;

/// <summary>
/// Punto único de autorización de cancelar y reactivar una Visita (FS-11). Las
/// dos acciones son simétricas por decisión de la coordinadora (2026-09-26): quien
/// puede cancelar puede reactivar, y nadie más.
/// <list type="bullet">
/// <item>Rol: el de cualquier <c>ICommand</c>, que ya filtra
/// <c>AutorizacionEscrituraBehavior</c> (Administrador, Dirección CAE, Coordinador
/// CAE y Gestor CAE; Consulta y Cliente, no).</item>
/// <item>Alcance: el de GESTIÓN sobre el Centro de la Visita. Administrador y
/// Dirección CAE, todo el Tenant; Coordinador CAE y Gestor CAE, su Asignación de
/// Cartera.</item>
/// </list>
/// Fuera de alcance se responde igual que «no existe», sin revelar qué hay fuera.
/// </summary>
public static class AutorizacionCancelacionVisita
{
    public static Error NoEncontrada => Error.Crear("Visita.NoEncontrada", "No encontramos esta visita.");

    public static Task<bool> PuedeGestionarAsync(IAlcanceDatosService alcanceDatos, Visita visita, CancellationToken cancellationToken) =>
        alcanceDatos.CentroParaGestionVisibleAsync(visita.CentroId, cancellationToken);

    /// <summary>
    /// La misma regla para un lote: el alcance se resuelve una vez. null = sin restricción.
    /// </summary>
    public static bool PuedeGestionar(IReadOnlyCollection<Guid>? centroIdsParaGestion, Visita visita) =>
        centroIdsParaGestion is null || centroIdsParaGestion.Contains(visita.CentroId);
}
