using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace CaeManager.Infrastructure.Persistence;

/// <summary>
/// En Blazor Server el <c>DbContext</c> vive lo que el circuito (ver <c>IDescarteCambiosPendientes</c>):
/// una entidad <see cref="IVersionable"/> que el circuito ya cargó o guardó sigue rastreada con los
/// valores de entonces. Generaliza lo que la PR #1661 hizo para la Plataforma CAE del Cliente empresarial.
/// </summary>
public static class ConcurrenciaContexto
{
    /// <summary>Conserva valores y marcas previos, incluidos los cambios pendientes ajenos.</summary>
    public static IInstantaneaCambiosDeComando CapturarRestauracion(DbContext contexto) => new Instantanea(contexto);

    private sealed class Instantanea : IInstantaneaCambiosDeComando
    {
        private readonly DbContext contexto;
        private Dictionary<object, EstadoEntrada> iniciales;
        private bool baseInvalida;

        public Instantanea(DbContext contexto)
        {
            this.contexto = contexto;
            iniciales = Capturar();
            contexto.SavedChanges += Guardado;
        }

        // Entries() detecta antes de capturar: una mutación previa aún sin detectar queda anotada como
        // cambio pendiente ajeno y se conserva. No adelanta nada que el comando no fuera a ver: su
        // SaveChanges y ObtenerActualAsync (Entries<T>()) detectan igual.
        private Dictionary<object, EstadoEntrada> Capturar() => contexto.ChangeTracker.Entries()
            .Select(e => new EstadoEntrada(e.Entity, e.State, e.CurrentValues.Clone(), e.OriginalValues.Clone(),
                e.Properties.Where(p => p.IsModified).Select(p => p.Metadata.Name).ToHashSet(),
                Navegaciones(e)))
            .ToDictionary(e => e.Entidad, ReferenceEqualityComparer.Instance);

        // Un guardado aceptado pasa a ser la nueva base: también cubre altas y bajas,
        // sin resucitar una fila eliminada ni volver a insertar una alta confirmada. El guardado ya
        // está confirmado: un fallo al recapturar no puede salir de SaveChanges como si no lo estuviera,
        // así que invalida la base y un fallo posterior del comando lo descartará todo.
        private void Guardado(object? sender, SavedChangesEventArgs args)
        {
            try
            {
                iniciales = Capturar();
            }
#pragma warning disable CA1031 // El guardado ya está confirmado: ningún fallo de la recaptura puede salir de SaveChanges.
            catch (Exception)
#pragma warning restore CA1031
            {
                baseInvalida = true;
            }
        }

        public void Dispose() => contexto.SavedChanges -= Guardado;

        /// <summary>
        /// Nunca lanza: corre en el finally del behavior y no debe tapar el Result.Fallo ni la
        /// excepción del comando. Si la restauración fina no es posible, vacía el rastreo, que es
        /// lo más conservador: el siguiente comando relee de la base y no escribe nada heredado.
        /// </summary>
        public void Restaurar()
        {
            try
            {
                if (baseInvalida)
                    contexto.ChangeTracker.Clear();
                else
                    RestaurarEntradas();
            }
            catch (ObjectDisposedException)
            {
                // El circuito ya cerró el contexto: no queda nada que pueda guardarse después.
            }
#pragma warning disable CA1031 // Corre en un finally: cualquier fallo cae al descarte completo, sin tapar el original.
            catch (Exception)
#pragma warning restore CA1031
            {
                try
                {
                    contexto.ChangeTracker.Clear();
                }
                catch (ObjectDisposedException)
                {
                    // Igual que arriba: un contexto cerrado ya no guarda nada.
                }
            }
        }

