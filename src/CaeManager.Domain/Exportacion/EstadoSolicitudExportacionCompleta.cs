namespace CaeManager.Domain.Exportacion;

/// <summary>
/// Ciclo de vida de una <see cref="SolicitudExportacionCompleta"/>. Se guarda como texto: el ordinal no se persiste.
/// </summary>
public enum EstadoSolicitudExportacionCompleta
{
    /// <summary>Registrada; el fichero todavía no se ha empezado a generar.</summary>
    Pendiente = 0,

    /// <summary>El servicio de fondo la reclamó y está generando el fichero.</summary>
    EnCurso = 1,

    /// <summary>El fichero cifrado existe y se puede descargar hasta <see cref="SolicitudExportacionCompleta.CaducaEnUtc"/> o hasta que otra del mismo Tenant queda lista; como mucho una por Tenant.</summary>
    Lista = 2,

    /// <summary>La generación no terminó. No cuenta para el límite de una exportación cada 24 horas.</summary>
    Fallida = 3,

    /// <summary>
    /// Pasó su vigencia, o la sustituyó otra más nueva del mismo Tenant al quedar lista: el fichero se borró y ya
    /// no se puede descargar.
    /// </summary>
    Caducada = 4,
}
