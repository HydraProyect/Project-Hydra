using CaeManager.Application.Common;
using CaeManager.Domain.Common;

namespace CaeManager.Application.Facturacion;

/// <summary>
/// Quién escribe las tarifas de un Cliente empresarial (Crear, Actualizar, Eliminar
/// TarifaCliente). Las tarifas son plano Comercial, no de Operación: no se acotan por
/// Asignación de Cartera, sino por rol. Antes esa frontera solo la ponía la página
/// <c>/facturacion</c> (<c>[Authorize(Roles = Administrador, DireccionCae)]</c>); un
/// comando enviado por otro camino (otra página, un endpoint futuro) la saltaba y un
/// Gestor CAE —con o sin cartera— podía fijar precios. Este guard la repite en
/// Application con los mismos dos roles.
///
/// El rol es el EFECTIVO (<see cref="ICurrentUserService.ObtenerRolEfectivoAsync"/>): el
/// Operador CAE externo con encargo de administración opera con rol Administrador y pasa;
/// una Sesión Privilegiada de plano 3 no tiene rol de negocio y no pasa (tampoco estas
/// escrituras son de Aprovisionamiento).
/// </summary>
public static class AutorizacionTarifasCliente
{
    private static readonly string[] RolesConAcceso = ["Administrador", "DireccionCae"];

    public static readonly Error SinPermiso = Error.Crear(
        "TarifaCliente.SinPermiso", "Solo Administrador o Dirección CAE pueden gestionar las tarifas.");

    public static async Task<bool> PuedeGestionarTarifasAsync(this ICurrentUserService currentUserService) =>
        await currentUserService.ObtenerRolEfectivoAsync() is { } rol && RolesConAcceso.Contains(rol);
}
