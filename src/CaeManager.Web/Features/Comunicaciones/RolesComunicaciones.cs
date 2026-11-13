using CaeManager.Infrastructure.Identity;

namespace CaeManager.Web.Features.Comunicaciones;

/// <summary>
/// Roles que pueden entrar al módulo de Comunicaciones (hallazgo N-2 de
/// Project-Hydra-Negocio/seguridad/INFORME-AUDITORIA-2.md). Es la misma lista que <c>RolesConMenuCompleto</c>
/// de <c>CatalogoMenuLateral</c>: todos los roles de gestión CAE, incluido Consulta en
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

    /// <summary>
    /// Quién lee el historial de reclamaciones enviadas dentro de Comunicaciones (filtro Origen «Reclamaciones»): los mismos
    /// roles que hoy leen la pestaña Reclamaciones de Documentos (<c>Documentos.RolesDeGestionDocumental</c>) y que
    /// <c>SituacionDocumentosEnCentrosService.RolesQueLeenReclamaciones</c> en el servidor. Consulta entra a la bandeja pero
    /// no a este historial. Un test de la Web fija que las tres listas no se separen. Es la misma puerta de presentación que
    /// la pestaña de Documentos: <c>ObtenerReclamacionesEnviadasQuery</c> acota por cartera, no por rol.
    /// </summary>
    public const string LecturaReclamaciones =
        $"{Roles.Administrador},{Roles.DireccionCae},{Roles.CoordinadorCae},{Roles.GestorCae}";
}
