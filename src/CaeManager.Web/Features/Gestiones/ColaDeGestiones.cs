using CaeManager.Application.Gestiones.Queries.ObtenerGestiones;
using CaeManager.Domain.Gestiones;

namespace CaeManager.Web.Features.Gestiones;

/// <summary>Cómo se despacha la Gestión de la cola que se tenía delante.</summary>
public enum DestinoEnCola
{
    /// <summary>Se completó (o se marcó como hecha): cuenta como hecha y «Deshacer» puede devolverla a la cola.</summary>
    Completada,

    /// <summary>Se pasó a la siguiente sin completarla: no vuelve a salir en esta cola.</summary>
    Saltada
}

/// <summary>
/// Cola de «Gestión rápida»: recorre, una detrás de otra, las Gestiones pendientes que el listado ya enseña con
/// el filtro actual. No decide qué se ve: recibe las filas que devolvió <c>ObtenerGestionesQuery</c> (con la
/// cartera y el alcance del usuario ya aplicados) y solo las ordena en el recorrido. Es estado de pantalla, sin
/// persistencia ni identidad: nada de lo que hace sale de lo que el usuario ya veía en el listado.
///
/// <para>
/// <b>Orden.</b> La primera es la primera del listado. Después de despachar una, la siguiente es la primera
/// restante «similar» a ella —mismo Tipo de documento y misma Plataforma CAE—, para trabajar seguidas las que
/// se hacen en el mismo sitio (iniciar sesión en la plataforma una sola vez); si no queda ninguna similar,
/// la primera restante en el orden del listado, que pasa a ser la nueva referencia. «Misma Plataforma CAE»
/// compara el nombre sin distinguir mayúsculas ni espacios sobrantes: dos Clientes empresariales que usan
/// Nalanda son la misma plataforma aunque cada uno tenga su código de cliente. Sin Plataforma CAE registrada, la
/// «plataforma» es ninguna, y solo es similar otra Gestión también sin ella.
/// </para>
///
/// <para>
/// <b>Contador.</b> «N de M»: M es lo que había al empezar y N la que se tiene delante (las ya despachadas más
/// una). Una saltada no se revisita en esta cola; salir y volver a empezar sí la ofrece de nuevo.
/// </para>
/// </summary>
public sealed class ColaDeGestiones
{
    /// <summary>
    /// Las que como mucho recorre una cola. Es un tope de la consulta, no una regla de negocio: una cola es para
    /// trabajar de seguido y a la vez, y la pantalla no debe cargar sin límite lo que el listado pagina.
    /// </summary>
    public const int TopeDeGestiones = 500;

    private readonly Dictionary<Guid, GestionListaDto> _porId;
    private readonly Dictionary<Guid, int> _posicionEnLista;
    private readonly List<GestionListaDto> _restantes;
    private readonly HashSet<Guid> _completadas = [];
    private int _saltadas;

    private ColaDeGestiones(List<GestionListaDto> pendientes)
    {
        _porId = pendientes.ToDictionary(g => g.Id);
        _posicionEnLista = pendientes.Select((g, indice) => (g.Id, indice)).ToDictionary(p => p.Id, p => p.indice);
        _restantes = pendientes;
        Total = pendientes.Count;
        Actual = _restantes[0];
        _restantes.RemoveAt(0);
    }

    /// <summary>
    /// La cola de las pendientes de <paramref name="enOrdenDeLista"/>, que llegan ya en el orden del listado.
    /// <c>null</c> si no hay ninguna pendiente: una cola vacía no se abre.
    /// </summary>
    public static ColaDeGestiones? Crear(IEnumerable<GestionListaDto> enOrdenDeLista)
    {
        var pendientes = enOrdenDeLista
            .Where(g => g.Estado == EstadoGestion.Pendiente)
            .DistinctBy(g => g.Id)
            .Take(TopeDeGestiones)
            .ToList();
        return pendientes.Count == 0 ? null : new ColaDeGestiones(pendientes);
    }

    /// <summary>La que se tiene delante; <c>null</c> cuando la cola se acabó.</summary>
    public GestionListaDto? Actual { get; private set; }

    /// <summary>M del contador: las que tenía la cola al empezar.</summary>
    public int Total { get; }

    public int Completadas => _completadas.Count;

    public int Saltadas => _saltadas;

    /// <summary>N del contador. Nunca pasa de <see cref="Total"/>, ni siquiera con la cola acabada.</summary>
    public int Posicion => Math.Min(Total, Completadas + Saltadas + 1);

    public bool Terminada => Actual is null;

    /// <summary>Despacha la actual y pasa a la siguiente (o termina la cola si no quedan).</summary>
    public void Avanzar(DestinoEnCola destino)
    {
        if (Actual is not { } despachada)
            return;

        if (destino == DestinoEnCola.Completada)
            _completadas.Add(despachada.Id);
        else
            _saltadas++;

        Actual = ExtraerSiguiente(despachada);
    }

    /// <summary>
    /// «Deshacer» de una Gestión que esta cola completó: vuelve a ser la actual, y la que se tenía delante regresa
    /// al sitio que le tocaba en el orden del listado. <c>false</c> si esta cola no la había completado (la
    /// reabrieron desde otro sitio): entonces la cola no se toca.
    /// </summary>
    public bool Reabrir(Guid id)
    {
        if (!_completadas.Remove(id))
            return false;

        if (Actual is { } enCurso)
            InsertarEnOrdenDeLista(enCurso);

        Actual = _porId[id];
        return true;
    }

    /// <summary>
    /// La siguiente de <paramref name="restantes"/> (en el orden del listado) tras despachar
    /// <paramref name="despachada"/>: la primera similar a ella, o si no hay ninguna, la primera. <c>null</c> si
    /// no quedan. Pura: es la regla de orden de la cola, probada sola.
    /// </summary>
    public static GestionListaDto? ElegirSiguiente(IReadOnlyList<GestionListaDto> restantes, GestionListaDto despachada)
    {
        var referencia = ClaveDeSimilitud(despachada);
        return restantes.FirstOrDefault(g => ClaveDeSimilitud(g) == referencia) ?? restantes.FirstOrDefault();
    }

    /// <summary>Lo que hace «similares» a dos Gestiones: el Tipo de documento y la Plataforma CAE (normalizada).</summary>
    public static (Guid TipoDocumentoId, string? Plataforma) ClaveDeSimilitud(GestionListaDto gestion) =>
        (gestion.TipoDocumentoId,
            string.IsNullOrWhiteSpace(gestion.PlataformaCaeNombre) ? null : gestion.PlataformaCaeNombre.Trim().ToUpperInvariant());

    private GestionListaDto? ExtraerSiguiente(GestionListaDto despachada)
    {
        var siguiente = ElegirSiguiente(_restantes, despachada);
        if (siguiente is not null)
            _restantes.Remove(siguiente);
        return siguiente;
    }

    private void InsertarEnOrdenDeLista(GestionListaDto gestion)
    {
        var posicion = _posicionEnLista[gestion.Id];
        var indice = _restantes.FindIndex(g => _posicionEnLista[g.Id] > posicion);
        _restantes.Insert(indice < 0 ? _restantes.Count : indice, gestion);
    }
}
