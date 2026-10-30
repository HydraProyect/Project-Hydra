using Microsoft.AspNetCore.DataProtection;

namespace CaeManager.Infrastructure.Autenticacion;

/// <summary>
/// Autorización de vida corta para recoger el archivo de UNA Acreditación pendiente desde la extensión de navegador.
/// Separa el «¿puede salir?» del «dame el contenido»: el ticket se emite después de comprobar cartera, estado, kill
/// switch y portal, y queda ligado al usuario, a la Acreditación, al Documento y al host del portal.
///
/// <para>
/// Mismo mecanismo que <see cref="TokenSesionExtension"/> y con su propio propósito, así que un token de sesión no vale
/// como ticket ni al revés. Un minuto cubre la recogida inmediata que hace la extensión y nada más: quien intercepte un
/// ticket además necesita el token de sesión de ese mismo usuario.
/// </para>
///
/// <para>
/// No es de un solo uso (no hay estado en el servidor): dentro de su minuto se puede repetir la recogida. Cada una pasa
/// otra vez las comprobaciones, el límite de ritmo y el registro de acceso.
/// </para>
/// </summary>
public static class TicketEntregaExtension
{
    /// <summary>Nombre del protector. Cambiarlo invalida los tickets vivos, que duran un minuto.</summary>
    private const string PropositoProtector = "CaeManager.Extension.EntregaArchivo.v1";

    public static readonly TimeSpan Vigencia = TimeSpan.FromSeconds(60);

    public static string Proteger(
        IDataProtectionProvider dataProtectionProvider, Guid usuarioId, Guid acreditacionId, Guid documentoId, string hostPortal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPortal);

        return CrearProtector(dataProtectionProvider).Protect(
            $"{usuarioId:N}|{acreditacionId:N}|{documentoId:N}|{hostPortal}", Vigencia);
    }

    /// <summary>
    /// La carga útil, o <c>null</c> si el ticket está caducado, manipulado o mal formado: quien llama no debe poder
    /// distinguirlos, y ninguno entrega nada.
    /// </summary>
    public static CargaUtilTicketEntrega? Leer(IDataProtectionProvider dataProtectionProvider, string? ticket)
    {
        if (string.IsNullOrWhiteSpace(ticket))
            return null;

        try
        {
            return ParsearCargaUtil(CrearProtector(dataProtectionProvider).Unprotect(ticket));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null; // también la caducidad, como en TokenSesionExtension.Leer.
        }
    }

    /// <summary>La mitad de <see cref="Leer"/> que no es criptografía; pública para probar cada rama de rechazo.</summary>
    public static CargaUtilTicketEntrega? ParsearCargaUtil(string cargaUtil)
    {
        var partes = cargaUtil.Split('|');
        if (partes.Length != 4)
            return null;

        if (!Guid.TryParseExact(partes[0], "N", out var usuarioId) || usuarioId == Guid.Empty
            || !Guid.TryParseExact(partes[1], "N", out var acreditacionId) || acreditacionId == Guid.Empty
            || !Guid.TryParseExact(partes[2], "N", out var documentoId) || documentoId == Guid.Empty
            || string.IsNullOrWhiteSpace(partes[3]))
            return null;

        return new CargaUtilTicketEntrega(usuarioId, acreditacionId, documentoId, partes[3]);
    }

    private static ITimeLimitedDataProtector CrearProtector(IDataProtectionProvider dataProtectionProvider) =>
        dataProtectionProvider.CreateProtector(PropositoProtector).ToTimeLimitedDataProtector();
}

/// <summary>Contenido verificado de un ticket de entrega — ver <see cref="TicketEntregaExtension"/>.</summary>
public record CargaUtilTicketEntrega(Guid UsuarioId, Guid AcreditacionId, Guid DocumentoId, string HostPortal);
