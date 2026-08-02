using CaeManager.Domain.Common;

namespace CaeManager.Domain.Configuracion;

/// <summary>
/// Combinación de filtros de una pantalla de listado, guardada con nombre
/// por un usuario (P3-31, docs/business/MATURITY_REVIEW.md). Extiende
/// <see cref="Entity"/>, no <see cref="EntidadConTenant"/> — mismo criterio
/// que <see cref="PreferenciaDashboardUsuario"/>: es preferencia de un
/// usuario, no un dato del tenant.
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
