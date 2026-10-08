using CaeManager.Domain.Common;

namespace CaeManager.Domain.Configuracion;

/// <summary>
/// Combinación de filtros de una pantalla de listado, guardada con nombre
/// por un usuario (P3-31, Project-Hydra-Negocio/MATURITY_REVIEW.md).
///
/// Pertenece al Tenant en el que se guardó (decisión D4 del 2026-10-08): la
/// clave es Tenant + Usuario + Pantalla + Nombre. Un Gestor CAE con Asignación
/// de Cartera sobre varios Tenants tiene en cada uno sus propios filtros,
/// porque los valores que guardan (Empresas, Clientes empresariales, Gestores
/// CAE) son identificadores de ese Tenant y en otro no significan nada. Por
/// eso extiende <see cref="EntidadConTenant"/> y no <see cref="Entity"/>, a
/// diferencia de <see cref="PreferenciaDashboardUsuario"/>.
///
/// <see cref="ValoresJson"/> es opaco para Domain/Application: cada pantalla
/// serializa y entiende su propia forma de filtros (Application no define un
/// DTO de filtro por pantalla, evita acoplar este agregado a cada feature).
/// </summary>
public class FiltroGuardado : EntidadConTenant
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
