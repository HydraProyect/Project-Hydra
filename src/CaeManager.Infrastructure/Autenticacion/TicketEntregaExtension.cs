using Microsoft.AspNetCore.DataProtection;

namespace CaeManager.Infrastructure.Autenticacion;

/// <summary>
/// Autorización de vida corta para recoger el archivo de UNA Acreditación pendiente desde la extensión de navegador.
/// Separa el «¿puede salir?» del «dame el contenido»: el ticket se emite después de comprobar cartera, estado, kill
/// switch y portal, y queda ligado al usuario, al Tenant, a la Asignación de Operación (si es un Tenant beneficiario), a
/// la Acreditación, al Documento y al host del portal.
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
///
/// <para>
/// <b>Relevo entre versiones.</b> Se emite siempre el propósito vigente y al recoger se leen el vigente y el anterior:
/// un ticket emitido por el binario saliente sigue valiendo en el entrante. Al revés no hay arreglo posible desde aquí:
/// el binario saliente no conoce un propósito posterior, así que un ticket del entrante que recoja el saliente responde
/// 404. La extensión lo muestra como fallo de recogida y volver a intentarlo emite otro ticket.
/// </para>
/// </summary>
public static class TicketEntregaExtension
{
    /// <summary>
    /// Nombre del protector con el que se emite. v2: la carga añade el Tenant y la Asignación de Operación.
    /// </summary>
    private const string PropositoProtector = "CaeManager.Extension.EntregaArchivo.v2";

    /// <summary>
    /// Solo se lee, nunca se emite: lo emitía el binario anterior a la Asignación de Operación en el ticket, que solo
    /// operaba en el Tenant de origen. Su carga no lleva Tenant ni Asignación de Operación y no se inventan:
    /// <see cref="ParsearCargaUtilAnterior"/> los deja vacíos y solo vale en el Tenant de origen de ese usuario. Se retira
    /// cuando no quede por desplegar ningún relevo desde ese binario: un ticket v1 vive un minuto.
    /// </summary>
    private const string PropositoProtectorAnterior = "CaeManager.Extension.EntregaArchivo.v1";

    public static readonly TimeSpan Vigencia = TimeSpan.FromSeconds(60);

    public static string Proteger(
        IDataProtectionProvider dataProtectionProvider, Guid usuarioId, Guid tenantId, Guid? asignacionOperacionId,
        Guid acreditacionId, Guid documentoId, string hostPortal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPortal);

        return CrearProtector(dataProtectionProvider, PropositoProtector).Protect(
            $"{usuarioId:N}|{tenantId:N}|{asignacionOperacionId ?? Guid.Empty:N}|{acreditacionId:N}|{documentoId:N}|{hostPortal}",
            Vigencia);
    }

    /// <summary>
    /// La carga útil, o <c>null</c> si el ticket está caducado, manipulado o mal formado: quien llama no debe poder
    /// distinguirlos, y ninguno entrega nada.
    /// </summary>
    public static CargaUtilTicketEntrega? Leer(IDataProtectionProvider dataProtectionProvider, string? ticket)
    {
        if (string.IsNullOrWhiteSpace(ticket))
            return null;

        return Leer(dataProtectionProvider, PropositoProtector, ticket, ParsearCargaUtil)
            ?? Leer(dataProtectionProvider, PropositoProtectorAnterior, ticket, ParsearCargaUtilAnterior);
    }

    private static CargaUtilTicketEntrega? Leer(
        IDataProtectionProvider dataProtectionProvider, string proposito, string ticket,
        Func<string, CargaUtilTicketEntrega?> parsear)
    {
        try
        {
            return parsear(CrearProtector(dataProtectionProvider, proposito).Unprotect(ticket));
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null; // también la caducidad, como en TokenSesionExtension.Leer.
        }
    }

    /// <summary>La mitad de <see cref="Leer(IDataProtectionProvider, string?)"/> que no es criptografía; pública para probar cada rama de rechazo.</summary>
    public static CargaUtilTicketEntrega? ParsearCargaUtil(string cargaUtil)
    {
        var partes = cargaUtil.Split('|');
        if (partes.Length != 6)
            return null;

        // La Asignación de Operación va vacía en el Tenant de origen; el resto no puede estarlo.
        if (!Guid.TryParseExact(partes[0], "N", out var usuarioId) || usuarioId == Guid.Empty
            || !Guid.TryParseExact(partes[1], "N", out var tenantId) || tenantId == Guid.Empty
            || !Guid.TryParseExact(partes[2], "N", out var asignacionOperacionId)
            || !Guid.TryParseExact(partes[3], "N", out var acreditacionId) || acreditacionId == Guid.Empty
            || !Guid.TryParseExact(partes[4], "N", out var documentoId) || documentoId == Guid.Empty
            || string.IsNullOrWhiteSpace(partes[5]))
            return null;

        return new CargaUtilTicketEntrega(
            usuarioId, tenantId, asignacionOperacionId == Guid.Empty ? null : asignacionOperacionId,
            acreditacionId, documentoId, partes[5]);
    }

    /// <summary>La carga de <see cref="PropositoProtectorAnterior"/>: <c>usuario|acreditación|documento|host</c>.</summary>
    public static CargaUtilTicketEntrega? ParsearCargaUtilAnterior(string cargaUtil)
    {
        var partes = cargaUtil.Split('|');
        if (partes.Length != 4)
            return null;

        if (!Guid.TryParseExact(partes[0], "N", out var usuarioId) || usuarioId == Guid.Empty
            || !Guid.TryParseExact(partes[1], "N", out var acreditacionId) || acreditacionId == Guid.Empty
            || !Guid.TryParseExact(partes[2], "N", out var documentoId) || documentoId == Guid.Empty
            || string.IsNullOrWhiteSpace(partes[3]))
            return null;

        return new CargaUtilTicketEntrega(usuarioId, null, null, acreditacionId, documentoId, partes[3]);
    }

    private static ITimeLimitedDataProtector CrearProtector(IDataProtectionProvider dataProtectionProvider, string proposito) =>
        dataProtectionProvider.CreateProtector(proposito).ToTimeLimitedDataProtector();
}

/// <summary>Contenido verificado de un ticket de entrega — ver <see cref="TicketEntregaExtension"/>.</summary>
/// <param name="TenantId">
/// El Tenant con el que se emitió, o <c>null</c> en un ticket del propósito anterior, que no lo lleva: esos solo valen en
/// el Tenant de origen del usuario. Un <c>null</c> nunca significa «cualquier Tenant».
/// </param>
public record CargaUtilTicketEntrega(
    Guid UsuarioId, Guid? TenantId, Guid? AsignacionOperacionId, Guid AcreditacionId, Guid DocumentoId, string HostPortal);
