using CaeManager.Domain.Common;

namespace CaeManager.Domain.Configuracion;

/// <summary>
/// Combinación de filtros de una pantalla de listado, guardada con nombre
/// por un usuario (P3-31, Project-Hydra-Negocio/MATURITY_REVIEW.md). Es una
/// preferencia de un usuario <b>dentro de un Tenant</b>: sus valores llevan
/// identificadores del Tenant propietario en el que se creó, y un mismo usuario
/// (un Gestor CAE de un Operador CAE externo) trabaja sobre varios.
///
/// El Tenant no tiene columna propia: viaja dentro de <see cref="Pantalla"/>,
/// que guarda una clave compuesta «pantalla@tenant» y no el nombre de la
/// pantalla a secas. La compone Application en un único sitio
/// (<c>PantallasConFiltrosGuardados.ClaveAlmacenada</c>) con el Tenant activo de
/// la sesión; este agregado la trata como un texto opaco. Las filas guardadas
/// antes de esa regla conservan el nombre a secas y no se leen: no se sabe a
/// qué Tenant pertenecían sus identificadores.
///
/// Extiende <see cref="Entity"/> y no <see cref="EntidadConTenant"/>, y la
/// tabla no lleva filtro global ni RLS de Tenant. Es <b>deuda declarada</b>, no
/// el diseño final: la frontera de Tenant la sostiene solo el código de
/// Application que compone y compara la clave, no la base. Lo correcto es una
/// columna <c>TenantId</c> propia (con su filtro y su política de aislamiento),
/// pendiente de una migración futura; hasta entonces la propiedad
/// <see cref="Pantalla"/> nombra mal lo que contiene.
///
/// <see cref="ValoresJson"/> es opaco para Domain/Application: cada pantalla
/// serializa y entiende su propia forma de filtros (Application no define un
/// DTO de filtro por pantalla, evita acoplar este agregado a cada feature).
/// </summary>
public class FiltroGuardado : Entity
{
    public Guid UsuarioId { get; private set; }
    public string Pantalla { get; private set; } = string.Empty;
    public string Nombre { get; private set; } = string.Empty;
    public string ValoresJson { get; private set; } = string.Empty;
    public DateTime CreadoEnUtc { get; private set; } = DateTime.UtcNow;

    private FiltroGuardado()
    {
        // Requerido por EF Core.
    }

    public FiltroGuardado(Guid usuarioId, string pantalla, string nombre, string valoresJson)
    {
        if (usuarioId == Guid.Empty)
            throw new ArgumentException("El filtro guardado debe pertenecer a un usuario.", nameof(usuarioId));
        if (string.IsNullOrWhiteSpace(pantalla))
            throw new ArgumentException("Falta la pantalla a la que pertenece el filtro.", nameof(pantalla));
        if (string.IsNullOrWhiteSpace(nombre))
            throw new ArgumentException("El filtro guardado necesita un nombre.", nameof(nombre));
        if (string.IsNullOrWhiteSpace(valoresJson))
            throw new ArgumentException("El filtro guardado no puede estar vacío.", nameof(valoresJson));

        UsuarioId = usuarioId;
        Pantalla = pantalla;
        Nombre = nombre.Trim();
        ValoresJson = valoresJson;
    }
}
