namespace CaeManager.Application.Tenants;

/// <summary>
/// El perfil de Propiedad de una cuenta en su Tenant de origen: <c>Administrador</c>,
/// <c>DireccionCae</c> o <c>null</c>. Leído de Identity en el momento, nunca de la claim.
///
/// <para>
/// <b>Un solo consumidor: <see cref="TechoDeRolPorEncargo"/>.</b> El rol de origen no
/// autoriza ni acota (decisión P7, 2026-09-23); este puerto es la excepción acotada de
/// la decisión D-8 (2026-10-08): solo eleva dentro de un Tenant propietario que ha
/// registrado un Encargo de administración vigente a favor del Operador CAE externo de
/// la cuenta, y solo a quien ya tiene Asignación de Cartera ahí. Quien lo consuma fuera
/// de ese cálculo reabre el riesgo que P7 cerró; lo vigila
/// <c>RolDeOrigenFueraDeAutorizacionTests</c> por ruta exacta.
/// </para>
/// </summary>
public interface IPerfilDePropiedadEnOrigen
{
    /// <summary>
    /// <c>Administrador</c> o <c>DireccionCae</c> si la cuenta es miembro de
    /// <paramref name="tenantOrigenId"/>, no está desactivada y ese es su único rol;
    /// <c>null</c> en cualquier otro caso.
    /// </summary>
    Task<string?> ObtenerAsync(Guid usuarioId, Guid tenantOrigenId, CancellationToken cancellationToken = default);
}
