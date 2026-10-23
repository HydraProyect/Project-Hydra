using CaeManager.Application.Common;
using CaeManager.Domain.Common;

namespace CaeManager.Application.AutorizacionesLectura;

/// <summary>
/// Quién concede, cambia, revoca y lee las autorizaciones de lectura documental del Tenant de la Empresa contratista.
///
/// <para>
/// <b>Solo rol efectivo Administrador en el Tenant activo</b> (decisión DN-2 abierta: es la opción más estrecha, y
/// falla cerrado). Es el mismo mecanismo que los demás actos solo de Administrador
/// (<c>ActualizarLecturaIaGlobalCommand</c> y hermanos): <see cref="ICurrentUserService.ObtenerRolEfectivoAsync"/>
/// comparado con «Administrador». No se inventa otro. Gestor CAE, Coordinador CAE, Dirección CAE, Consulta y Cliente
/// quedan fuera aunque su rol escriba.
/// </para>
///
/// <para>
/// <b>Lo que ese mecanismo ya incluye.</b> El rol efectivo es Administrador para el Administrador del Tenant y, en un
/// Workspace operativo derivado abierto por Asignación de Operación, para el Administrador del Operador CAE externo
/// que opera el Tenant, porque su perfil de origen rige ahí con todos sus poderes (decisión del 2026-10-20, más amplia
/// que la del encargo de administración pleno del 2026-10-15, con la que el Administrador del Operador CAE ya era
/// Administrador pleno). Se acepta tal cual: es el criterio vigente del rol efectivo, y estrecharlo solo aquí sería una
/// regla nueva que DN-2 tendría que decidir.
/// </para>
///
/// <para>
/// <b>Una Sesión Privilegiada de plataforma nunca concede.</b> Bajo el plano 3 el rol efectivo es null, así que este
/// predicado ya la rechaza; y antes, <c>AutorizacionEscrituraBehavior</c> deniega todo comando que no sea
/// <c>IComandoDeAprovisionamiento</c>, y estos no lo son. Soporte TALVEG no es Administrador del Tenant.
/// </para>
/// </summary>
public static class AutorizacionLecturaAutoridad
{
    // Application no puede referenciar Infrastructure.Identity.Roles: mismo motivo que en AutorizacionEscrituraBehavior.
    private const string RolAdministrador = "Administrador";

    public static readonly Error SoloAdministrador = Error.Crear(
        "AutorizacionLectura.SoloAdministrador",
        "Solo el Administrador de la organización puede autorizar la lectura de sus documentos.");

    /// <summary>Un único error para «no existe» y «no es de este Tenant»: la respuesta no sirve de oráculo.</summary>
    public static readonly Error NoEncontrada = Error.Crear(
        "AutorizacionLectura.NoEncontrada", "La autorización de lectura no existe o no tienes acceso.");

    /// <summary>Un único error aunque falte más de un Trabajador: no dice cuál ni cuántos existen.</summary>
    public static readonly Error TrabajadorNoEncontrado = Error.Crear(
        "AutorizacionLectura.TrabajadorNoEncontrado", "No encontramos alguno de los Trabajadores seleccionados.");

    public static readonly Error Revocada = Error.Crear(
        "AutorizacionLectura.Revocada", "Esta autorización de lectura está revocada: ya no se puede cambiar.");

    public static async Task<bool> EsAdministradorAsync(ICurrentUserService currentUserService) =>
        await currentUserService.ObtenerRolEfectivoAsync() == RolAdministrador;
}
