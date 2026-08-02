using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;

namespace CaeManager.Application.Integraciones;

/// <summary>
/// Refresca el access token de Graph de una ConexionIntegracion a partir de
/// su refresh token guardado, y actualiza (sin guardar) el refresh token si
/// Graph devolvió uno nuevo — Graph puede rotarlo en cada canje. No llama a
/// <c>SaveChangesAsync</c>: el Command que invoca esto decide cuándo
/// confirmar, para que el refresh y el resto de su trabajo se guarden como
/// una única transacción.
/// </summary>
public class AccesoGraphService(ICredencialIntegracionRepository credencialRepositorio, IMicrosoft365GraphClient graphClient)
{
    public async Task<Result<string>> ObtenerAccessTokenVigenteAsync(Guid conexionIntegracionId, CancellationToken cancellationToken)
    {
        var credencial = await credencialRepositorio.ObtenerPorConexionAsync(conexionIntegracionId, cancellationToken);
        if (credencial is null)
            return Result.Fallo<string>(Error.Crear(
                "ConexionIntegracion.SinCredencial", "Esta conexión no tiene credenciales configuradas."));

        var resultado = await graphClient.RefrescarTokensAsync(credencial.RefreshToken, cancellationToken);
        if (resultado.EsFallido)
            return Result.Fallo<string>(resultado.Error);

        credencial.ActualizarRefreshToken(resultado.Valor.RefreshToken);
        return Result.Exito(resultado.Valor.AccessToken);
    }
}
