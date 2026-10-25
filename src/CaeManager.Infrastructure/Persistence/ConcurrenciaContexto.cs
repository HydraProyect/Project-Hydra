using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

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
            catch (InvalidOperationException)
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
            catch (InvalidOperationException)
            {
                contexto.ChangeTracker.Clear();
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

                // Lo que el comando cargó o creó no tiene un estado previo que conservar: sale del rastreo
                // (altas, ediciones, bajas y lecturas) y el siguiente comando lo relee de la base. Devolverlo
                // a Unchanged dejaría en memoria las colecciones y referencias que el comando tocó.
                foreach (var entrada in entradas.Where(e => !iniciales.ContainsKey(e.Entity)))
                    entrada.State = EntityState.Detached;

                // Lo que el comando o su pipeline desacoplaron (DescartarLoteFallido, el Clear de
                // TransaccionDeComando tras deshacer la transacción, un descarte explícito) se queda
                // fuera: volver a rastrearlo reintroduciría un lote que la base rechazó o filas de una
                // transacción deshecha, y podría chocar con otra instancia de la misma clave. Perder
                // así un cambio pendiente previo nunca escribe nada que nadie pidiera.
                var actuales = entradas.ToDictionary(e => e.Entity, ReferenceEqualityComparer.Instance);
                foreach (var (entidad, inicial) in iniciales)
                {
                    if (!actuales.TryGetValue(entidad, out var entrada))
                        continue;
                    // Los valores se restauran; las navegaciones en memoria, no. Si el comando cambió una
                    // colección o una referencia de una entidad previa, esta sale del rastreo por la misma
                    // razón que las anteriores.
                    if (!MismasNavegaciones(inicial.Navegaciones, Navegaciones(entrada)))
                    {
                        entrada.State = EntityState.Detached;
                        continue;
                    }
                    entrada.State = inicial.Estado;
                    entrada.OriginalValues.SetValues(inicial.Originales);
                    entrada.CurrentValues.SetValues(inicial.Actuales);
                    if (inicial.Estado is EntityState.Modified or EntityState.Unchanged)
                        foreach (var propiedad in entrada.Properties)
                            propiedad.IsModified = inicial.Modificadas.Contains(propiedad.Metadata.Name);
                }
            }
            finally
            {
                contexto.ChangeTracker.AutoDetectChangesEnabled = deteccionAutomatica;
            }
        }
    }

    // Por navegación: la entidad referenciada o los elementos de la colección, por identidad.
    private static object?[][] Navegaciones(EntityEntry entrada) => entrada.Navigations
        .Select(n => n is CollectionEntry coleccion
            ? coleccion.CurrentValue?.Cast<object?>().ToArray() ?? []
            : [n.CurrentValue])
        .ToArray();

    private static bool MismasNavegaciones(object?[][] antes, object?[][] despues) =>
        antes.Length == despues.Length
        && antes.Zip(despues).All(par => par.First.Length == par.Second.Length
            && par.First.ToHashSet(ReferenceEqualityComparer.Instance).SetEquals(par.Second));

    private sealed record EstadoEntrada(object Entidad, EntityState Estado, PropertyValues Actuales,
        PropertyValues Originales, HashSet<string> Modificadas, object?[][] Navegaciones);

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
