using CaeManager.Domain.Configuracion;
using Microsoft.AspNetCore.DataProtection;

namespace CaeManager.Infrastructure.Autenticacion;

/// <summary>
/// Token opaco y de vida corta con el que la extensión de navegador se
/// autentica contra Hydra (MVP1 de integración con plataformas CAE).
///
/// <para>
/// <b>Por qué Data Protection y no un JWT.</b> Es el mecanismo que este
/// repositorio ya usa para exactamente esta forma de problema —
/// <c>ClienteActivoSeleccionado.Proteger</c> emite así el token de contexto
/// de workspace, con <see cref="ITimeLimitedDataProtector"/> y el mismo
/// formato de carga útil separada por <c>|</c>. Añadir una dependencia de
/// JWT traería un segundo sistema de claves que rotar, revocar y auditar
/// para no ganar nada: el token no lo consume ningún tercero, solo lo
/// devuelve quien lo recibió.
/// </para>
///
/// <para>
/// El token vincula el sello de seguridad y el identificador de la sesión interactiva.
/// Ambos se comprueban en cada petición para aplicar la revocación.
/// </para>
///
/// <para>
/// <b>Marcas de la sesión de origen (issue #1891, ASVS 4.0.3 V3.3.2).</b> También lleva la
/// hora de inicio de la sesión de origen y su última interacción conocida al emitir
/// (<see cref="MarcasSesionOrigen"/>), las mismas marcas que la cookie. Con ellas
/// <see cref="ISesionOrigenExtension"/> deja de admitir el token en cuanto la sesión de origen
/// supera su tope desde el inicio o el plazo de inactividad del Tenant propietario de la
/// cuenta. Lleva también el plazo de inactividad que regía al emitir: rige el menor entre ese
/// y el vigente, para que ampliar el plazo no reviva un token ya vencido. Usar la extensión no
/// es una interacción: no mueve ninguna de las marcas. Un token sin ellas, emitido antes de este
/// cambio, no se admite.
/// </para>
///
/// <para>
/// La vigencia es corta a propósito: el token vive en el almacenamiento de
/// una extensión, en la máquina del gestor, fuera del tarro de cookies del
/// navegador y de sus protecciones. Ocho horas cubren una jornada completa
/// sin renovar, y acotan a una jornada la ventana de un portátil perdido.
/// Es solo la cota exterior: antes vence la sesión de origen, por inactividad
/// o por su tope. No hay refresco: al caducar se vuelve a pasar por Hydra,
/// que es donde ya hay sesión iniciada.
/// </para>
/// </summary>
public static class TokenSesionExtension
{
    /// <summary>
    /// Nombre del protector. <b>No renombrar</b>: cambiarlo invalida de golpe
    /// todos los tokens vivos (mismo criterio que los protectores de
    /// credenciales de <c>CaeManagerDbContext.OnModelCreating</c>).
    /// </summary>
    private const string PropositoProtector = "CaeManager.Extension.Sesion.v1";

    public static readonly TimeSpan Vigencia = TimeSpan.FromHours(8);

    /// <summary>
    /// Emite el token. Solo debe llamarlo el endpoint de emisión, y solo tras
    /// comprobar que quien lo pide es el propio <paramref name="usuarioId"/>
    /// con sesión interactiva viva.
    /// </summary>
    /// <param name="securityStamp">
    /// El security stamp del usuario <b>en el momento de emitir</b>. Quien
    /// consume el token lo compara con el de la base: si no coinciden, el
    /// token es de antes de una invalidación y no vale.
    /// </param>
    /// <param name="marcas">
    /// Las marcas de la sesión de origen <b>vigentes al emitir</b>
    /// (<see cref="ISesionOrigenExtension.MarcasVigentes"/>).
    /// </param>
    public static string Proteger(
        IDataProtectionProvider dataProtectionProvider, Guid usuarioId, string securityStamp, Guid sid, MarcasSesionOrigen marcas)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(securityStamp);
        ArgumentNullException.ThrowIfNull(marcas);
        if (sid == Guid.Empty) throw new ArgumentException("Identificador de sesión vacío.", nameof(sid));
        var minutos = marcas.PlazoInactividad.TotalMinutes;
        if (minutos != Math.Floor(minutos) || minutos > int.MaxValue
            || !ParametroSistema.MinutosInactividadSesionValidos((int)minutos))
            throw new ArgumentException("Plazo de inactividad fuera de rango.", nameof(marcas));