        private void RestaurarEntradas()
        {
            var deteccionAutomatica = contexto.ChangeTracker.AutoDetectChangesEnabled;
            try
            {
                contexto.ChangeTracker.DetectChanges();
                var entradas = contexto.ChangeTracker.Entries().ToList();
                contexto.ChangeTracker.AutoDetectChangesEnabled = false;

                // Lo que el comando creó, editó o borró sin estar antes rastreado no tiene un estado previo
                // que conservar: sale del rastreo y el siguiente comando lo relee de la base. Lo que solo
                // leyó se queda: desacoplarlo dejaría colecciones fantasma en las entidades previas.
                // Un principal que el comando solo leyó no tiene instantánea con la que ver lo que se le quitó
                // de una colección: sale también si una de esas entradas apunta a él por clave ajena.
                var desacopladas = new HashSet<object>(ReferenceEqualityComparer.Instance);
                var tocadas = entradas.Where(e => !iniciales.ContainsKey(e.Entity)
                    && e.State != EntityState.Unchanged).ToList();
                var indices = new Dictionary<IKey, ILookup<string, EntityEntry>>();
                var principales = tocadas.SelectMany(e => PrincipalesRastreados(e, entradas, indices))
                    .Where(p => !iniciales.ContainsKey(p.Entity)).ToList();
                foreach (var entrada in tocadas.Concat(principales))
                {
                    if (!desacopladas.Add(entrada.Entity))
                        continue;
                    entrada.State = EntityState.Detached;
                }

                // Los valores se restauran; las navegaciones en memoria, no. Si el comando dejó en una
                // colección o referencia algo que no está en la base, o quitó algo que sigue en ella, la
                // entidad sale del rastreo por la misma razón, y con ella lo que apunte a algo desacoplado
                // aquí (hasta un punto fijo), para que la siguiente lectura reconstruya el grafo entero.
                // El fixup de una lectura solo añade entidades rastreadas y no cuenta. «rastreadas» queda fija
                // tras el primer paso: una entidad desacoplada en cascada sigue existiendo en la base.
                var rastreadas = contexto.ChangeTracker.Entries().Select(e => e.Entity)
                    .ToHashSet(ReferenceEqualityComparer.Instance);
                bool hubo;
                do
                {
                    hubo = false;
                    foreach (var entrada in contexto.ChangeTracker.Entries().ToList())
                    {
                        iniciales.TryGetValue(entrada.Entity, out var inicial);
                        if (!NavegacionTocada(inicial?.Navegaciones, Navegaciones(entrada), rastreadas, desacopladas))
                            continue;
                        desacopladas.Add(entrada.Entity);
                        entrada.State = EntityState.Detached;
                        hubo = true;
                    }
                }
                while (hubo);

                foreach (var entrada in contexto.ChangeTracker.Entries().ToList())
                {
                    if (!iniciales.TryGetValue(entrada.Entity, out var inicial))
                        continue;
                    entrada.State = inicial.Estado;
                    entrada.OriginalValues.SetValues(inicial.Originales);
                    entrada.CurrentValues.SetValues(inicial.Actuales);
                    if (inicial.Estado is EntityState.Modified or EntityState.Unchanged)
                        foreach (var propiedad in entrada.Properties)
                            propiedad.IsModified = inicial.Modificadas.Contains(propiedad.Metadata.Name);
                }
                // Las entradas previas que ya no están rastreadas las desacoplaron el comando o su pipeline
                // (DescartarLoteFallido, el Clear de TransaccionDeComando tras deshacer la transacción, un
                // descarte explícito) y se quedan fuera: volver a rastrearlas reintroduciría un lote que la
                // base rechazó o filas de una transacción deshecha, y podría chocar con otra instancia de la
                // misma clave. Perder así un cambio pendiente previo nunca escribe nada que nadie pidiera.
            }
            finally
            {
                contexto.ChangeTracker.AutoDetectChangesEnabled = deteccionAutomatica;
            }
        }
    }

    // Por navegación: la entidad referenciada o los elementos de la colección, por identidad.
    private static object[][] Navegaciones(EntityEntry entrada) => entrada.Navigations
        .Select(n => n is CollectionEntry coleccion
            ? coleccion.CurrentValue?.Cast<object>().ToArray() ?? []
            : n.CurrentValue is { } referencia ? [referencia] : Array.Empty<object>())
        .ToArray();

    // Los principales rastreados a los que apunta la entrada por sus claves ajenas, con los valores
    // originales y los actuales (una edición puede haberla movido de principal).
    // El índice por clave principal se construye una vez por clave y restauración: un comando masivo que
    // falla no recorre todo el rastreo por cada alta. Una coincidencia falsa solo desacoplaría de más.
    private static IEnumerable<EntityEntry> PrincipalesRastreados(EntityEntry entrada, List<EntityEntry> entradas,
        Dictionary<IKey, ILookup<string, EntityEntry>> indices)
    {
        foreach (var clave in entrada.Metadata.GetForeignKeys())
        {
            if (!indices.TryGetValue(clave.PrincipalKey, out var indice))
            {
                indice = entradas.Where(e => clave.PrincipalEntityType.IsAssignableFrom(e.Metadata))
                    .ToLookup(e => ValorDeClave(clave.PrincipalKey.Properties.Select(p => e.Property(p.Name).CurrentValue)));
                indices[clave.PrincipalKey] = indice;
            }
            foreach (var valores in new[]
                     {
                         clave.Properties.Select(p => entrada.Property(p.Name).OriginalValue).ToArray(),
                         clave.Properties.Select(p => entrada.Property(p.Name).CurrentValue).ToArray(),
                     })
            {
                if (valores.Any(v => v is null))
                    continue;
                foreach (var principal in indice[ValorDeClave(valores)])
                    yield return principal;
            }
        }
    }

