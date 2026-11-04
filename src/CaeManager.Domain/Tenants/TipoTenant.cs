namespace CaeManager.Domain.Tenants;

/// <summary>
/// Tipo de Tenant propietario según quién gestiona su CAE (DM-1 a DM-7 y alta autónoma, DA-1). Es un
/// dato comercial y de oferta: no concede ninguna capacidad ni autoriza nada, y no sustituye a
/// <see cref="Tenant.PuedeActuarComoOperadorCaeExterno"/>, que sigue siendo la capacidad explícita.
/// <para>
/// Las letras P, E y O son las de los documentos de producto. En la base se guarda el nombre del valor.
/// Null en <see cref="Tenant.TipoTenant"/> significa «sin declarar»: los Tenants anteriores al alta
/// autónoma no se rellenan.
/// </para>
/// </summary>
public enum TipoTenant
{
    /// <summary>P: el Tenant propietario gestiona su CAE con su propio personal.</summary>
    GestionPropia,

    /// <summary>E: el Tenant propietario encarga la gestión de su CAE a un Operador CAE externo.</summary>
    GestionExterna,

    /// <summary>O: el Tenant es un Operador CAE externo que gestiona la CAE de otros Tenants.</summary>
    OperadorCaeExterno,
}
