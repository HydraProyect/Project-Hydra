using System.Text;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Una ubicación de deuda: <b>dónde</b> (fichero, entidad…) y <b>qué</b> (símbolo, clave…).
/// El recuento de apariciones de esa pareja es el valor que congela la lista.
/// </summary>
internal readonly record struct Ubicacion(string Lugar, string Simbolo)
{
    public override string ToString() => $"{Lugar} :: {Simbolo}";
}

/// <summary>
/// Mecánica común de los trinquetes por <b>lista de ubicaciones</b> (S11 del análisis de causas
/// raíz de 2026-10-02): en vez de una cifra suelta («el término aparece 340 veces»), un fichero
/// versionado con una línea por <c>lugar :: símbolo = n</c>.
///
/// <para>
/// <b>Por qué no una cifra.</b> Una cifra se puede subir editando un número, y dos PR paralelas
/// que la suben chocan en el mismo literal (<c>337→340</c>, <c>71→72</c> en
/// <c>TerminologiaCanonicaTests</c>). Aquí un uso nuevo en un fichero nuevo es una línea que
/// falta, y nadie puede «subir el número» sin que el diff enseñe qué fichero y qué símbolo.
/// </para>
///
/// <para>
/// <b>Decrece y no solo no crece.</b> Cuatro desvíos ponen el test en rojo, los cuatro con el
/// mensaje que dice qué línea tocar: <c>NUEVA</c> (medida y no listada), <c>CRECE</c> (más
/// apariciones que las listadas), <c>BAJA</c> (menos: hay que bajar la lista) y <c>OBSOLETA</c>
/// (listada y ya no medida: hay que borrarla). Los dos últimos son lo que hace que la lista
/// baje con el código y que un escáner que se queda ciego no pueda dar verde: si deja de ver, todas
/// las líneas pasan a <c>OBSOLETA</c> a la vez.
/// </para>
///
/// <para>
/// <b>Lista y control positivo, separados.</b> La lista es solo la deuda; el control positivo de
/// cada instrumento vive aparte, en los tests que alimentan al detector con fuentes sintéticas
/// (nunca con una entrada de la propia lista).
/// </para>
///
/// <para>
/// <b>Volcado opcional.</b> Si la variable de entorno <c>HYDRA_TRINQUETES_VOLCAR</c> apunta a un
/// directorio, cada comprobación escribe ahí la lista medida (<c>&lt;nombre&gt;.txt</c>) sin
/// cambiar su veredicto: sirve para regenerar una lista tras bajar deuda, y el resultado se
/// copia a mano al repositorio, donde el diff lo revisa. El test nunca reescribe la lista.
/// </para>
/// </summary>
internal static class ListaCongelada
{
    private const int MaximoDeLineasEnElMensaje = 40;

    public static string RutaDeLista(string nombre) =>
        Path.Combine(FuentesDeSrc.RaizDelRepositorio(), "tests", "CaeManager.Architecture.Tests", "Congelados", nombre + ".txt");

    /// <summary>
    /// Compara lo medido con la lista versionada y devuelve los desvíos (vacío = coincide).
    /// </summary>
    public static IReadOnlyList<string> Desvios(
        IReadOnlyDictionary<Ubicacion, int> medido,
        IReadOnlyDictionary<Ubicacion, int> listado)
    {
        var desvios = new List<string>();

        foreach (var (ubicacion, n) in medido.OrderBy(p => p.Key.Lugar, StringComparer.Ordinal)
                     .ThenBy(p => p.Key.Simbolo, StringComparer.Ordinal))
        {
            if (!listado.TryGetValue(ubicacion, out var esperado))
                desvios.Add($"NUEVA     {ubicacion} = {n}");
            else if (n > esperado)
                desvios.Add($"CRECE     {ubicacion}: lista {esperado}, medido {n}");
            else if (n < esperado)
                desvios.Add($"BAJA      {ubicacion}: lista {esperado}, medido {n} -> baja la línea a {n}");
        }

        foreach (var (ubicacion, esperado) in listado.OrderBy(p => p.Key.Lugar, StringComparer.Ordinal)
                     .ThenBy(p => p.Key.Simbolo, StringComparer.Ordinal))
        {
            if (!medido.ContainsKey(ubicacion))
                desvios.Add($"OBSOLETA  {ubicacion} = {esperado} -> ya no se mide: borra la línea");
        }

        return desvios;
    }

