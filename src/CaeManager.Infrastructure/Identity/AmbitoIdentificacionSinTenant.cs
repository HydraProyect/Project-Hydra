namespace CaeManager.Infrastructure.Identity;

/// <summary>
/// Declara que el código que corre dentro es un punto de entrada de identificación
/// que necesita encontrar una cuenta ANTES de que exista Tenant: el formulario de
/// login, la verificación en dos pasos, la recuperación de contraseña, la vuelta del
/// SSO de Microsoft, la validación del sello de seguridad de la cookie y el token de
/// la extensión (P1-M1).
///
/// <para>
/// <c>AspNetUsers</c> tiene RLS por Tenant (migración <c>RlsAspNetUsers</c>) y, sin
/// Tenant en el contexto, no deja ver ninguna fila: fallo cerrado. Dentro de este
/// ámbito, y solo mientras <see cref="Application.Common.ITenantActual"/> siga sin
/// Tenant, <see cref="AlmacenUsuarios"/> resuelve el Tenant de la cuenta buscada
/// con una función <c>SECURITY DEFINER</c> que devuelve ese dato y nada más, y lee
/// la fila dentro de <see cref="Application.Common.AmbitoTenantExplicito"/>, bajo
/// la política de su propio Tenant.
/// </para>
///
/// <para>
/// <b>Es una declaración explícita, no el estado por defecto.</b> Código sin Tenant
/// que no abra este ámbito —un job de fondo o un seeder que olvidó su
/// <c>AmbitoTenantExplicito</c>— sigue sin ver ninguna cuenta. Por eso no se abre en
/// un middleware para toda petición anónima: cada punto de entrada lo abre donde lo
/// necesita, y una búsqueda por el nombre del tipo enumera todos.
/// </para>
///
/// <para>
/// Basado en <see cref="AsyncLocal{T}"/>, como <c>AmbitoTenantExplicito</c>: fluye por
/// las llamadas <c>await</c> anidadas dentro del <c>using</c> que lo abre.
/// </para>
/// </summary>
public static class AmbitoIdentificacionSinTenant
{
    private static readonly AsyncLocal<bool> _abierto = new();

    public static bool Abierto => _abierto.Value;

    public static IDisposable Abrir()
    {
        var anterior = _abierto.Value;
        _abierto.Value = true;
        return new Restaurador(anterior);
    }

    private sealed class Restaurador(bool valorAnterior) : IDisposable
    {
        public void Dispose() => _abierto.Value = valorAnterior;
    }
}
