namespace CaeManager.Domain.Proyectos;

/// <summary>
/// Por qué un participante (<see cref="ProyectoTecnico"/>) dejó de estar en el Proyecto. D-4 sobre la #2005 (decisión
/// del 2026-11-15): la baja que hace el Sistema al cerrar un evento terminado sigue siendo automática, pero su motivo
/// tiene que verse en Proyectos para que nadie la confunda con una desasignación hecha a mano. Se persiste como entero:
/// los valores no se renumeran.
/// </summary>
public enum MotivoBajaProyectoTecnico
{
    /// <summary>Una persona lo dio de baja (DesasignarTecnicoProyectoCommand).</summary>
    Desasignado = 1,

    /// <summary>El Sistema lo dio de baja al cerrar el evento terminado (CierreEventosTerminadosHostedService).</summary>
    FinDelEvento = 2,
}
