using CaeManager.Infrastructure.Configuracion;

namespace CaeManager.Infrastructure.Identity;

/// <summary>
/// Configuración del inicio de sesión corporativo vía Microsoft Entra ID
/// (OpenID Connect). Sin TenantId/ClientId/ClientSecret, el método de login
/// externo queda inerte (mismo patrón que Sentry/Backups/Anthropic): el
/// botón "Iniciar sesión con Microsoft" ni se muestra, y el login local
/// sigue funcionando exactamente igual que hoy — ver
/// RestriccionLoginLocalClaimsTransformation y Project-Hydra-Negocio/tecnico/ARCHITECTURE.md.
/// </summary>
public class AzureAdOptions : IOpcionesConGate
{
    public const string SeccionConfiguracion = "AzureAd";

    /// <summary>Id del tenant de Entra ID de la empresa — restringe el login a cuentas de esa organización, nunca "cualquier cuenta Microsoft".</summary>
    public string? TenantId { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public string Instance { get; set; } = "https://login.microsoftonline.com/";

    /// <summary>
    /// El tenant de <b>Hydra</b> al que pertenecen las cuentas que entran por
    /// este login (creadas antes: no hay alta automática, D-3). No es <see cref="TenantId"/>: aquel es
    /// el tenant de <i>Entra</i>, y son dos espacios de identificadores
    /// distintos que no se pueden deducir el uno del otro.
    ///
    /// <para>
    /// Sin este valor, la auto-provisión creaba el <c>ApplicationUser</c> sin
    /// <c>TenantId</c> — es decir, con <c>Guid.Empty</c>, que no es ningún
    /// tenant real (el tenant #1 es <c>…0001</c>, no ceros). La cuenta quedaba
    /// huérfana: no veía nada, no pertenecía a nadie, y aun así aparecía en la
    /// sala de espera de <c>/roles</c> para que cualquier Administrador la
    /// adoptara. Ahora, sin este valor, el callback rechaza todo inicio de
    /// sesión con Microsoft en vez de abrir cuentas, y con él tampoco crea ninguna.
    /// </para>
    ///
    /// <para>
    /// Es también el único Tenant en cuyas cuentas se puede entrar por Microsoft:
    /// una cuenta de otro Tenant con el mismo correo no se vincula ni se abre
    /// (ver <c>InicioSesionMicrosoft.DecidirCuenta</c>). No entra en el gate a
    /// propósito: sin él, el esquema sigue registrado y la restricción del login
    /// local sigue activa (fallo cerrado), el callback rechaza a todo el mundo y
    /// el arranque avisa de que falta. Meterlo en el gate levantaría en silencio
    /// esa restricción en un entorno con solo las otras tres claves.
    /// </para>
    ///
    /// <para>
    /// Decide también a quién alcanza esa restricción (D-1, 2026-10-25): solo a
    /// las cuentas cuyo Tenant propietario es este. Sin él, alcanza a todas (ver
    /// <c>RestriccionLoginLocalClaimsTransformation</c>).
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

    public bool EstaConfigurado => Evaluar().Completo;


    private EvaluacionGate Evaluar() => GateDeConfiguracion.Evaluar(null,
        (nameof(TenantId), TenantId), (nameof(ClientId), ClientId), (nameof(ClientSecret), ClientSecret));

    /// <summary>El Tenant destino declarado, o <see cref="Guid.Empty"/> si no lo hay.</summary>
    public Guid TenantDestino => TenantHydraId ?? Guid.Empty;

    /// <summary>Hay Tenant destino declarado (no nulo ni <see cref="Guid.Empty"/>).</summary>
    public bool TieneTenantDestino => TenantDestino != Guid.Empty;

    /// <summary>
    /// Los «falta X» del gate y, con las tres claves puestas pero sin
    /// <see cref="TenantHydraId"/>, también ese: el esquema queda registrado y la
    /// restricción del login local activa, pero el callback rechaza a todo el mundo.
    /// </summary>
    public IReadOnlyList<string> ProblemasDeConfiguracion()
    {
        var evaluacion = Evaluar();
        return evaluacion.Completo && !TieneTenantDestino ? ["falta TenantHydraId"] : evaluacion.Problemas;
    }
}
