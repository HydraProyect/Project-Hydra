namespace CaeManager.Domain.Auditoria;

/// <summary>
/// Vía por la que se operaba al provocar un cambio auditado. Es el espejo en
/// Domain de <c>TipoViaAcceso</c> de Application: se duplica en vez de
/// compartirse porque Domain no referencia Application, el mismo motivo por el
/// que los códigos de rol viajan como texto (ver AutorizacionEscrituraBehavior).
/// </summary>
public enum TipoViaAccesoAuditoria
{
    /// <summary>El usuario opera su propio tenant.</summary>
    Normal = 0,

    /// <summary>Opera un tenant ajeno bajo una <c>AsignacionOperacion</c>; el Id va en <c>ViaAccesoId</c>.</summary>
    OperacionDelegada = 1,

    /// <summary>Acceso privilegiado de plataforma. Reservado hasta que exista la capacidad.</summary>
    SesionPrivilegiada = 2,

    /// <summary>No se pudo resolver. Explícito para que el hueco se vea, en vez de pasar por Normal.</summary>
    Desconocida = 3
}
