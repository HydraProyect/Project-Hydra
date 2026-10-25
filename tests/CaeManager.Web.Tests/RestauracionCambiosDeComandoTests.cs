using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using MediatR;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Web.Tests;

public class RestauracionCambiosDeComandoTests
{
    [Fact]
    public async Task El_contexto_del_circuito_restaura_la_empresa_sin_abrir_conexion()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql("Host=localhost;Database=no_se_abre;Username=no_se_usa").Options;
        await using var contexto = new CaeManagerDbContext(options,
            new EphemeralDataProtectionProvider(), new TenantActualAmbiental { TenantId = Guid.NewGuid() });
        var empresa = Empresa.CrearComoCliente("Empresa de prueba", "B12345674", false, null, null);
        contexto.Attach(empresa);
        var behavior = new RestauracionCambiosDeComandoBehavior<Comando, Result>(new ProveedorPrueba(contexto, soloUnitOfWork: true));
        await behavior.Handle(new Comando(), ct =>
        {
            empresa.Actualizar("Edición rechazada", "B12345674");
            return Task.FromResult(Result.Fallo(Error.Crear("Prueba.Fallo", "Fallo esperado")));
        }, default);
        empresa.RazonSocial.Should().Be("Empresa de prueba");
        contexto.Entry(empresa).State.Should().Be(EntityState.Unchanged);
        (await contexto.SaveChangesAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Un_comando_fallido_no_se_guarda_con_el_siguiente(bool lanza)
    {
        await using var contexto = await CrearContexto();
        var primera = await contexto.Filas.FindAsync(1);
        var segunda = await contexto.Filas.FindAsync(2);
        var behavior = new RestauracionCambiosDeComandoBehavior<Comando, Result>(new ProveedorPrueba(contexto));
        async Task<Result> Fallar(CancellationToken ct)
        {
            primera!.Nombre = "Edición rechazada";
            await Task.Yield();
            if (lanza) throw new InvalidOperationException("Fallo esperado");
            return Result.Fallo(Error.Crear("Prueba.Fallo", "Fallo esperado"));
        }

        if (lanza)
            await FluentActions.Awaiting(() => behavior.Handle(new Comando(), Fallar, default))
                .Should().ThrowAsync<InvalidOperationException>().WithMessage("Fallo esperado");
        else
            (await behavior.Handle(new Comando(), Fallar, default)).EsFallido.Should().BeTrue();

        await behavior.Handle(new Comando(), async ct =>
        {
            segunda!.Nombre = "Edición aceptada";
            await contexto.SaveChangesAsync(ct);
            return Result.Exito();
        }, default);

        (await contexto.Filas.AsNoTracking().SingleAsync(f => f.Id == 1)).Nombre.Should().Be("Primera");
        (await contexto.Filas.AsNoTracking().SingleAsync(f => f.Id == 2)).Nombre.Should().Be("Edición aceptada");
        primera!.Nombre.Should().Be("Primera");
        contexto.Entry(primera).State.Should().Be(EntityState.Unchanged);
    }

    [Fact]
    public async Task El_fallo_restaura_los_cambios_previos_incluso_en_la_misma_entidad()
    {
        await using var contexto = await CrearContexto();
        var primera = (await contexto.Filas.FindAsync(1))!;
        var segunda = (await contexto.Filas.FindAsync(2))!;
        primera.Nombre = "Cambio previo";
        var agregadaPrevia = new Fila { Id = 3, Nombre = "Alta previa" };
        contexto.Add(agregadaPrevia);
        contexto.Remove(segunda);
        var behavior = new RestauracionCambiosDeComandoBehavior<ComandoGenerico, Result<int>>(new ProveedorPrueba(contexto));
        var agregada = new Fila { Id = 4, Nombre = "Alta rechazada" };

        await behavior.Handle(new ComandoGenerico(), ct =>
        {
            primera.Nombre = "Cambio rechazado";
            agregadaPrevia.Nombre = "Alta modificada y rechazada";
            contexto.Entry(segunda).State = EntityState.Unchanged;
            contexto.Add(agregada);
            return Task.FromResult(Result.Fallo<int>(Error.Crear("Prueba.Fallo", "Fallo esperado")));
        }, default);

        primera.Nombre.Should().Be("Cambio previo");
        contexto.Entry(primera).Property(f => f.Nombre).OriginalValue.Should().Be("Primera");
        contexto.Entry(primera).Property(f => f.Nombre).IsModified.Should().BeTrue();
        agregadaPrevia.Nombre.Should().Be("Alta previa");
        contexto.Entry(agregadaPrevia).State.Should().Be(EntityState.Added);
        contexto.Entry(segunda).State.Should().Be(EntityState.Deleted);
        contexto.Entry(agregada).State.Should().Be(EntityState.Detached);
        await contexto.SaveChangesAsync();
        (await contexto.Filas.AsNoTracking().SingleAsync(f => f.Id == 1)).Nombre.Should().Be("Cambio previo");
        (await contexto.Filas.AsNoTracking().Select(f => f.Id).ToListAsync()).Should().BeEquivalentTo([1, 3]);
    }

    [Fact]
    public async Task Restaura_las_entidades_cargadas_y_eliminadas_dentro_del_comando()
    {
        await using var contexto = await CrearContexto();
        contexto.ChangeTracker.Clear();
        var behavior = new RestauracionCambiosDeComandoBehavior<Comando, Result>(new ProveedorPrueba(contexto));
        await behavior.Handle(new Comando(), async ct =>
        {
            var primera = (await contexto.Filas.FindAsync([1], ct))!;
            primera.Nombre = "Rechazada";
            contexto.Remove((await contexto.Filas.FindAsync([2], ct))!);
            return Result.Fallo(Error.Crear("Prueba.Fallo", "Fallo esperado"));
        }, default);
        contexto.ChangeTracker.HasChanges().Should().BeFalse();
        (await contexto.Filas.FindAsync(1))!.Nombre.Should().Be("Primera");
        (await contexto.SaveChangesAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Un_comando_exitoso_conserva_sus_cambios_y_guarda()
    {
        await using var contexto = await CrearContexto();
        var behavior = new RestauracionCambiosDeComandoBehavior<Comando, Result>(new ProveedorPrueba(contexto));
        var resultado = await behavior.Handle(new Comando(), async ct =>
        {
            (await contexto.Filas.FindAsync([1], ct))!.Nombre = "Aceptada";
            await contexto.SaveChangesAsync(ct);
            return Result.Exito();
        }, default);
        resultado.EsExitoso.Should().BeTrue();
        (await contexto.Filas.AsNoTracking().SingleAsync(f => f.Id == 1)).Nombre.Should().Be("Aceptada");
    }

    [Fact]
    public async Task El_fallo_descarta_solo_lo_pendiente_despues_de_un_guardado_confirmado()
    {
        await using var contexto = await CrearContexto();
        var primera = (await contexto.Filas.FindAsync(1))!;
        var behavior = new RestauracionCambiosDeComandoBehavior<Comando, Result>(new ProveedorPrueba(contexto));
        await behavior.Handle(new Comando(), async ct =>
        {
            primera.Nombre = "Confirmada";
            await contexto.SaveChangesAsync(ct);
            primera.Nombre = "Pendiente rechazada";
            return Result.Fallo(Error.Crear("Prueba.Fallo", "Fallo esperado"));
        }, default);
        primera.Nombre.Should().Be("Confirmada");
        contexto.ChangeTracker.HasChanges().Should().BeFalse();
        (await contexto.Filas.AsNoTracking().SingleAsync(f => f.Id == 1)).Nombre.Should().Be("Confirmada");
    }

    [Fact]
    public async Task Un_fallo_posterior_a_un_guardado_no_resucita_bajas_ni_repite_altas()
    {
        await using var contexto = await CrearContexto();
        var primera = (await contexto.Filas.FindAsync(1))!;
        var agregada = new Fila { Id = 3, Nombre = "Alta confirmada" };
        contexto.Add(agregada);
        var behavior = new RestauracionCambiosDeComandoBehavior<Comando, Result>(new ProveedorPrueba(contexto));
        await behavior.Handle(new Comando(), async ct =>
        {
            contexto.Remove(primera);
            await contexto.SaveChangesAsync(ct);
            agregada.Nombre = "Cambio rechazado";
            return Result.Fallo(Error.Crear("Prueba.Fallo", "Fallo esperado"));
        }, default);
        contexto.Entry(primera).State.Should().Be(EntityState.Detached);
        agregada.Nombre.Should().Be("Alta confirmada");
        contexto.ChangeTracker.HasChanges().Should().BeFalse();
        (await contexto.SaveChangesAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Una_consulta_no_captura_ni_descarta_cambios()
    {
        await using var contexto = await CrearContexto();
        var primera = (await contexto.Filas.FindAsync(1))!;
        var behavior = new RestauracionCambiosDeComandoBehavior<Consulta, Result>(new ProveedorPrueba(contexto));
        await behavior.Handle(new Consulta(), ct =>
        {
            primera.Nombre = "Cambio de consulta conservado";
            return Task.FromResult(Result.Fallo(Error.Crear("Prueba.Fallo", "Fallo esperado")));
        }, default);
        contexto.Capturas.Should().Be(0);
        primera.Nombre.Should().Be("Cambio de consulta conservado");
    }

    [Fact]
    public async Task Un_hijo_anadido_a_la_coleccion_por_un_comando_fallido_no_se_guarda_despues()
    {
        await using var contexto = await CrearContexto();
        var padre = await contexto.Padres.Include(p => p.Hijos).SingleAsync(p => p.Id == 1);
        var segunda = (await contexto.Filas.FindAsync(2))!;
        var behavior = new RestauracionCambiosDeComandoBehavior<Comando, Result>(new ProveedorPrueba(contexto));
        await behavior.Handle(new Comando(), ct =>
        {
            padre.Hijos.Add(new Hijo { Id = 11, Nombre = "Alta rechazada" });
            return Task.FromResult(Result.Fallo(Error.Crear("Prueba.Fallo", "Fallo esperado")));
        }, default);

        segunda.Nombre = "Edición aceptada";
        await contexto.SaveChangesAsync();

        (await contexto.Hijos.AsNoTracking().Select(h => h.Id).ToListAsync()).Should().BeEquivalentTo([10]);
        // El padre arrastra el alta en memoria: sale del rastreo y la siguiente lectura lo trae limpio.
        contexto.Entry(padre).State.Should().Be(EntityState.Detached);
        var releido = await contexto.Padres.Include(p => p.Hijos).SingleAsync(p => p.Id == 1);
        releido.Should().NotBeSameAs(padre);
        releido.Hijos.Select(h => h.Id).Should().BeEquivalentTo([10]);
    }

    [Fact]
    public async Task Lo_que_el_comando_fallido_cargo_y_toco_sale_del_rastreo()
    {
        await using var contexto = await CrearContexto();
        var behavior = new RestauracionCambiosDeComandoBehavior<Comando, Result>(new ProveedorPrueba(contexto));
        Padre? padre = null;
        await behavior.Handle(new Comando(), async ct =>
        {
            padre = await contexto.Padres.Include(p => p.Hijos).SingleAsync(p => p.Id == 1, ct);
            padre.Hijos.Add(new Hijo { Id = 12, Nombre = "Alta rechazada" });
            padre.Hijos.First(h => h.Id == 10).Nombre = "Edición rechazada";
            return Result.Fallo(Error.Crear("Prueba.Fallo", "Fallo esperado"));
        }, default);

        contexto.ChangeTracker.Entries().Should().BeEmpty();
        (await contexto.SaveChangesAsync()).Should().Be(0);
        var releido = await contexto.Padres.Include(p => p.Hijos).SingleAsync(p => p.Id == 1);
        releido.Should().NotBeSameAs(padre);
        releido.Hijos.Should().ContainSingle().Which.Nombre.Should().Be("Hijo previo");
    }

    [Fact]
    public async Task La_restauracion_no_tapa_el_fallo_si_el_contexto_ya_se_cerro()
    {
        var contexto = await CrearContexto();
        var primera = (await contexto.Filas.FindAsync(1))!;
        var behavior = new RestauracionCambiosDeComandoBehavior<Comando, Result>(new ProveedorPrueba(contexto));
        var resultado = await behavior.Handle(new Comando(), async ct =>
        {
            primera.Nombre = "Edición rechazada";
            await contexto.DisposeAsync();
            return Result.Fallo(Error.Crear("Prueba.Fallo", "Fallo esperado"));
        }, default);

        resultado.EsFallido.Should().BeTrue();
    }

    [Fact]
    public async Task Un_hijo_quitado_de_la_coleccion_por_un_comando_fallido_no_se_borra_despues()
    {
        await using var contexto = await CrearContexto();
        var padre = await contexto.Padres.Include(p => p.Hijos).SingleAsync(p => p.Id == 1);
        var segunda = (await contexto.Filas.FindAsync(2))!;
        var behavior = new RestauracionCambiosDeComandoBehavior<Comando, Result>(new ProveedorPrueba(contexto));
        await behavior.Handle(new Comando(), ct =>
        {
            padre.Hijos.Clear();
            return Task.FromResult(Result.Fallo(Error.Crear("Prueba.Fallo", "Fallo esperado")));
        }, default);

        segunda.Nombre = "Edición aceptada";
        await contexto.SaveChangesAsync();

        (await contexto.Hijos.AsNoTracking().Select(h => h.Id).ToListAsync()).Should().BeEquivalentTo([10]);
        contexto.Entry(padre).State.Should().Be(EntityState.Detached);
        (await contexto.Padres.Include(p => p.Hijos).SingleAsync(p => p.Id == 1))
            .Hijos.Select(h => h.Id).Should().BeEquivalentTo([10]);
    }

    [Fact]
    public async Task No_vuelve_a_rastrear_lo_que_el_comando_desacoplo_tras_un_guardado_deshecho()
    {
        // Patrón de TransaccionDeComando: el comando guarda dentro de la transacción, falla, la
        // transacción se deshace y el contexto se vacía. La instantánea posterior al guardado no
        // debe devolver al rastreo un alta que la base ya no tiene.
        await using var contexto = await CrearContexto();
        var primera = (await contexto.Filas.FindAsync(1))!;
        var behavior = new RestauracionCambiosDeComandoBehavior<Comando, Result>(new ProveedorPrueba(contexto));
        var alta = new Fila { Id = 5, Nombre = "Alta deshecha" };
        await behavior.Handle(new Comando(), async ct =>
        {
            await using var transaccion = await contexto.Database.BeginTransactionAsync(ct);
            contexto.Add(alta);
            primera.Nombre = "Edición deshecha";
            await contexto.SaveChangesAsync(ct);
            await transaccion.RollbackAsync(ct);
            contexto.ChangeTracker.Clear();
            return Result.Fallo(Error.Crear("Prueba.Fallo", "Fallo esperado"));
        }, default);

        contexto.Entry(alta).State.Should().Be(EntityState.Detached);
        contexto.Entry(primera).State.Should().Be(EntityState.Detached);
        contexto.ChangeTracker.Entries().Should().BeEmpty();
        (await contexto.Filas.FindAsync(1))!.Nombre.Should().Be("Primera");
    }

    [Fact]
    public async Task No_vuelve_a_rastrear_el_lote_que_descarto_un_guardado_rechazado()
    {
        // Patrón de DescartarLoteFallido (#1667): tras el rechazo se desacopla lo pendiente para que
        // el siguiente comando relea la fila; la restauración no debe devolverla al rastreo.
        await using var contexto = await CrearContexto();
        var primera = (await contexto.Filas.FindAsync(1))!;
        var behavior = new RestauracionCambiosDeComandoBehavior<Comando, Result>(new ProveedorPrueba(contexto));
        await behavior.Handle(new Comando(), ct =>
        {
            primera.Nombre = "Edición en conflicto";
            ConcurrenciaContexto.DescartarLoteFallido(contexto);
            return Task.FromResult(Result.Fallo(Error.Crear("Prueba.Fallo", "Fallo esperado")));
        }, default);

        contexto.Entry(primera).State.Should().Be(EntityState.Detached);
        (await contexto.Filas.FindAsync(1)).Should().NotBeSameAs(primera);
    }

    private static async Task<ContextoPrueba> CrearContexto()
    {
        var conexion = new SqliteConnection("Data Source=:memory:");
        await conexion.OpenAsync();
        var contexto = new ContextoPrueba(new DbContextOptionsBuilder<ContextoPrueba>()
            .UseSqlite(conexion, contextOwnsConnection: true).Options);
        await contexto.Database.EnsureCreatedAsync();
        contexto.AddRange(new Fila { Id = 1, Nombre = "Primera" }, new Fila { Id = 2, Nombre = "Segunda" });
        contexto.Add(new Padre { Id = 1, Hijos = { new Hijo { Id = 10, Nombre = "Hijo previo" } } });
        await contexto.SaveChangesAsync();
        contexto.ChangeTracker.Clear();
        return contexto;
    }

    private sealed record Comando : ICommand;
    private sealed record ComandoGenerico : ICommand<int>;
    private sealed record Consulta : IRequest<Result>;
    private sealed class Fila
    {
        public int Id { get; set; }
        public string Nombre { get; set; } = "";
    }

    private sealed class Padre
    {
        public int Id { get; set; }
        public List<Hijo> Hijos { get; } = [];
    }

    private sealed class Hijo
    {
        public int Id { get; set; }
        public int PadreId { get; set; }
        public string Nombre { get; set; } = "";
    }

    private sealed class ContextoPrueba(DbContextOptions<ContextoPrueba> options)
        : DbContext(options), IRestauracionCambiosDeComando, IUnitOfWork
    {
        public DbSet<Fila> Filas => Set<Fila>();
        public DbSet<Padre> Padres => Set<Padre>();
        public DbSet<Hijo> Hijos => Set<Hijo>();
        public int Capturas { get; private set; }
        public IInstantaneaCambiosDeComando CapturarRestauracion()
        {
            Capturas++;
            return ConcurrenciaContexto.CapturarRestauracion(this);
        }
    }

    private sealed class ProveedorPrueba(IRestauracionCambiosDeComando contexto, bool soloUnitOfWork = false) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IRestauracionCambiosDeComando) && !soloUnitOfWork ? contexto
            : serviceType == typeof(IUnitOfWork) ? contexto as IUnitOfWork : null;
    }
}
