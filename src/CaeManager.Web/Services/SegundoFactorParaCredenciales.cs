using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Services;

/// <summary>
/// A dónde va una pantalla cuando <c>SegundoFactorRequeridoParaCredencialesException</c>
/// le dice que el usuario no puede leer ni escribir datos de credencial del Tenant
/// propietario porque no tiene la autenticación en dos pasos activa (P1-I1, P1-I2).
/// La regla vive en <c>AutorizacionSecretosDeTenantBehavior</c>; esto solo es la
/// ruta, con un <c>motivo</c> para que <c>ConfigurarAutenticadorDosFactores</c>
/// explique por qué se le pide y un <c>returnUrl</c> para devolverle a la ficha de
/// la que venía. Se navega con <c>forceLoad</c>: esa página es estática (AuthLayout).
/// </summary>
public static class SegundoFactorParaCredenciales
{
    public const string Motivo = "credenciales";

    public const string ParametroVuelta = "returnUrl";

    public const string RutaConfigurar = "/cuenta/configurar-2fa?motivo=" + Motivo;

    /// <summary>
    /// <see cref="RutaConfigurar"/> con la ruta local a la que volver. Una ruta que
    /// no sea local se descarta aquí y otra vez al leerla (<see cref="EsRutaLocal"/>).
    /// </summary>
    public static string RutaConfigurarVolviendoA(string? rutaDeVuelta) =>
        EsRutaLocal(rutaDeVuelta)
            ? RutaConfigurar + "&" + ParametroVuelta + "=" + Uri.EscapeDataString(rutaDeVuelta!)
            : RutaConfigurar;

    /// <summary>La ruta y la consulta de la pantalla actual, sin esquema ni host.</summary>
    public static string RutaActual(NavigationManager navigationManager) =>
        new Uri(navigationManager.Uri).PathAndQuery;

    public static void IrAConfigurar(NavigationManager navigationManager) =>
        navigationManager.NavigateTo(RutaConfigurarVolviendoA(RutaActual(navigationManager)), forceLoad: true);

    /// <summary>
    /// Solo rutas de esta misma aplicación: empiezan por una única <c>/</c>, sin
    /// <c>//</c> ni <c>/\</c> (que el navegador lee como otro host), sin barras
    /// invertidas y sin caracteres de control. Es la regla de
    /// <c>IUrlHelper.IsLocalUrl</c>, sin la forma <c>~/</c>. Cualquier otra cosa
    /// —URL absoluta, relativa al protocolo, <c>javascript:</c>— no es una vuelta
    /// sino una redirección abierta, y se sustituye por el inicio.
    /// </summary>
    public static bool EsRutaLocal(string? ruta) =>
        !string.IsNullOrEmpty(ruta)
        && ruta[0] == '/'
        && (ruta.Length == 1 || (ruta[1] != '/' && ruta[1] != '\\'))
        && !ruta.Contains('\\')
        && !ruta.Any(char.IsControl);
}
