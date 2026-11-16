using System.Security.Claims;
using CaeManager.Infrastructure.Autenticacion;
using CaeManager.Infrastructure.Identity;

namespace CaeManager.Web.Services;

/// <summary>
/// <see cref="ISesionOrigenExtension"/> con las reglas de la cookie (<see cref="CaducidadSesion"/>) y
/// las interacciones anotadas en <see cref="ActividadSesiones"/>. Solo lee: el uso de la extensión no
/// anota ninguna interacción.
/// </summary>
public sealed class SesionOrigenExtension(ActividadSesiones actividad, TimeProvider reloj) : ISesionOrigenExtension
{
    public MarcasSesionOrigen? MarcasVigentes(ClaimsPrincipal sesion) =>
        SignInManagerCuentaDesactivada.LeerSid(sesion) is { } sid
            ? CaducidadSesion.MarcasParaExtension(sesion, actividad.Leer(sid), reloj.GetUtcNow())
            : null;

    public bool SigueVigente(ClaimsPrincipal cuenta, Guid sid, MarcasSesionOrigen marcas) =>
        CaducidadSesion.SesionOrigenVigente(cuenta, marcas, actividad.Leer(sid), reloj.GetUtcNow());
}
