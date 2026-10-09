namespace CaeManager.Domain.Tenants;

/// <summary>
/// Quién registró el <see cref="EncargoAdministracion"/>. Solo hay dos vías, y
/// ninguna pasa por el Operador CAE externo que lo recibe.
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
}
