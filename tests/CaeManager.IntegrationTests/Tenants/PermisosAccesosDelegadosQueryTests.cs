using System.Data.Common;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Commands.DesactivarDelegacionTenant;
using CaeManager.Application.Tenants.Commands.ReactivarDelegacionTenant;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Operaciones;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace CaeManager.IntegrationTests.Tenants;

public class PermisosAccesosDelegadosQueryTests(ITestOutputHelper salida) : IAsyncLifetime
{
    private readonly string _conexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private Guid _operador;
    private readonly Guid _usuario = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var db = CrearContexto();
        await db.Database.MigrateAsync();
        // La siembra usa al propietario de la base; la medición observa SQL real, no simula EF.
        var operador = new Tenant("Operador CAE externo");
        _operador = operador.Id;
        db.Tenants.Add(operador);
        db.Users.Add(new ApplicationUser { Id = _usuario, TenantId = _operador, UserName = "gestor" });
        var rol = await db.Roles.SingleOrDefaultAsync(r => r.Name == Roles.GestorCae);
        if (rol is null)
        {
            rol = new IdentityRole<Guid>(Roles.GestorCae) { Id = Guid.NewGuid() };
            db.Roles.Add(rol);
        }
        db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = _usuario, RoleId = rol.Id });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_conexion);

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public async Task La_carga_individual_crece_con_N_y_el_lote_mantiene_cuatro_lecturas(int n)
    {
        var ids = new List<(Guid Id, bool Activa)>();
        var ahora = DateTime.UtcNow;
        await using (var siembra = CrearContexto())
        {
            for (var i = 0; i < n; i++)
            {
                var propietario = new Tenant($"Tenant propietario {i}");
                var acceso = new DelegacionTenant(_operador, propietario.Id);
                if (i % 2 != 0)
                    acceso.Desactivar();
                var operacion = AsignacionOperacion.Externa(propietario.Id, _operador, ServicioCae.Outbound,
                    AmbitoAsignacion.Universal, ahora, null, ahora);
                siembra.Tenants.Add(propietario);
                siembra.DelegacionesTenant.Add(acceso);
                siembra.AsignacionesOperacion.Add(operacion);
                siembra.AsignacionesCartera.Add(AsignacionCartera.Externa(operacion, _usuario, Roles.GestorCae,
                    AmbitoAsignacion.Universal, ahora, null, ahora));
                ids.Add((acceso.Id, acceso.Activa));
            }
            await siembra.SaveChangesAsync();
        }

        var contador = new ContadorLecturas();
        await using var db = CrearContexto(contador);
        var sesion = new Sesion(_usuario, _operador);
        using var usuarios = new UserManager<ApplicationUser>(
            new UserStore<ApplicationUser, IdentityRole<Guid>, CaeManagerDbContext, Guid>(db),
            Options.Create(new IdentityOptions()), new PasswordHasher<ApplicationUser>(), [], [],
            new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), null!,
            NullLogger<UserManager<ApplicationUser>>.Instance);
        var directorio = new DirectorioUsuariosTenant(usuarios, db,
            new TenantActualAmbiental { TenantId = _operador }, new PuertaAccesoDatos(), db, sesion);
        var catalogo = new CatalogoIncorporacionCartera(db, sesion);
        // Solo se invocan las consultas: las dependencias exclusivas de los comandos no se usan.
        var revocar = new DesactivarDelegacionTenantCommandHandler(
            null!, sesion, null!, db, null!, directorio, db, catalogo);
        var reactivar = new ReactivarDelegacionTenantCommandHandler(
            null!, sesion, null!, db, db, null!, catalogo, null!, directorio, null!, null!, null!);
        PermisosAccesosDelegadosQueryHandler Lote() => new(db, sesion, directorio, catalogo,
            new TenantActualAmbiental { TenantId = _operador }, null!);
        var individuales = new Dictionary<Guid, PermisosAccesoDelegadoDto>();
        foreach (var (id, activa) in ids)
            individuales[id] = activa
                ? new(await revocar.Handle(new PuedeRevocarQuery(id), CancellationToken.None), false)
                : new(false, await reactivar.Handle(new PuedeReactivarQuery(id), CancellationToken.None));

        var antes = contador.Total;
        antes.Should().Be(4 * ids.Count(d => d.Activa) + ids.Count(d => !d.Activa),
            "por acceso activo se leen acceso, cuenta, rol y Cartera; del revocado, ajeno a su origen, solo el acceso");
        contador.Total = 0;
        var lote = await Lote().Handle(new PermisosAccesosDelegadosQuery(ids.Select(d => d.Id).ToArray()), CancellationToken.None);
        var despues = contador.Total;
        lote.Should().BeEquivalentTo(individuales);
        ids.Where(d => d.Activa).Should().OnlyContain(d => lote[d.Id].PuedeRevocar,
            "el control positivo evita medir un lote que no autoriza por un fallo de siembra");
        ids.Where(d => !d.Activa).Should().NotContain(d => lote[d.Id].PuedeReactivar,
            "el Operador CAE externo nunca reactiva");
        despues.Should().Be(4, "una lectura de accesos con plataforma, dos de Identity y una de Carteras, para cualquier N");
        salida.WriteLine($"N={n}: individuales={antes}, lote={despues}");

        contador.Total = 0;
        var vacio = await Lote().Handle(new PermisosAccesosDelegadosQuery([]), CancellationToken.None);
        vacio.Should().BeEmpty();
        contador.Total.Should().Be(0);
    }

    private CaeManagerDbContext CrearContexto(DbCommandInterceptor? contador = null)
    {
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_conexion, pg => pg.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"));
        if (contador is not null)
            opciones.AddInterceptors(contador);
        return new CaeManagerDbContext(opciones.Options, new EphemeralDataProtectionProvider(),
            new TenantActualAmbiental { TenantId = _operador });
    }

    // Mismo patrón que ObtenerClientesAutorizadosQueryTests: cuenta comandos de lectura EF.
    private sealed class ContadorLecturas : DbCommandInterceptor
    {
        public int Total { get; set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Total++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Sesion(Guid usuarioId, Guid origen) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(usuarioId);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(origen);
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(Roles.GestorCae);
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }
}