        return CrearProtector(dataProtectionProvider).Protect(
            $"{usuarioId:N}|{securityStamp}|{sid:N}|{Sello(marcas.Inicio)}|{Sello(marcas.UltimaInteraccion)}|{(int)minutos}", Vigencia);
    }

    /// <summary>
    /// Devuelve la carga útil, o <c>null</c> si el token está caducado,
    /// manipulado, cifrado con una clave que ya no existe o simplemente mal
    /// formado. Los cuatro casos se tratan igual a propósito: quien llama no
    /// debe poder distinguirlos, y ninguno concede acceso.
    /// </summary>
    public static CargaUtilTokenExtension? Leer(IDataProtectionProvider dataProtectionProvider, string token)
    {
        string desprotegido;
        try
        {
            desprotegido = CrearProtector(dataProtectionProvider).Unprotect(token);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Es también el camino de la caducidad: ITimeLimitedDataProtector
            // señala un token vencido con esta misma excepción.
            return null;
        }

        return ParsearCargaUtil(desprotegido);
    }

    /// <summary>
    /// La mitad de <see cref="Leer"/> que no es criptografía: interpretar la
    /// carga útil ya descifrada. Pública para poder probar cada rama de
    /// rechazo sin fabricar tokens —el propósito del protector es privado a
    /// propósito— y porque aceptar una carga malformada aquí sería conceder
    /// acceso, no un error de formato.
    /// </summary>
    public static CargaUtilTokenExtension? ParsearCargaUtil(string cargaUtil)
    {
        // El security stamp de Identity es Base32 (sin '|'), así que seis
        // partes exactas es la única forma válida. La de tres, sin las marcas de
        // la sesión de origen, es la de antes del issue #1891 y no vale.
        var partes = cargaUtil.Split('|');
        if (partes.Length != 6)
            return null;

        if (!Guid.TryParseExact(partes[0], "N", out var usuarioId) || usuarioId == Guid.Empty)
            return null;

        if (string.IsNullOrWhiteSpace(partes[1]))
            return null;

        if (!Guid.TryParseExact(partes[2], "N", out var sid) || sid == Guid.Empty)
            return null;

        if (LeerSello(partes[3]) is not { } inicio || LeerSello(partes[4]) is not { } ultimaInteraccion)
            return null;

        if (!int.TryParse(partes[5], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var minutos)
            || !ParametroSistema.MinutosInactividadSesionValidos(minutos))
            return null;

        return new CargaUtilTokenExtension(
            usuarioId, partes[1], sid, new MarcasSesionOrigen(inicio, ultimaInteraccion, TimeSpan.FromMinutes(minutos)));
    }

    private static string Sello(DateTimeOffset momento) =>
        momento.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static DateTimeOffset? LeerSello(string sello) =>
        long.TryParse(sello, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var segundos)
        && segundos <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
            ? DateTimeOffset.FromUnixTimeSeconds(segundos)
            : null;

    private static ITimeLimitedDataProtector CrearProtector(IDataProtectionProvider dataProtectionProvider) =>
        dataProtectionProvider.CreateProtector(PropositoProtector).ToTimeLimitedDataProtector();
}

/// <summary>Contenido verificado de un token de extensión — ver <see cref="TokenSesionExtension"/>.</summary>
public record CargaUtilTokenExtension(Guid UsuarioId, string SecurityStamp, Guid Sid, MarcasSesionOrigen Marcas);
