using CaeManager.Infrastructure.Identity;

namespace CaeManager.Web.Features.Comunicaciones;

/// <summary>
/// Roles que pueden entrar al módulo de Comunicaciones (hallazgo N-2 de
/// INFORME-AUDITORIA-2.md). Es la misma lista que <c>RolesConMenuCompleto</c>
/// de <c>NavMenu</c>: todos los roles de gestión CAE, incluido Consulta en
/// solo lectura, y nunca el rol Cliente — un contacto de una empresa cliente
/// externa no puede leer el correo de las demás.
///
/// Constante propia en vez de repetir la cadena en cada página porque
/// <c>[Authorize(Roles = …)]</c> exige una constante en tiempo de
/// compilación: no admite un array ni una llamada.
/// </summary>
public static class RolesComunicaciones
{
    public const string Gestion =
        $"{Roles.Administrador},{Roles.DireccionCae},{Roles.CoordinadorCae},{Roles.GestorCae},{Roles.Consulta}";
}
