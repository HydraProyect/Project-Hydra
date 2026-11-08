using CaeManager.Domain.Common;

namespace CaeManager.Application.Integraciones;

/// <summary>
/// Buzón listo para <c>ConectarBuzonMicrosoft365Command</c>: la dirección que se conecta, el token con el que se
/// suscribe, el refresh token que se guarda (null en un compartido) y su identidad estable.
/// </summary>
public sealed record BuzonMicrosoft365Preparado(
    string BuzonEmail, string AccessToken, string? RefreshToken, string? DirectorioEntraId, string IdentidadBuzon);

/// <summary>
/// Orquestación del alta de un buzón de Microsoft 365 tras el canje del código OAuth (callback de
/// <c>ConectarMicrosoft365Endpoints</c>). Vive aquí, y no en el endpoint, para que sus garantías tengan test:
/// <list type="bullet">
/// <item>el buzón propio sale de <c>/me</c> y se abre con el token delegado, que se guarda;</item>
/// <item>el compartido se abre con el token de aplicación del directorio de quien inició sesión, que sale de
/// <c>/organization</c> con su token delegado y nunca de un campo; el refresh token de esa persona no se guarda;</item>
/// <item>la identidad estable del buzón (que es la clave de la reclamación global) se lee con el mismo token
/// que lo abre, y en el compartido esa lectura es además la sonda de que está dentro del ámbito de RBAC for
/// Applications: si falla, no hay alta;</item>
/// <item>escribir la propia dirección en el campo del compartido conecta el buzón propio.</item>
/// </list>
/// </summary>
public class AltaBuzonMicrosoft365Service(IMicrosoft365GraphClient graphClient)
{
    /// <summary>Fallo de autenticación o de lectura en Microsoft: el alta se repite desde el principio.</summary>
    public const string ErrorAutenticacion = "AltaBuzon.Autenticacion";

    /// <summary>El directorio no da token de aplicación o el compartido está fuera de su ámbito de Exchange.</summary>
    public const string ErrorSinAccesoCompartido = "AltaBuzon.SinAccesoCompartido";

    public async Task<Result<BuzonMicrosoft365Preparado>> PrepararAsync(
        TokensGraphDto tokensDelegados, string? buzonCompartido, CancellationToken cancellationToken)
    {
        var propio = await graphClient.ObtenerBuzonEmailAsync(tokensDelegados.AccessToken, cancellationToken);
        if (propio.EsFallido)
            return Fallo(ErrorAutenticacion);

        var esCompartido = buzonCompartido is not null &&
            !string.Equals(buzonCompartido, propio.Valor, StringComparison.OrdinalIgnoreCase);

        if (!esCompartido)
        {
            var identidadPropia = await graphClient.ObtenerIdentidadBuzonAsync(
                tokensDelegados.AccessToken, new BuzonGraph(propio.Valor, EsCompartido: false), cancellationToken);
            return identidadPropia.EsFallido
                ? Fallo(ErrorAutenticacion)
                : Result.Exito(new BuzonMicrosoft365Preparado(
                    propio.Valor, tokensDelegados.AccessToken, tokensDelegados.RefreshToken, null, identidadPropia.Valor));
        }

        var directorio = await graphClient.ObtenerDirectorioAsync(tokensDelegados.AccessToken, cancellationToken);
        if (directorio.EsFallido)
            return Fallo(ErrorAutenticacion);

        var tokenAplicacion = await graphClient.ObtenerTokenAplicacionAsync(directorio.Valor, cancellationToken);
        if (tokenAplicacion.EsFallido)
            return Fallo(ErrorSinAccesoCompartido);

        var identidad = await graphClient.ObtenerIdentidadBuzonAsync(
            tokenAplicacion.Valor.AccessToken, new BuzonGraph(buzonCompartido!, EsCompartido: true), cancellationToken);
        if (identidad.EsFallido)
            return Fallo(identidad.Error.Codigo == CodigosErrorMicrosoft365.SinAccesoBuzon ? ErrorSinAccesoCompartido : ErrorAutenticacion);

        return Result.Exito(new BuzonMicrosoft365Preparado(
            buzonCompartido!, tokenAplicacion.Valor.AccessToken, null, directorio.Valor, identidad.Valor));
    }

    private static Result<BuzonMicrosoft365Preparado> Fallo(string codigo) =>
        Result.Fallo<BuzonMicrosoft365Preparado>(Error.Crear(codigo, "No pudimos conectar el buzón de Microsoft 365."));
}
