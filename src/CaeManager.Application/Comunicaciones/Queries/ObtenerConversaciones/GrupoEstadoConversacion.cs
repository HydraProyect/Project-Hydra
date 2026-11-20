using CaeManager.Domain.Comunicaciones;

namespace CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;

/// <summary>
/// Estados agrupados de las vistas «Abiertas» y «Archivadas» de la bandeja (decisión DN-O, Chris, 2026-11-20, DECISION_LOG de
/// Project-Hydra-Negocio). A diferencia de <see cref="ObtenerConversacionesQuery.Estado"/>, que filtra por un estado concreto, aquí
/// un solo valor reúne varios estados.
/// </summary>
public enum GrupoEstadoConversacion
{
    /// <summary>
    /// Conversaciones que se siguen atendiendo: <see cref="EstadoConversacion.Abierta"/>,
    /// <see cref="EstadoConversacion.EsperandoRespuestaExterna"/> (en pantalla, esperando respuesta del Interlocutor),
    /// <see cref="EstadoConversacion.EnEsperaDeTercero"/> (pendiente de tercero) y el estado histórico
    /// <see cref="EstadoConversacion.Pendiente"/>, que la máquina de estados sigue contando como vivo.
    /// </summary>
    Abiertas = 0,

    /// <summary><see cref="EstadoConversacion.Resuelta"/> y <see cref="EstadoConversacion.Cerrada"/>.</summary>
    Archivadas = 1
}

/// <summary>
/// Fuente única de qué estados componen cada <see cref="GrupoEstadoConversacion"/> de la bandeja: el filtro de la lista y el
/// indicador «acción por aprobar» (DN-L) leen de aquí, para que «abierta» signifique lo mismo en los dos sitios. No es la fuente de
/// otros recuentos que mantienen su propia definición (la columna «Abiertas» de Buzones cuenta solo el estado Abierta).
/// </summary>
public static class GruposEstadoConversacion
{
    /// <summary>Los estados vivos de la máquina de estados (<see cref="TransicionesEstadoConversacion.EstadosVivos"/>), no una lista aparte.</summary>
    public static IReadOnlyList<EstadoConversacion> Abiertas => TransicionesEstadoConversacion.EstadosVivos;

    public static IReadOnlyList<EstadoConversacion> Archivadas { get; } = [EstadoConversacion.Resuelta, EstadoConversacion.Cerrada];

    public static IReadOnlyList<EstadoConversacion> Estados(GrupoEstadoConversacion grupo) => grupo switch
    {
        GrupoEstadoConversacion.Abiertas => Abiertas,
        GrupoEstadoConversacion.Archivadas => Archivadas,
        _ => throw new ArgumentOutOfRangeException(nameof(grupo), grupo, "Grupo de estados desconocido.")
    };

    /// <summary>Abierta según DN-O: ni Resuelta ni Cerrada.</summary>
    public static bool EsAbierta(EstadoConversacion estado) => Abiertas.Contains(estado);
}
