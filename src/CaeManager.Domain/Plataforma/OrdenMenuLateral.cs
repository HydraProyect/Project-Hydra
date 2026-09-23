using System.Text.RegularExpressions;
using CaeManager.Domain.Common;

namespace CaeManager.Domain.Plataforma;

/// <summary>
/// Orden global del menú lateral de los usuarios internos (decisión del propietario de
/// 2026-09-23, MVP-1). Lo decide el Actor de Plataforma TALVEG y lo ven todos los usuarios de
/// todos los Tenants: es una fila del <b>plano de Plataforma</b> (ADR-011), no de ningún Tenant, y
/// por eso no hereda de <see cref="EntidadConTenant"/>.
///
/// <para>
/// Solo guarda identificadores, nunca qué enlaces existen ni quién los ve: el catálogo del menú
/// (<c>CatalogoMenuLateral</c>, capa Web) sigue siendo la única fuente de los enlaces, sus grupos
/// y sus reglas de visibilidad. Reordenar no muestra ni oculta nada a nadie.
/// </para>
///
/// <para>
/// <see cref="OrdenEnlaces"/> es una lista plana y no una por grupo a propósito: el grupo de un
/// enlace lo dice siempre el catálogo, así que mover un enlace a otro grupo (fuera de la
/// decisión) es imposible por construcción, no por una validación que alguien podría saltarse.
/// </para>
///
/// <para>
/// Reconciliación al leer (en la capa Web, junto al catálogo del menú): un identificador guardado que
/// ya no existe se ignora, y un grupo o enlace nuevo que no esté guardado va al final. Por eso
/// aquí no se valida que los identificadores existan — solo su forma — y "restablecer el orden
/// por defecto" es guardar las dos listas vacías.
/// </para>
/// </summary>
public class OrdenMenuLateral : Entity, IVersionable
{
    /// <summary>
    /// Clave fija, mismo motivo que <see cref="EstadoBootstrapPlataforma.ClaveCanonica"/>: la
    /// unicidad de la fila la garantiza la clave primaria, no una comprobación leída antes.
    /// </summary>
    public static readonly Guid ClaveCanonica = new("0dde0000-0000-4000-8000-00000000e4a1");

    /// <summary>Tope por lista: el menú tiene decenas de entradas, no miles.</summary>
    public const int MaximoIdentificadores = 200;

    private static readonly Regex FormaIdentificador = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant);

    public List<string> OrdenGrupos { get; private set; } = [];
    public List<string> OrdenEnlaces { get; private set; } = [];

    /// <summary>Actor real que guardó el último cambio (nunca el usuario simulado).</summary>
    public Guid ActualizadoPorUsuarioId { get; private set; }
    public DateTime ActualizadoEnUtc { get; private set; }

    /// <summary>
    /// Token de concurrencia: lo renueva <c>ConcurrenciaOptimistaInterceptor</c> en cada
    /// modificación, y el comando compara la versión que se vio en pantalla.
    /// </summary>
    public Guid Version { get; private set; } = Guid.NewGuid();

    private OrdenMenuLateral()
    {
    }

    public static OrdenMenuLateral Crear(
        IReadOnlyList<string> grupos, IReadOnlyList<string> enlaces, Guid actorRealUsuarioId, DateTime ahoraUtc)
    {
        var orden = new OrdenMenuLateral();
        typeof(Entity).GetProperty(nameof(Id))!.SetValue(orden, ClaveCanonica);
        orden.Reordenar(grupos, enlaces, actorRealUsuarioId, ahoraUtc);
        return orden;
    }

    public void Reordenar(
        IReadOnlyList<string> grupos, IReadOnlyList<string> enlaces, Guid actorRealUsuarioId, DateTime ahoraUtc)
    {
        if (actorRealUsuarioId == Guid.Empty)
            throw new ArgumentException("El cambio de orden necesita un Actor real.", nameof(actorRealUsuarioId));

        OrdenGrupos = Validar(grupos, nameof(grupos));
        OrdenEnlaces = Validar(enlaces, nameof(enlaces));
        ActualizadoPorUsuarioId = actorRealUsuarioId;
        ActualizadoEnUtc = ahoraUtc;
    }

    private static List<string> Validar(IReadOnlyList<string> identificadores, string nombre)
    {
        ArgumentNullException.ThrowIfNull(identificadores, nombre);

        if (identificadores.Count > MaximoIdentificadores)
            throw new ArgumentException($"Como mucho {MaximoIdentificadores} identificadores.", nombre);

        foreach (var id in identificadores)
        {
            if (id is null || id.Length > 64 || !FormaIdentificador.IsMatch(id))
                throw new ArgumentException($"Identificador de menú no válido: «{id}».", nombre);
        }

        if (identificadores.Distinct(StringComparer.Ordinal).Count() != identificadores.Count)
            throw new ArgumentException("Hay identificadores repetidos.", nombre);

        return [.. identificadores];
    }
}
