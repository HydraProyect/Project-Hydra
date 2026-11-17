using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.WebUtilities;

namespace CaeManager.Web.Services;

/// <summary>
/// La selección de contexto vigente de cada sesión de navegador, vista desde el servidor (D-6 b, R-2; propuesta de
/// buzones de varios Tenants § 5.4). La cookie de contexto es del navegador y la comparten todas sus pestañas, pero
/// un circuito de Blazor Server ya abierto no la vuelve a ver: memoiza la de su negociación y conserva ese Tenant
/// (<see cref="ClienteActivoSeleccionado"/>, <see cref="RevalidacionCircuitoActivoHandler"/>). Este registro es lo
/// que le permite a <see cref="GuardaSeleccionVigente"/> saber que otra pestaña cambió la selección.
///
/// <para>
/// <b>Versión de selección.</b> La huella de cada emisión de la cookie: el token protegido cambia en cada emisión,
/// así que dos selecciones distintas —también la misma Empresa gestionada elegida otra vez— nunca comparten versión.
/// La huella es SHA-256 truncada y no permite reconstruir el token. La vuelta al Tenant de origen, sin cookie, es
/// <see cref="VersionOrigen"/>.
/// </para>
///
/// <para>
/// <b>Qué no es.</b> No autoriza nada: la autorización sigue en <c>AutorizacionEscrituraBehavior</c>, en la
/// revalidación de la selección y en la RLS. Es una guarda de integridad contra la pestaña que escribe sin saber que
/// el contexto cambió. Vive en memoria del proceso, igual que los circuitos que protege (una sola réplica; ver el
/// aviso del backplane de SignalR): un registro vacío, tras un arranque, no rechaza nada porque ningún circuito
/// anterior al arranque sigue vivo. Una entrada caduca con la vigencia del token.
/// </para>
///
/// <para>
/// <b>Gana la última escritura, a propósito.</b> Dos cambios casi simultáneos desde dos pestañas dejan aquí el que
/// el servidor registró último, y en el navegador la cookie de la respuesta que llegó última; pueden no coincidir. Un
/// compare-and-set no lo arregla, porque la carrera es con el orden de llegada de las respuestas, que el servidor no
/// ve. Lo cierra la adopción al abrir circuito (<see cref="ClienteActivoSeleccionado.AdoptarComoVigente"/>): la
/// pestaña que recarga pinta con la cookie que el navegador tiene de verdad, y esa pasa a ser la vigente. Queda una
/// carrera: un circuito que negoció con la cookie anterior antes de que llegara la respuesta del cambio y se abre
/// después del circuito de la pestaña que cambió deja vigente la anterior. Esa pestaña escribe entonces con una
/// selección que ya no es la de la cookie del navegador, y la otra pide recargar. No abre ningún Tenant que no esté
/// autorizado (la revalidación y la RLS siguen mandando) y se corrige al recargar cualquiera de las dos.
/// </para>
/// </summary>
public sealed class RegistroSeleccionVigente
{
    public const string VersionOrigen = "origen";

    private const int UmbralPurga = 4096;

    private readonly ConcurrentDictionary<string, (string Version, DateTime CaducaUtc)> _vigentes = new(StringComparer.Ordinal);

    /// <summary>La sesión de navegador: el usuario y el <c>talveg:sid</c> de su inicio de sesión, o <c>null</c> sin usuario.</summary>
    public static string? ClaveDe(ClaimsPrincipal usuario) =>
        ClienteActivoSeleccionado.LeerUsuarioActual(usuario) is { } usuarioId
            ? $"{usuarioId:N}|{usuario.FindFirst(SignInManagerCuentaDesactivada.ClaimSid)?.Value}"
            : null;

    public static string Huella(string valorCookie) =>
        WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.UTF8.GetBytes(valorCookie)), 0, 16);

    public void Registrar(string claveSesion, string version)
    {
        var ahora = DateTime.UtcNow;
        _vigentes[claveSesion] = (version, ahora.Add(CookieDeContextoTenant.VigenciaSeleccion));

        if (_vigentes.Count <= UmbralPurga) return;
        foreach (var (clave, valor) in _vigentes)
        {
            if (valor.CaducaUtc <= ahora)
                _vigentes.TryRemove(clave, out _);
        }
    }

    /// <summary>La versión vigente de esa sesión, o <c>null</c> si este proceso no ha visto ningún cambio en ella.</summary>
    public string? Vigente(string claveSesion) =>
        _vigentes.TryGetValue(claveSesion, out var valor) && valor.CaducaUtc > DateTime.UtcNow ? valor.Version : null;
}
