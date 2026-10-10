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
///
/// <para>
/// <b>Vista recordada</b> (decisión del 2026-10-08): la misma tabla guarda,
/// además de los filtros con nombre, la última vista de cada Usuario en cada
/// pantalla de cada Tenant. Es la fila cuyo <see cref="Nombre"/> es
/// <see cref="NombreVistaRecordada"/>; el índice único de la clave garantiza
/// que hay como mucho una. No es un filtro con nombre y no se comporta como
/// tal: solo nace con <see cref="CrearVistaRecordada"/>, es la única fila cuyos
/// valores cambian después de creada (<see cref="RecordarVista"/>), el
/// constructor público rechaza ese nombre, y ni el listado de filtros
/// guardados ni su borrado por Id la alcanzan (lo garantizan sus casos de uso
/// en Application). Comparte con los filtros con nombre lo que importa: Tenant,
/// filtro global de EF y política RLS <c>aislamiento_tenant</c>.
/// </para>
/// </summary>
public class FiltroGuardado : EntidadConTenant
{
    /// <summary>
    /// Nombre reservado de la fila que guarda la vista recordada. Con guiones
    /// bajos a los lados para que nadie lo teclee por accidente; quien lo teclee
    /// a propósito se encuentra con <see cref="EsNombreReservado"/>.
    /// </summary>
    public const string NombreVistaRecordada = "__vista_recordada__";

    public Guid UsuarioId { get; private set; }
    public string Pantalla { get; private set; } = string.Empty;
    public string Nombre { get; private set; } = string.Empty;
    public string ValoresJson { get; private set; } = string.Empty;
    public DateTime CreadoEnUtc { get; private set; } = DateTime.UtcNow;

    private FiltroGuardado()
    {
        // Requerido por EF Core.
    }

    /// <summary>Filtro con nombre. El nombre reservado de la vista recordada no se admite aquí.</summary>
    public FiltroGuardado(Guid usuarioId, string pantalla, string nombre, string valoresJson)
        : this(usuarioId, pantalla, nombre, valoresJson, esVistaRecordada: false)
    {
    }

    // El quinto parámetro no corresponde a ninguna propiedad: EF no puede enlazar este
    // constructor y sigue materializando con el de sin parámetros.
    private FiltroGuardado(Guid usuarioId, string pantalla, string nombre, string valoresJson, bool esVistaRecordada)
    {
        if (usuarioId == Guid.Empty)
            throw new ArgumentException("El filtro guardado debe pertenecer a un usuario.", nameof(usuarioId));
        if (string.IsNullOrWhiteSpace(pantalla))
            throw new ArgumentException("Falta la pantalla a la que pertenece el filtro.", nameof(pantalla));
        if (string.IsNullOrWhiteSpace(nombre))
            throw new ArgumentException("El filtro guardado necesita un nombre.", nameof(nombre));
        if (string.IsNullOrWhiteSpace(valoresJson))
            throw new ArgumentException("El filtro guardado no puede estar vacío.", nameof(valoresJson));
        if (!esVistaRecordada && EsNombreReservado(nombre))
            throw new ArgumentException("Ese nombre está reservado para la vista recordada.", nameof(nombre));

        UsuarioId = usuarioId;
        Pantalla = pantalla;
        Nombre = nombre.Trim();
        ValoresJson = valoresJson;
    }

    /// <summary>
    /// La vista recordada de un Usuario en una pantalla. El Tenant lo sella el
    /// guardado, como en cualquier <see cref="EntidadConTenant"/>.
    /// </summary>
    public static FiltroGuardado CrearVistaRecordada(Guid usuarioId, string pantalla, string valoresJson) =>
        new(usuarioId, pantalla, NombreVistaRecordada, valoresJson, esVistaRecordada: true);

    /// <summary>Si esta fila es la vista recordada y no un filtro con nombre.</summary>
    public bool EsVistaRecordada => Nombre == NombreVistaRecordada;

    /// <summary>
    /// Si un nombre escrito a mano choca con el reservado. Es más estricto que la
    /// columna —que distingue mayúsculas y solo colisionaría con el valor exacto—
    /// a propósito: tampoco se admite una variante que se confunda con él.
    /// </summary>
    public static bool EsNombreReservado(string? nombre) =>
        string.Equals(nombre?.Trim(), NombreVistaRecordada, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Sustituye la vista recordada por la de ahora. Solo la fila reservada: un
    /// filtro con nombre no cambia después de guardado.
    /// </summary>
    public void RecordarVista(string valoresJson)
    {
        if (!EsVistaRecordada)
            throw new InvalidOperationException("Solo la vista recordada cambia de valores; un filtro con nombre se guarda una vez.");
        if (string.IsNullOrWhiteSpace(valoresJson))
            throw new ArgumentException("La vista recordada no puede estar vacía.", nameof(valoresJson));

        ValoresJson = valoresJson;
    }
}
