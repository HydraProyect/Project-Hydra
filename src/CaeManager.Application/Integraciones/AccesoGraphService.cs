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
/// </summary>
public class AccesoGraphService(ICredencialIntegracionRepository credencialRepositorio, IMicrosoft365GraphClient graphClient)
{
    public async Task<Result<string>> ObtenerAccessTokenVigenteAsync(Guid conexionIntegracionId, CancellationToken cancellationToken)
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