    /// <summary>
    /// Lee la lista versionada, vuelca la medida si se pidió y devuelve el mensaje de fallo, o
    /// <c>null</c> si coincide.
    /// </summary>
    public static string? Verificar(string nombre, IReadOnlyDictionary<Ubicacion, int> medido, string guiaDeCorreccion)
    {
        VolcarSiSePide(nombre, medido);

        var ruta = RutaDeLista(nombre);
        if (!File.Exists(ruta))
        {
            return $"No existe la lista congelada '{nombre}' ({ruta}). Genera la medida con " +
                   "HYDRA_TRINQUETES_VOLCAR=<directorio> y revísala antes de añadirla.";
        }

        var desvios = Desvios(medido, Leer(File.ReadAllText(ruta)));
        if (desvios.Count == 0)
            return null;

        var visibles = desvios.Take(MaximoDeLineasEnElMensaje).ToList();
        var resto = desvios.Count - visibles.Count;
        return $"La lista congelada '{nombre}' ({ruta}) no coincide con el código ({desvios.Count} desvíos):\n  " +
               string.Join("\n  ", visibles) +
               (resto > 0 ? $"\n  … y {resto} más" : string.Empty) +
               $"\n{guiaDeCorreccion}";
    }

    /// <summary>
    /// Formato: una línea por ubicación, <c>lugar :: símbolo = n</c>; <c>#</c> abre un comentario.
    /// Una línea mal formada, una repetida o un recuento menor que 1 es un error: la lista es un
    /// conjunto de hechos, no prosa.
    /// </summary>
    public static IReadOnlyDictionary<Ubicacion, int> Leer(string texto)
    {
        var resultado = new Dictionary<Ubicacion, int>();
        var numero = 0;

        foreach (var cruda in texto.Split('\n'))
        {
            numero++;
            var linea = cruda.Trim();
            if (linea.Length == 0 || linea.StartsWith('#'))
                continue;

            var separador = linea.IndexOf(" :: ", StringComparison.Ordinal);
            var igual = linea.LastIndexOf(" = ", StringComparison.Ordinal);
            if (separador <= 0 || igual <= separador + 4
                || !int.TryParse(linea[(igual + 3)..], out var n) || n < 1)
            {
                throw new FormatException(
                    $"Línea {numero} de la lista congelada mal formada: '{linea}'. Formato: 'lugar :: símbolo = n' con n >= 1.");
            }

            var ubicacion = new Ubicacion(linea[..separador], linea[(separador + 4)..igual]);
            if (!resultado.TryAdd(ubicacion, n))
                throw new FormatException($"Línea {numero} de la lista congelada repetida: '{ubicacion}'.");
        }

        return resultado;
    }

    public static string Serializar(IReadOnlyDictionary<Ubicacion, int> medido)
    {
        var sb = new StringBuilder();
        foreach (var (ubicacion, n) in medido.OrderBy(p => p.Key.Lugar, StringComparer.Ordinal)
                     .ThenBy(p => p.Key.Simbolo, StringComparer.Ordinal))
        {
            sb.Append(ubicacion.Lugar).Append(" :: ").Append(ubicacion.Simbolo).Append(" = ").Append(n).Append('\n');
        }

        return sb.ToString();
    }

    private static void VolcarSiSePide(string nombre, IReadOnlyDictionary<Ubicacion, int> medido)
    {
        var directorio = Environment.GetEnvironmentVariable("HYDRA_TRINQUETES_VOLCAR");
        if (string.IsNullOrWhiteSpace(directorio))
            return;

        Directory.CreateDirectory(directorio);
        File.WriteAllText(Path.Combine(directorio, nombre + ".txt"), Serializar(medido));
    }
}
