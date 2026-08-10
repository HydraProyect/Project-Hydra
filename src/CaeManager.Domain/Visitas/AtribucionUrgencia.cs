namespace CaeManager.Domain.Visitas;

/// <summary>
/// A quién es imputable que una Visita se gestionara con prisas. Es el dato
/// que separa "el gestor tardó" de "el cliente avisó con tiempo pero mandó los
/// papeles la víspera": sin él, una antelación efectiva corta parece siempre
/// culpa de quien la gestiona.
/// </summary>
public enum AtribucionUrgencia
{
    /// <summary>Hubo margen efectivo suficiente. Es también el valor de las visitas creadas a mano, sin solicitud de origen que medir.</summary>
    SinUrgencia = 0,

    /// <summary>El cliente avisó de la visita con menos de HorasCriticasVisita de margen: llegó tarde de entrada.</summary>
    SolicitudTardiaCliente = 1,

    /// <summary>El cliente avisó con margen pero no completó la documentación hasta la víspera — el "falso aviso con tiempo".</summary>
    DocumentacionTardiaCliente = 2,

    /// <summary>
    /// El expediente estuvo completo con margen y aun así la gestión se hizo a última hora.
    /// <b>Ningún emisor lo asigna todavía</b>: requiere el instante en que el gestor cerró la
    /// gestión, que es dato de la fase financiera/CX. Existe aquí como contrato de ese trabajo
    /// posterior — no lo interpretes como "calculado y siempre cero".
    /// </summary>
    RetrasoGestor = 3
}
