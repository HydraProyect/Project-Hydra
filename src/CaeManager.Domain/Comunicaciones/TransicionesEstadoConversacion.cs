using CaeManager.Domain.Common;

namespace CaeManager.Domain.Comunicaciones;

/// <summary>
/// Máquina de estados de la Conversacion (decisión de producto 2026-10-17, hueco 2 de Comunicaciones): qué estado
/// puede seguir a cuál. Antes <c>CambiarEstado</c> asignaba cualquier valor sin comprobar nada.
///
/// <list type="bullet">
/// <item><description>Los cuatro estados <b>vivos</b> (<see cref="EstadoConversacion.Abierta"/>,
/// <see cref="EstadoConversacion.Pendiente"/>, <see cref="EstadoConversacion.EsperandoAlCliente"/>,
/// <see cref="EstadoConversacion.EnEsperaDeTercero"/>) pasan libremente entre sí y a Resuelta o Cerrada: el
/// Gestor CAE decide en cada momento por qué está esperando. Cerrar directamente desde un estado vivo es
/// lo que hace la vinculación de conversaciones con la origen.</description></item>
/// <item><description><see cref="EstadoConversacion.Resuelta"/> solo vuelve a Abierta (reapertura) o pasa a
/// Cerrada: no se «espera» una conversación ya resuelta sin reabrirla antes.</description></item>
/// <item><description><see cref="EstadoConversacion.Cerrada"/> solo se reabre, a Abierta, a mano. El cierre
/// automático (<see cref="Conversacion.PlazoCierreAutomaticoTrasResolucion"/>) lo hace Sistema, nunca una
/// persona.</description></item>
/// <item><description>Pasar a un estado igual al actual no es una transición y se rechaza.</description></item>
/// </list>
/// </summary>
public static class TransicionesEstadoConversacion
{
    /// <summary>
    /// Estados en los que el hilo se sigue atendiendo: lo que antes se escribía a mano como «Abierta o Pendiente».
    /// Es la fuente única para las consultas que buscan «conversaciones vivas» (candidatas de vinculación, reparto
    /// del pool de WhatsApp).
    /// </summary>
    public static readonly IReadOnlyList<EstadoConversacion> EstadosVivos =
    [
        EstadoConversacion.Abierta,
        EstadoConversacion.Pendiente,
        EstadoConversacion.EsperandoAlCliente,
        EstadoConversacion.EnEsperaDeTercero
    ];

    public static bool EsVivo(EstadoConversacion estado) => EstadosVivos.Contains(estado);

    public static bool EstaPermitida(EstadoConversacion desde, EstadoConversacion hacia)
    {
        if (desde == hacia)
            return false;

        return desde switch
        {
            EstadoConversacion.Resuelta => hacia is EstadoConversacion.Abierta or EstadoConversacion.Cerrada,
            EstadoConversacion.Cerrada => hacia is EstadoConversacion.Abierta,
            _ => true
        };
    }

    public static Error ErrorTransicionNoPermitida(EstadoConversacion desde, EstadoConversacion hacia) =>
        Error.Crear(
            "Conversacion.TransicionDeEstadoNoPermitida",
            desde == hacia
                ? $"La conversación ya está en estado {desde}."
                : $"Una conversación {desde} no puede pasar a {hacia}.");
}
