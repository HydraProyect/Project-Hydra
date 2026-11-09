using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;

namespace CaeManager.Application.Integraciones;

/// <summary>
/// Devuelve el access token vigente de una ConexionIntegracion — cacheado en
/// <see cref="CredencialIntegracion"/> mientras no esté a punto de expirar
/// (auditoría módulo 6); solo si hace falta, refresca contra Graph a partir
/// del refresh token guardado y actualiza (sin guardar) tanto el refresh
/// token como la caché del access token — Graph puede rotar el primero en
/// cada canje. No llama a <c>SaveChangesAsync</c>: el Command que invoca
/// esto decide cuándo confirmar, para que el refresh y el resto de su
/// trabajo se guarden como una única transacción.
///
/// Cachear no es solo una optimización: cuantas menos veces se rota el
/// refresh token, más pequeña es la ventana en la que dos operaciones
/// concurrentes sobre la misma conexión pueden competir por el mismo
/// refresco (ver <see cref="CredencialIntegracion.Version"/>).
///
/// Un buzón compartido (<see cref="ConexionIntegracion.EsBuzonCompartido"/>) no tiene credencial: se abre
/// con el token de aplicación de su directorio, y por eso todo el que tenga la conexión a mano debe pedir
/// el token con la sobrecarga que la recibe. La que recibe solo el Id falla cerrada ante un compartido
/// (<c>ConexionIntegracion.SinCredencial</c>): nunca cae en el token de una persona.
/// </summary>
public class AccesoGraphService(ICredencialIntegracionRepository credencialRepositorio, IMicrosoft365GraphClient graphClient)
{
    public async Task<Result<string>> ObtenerAccessTokenVigenteAsync(ConexionIntegracion conexion, CancellationToken cancellationToken)
    {
        if (!conexion.EsBuzonCompartido)
            return await ObtenerAccessTokenVigenteAsync(conexion.Id, cancellationToken);

        if (conexion.DirectorioEntraId is not { } directorio)
            return Result.Fallo<string>(Error.Crear(
                "ConexionIntegracion.SinDirectorio", "Este buzón compartido no tiene directorio de Microsoft."));

        var token = await graphClient.ObtenerTokenAplicacionAsync(directorio, cancellationToken);
        return token.EsFallido ? Result.Fallo<string>(token.Error) : Result.Exito(token.Valor.AccessToken);
    }

    /// <summary>
    /// Token delegado de la credencial de una conexión. <c>internal</c> a propósito: fuera de Application nadie
    /// puede pedir un token sin pasar la conexión, que es lo que decide entre credencial delegada y token de
    /// aplicación; un llamador que volviese al Id dejaría de compilar en vez de dejar sin ingesta, en silencio,
    /// a los buzones compartidos.
    /// </summary>
    private async Task<Result<string>> ObtenerAccessTokenVigenteAsync(Guid conexionIntegracionId, CancellationToken cancellationToken)
    {
        var credencial = await credencialRepositorio.ObtenerPorConexionAsync(conexionIntegracionId, cancellationToken);
        if (credencial is null)
            return Result.Fallo<string>(Error.Crear(
                "ConexionIntegracion.SinCredencial", "Esta conexión no tiene credenciales configuradas."));

        if (credencial.TieneAccessTokenVigente(DateTime.UtcNow))
            return Result.Exito(credencial.AccessToken!);

        var resultado = await graphClient.RefrescarTokensAsync(credencial.RefreshToken, cancellationToken);
        if (resultado.EsFallido)
            return Result.Fallo<string>(resultado.Error);

        credencial.ActualizarRefreshToken(resultado.Valor.RefreshToken);
        credencial.ActualizarAccessTokenCacheado(resultado.Valor.AccessToken, resultado.Valor.AccessTokenExpiraUtc);
        return Result.Exito(resultado.Valor.AccessToken);
    }
}
