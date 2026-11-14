using CaeManager.Infrastructure.Identity;

namespace CaeManager.Web.Features.Documentos;

/// <summary>Qué pinta <c>/documentos</c> para un Id de <c>?pestana=</c>.</summary>
public enum VistaDePestana
{
    /// <summary>El listado (con o sin filtros equivalentes a la pestaña que tenía el enlace).</summary>
    Listado,

    /// <summary>Plataformas CAE con <c>?acreditacionId=</c>: el bucle de corrección, sin sustituto completo todavía.</summary>
    PlataformaDeAcreditacion,

    /// <summary>Reclamaciones: el compositor «Nueva reclamación», sin sustituto completo todavía.</summary>
    Reclamaciones,

    /// <summary>A la espera de saber el rol para llevar a otra pantalla (<see cref="PestanaResuelta.RutaExterna"/>).</summary>
    Redirigiendo,
}

/// <param name="Vista">Lo que pinta <c>/documentos</c> mientras tanto.</param>
/// <param name="RutaExterna">Pantalla propia a la que se lleva, si el sustituto no es el listado.</param>
/// <param name="Agrupar">Valor de <c>agrupar</c> que equivale a la pestaña, si hay.</param>
public sealed record PestanaResuelta(
    VistaDePestana Vista, string? RutaExterna = null, string? Agrupar = null);

/// <summary>
/// Tabla única de lo que hace cada Id de <c>?pestana=</c> desde que <c>/documentos</c> no tiene barra de pestañas (lienzo
/// «Documentos · listado sin pestañas», PR8). Los Ids se conservan (README del lienzo, «Lo que NO se toca») y cada uno lleva a
/// su sustituto; esto solo cambia qué se muestra, nunca qué se autoriza: las pantallas de destino se siguen autorizando por su
/// cuenta (<c>[Authorize]</c>) y cada Command por la suya.
///
/// <list type="bullet">
/// <item><c>listado</c>: el listado.</item>
/// <item><c>plataforma</c>: el listado agrupado por Plataforma CAE (P-1). Con <c>acreditacionId</c> sigue siendo la vista de
/// Plataformas CAE, porque el listado no resalta ni acota a la acreditación del enlace (bucle de corrección, Mi trabajo).</item>
/// <item><c>reclamaciones</c>: sigue siendo la vista de Reclamaciones para quien gestiona: el compositor «Nueva reclamación»
/// no tiene aún sustituto completo (Alertas pide elegir el lote).</item>
/// <item><c>sugerencias</c> (Preventivo): <c>/alertas</c>, cuya sección «Faltan documentos» (PR5, #2209) es su sustituto y usa la
/// misma consulta. Solo para los roles que abre Alertas (Administrador, Dirección CAE, Coordinador CAE, Gestor CAE y Consulta);
/// quien no la abre (Usuario de Cliente) se queda en el listado, sin aviso de rol. Antes la pestaña no restringía por rol.</item>
/// <item><c>revision-ia</c>: <c>/documentos/revision-ia</c> para quien gestiona.</item>
/// <item><c>plantillas</c>: <c>/plantillas</c> (catálogo) para quien gestiona.</item>
/// </list>
///
/// Quien no gestiona documentos (Consulta, Usuario de Cliente) se queda en el listado con las vistas que exigían gestión, sin
/// aviso de rol (D-3: se oculta, no se enseña un «sin permiso»).
/// </summary>
public static class RedireccionDePestanas
{
    public const string RutaRevisionIa = "/documentos/revision-ia";
    public const string RutaPlantillas = "/plantillas";
    public const string AgruparPorPlataformaCae = "plataforma";
    public const string RutaAlertas = "/alertas";

    /// <summary>Los roles del <c>[Authorize]</c> de <c>/alertas</c>; <c>RedireccionDePestanasTests</c> los compara con él.</summary>
    public const string RolesQueAbrenAlertas =
        $"{Roles.Administrador},{Roles.DireccionCae},{Roles.CoordinadorCae},{Roles.GestorCae},{Roles.Consulta}";

    /// <param name="idDePestana">Id canónico (<c>Documentos.IdDePestanaDeUrl</c>), o null si no hay o no es válido.</param>
    /// <param name="conAcreditacionId">El enlace trae <c>acreditacionId</c>.</param>
    /// <param name="gestionaDocumentos">null mientras no se ha leído el rol.</param>
    /// <param name="puedeVerAlertas">Rol que abre <c>/alertas</c> (su <c>[Authorize]</c>); null mientras no se ha leído.</param>
    public static PestanaResuelta Resolver(string? idDePestana, bool conAcreditacionId, bool? gestionaDocumentos, bool? puedeVerAlertas = null)
    {
        switch (idDePestana)
        {
            case "sugerencias":
                return puedeVerAlertas switch
                {
                    true => new PestanaResuelta(VistaDePestana.Redirigiendo, RutaExterna: RutaAlertas),
                    false => new PestanaResuelta(VistaDePestana.Listado),
                    null => new PestanaResuelta(VistaDePestana.Redirigiendo),
                };

            case "plataforma" when !conAcreditacionId:
                return new PestanaResuelta(VistaDePestana.Listado, Agrupar: AgruparPorPlataformaCae);

            case "plataforma":
                return gestionaDocumentos == false
                    ? new PestanaResuelta(VistaDePestana.Listado)
                    : new PestanaResuelta(VistaDePestana.PlataformaDeAcreditacion);

            case "reclamaciones":
                return gestionaDocumentos == false
                    ? new PestanaResuelta(VistaDePestana.Listado)
                    : new PestanaResuelta(VistaDePestana.Reclamaciones);

            case "revision-ia":
            case "plantillas":
                var ruta = idDePestana == "plantillas" ? RutaPlantillas : RutaRevisionIa;
                return gestionaDocumentos switch
                {
                    true => new PestanaResuelta(VistaDePestana.Redirigiendo, RutaExterna: ruta),
                    false => new PestanaResuelta(VistaDePestana.Listado),
                    null => new PestanaResuelta(VistaDePestana.Redirigiendo),
                };

            default:
                return new PestanaResuelta(VistaDePestana.Listado);
        }
    }
}
