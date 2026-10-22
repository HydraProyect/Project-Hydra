namespace CaeManager.Web.Components.DesignSystem;

/// <summary>
/// Los grupos abiertos de un listado con «Agrupar»: todos nacen plegados, los que el usuario abre se recuerdan
/// mientras dure la vista (la instancia de la pantalla) y se reinician al recargar. No viajan en la URL.
/// Estado de cliente de la maqueta: <c>gAbiertos: { [valorDelGrupo]: true }</c>.
/// </summary>
public sealed class GruposAbiertos
{
    private readonly HashSet<string> _abiertos = new(StringComparer.Ordinal);

    public bool EstaAbierto(string clave) => _abiertos.Contains(clave);

    /// <summary>Abre el grupo si estaba plegado y lo pliega si estaba abierto.</summary>
    public void Alternar(string clave)
    {
        if (!_abiertos.Remove(clave))
            _abiertos.Add(clave);
    }

    /// <summary>Pliega todos (p. ej. «Contraer todo»).</summary>
    public void PlegarTodos() => _abiertos.Clear();

    public int Cantidad => _abiertos.Count;
}

/// <summary>Un grupo de un listado: su clave (el valor por el que se agrupa), su nombre y lo que dice su cabecera.</summary>
/// <param name="Total">Filas del grupo entre todas las visibles tras filtrar.</param>
/// <param name="Incidencias">Filas del grupo en rojo (Bloqueo CAE, Vencido o Pendiente).</param>
public sealed record GrupoDeLista(string Clave, string Nombre, int Total, int Incidencias);

/// <summary>
/// Una fila de un listado agrupado en el orden en que se pinta. <paramref name="Cabecera"/> viene rellena solo en la
/// primera fila de cada grupo (la pantalla pinta ahí la cabecera) y <paramref name="Oculta"/> es verdadero si el
/// grupo está plegado (la pantalla no pinta la fila).
/// </summary>
public readonly record struct FilaDeGrupo<T>(T Fila, GrupoDeLista? Cabecera, bool Abierto, bool Oculta);

/// <summary>
/// Agrupa las filas visibles de un listado para «Agrupar» con grupos plegables. Las agrupa TODAS: el tamaño de
/// página no corta los grupos (la pantalla pide todas las filas filtradas cuando agrupa).
/// </summary>
public static class GruposDeLista
{
    /// <summary>
    /// Sin agrupar (<paramref name="agrupado"/> falso) devuelve las filas tal cual, sin cabecera y sin ocultar. Agrupado,
    /// devuelve los grupos en el orden en que aparece su primera fila (la pantalla ya ordena por el campo del grupo),
    /// con la cabecera en la primera fila de cada uno y las filas de los plegados marcadas como ocultas.
    /// </summary>
    public static IEnumerable<FilaDeGrupo<T>> Agrupar<T>(
        IEnumerable<T> filas, bool agrupado, Func<T, string> clave, Func<string, T, string> nombre,
        Func<T, bool> enRojo, GruposAbiertos abiertos)
    {
        ArgumentNullException.ThrowIfNull(filas);
        ArgumentNullException.ThrowIfNull(abiertos);

        if (!agrupado)
        {
            foreach (var fila in filas)
                yield return new FilaDeGrupo<T>(fila, null, true, false);
            yield break;
        }

        foreach (var grupo in filas.GroupBy(clave, StringComparer.Ordinal))
        {
            var abierto = abiertos.EstaAbierto(grupo.Key);
            var primera = true;
            var total = grupo.Count();
            var incidencias = grupo.Count(enRojo);
            foreach (var fila in grupo)
            {
                var cabecera = primera ? new GrupoDeLista(grupo.Key, nombre(grupo.Key, fila), total, incidencias) : null;
                primera = false;
                yield return new FilaDeGrupo<T>(fila, cabecera, abierto, !abierto);
            }
        }
    }
}
