namespace CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;

/// <summary>Etiqueta visible de un Trabajador en un selector, ya garantizada única dentro de su lista.</summary>
public sealed record EtiquetaTrabajador(Guid Id, string Texto);

/// <summary>
/// Único sitio que construye las etiquetas de los selectores de Trabajador (buscadores con
/// autocompletado, <c>&lt;select&gt;</c>, listas de casillas). Garantiza que dentro de una misma
/// lista no haya dos etiquetas iguales: desde P4 (2026-09-23) la etiqueta ya no lleva el DNI, que
/// era lo que antes la hacía única, y un buscador que traduce el texto elegido a un Id no puede
/// distinguir dos opciones con el mismo texto.
/// <list type="number">
/// <item>Si el nombre es único en la lista, la etiqueta es solo el nombre.</item>
/// <item>Si se repite, se añade el empleador (Empresa o Subcontrata) o el Alias, el primero que
/// desempate a todo el grupo; si ninguno solo basta, los dos.</item>
/// <item>Si aun así dos etiquetas coinciden, se añade un sufijo « [n]» determinista, numerado por
/// orden de Id.</item>
/// </list>
/// Con <paramref name="aliasSiempre"/> el Alias entra en la etiqueta aunque el nombre sea único,
/// para que escribirlo en un buscador con autocompletado también encuentre al Trabajador.
/// </summary>
public static class EtiquetasSelectorTrabajador
{
    public static IReadOnlyList<EtiquetaTrabajador> Construir(
        IEnumerable<TrabajadorSelectorDto> trabajadores, bool aliasSiempre = false)
    {
        var lista = trabajadores.ToList();
        var textos = new Dictionary<Guid, string>(lista.Count);

        foreach (var grupo in lista.GroupBy(t => Base(t, aliasSiempre), StringComparer.Ordinal))
        {
            var miembros = grupo.ToList();
            if (miembros.Count == 1)
            {
                textos[miembros[0].Id] = grupo.Key;
                continue;
            }

            Func<TrabajadorSelectorDto, string>[] candidatas =
            [
                t => ConEmpleador(Base(t, aliasSiempre), t),
                t => ConAlias(t),
                t => ConEmpleador(ConAlias(t), t),
            ];

            var elegida = candidatas.FirstOrDefault(c => SonDistintas(miembros.Select(c))) ?? candidatas[^1];
            foreach (var t in miembros)
                textos[t.Id] = elegida(t);
        }

        // Último recurso: mismo nombre, mismo empleador y sin Alias que los distinga (o una
        // coincidencia accidental entre grupos). El sufijo sale del orden por Id, así que es estable.
        foreach (var repetidos in lista.GroupBy(t => textos[t.Id], StringComparer.Ordinal).Where(g => g.Count() > 1).ToList())
        {
            var n = 0;
            foreach (var t in repetidos.OrderBy(t => t.Id))
                textos[t.Id] = $"{repetidos.Key} [{++n}]";
        }

        // Un sufijo podría coincidir con el nombre literal de otro Trabajador (p. ej. uno llamado
        // «Ana [1]»): los miembros de cada grupo que aún colisione reciben su Id.
        foreach (var repetidos in Repetidos(lista, textos))
        {
            foreach (var t in repetidos)
                textos[t.Id] = $"{repetidos.Key} [{t.Id:N}]";
        }

        // Y la etiqueta con Id también podría coincidir con un nombre literal («Ana [1] [<Id>]»).
        // Si queda alguna colisión, TODAS las etiquetas reciben su Id: el sufijo « [<32 hex>]» tiene
        // longitud fija y el Id es único, así que dos etiquetas no pueden coincidir. Termina en un
        // paso, sin bucle.
        if (Repetidos(lista, textos).Count > 0)
        {
            foreach (var t in lista)
                textos[t.Id] = $"{textos[t.Id]} [{t.Id:N}]";
        }

        return lista.Select(t => new EtiquetaTrabajador(t.Id, textos[t.Id])).ToList();
    }

    private static List<IGrouping<string, TrabajadorSelectorDto>> Repetidos(
        List<TrabajadorSelectorDto> lista, Dictionary<Guid, string> textos) =>
        lista.GroupBy(t => textos[t.Id], StringComparer.Ordinal).Where(g => g.Count() > 1).ToList();

    private static string Base(TrabajadorSelectorDto t, bool aliasSiempre) =>
        aliasSiempre ? ConAlias(t) : t.NombreCompleto;

    private static string ConAlias(TrabajadorSelectorDto t) =>
        string.IsNullOrWhiteSpace(t.Alias) ? t.NombreCompleto : $"{t.NombreCompleto} — {t.Alias}";

    private static string ConEmpleador(string etiqueta, TrabajadorSelectorDto t) =>
        string.IsNullOrWhiteSpace(t.EmpleadorNombre) ? etiqueta : $"{etiqueta} ({t.EmpleadorNombre})";

    private static bool SonDistintas(IEnumerable<string> etiquetas)
    {
        var vistas = new HashSet<string>(StringComparer.Ordinal);
        return etiquetas.All(vistas.Add);
    }
}
