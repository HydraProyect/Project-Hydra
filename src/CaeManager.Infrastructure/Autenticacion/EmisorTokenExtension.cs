using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Autenticacion;

/// <summary>
/// La comprobación y la emisión de <see cref="TokenSesionExtension"/>,
/// extraídas de <c>ExtensionTokenEndpoints</c> (Incremento 1, PR #484) para
/// que también las use la página Blazor "Conectar extensión"
/// (<c>/cuenta/extension</c>, Incremento 4) sin repetirlas — ambas exigen
/// exactamente lo mismo: sesión interactiva ya resuelta, ninguna Sesión Privilegiada
/// seleccionada y una cuenta con security stamp. Se queda en Infrastructure, no en Application, porque ya
/// depende directamente de <see cref="UserManager{TUser}"/> y
/// <see cref="IDataProtectionProvider"/> — el mismo criterio por el que el
/// endpoint original tampoco pasaba por Application.
/// </summary>
public static class EmisorTokenExtension
{
    public static async Task<Result<(string Token, DateTime ExpiraEnUtc)>> EmitirAsync(
        Guid usuarioId, Guid? sid, UserManager<ApplicationUser> userManager, IDataProtectionProvider dataProtectionProvider,
        IRegistroSesionesUsuario sesiones, IClienteActivoSeleccionado seleccion, ILoggerFactory fabricaRegistro)
    {
        // Ninguna guarda del pipeline alcanza esta vía: las dos entradas llaman aquí sin pasar por MediatR
        // (OWASP ASVS 4.0.3 V4.1.3, V3.5). La señal es la coordenada en bruto y no ISesionPrivilegiadaActual,
        // por el mismo motivo que ObtenerConexionesIntegracionQuery: es con la que TenantRlsConnectionInterceptor
        // adopta el rol de soporte, y no distingue capacidades, así que una impersonación queda igual de denegada.
        // Va antes de cualquier lectura de la sesión de usuario, la cuenta o el sello. El aviso nombra al Actor
        // real; el Usuario simulado, si lo hay, consta en la Sesión Privilegiada citada y no se le atribuye nada.
        // Es coherencia de la Sesión Privilegiada seleccionada, no una barrera frente al Actor real: sin esa selección
        // (sin la cookie, o en un circuito abierto antes de seleccionarla) obtiene un token de su propia cuenta y nunca
        // de otra, porque usuarioId sale siempre del principal autenticado.
        if (seleccion.SesionPrivilegiadaIdSeleccionada is { } sesionPrivilegiadaId)
        {
            fabricaRegistro.CreateLogger(typeof(EmisorTokenExtension)).LogWarning(
                "Emisión del token de la extensión denegada: Actor real {ActorRealId} en la Sesión Privilegiada {SesionPrivilegiadaId}.",
                usuarioId, sesionPrivilegiadaId);

            return Result.Fallo<(string, DateTime)>(Error.Crear(
                "Autorizacion.SesionPrivilegiadaSoloLectura",
                "No se puede conectar la extensión durante una Sesión Privilegiada. Cierra la Sesión Privilegiada para continuar."));
        }

        if (sid is null || sid == Guid.Empty || !await sesiones.EstaVigenteAsync(usuarioId, sid.Value))
            return Result.Fallo<(string, DateTime)>(Error.Crear("Extension.SesionNoVigente", "La sesión ya no está vigente. Vuelve a iniciar sesión."));

        var usuario = await userManager.FindByIdAsync(usuarioId.ToString());
        if (usuario is null)
            return Result.Fallo<(string, DateTime)>(Error.Crear("Extension.UsuarioNoEncontrado", "No encontramos el usuario."));

        // Un usuario sin security stamp no puede recibir token: sin él, el
        // handler no tendría con qué revocarlo. Se rechaza en vez de
        // generarlo aquí — emitir un stamp es un efecto sobre la cuenta, y
        // esto no está para tocar cuentas.
        if (string.IsNullOrWhiteSpace(usuario.SecurityStamp))
            return Result.Fallo<(string, DateTime)>(Error.Crear(
                "Extension.CuentaAMedioActivar",
                "La cuenta no puede conectar la extensión todavía. Cambia la contraseña para completar su activación."));

        var token = TokenSesionExtension.Proteger(dataProtectionProvider, usuarioId, usuario.SecurityStamp, sid.Value);
        return Result.Exito((token, DateTime.UtcNow.Add(TokenSesionExtension.Vigencia)));
    }
}
