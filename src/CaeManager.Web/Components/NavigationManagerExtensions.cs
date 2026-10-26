using Microsoft.AspNetCore.Components;
using System.Runtime.CompilerServices;

namespace CaeManager.Web.Components;

/// <summary>
/// Project-Hydra-Negocio/tecnico/docs/archive/design/UX_PATTERNS.md § "Estado de filtros persiste en la URL (query string)
/// para que se pueda compartir/recargar sin perder el contexto" — P1-18 de
/// Project-Hydra-Negocio/MATURITY_REVIEW.md. Cada página con filtros llama a esto
/// cuando el usuario cambia uno, en vez de guardarlo solo en un campo
/// privado del componente.
/// </summary>
public static class NavigationManagerExtensions
{
    /// <summary>
    /// Escrituras de filtros que el navegador aún no ha confirmado, una entrada por instancia de
    /// <see cref="NavigationManager"/>. En Blazor Server el <see cref="NavigationManager"/> es Scoped, así que cada
    /// circuito (un usuario, una pestaña) tiene la suya y nunca se compone sobre la URL de otro. La tabla es débil: la
    /// entrada muere con su <see cref="NavigationManager"/> al cerrarse el circuito, sin crecer con los usuarios.
    /// </summary>
    private static readonly ConditionalWeakTable<NavigationManager, EscriturasPendientes> Pendientes = new();

    private sealed class EscriturasPendientes
    {
        // Tope de destinos de una misma cadena sin el acuse del último: más allá algo ha perdido los acuses y la cadena
        // vuelve a empezar (la composición degrada a la de antes, sobre Uri), en vez de crecer sin límite.
        private const int MaximoDestinosPorCadena = 32;

        // URL confirmada sobre la que empezó la cadena, último destino pedido y todos los destinos pedidos en ella
        // (los ya confirmados también: Uri puede ser un acuse intermedio mientras el último sigue en vuelo).
        private string? _origen;
        private string? _destino;
        private readonly HashSet<string> _destinosDeLaCadena = new(StringComparer.Ordinal);

        // Última ubicación cuyo LocationChanged ha visto este manejador, y si la cadena empezó sobre una URL cuyo
        // evento aún no había llegado aquí (la página escribió dentro de él, antes que este manejador).
        private string _ultimaUbicacionVista;
        private bool _esperaElEventoDelOrigen;

        public EscriturasPendientes(NavigationManager navigation)
        {
            // Se crea en la primera escritura: si ocurre dentro de un LocationChanged, este manejador ya no recibe ese
            // evento (la lista de suscriptores se fija al dispararlo), así que la ubicación actual cuenta como vista.
            _ultimaUbicacionVista = navigation.Uri;
            navigation.LocationChanged += (_, args) => AlCambiarUbicacion(args.Location);
        }

        /// <summary>
        /// URL sobre la que componer la siguiente escritura. Solo es el último destino solicitado si la URL actual
        /// sigue siendo la de esta cadena de escrituras (la de partida o un acuse intermedio); cualquier otra (un
        /// enlace del menú, «atrás» o «adelante», un NavigateTo ajeno) la abandona aunque su LocationChanged aún no
        /// haya llegado aquí: el Router atiende ese evento antes y la página nueva puede escribir ya en su
        /// OnInitialized.
        /// </summary>
        public string BaseDeComposicion(string actual)
        {
            if (_destino is not null && (actual == _origen || _destinosDeLaCadena.Contains(actual)))
                return _destino;

            Reiniciar();
            _origen = actual;
            _esperaElEventoDelOrigen = actual != _ultimaUbicacionVista;
            return actual;
        }

        public void Solicitar(string destino)
        {
            // Con el tope solo se olvidan los acuses intermedios: el origen y el último destino siguen valiendo, y un
            // acuse intermedio olvidado se tratará como navegación ajena (vuelta a componer sobre Uri).
            if (_destinosDeLaCadena.Count >= MaximoDestinosPorCadena)
                _destinosDeLaCadena.Clear();
            _destino = destino;
            _destinosDeLaCadena.Add(destino);
        }

        public void Reiniciar()
        {
            _origen = null;
            _destino = null;
            _esperaElEventoDelOrigen = false;
            _destinosDeLaCadena.Clear();
        }

        private void AlCambiarUbicacion(string ubicacion)
        {
            _ultimaUbicacionVista = ubicacion;

            // El acuse del último destino da la cadena por terminada, aunque coincida con la URL de partida (teclear
            // y borrar).
            if (ubicacion == _destino)
            {
                Reiniciar();
                return;
            }

            // El evento de la URL de partida que llega tarde porque la página escribió dentro de él: la cadena acaba
            // de empezar sobre esa misma URL. Solo una vez: otra navegación a la misma URL (un enlace del menú a la
            // página en la que ya se está) sí es ajena.
            if (_esperaElEventoDelOrigen && ubicacion == _origen)
            {
                _esperaElEventoDelOrigen = false;
                return;
            }

            // Un acuse intermedio no borra la composición más reciente; una navegación ajena (incluidos «atrás» y
            // «adelante») abandona las escrituras pendientes.
            if (!_destinosDeLaCadena.Contains(ubicacion))
                Reiniciar();
        }
    }

    /// <summary>
    /// Marca de la navegación con la que una página escribe sus propios filtros en su URL. Viaja como estado de la
    /// entrada del historial solo para que <c>AvisoCambiosSinGuardar</c> la reconozca: la página no sale de ningún
    /// sitio y sus formularios siguen montados, así que no hay nada que preguntar. Quien escriba en la URL algo que
    /// SÍ cierra un formulario (otro Cliente en Proyectos) pregunta antes por su cuenta, como ya hacía.
    /// </summary>
    public const string EstadoEscrituraDeFiltros = "talveg:escritura-de-filtros";

    /// <summary>
    /// Actualiza un parámetro de la URL actual sin recargar la página
    /// (<c>replace: true</c> para no llenar el historial de un clic por
    /// tecla). Un valor vacío o en blanco quita el parámetro en vez de
    /// dejarlo como cadena vacía en la URL.
    /// </summary>
    public static void ActualizarFiltroEnUrl(this NavigationManager navigation, string nombreParametro, string? valor) =>
        navigation.ActualizarFiltrosEnUrl(new Dictionary<string, string?> { [nombreParametro] = valor });

    /// <summary>
    /// Igual que <see cref="ActualizarFiltroEnUrl"/> pero para varios
    /// parámetros a la vez, en una única navegación. Mientras el navegador no
    /// confirma la navegación, compone los cambios sobre el último destino solicitado.
    /// </summary>
    public static void ActualizarFiltrosEnUrl(this NavigationManager navigation, IReadOnlyDictionary<string, string?> parametros)
    {
        var normalizados = new Dictionary<string, object?>();
        foreach (var (nombreParametro, valor) in parametros)
            normalizados[nombreParametro] = string.IsNullOrWhiteSpace(valor) ? null : valor;

        var pendientes = Pendientes.GetValue(navigation, n => new EscriturasPendientes(n));
        var destino = navigation.GetUriWithQueryParameters(pendientes.BaseDeComposicion(navigation.Uri), normalizados);
        pendientes.Solicitar(destino);
        try
        {
            navigation.NavigateTo(destino,
                new NavigationOptions { ReplaceHistoryEntry = true, HistoryEntryState = EstadoEscrituraDeFiltros });
        }
        catch
        {
            pendientes.Reiniciar();
            throw;
        }
    }
}
