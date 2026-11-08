using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Revisión puente de la PR #2074, previa a la ronda 5, hallazgos 1 y 2: las lecturas que deciden bajo el candado de las
/// altas con acreditación (<see cref="ConcurrenciaContexto.ListarActualesAsync{T}"/> y
/// <see cref="ConcurrenciaContexto.RecargarColeccionAsync{T, THijo}"/>) con dos contextos sobre la misma base SQLite en
/// memoria: el del circuito, que ya rastrea las filas, y el de otra operación, que las borra y confirma. La misma
/// propiedad contra PostgreSQL y con <c>TipoDocumento</c> la mide <c>LecturasActualizadasBajoElCandadoTests</c> en la
/// suite de integración.
/// </summary>
public sealed class LecturasActualizadasConcurrenciaContextoTests : IAsyncLifetime
{
    private readonly SqliteConnection conexion = new("Data Source=:memory:");

    public async Task InitializeAsync()
    {
        await conexion.OpenAsync();
        await using var contexto = CrearContexto();
        await contexto.Database.EnsureCreatedAsync();
        contexto.AddRange(new Fila { Id = 1, Grupo = 1 }, new Fila { Id = 2, Grupo = 1 }, new Fila { Id = 3, Grupo = 2 });
        var padre = new Padre { Id = 1 };
        padre.Agregar(new Hijo { Id = 10, Nombre = "Se queda" });
        padre.Agregar(new Hijo { Id = 11, Nombre = "Lo borra otro" });
        contexto.Add(padre);
        await contexto.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await conexion.DisposeAsync();

    [Fact]
    public async Task La_lectura_actualizada_desacopla_la_rastreada_sin_cambios_que_otro_contexto_borro()
    {
        await using var circuito = CrearContexto();
        var borrada = await circuito.Filas.SingleAsync(f => f.Id == 1);
        var otraDelGrupo = await circuito.Filas.SingleAsync(f => f.Id == 2);
        await BorrarDesdeOtroContextoAsync<Fila>(1);

        var actuales = await circuito.ListarActualesAsync(circuito.Filas, f => f.Grupo == 1);

        actuales.Should().ContainSingle().Which.Should().BeSameAs(otraDelGrupo);
        circuito.Entry(borrada).State.Should().Be(EntityState.Detached);
        circuito.Entry(otraDelGrupo).State.Should().Be(EntityState.Unchanged);
    }

    [Fact]
    public async Task La_lectura_actualizada_no_toca_la_rastreada_con_cambios_ni_la_de_fuera_del_conjunto()
    {
        await using var circuito = CrearContexto();
        var conCambios = await circuito.Filas.SingleAsync(f => f.Id == 1);
        conCambios.Nombre = "Cambio de la operación en curso";
        var deOtroGrupo = await circuito.Filas.SingleAsync(f => f.Id == 3);
        await BorrarDesdeOtroContextoAsync<Fila>(1);
        await BorrarDesdeOtroContextoAsync<Fila>(3);

        await circuito.ListarActualesAsync(circuito.Filas, f => f.Grupo == 1);

        circuito.Entry(conCambios).State.Should().Be(EntityState.Modified);
        circuito.Entry(deOtroGrupo).State.Should().Be(EntityState.Unchanged, "no pertenece al conjunto de la consulta");
    }

    [Fact]
    public async Task La_lectura_actualizada_relee_la_rastreada_que_otro_contexto_metio_en_el_conjunto()
    {
        // Con sus valores viejos la rastreada no cumple el filtro, pero la fila confirmada sí: la consulta la devuelve
        // y tiene que salir releída.
        await using var circuito = CrearContexto();
        var rastreada = await circuito.Filas.SingleAsync(f => f.Id == 3);
        await using (var otra = CrearContexto())
        {
            (await otra.Filas.SingleAsync(f => f.Id == 3)).Grupo = 1;
            await otra.SaveChangesAsync();
        }

        var actuales = await circuito.ListarActualesAsync(circuito.Filas, f => f.Grupo == 1);

        actuales.Should().Contain(rastreada);
        rastreada.Grupo.Should().Be(1);
        circuito.Entry(rastreada).State.Should().Be(EntityState.Unchanged);
    }

    [Fact]
    public async Task Recargar_la_coleccion_quita_el_hijo_que_otro_contexto_borro_y_reemplazarla_ya_no_choca()
    {
        await using var circuito = CrearContexto();
        var padre = await circuito.Padres.Include(p => p.Hijos).SingleAsync(p => p.Id == 1);
        var borrado = padre.Hijos.Single(h => h.Id == 11);
        await BorrarDesdeOtroContextoAsync<Hijo>(11);

        await circuito.RecargarColeccionAsync(padre, p => p.Hijos);

        padre.Hijos.Select(h => h.Id).Should().Equal(10);
        circuito.Entry(borrado).State.Should().Be(EntityState.Detached);
        padre.Vaciar();
        padre.Agregar(new Hijo { Id = 12, Nombre = "Nuevo" });
        await circuito.Invoking(c => c.SaveChangesAsync()).Should().NotThrowAsync();
        await using var comprobacion = CrearContexto();
        (await comprobacion.Set<Hijo>().Select(h => h.Id).ToListAsync()).Should().Equal(12);
    }

    [Fact]
    public async Task Sin_recargar_la_coleccion_reemplazarla_choca_con_el_hijo_borrado()
    {
        // Control del instrumento: sin la recarga, la colección conserva el hijo borrado y el guardado intenta borrarlo.
        await using var circuito = CrearContexto();
        var padre = await circuito.Padres.Include(p => p.Hijos).SingleAsync(p => p.Id == 1);
        await BorrarDesdeOtroContextoAsync<Hijo>(11);
        await circuito.Padres.Include(p => p.Hijos).SingleAsync(p => p.Id == 1);

        padre.Vaciar();

        await circuito.Invoking(c => c.SaveChangesAsync()).Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    private async Task BorrarDesdeOtroContextoAsync<T>(int id) where T : class
    {
        await using var otra = CrearContexto();
        otra.Remove((await otra.Set<T>().FindAsync(id))!);
        await otra.SaveChangesAsync();
    }

    private ContextoPrueba CrearContexto() =>
        new(new DbContextOptionsBuilder<ContextoPrueba>().UseSqlite(conexion).Options);

    private sealed class Fila
    {
        public int Id { get; set; }
        public int Grupo { get; set; }
        public string Nombre { get; set; } = "";
    }

    /// <summary>Como <c>TipoDocumento</c> y sus alias: colección de solo lectura sobre un campo privado.</summary>
    private sealed class Padre
    {
        private readonly List<Hijo> hijos = [];
        public int Id { get; set; }
        public IReadOnlyList<Hijo> Hijos => hijos.AsReadOnly();
        public void Agregar(Hijo hijo) => hijos.Add(hijo);
        public void Vaciar() => hijos.Clear();
    }

    private sealed class Hijo
    {
        public int Id { get; set; }
        public int PadreId { get; set; }
        public string Nombre { get; set; } = "";
    }

    private sealed class ContextoPrueba(DbContextOptions<ContextoPrueba> options) : DbContext(options)
    {
        public DbSet<Fila> Filas => Set<Fila>();
        public DbSet<Padre> Padres => Set<Padre>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Fila>().Property(f => f.Id).ValueGeneratedNever();
            modelBuilder.Entity<Hijo>().Property(h => h.Id).ValueGeneratedNever();
            modelBuilder.Entity<Padre>(padre =>
            {
                padre.Property(p => p.Id).ValueGeneratedNever();
                padre.HasMany(p => p.Hijos).WithOne().HasForeignKey(h => h.PadreId).OnDelete(DeleteBehavior.Cascade);
                padre.Navigation(p => p.Hijos).HasField("hijos").UsePropertyAccessMode(PropertyAccessMode.Field);
            });
        }
    }
}