    private static string ValorDeClave(IEnumerable<object?> valores) =>
        string.Join('\u001f', valores.Select(v => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)));

    // Sin instantánea (lo leyó el comando) solo cuenta lo añadido que no está rastreado.
    private static bool NavegacionTocada(object[][]? antes, object[][] despues, HashSet<object> rastreadas,
        HashSet<object> desacopladas)
    {
        for (var i = 0; i < despues.Length; i++)
        {
            var previos = antes is not null && i < antes.Length ? antes[i] : [];
            if (previos.Length == 0 && despues[i].Length == 0)
                continue;
            var conjuntoPrevio = previos.ToHashSet(ReferenceEqualityComparer.Instance);
            var conjuntoActual = despues[i].ToHashSet(ReferenceEqualityComparer.Instance);
            if (despues[i].Any(e => desacopladas.Contains(e) || (!conjuntoPrevio.Contains(e) && !rastreadas.Contains(e)))
                || previos.Any(e => !conjuntoActual.Contains(e) && rastreadas.Contains(e)))
                return true;
        }
        return false;
    }

    private sealed record EstadoEntrada(object Entidad, EntityState Estado, PropertyValues Actuales,
        PropertyValues Originales, HashSet<string> Modificadas, object[][] Navegaciones);

    /// <summary>
    /// Ejecuta la consulta normal, con sus filtros, y si devuelve una instancia que el circuito ya tenía
    /// rastreada la relee: EF no reemplaza sus valores al materializar otra consulta, y el comando
    /// compararía la Version de pantalla con un token viejo (conflicto falso permanente tras la edición de
    /// otra persona). Se relee la instancia sin cambios y la que arrastra un guardado fallido: su Version
    /// actual ya no es la original porque <c>ConcurrenciaOptimistaInterceptor</c> le dio un token nuevo que
    /// la base nunca recibió. Una instancia con cambios de la operación en curso (Version intacta) no se
    /// relee: la relectura los perdería en silencio. Devuelve null si la fila desapareció entre la consulta
    /// y la relectura.
    /// </summary>
    public static async Task<T?> ObtenerActualAsync<T>(
        this DbContext contexto, IQueryable<T> consulta, CancellationToken cancellationToken = default)
        where T : class, IVersionable
    {
        var rastreadas = contexto.ChangeTracker.Entries<T>()
            .Where(e => e.State == EntityState.Unchanged
                        || (e.State == EntityState.Modified && ArrastraGuardadoFallido(e)))
            .Select(e => (object)e.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
        var entidad = await consulta.FirstOrDefaultAsync(cancellationToken);
        if (entidad is null || !rastreadas.Contains(entidad))
            return entidad;

        var entrada = contexto.Entry(entidad);
        await entrada.ReloadAsync(cancellationToken);
        return entrada.State == EntityState.Detached ? null : entidad;
    }

    private static bool ArrastraGuardadoFallido(EntityEntry entrada)
    {
        var version = entrada.Property(nameof(IVersionable.Version));
        return !Equals(version.CurrentValue, version.OriginalValue);
    }

    /// <summary>
    /// Tras un guardado que la base rechazó entero (un <see cref="DbUpdateConcurrencyException"/>, o el
    /// choque de CIF de Z5-7), EF deja sus entradas como estaban: el siguiente SaveChanges del circuito
    /// reintentaría la edición perdida, sus cambios relacionados (por ejemplo, las Relaciones Empresariales
    /// de una edición de Empresa) y la auditoría del intento. Se desacopla todo lo pendiente en el momento
    /// del fallo, incluido lo que los interceptores añadieron durante el guardado; las entidades sin
    /// cambios se conservan. Quien llama relanza la excepción hasta ConcurrenciaBehavior.
    /// </summary>
    public static void DescartarLoteFallido(DbContext contexto) =>
        DescartarEdicionPerdida(contexto, contexto.ChangeTracker.Entries()
            .Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted));

    /// <summary>
    /// Desacopla las entradas indicadas y las auditorías aún sin guardar.
    /// </summary>
    public static void DescartarEdicionPerdida(DbContext contexto, IEnumerable<EntityEntry> entradas)
    {
        var perdidas = entradas.ToList();
        var auditorias = contexto.ChangeTracker.Entries<RegistroAuditoria>()
            .Where(e => e.State == EntityState.Added).ToList();
        foreach (var entrada in perdidas)
            entrada.State = EntityState.Detached;
        foreach (var entrada in auditorias)
            entrada.State = EntityState.Detached;
    }
}
