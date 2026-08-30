namespace CaeManager.Infrastructure.Identity;

/// <summary>
/// Configuración del inicio de sesión corporativo vía Microsoft Entra ID
/// (OpenID Connect). Sin TenantId/ClientId/ClientSecret, el método de login
/// externo queda inerte (mismo patrón que Sentry/Backups/Anthropic): el
/// botón "Iniciar sesión con Microsoft" ni se muestra, y el login local
/// sigue funcionando exactamente igual que hoy — ver
/// RestriccionLoginLocalClaimsTransformation y ARCHITECTURE.md.
/// </summary>
public class AzureAdOptions
{
    public const string SeccionConfiguracion = "AzureAd";

    /// <summary>Id del tenant de Entra ID de la empresa — restringe el login a cuentas de esa organización, nunca "cualquier cuenta Microsoft".</summary>
    public string? TenantId { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public string Instance { get; set; } = "https://login.microsoftonline.com/";

    /// <summary>
    /// El tenant de <b>Hydra</b> al que pertenecen las cuentas que se
    /// auto-provisionan por este login. No es <see cref="TenantId"/>: aquel es
    /// el tenant de <i>Entra</i>, y son dos espacios de identificadores
    /// distintos que no se pueden deducir el uno del otro.
    ///
    /// <para>
    /// Sin este valor, la auto-provisión creaba el <c>ApplicationUser</c> sin
    /// <c>TenantId</c> — es decir, con <c>Guid.Empty</c>, que no es ningún
    /// tenant real (el tenant #1 es <c>…0001</c>, no ceros). La cuenta quedaba
    /// huérfana: no veía nada, no pertenecía a nadie, y aun así aparecía en la
    /// sala de espera de <c>/roles</c> para que cualquier Administrador la
    /// adoptara. Ahora, sin este valor configurado, el alta se rechaza en vez
    /// de crear esa cuenta ambigua.
    /// </para>
    ///
    /// <para>
    /// Un único tenant destino es la forma correcta <b>mientras</b>
    /// <see cref="TenantId"/> restrinja el login a una sola organización de
    /// Entra, que es el diseño de hoy. Admitir varias organizaciones exigiría
    /// un mapeo emisor/<c>tid</c> → tenant de Hydra, y esa es una decisión de
    /// producto que no se puede inventar aquí.
    /// </para>
    /// </summary>
    public Guid? TenantHydraId { get; set; }

    public bool EstaConfigurado =>
        !string.IsNullOrWhiteSpace(TenantId) && !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
