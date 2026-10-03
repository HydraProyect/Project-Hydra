namespace CaeManager.Web.Components.DesignSystem;

/// <summary>
/// La clave de un hermano keyed: la identidad que le da el modelo (<see cref="Base"/>) y cuántos hermanos con esa misma
/// identidad se han pintado ya antes (<see cref="Repeticion"/>, 0 para el primero). Tipo propio, no una tupla suelta:
/// no puede chocar con la clave de otro tipo de hermano (un <c>string</c> de grupo, un <c>Guid</c>) y deja a los trinquetes
/// reconocer el punto único por su nombre.
/// </summary>
public readonly record struct ClaveDeHermano(object Base, int Repeticion);

/// <summary>
/// Claves <c>@key</c> únicas entre los hermanos de un mismo contenedor, sea cual sea el dato del modelo.
///
/// <para>
/// <b>Por qué existe.</b> Dos hermanos con la misma <c>@key</c> hacen que el diff de Blazor lance «Attempting to return
/// wrong pooled instance» y MATA EL CIRCUITO (~1 s tras conectar; los clics siguientes se pierden): lo midió el E2E del
/// Coordinador CAE (#1064: la misma Faltante de un Trabajador en dos Centros compartía <c>ItemBandejaDto.Id</c>). Arreglar el
/// productor concreto no impide el siguiente: una clave construida solo es única si el autor de cada lista recuerda todas
/// las dimensiones que identifican su fila. Aquí la unicidad no depende del dato: la primera aparición de una identidad
/// conserva su clave base y cada repetición posterior recibe la misma base con su número de orden, así que la pantalla
/// pinta TODAS las filas (ninguna se oculta por repetida) y el circuito sigue vivo. Un duplicado sigue siendo un defecto
/// del productor, que los tests de unicidad de los datos (<c>IdDeFilaDeCola.Duplicados</c>) y el log de Mi trabajo señalan.
/// </para>
///
/// <para>
/// <b>Uso.</b> Una instancia por contenedor cuyos hijos comparten padre, creada en el propio marcado (se rehace en cada
/// pintado, y el orden de recorrido es el de pantalla, así que las claves son estables entre pintados):
/// <c>@{ var claves = new ClavesDeHermanos(); }</c> y <c>@key="claves.De(item.Id)"</c>. Si varios <c>@foreach</c> alimentan al
/// mismo padre (lotes de filas dentro de un grupo), comparten la instancia: la unicidad es del padre, no de cada bucle.
/// </para>
///
/// <para>
/// <b>Lo que no hace.</b> No une dos instancias: dos contenedores distintos tienen cada uno la suya, y eso es correcto (la
/// clave solo compite con sus hermanos). No sirve para la clave de reinicio de un único hijo (<c>@key="_version"</c>): ahí no
/// hay hermanos con clave.
/// </para>
/// </summary>
public sealed class ClavesDeHermanos
{
    private readonly Dictionary<object, int> _vistas = [];

    /// <summary>La clave del siguiente hermano con la identidad <paramref name="identidad"/>.</summary>
    public ClaveDeHermano De(object identidad)
    {
        ArgumentNullException.ThrowIfNull(identidad);
        var anteriores = _vistas.GetValueOrDefault(identidad);
        _vistas[identidad] = anteriores + 1;
        return new ClaveDeHermano(identidad, anteriores);
    }
}
