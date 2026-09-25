namespace CaeManager.Web.Services;

/// <summary>
/// A dónde va una pantalla cuando <c>SegundoFactorRequeridoParaCredencialesException</c>
/// le dice que el usuario no puede leer datos de credencial del Tenant propietario
/// porque no tiene la autenticación en dos pasos activa (P1-I1). La regla vive en
/// <c>AutorizacionSecretosDeTenantBehavior</c>; esto solo es la ruta, con un
/// <c>motivo</c> para que <c>ConfigurarAutenticadorDosFactores</c> explique por qué
/// se le pide. Se navega con <c>forceLoad</c>: esa página es estática (AuthLayout).
/// </summary>
public static class SegundoFactorParaCredenciales
{
    public const string Motivo = "credenciales";

    public const string RutaConfigurar = "/cuenta/configurar-2fa?motivo=" + Motivo;
}
