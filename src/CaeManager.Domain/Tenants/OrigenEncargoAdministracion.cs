namespace CaeManager.Domain.Tenants;

/// <summary>
/// Quién registró el <see cref="EncargoAdministracion"/>. Dos vías del producto y la
/// siembra de demostración; ninguna pasa por el Operador CAE externo que lo recibe.
/// </summary>
public enum OrigenEncargoAdministracion
{
    /// <summary>
    /// Lo registró un Actor de Plataforma TALVEG: al dar de alta el Tenant
    /// propietario, o después dentro de una Sesión Privilegiada con la capacidad
    /// de aprovisionamiento sobre ese Tenant propietario.
    /// </summary>
    AprovisionamientoDePlataforma = 1,

    /// <summary>Lo registró un Administrador miembro del Tenant propietario.</summary>
    AdministradorPropio = 2,

    /// <summary>
    /// Lo sembró la siembra de datos de demostración del piloto (decisión de Chris,
    /// 2026-10-16, opción A), en un Tenant propietario con marcador de demo y sin
    /// Administrador propio. <b>No lo registró nadie</b>: es el único origen sin
    /// <see cref="EncargoAdministracion.RegistradoPorUsuarioId"/>, y la base exige que vayan
    /// juntos (<c>CK_EncargosAdministracion_RegistranteSegunOrigen</c>). Así la fila no
    /// atribuye el acto a una persona que no lo hizo: ni a la identidad raíz de plataforma
    /// ni al Operador CAE externo que lo recibe. Ningún comando de Application lo produce:
    /// solo <see cref="EncargoAdministracion.SembrarDeDemostracion"/>, que llama la siembra
    /// (lo congela un test de arquitectura). No da autoridad propia: sube el techo igual que
    /// un encargo de cualquier otro origen.
    /// </summary>
    SiembraDeDemostracion = 3,
}
