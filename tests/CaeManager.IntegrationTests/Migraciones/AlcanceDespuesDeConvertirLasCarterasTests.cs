using CaeManager.Application.Plataforma;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CaeManager.IntegrationTests.Migraciones;

/// <summary>
/// D-7 (2026-10-02): <b>el alcance que queda tras convertir las carteras por Cliente empresarial en carteras
/// del Tenant entero</b>. Se mide el <see cref="AlcanceDatosService"/> real, contra PostgreSQL real, sobre
/// datos sembrados con el esquema ANTERIOR a <c>ConvierteCarterasPorClienteATenantEntero</c> (el estado de
/// staging y producción antes de desplegarla) y migrados hacia delante, para cada rol que interviene:
/// <list type="bullet">
/// <item><b>Gestor CAE</b> con cartera por Cliente empresarial vigente: pasa a todo el Tenant (la decisión del
/// propietario, el efecto buscado); con la cartera caducada, sin cartera o con la de otro Tenant: alcance cero,
/// no gana nada.</item>
/// <item><b>Coordinador CAE</b>: lo hereda de su equipo, también de un Gestor CAE desactivado (decisión C,
/// 2026-09-24); si su equipo no tenía cartera, alcance cero.</item>
/// <item><b>Dirección CAE</b>, <b>Administrador</b> y <b>Consulta</b>: alcance total; ninguna cartera se lo da ni
/// se lo quita.</item>
/// </list>
/// <para>
/// <b>Qué ya no se mide</b>: la comparación «antes ⊆ después» con el lector antiguo. Con la contracción de D-7
/// (incremento 3) el lector ya no interpreta una cartera por Cliente empresarial —el dominio y el CHECK
/// <c>CK_AsignacionesCartera_TenantEnteroSalvoCerrada</c> la hacen irrepresentable—, así que el «antes» solo
/// podía medirse con código retirado. Esa comparación se hizo y se midió en producción al desplegar el
/// incremento 2; lo que sigue vigente, y se comprueba aquí, es el resultado: quién gana el Tenant entero y
/// quién no gana nada.
/// </para>
/// </summary>
public class AlcanceDespuesDeConvertirLasCarterasTests : IAsyncLifetime
{
    private const string MigracionDelCambio = "20261002185604_ConvierteCarterasPorClienteATenantEntero";

    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    private readonly Tenant _tenant = new("Tenant de la medición");
    private readonly Tenant _otro = new("Otro Tenant de la medición");

    private readonly Guid _gestorConUnCliente = Guid.NewGuid();
    private readonly Guid _gestorConOtroCliente = Guid.NewGuid();
    private readonly Guid _gestorSinCartera = Guid.NewGuid();
    private readonly Guid _gestorYaUniversal = Guid.NewGuid();
    private readonly Guid _gestorCaducado = Guid.NewGuid();
    private readonly Guid _gestorDesactivado = Guid.NewGuid();
    private readonly Guid _gestorDeOtroTenant = Guid.NewGuid();
    private readonly Guid _coordinadorConEquipo = Guid.NewGuid();
    private readonly Guid _coordinadorSinCartera = Guid.NewGuid();
    private readonly Guid _coordinadorDelDesactivado = Guid.NewGuid();
    private readonly Guid _direccion = Guid.NewGuid();
    private readonly Guid _administrador = Guid.NewGuid();
    private readonly Guid _consulta = Guid.NewGuid();

    private Guid[] _clientes = [];
    private Guid[] _centros = [];
    private Guid _clienteDelOtroTenant;

    private sealed record Medida(bool AccesoTotal, IReadOnlyList<Guid>? Clientes, IReadOnlyList<Guid>? Centros);

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadena);

        // La base queda como estaba en staging y producción antes de desplegar el cambio.
        await using (var contexto = ContextoParaMigrar())
            await contexto.GetService<IMigrator>().MigrateAsync(MigracionAnterior);

        await using (var contexto = NuevoContexto(null))
        {
            contexto.Tenants.AddRange(_tenant, _otro);
            await contexto.SaveChangesAsync();
        }

        await SembrarTenantAsync();
        await SembrarOtroTenantAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    private string MigracionAnterior
    {
        get
        {
            using var contexto = ContextoParaMigrar();
            var migraciones = contexto.Database.GetMigrations().ToList();
            var indice = migraciones.IndexOf(MigracionDelCambio);
            indice.Should().BeGreaterThan(0);
            return migraciones[indice - 1];
        }
    }

    [Fact]
    public async Task Tras_convertir_las_carteras_por_Cliente_empresarial_gana_el_Tenant_entero_solo_quien_debe()
    {
        // Control de que el arnés sembró de verdad el estado previo: carteras por Cliente empresarial no cerradas.
        await using (var contexto = NuevoContexto(null))
            (await contexto.AsignacionesCartera.CountAsync(c => c.AmbitoRelacionClienteId != null && c.Estado != EstadoAsignacion.Cerrada))
                .Should().BeGreaterThan(0, "el estado previo tiene carteras repartidas por Cliente");

        await using (var contexto = ContextoParaMigrar())
            await contexto.GetService<IMigrator>().MigrateAsync();

        var despues = await MedirTodosAsync();
        var tenantEntero = _clientes;

        using var _ = new FluentAssertions.Execution.AssertionScope();

        // Quién gana el Tenant entero: Gestores CAE con cartera por Cliente empresarial vigente, y los Coordinadores CAE
        // de esos Gestores CAE (incluido un Gestor CAE desactivado: decisión C del 2026-09-24).
        foreach (var gana in new[] { _gestorConUnCliente, _gestorConOtroCliente, _gestorDesactivado, _coordinadorConEquipo, _coordinadorDelDesactivado })
        {
            despues[gana].Clientes.Should().BeEquivalentTo(tenantEntero, $"{Nombre(gana)} gana el Tenant entero");
            despues[gana].Centros.Should().BeEquivalentTo(_centros, $"{Nombre(gana)} gana los Centros del Tenant entero");
            despues[gana].AccesoTotal.Should().BeFalse("la cartera es autoridad de Operación, no alcance total");
        }

        // Quién NO gana nada: sin cartera, con la cartera caducada, equipo sin cartera, otro Tenant.
        foreach (var sinCambios in new[] { _gestorSinCartera, _gestorCaducado, _coordinadorSinCartera, _gestorDeOtroTenant })
        {
            despues[sinCambios].Clientes.Should().BeEmpty($"{Nombre(sinCambios)} no tenía cartera por Cliente vigente: no gana nada");
            despues[sinCambios].Centros.Should().BeEmpty();
            despues[sinCambios].AccesoTotal.Should().BeFalse();
        }

        // Quien ya tenía el Tenant entero lo conserva, y no más.
        despues[_gestorYaUniversal].Clientes.Should().BeEquivalentTo(tenantEntero);
        despues[_gestorYaUniversal].Centros.Should().BeEquivalentTo(_centros);

        // Roles de alcance total: ni la cartera ni su conversión les dan ni les quitan nada.
        foreach (var total in new[] { _direccion, _administrador, _consulta })
        {
            despues[total].AccesoTotal.Should().BeTrue($"{Nombre(total)} sigue con alcance total");
            despues[total].Clientes.Should().BeNull($"{Nombre(total)}: alcance total, sin lista");
        }

        // Nadie cruza de Tenant: el Gestor CAE del otro Tenant ve el suyo, y lo que se midió en _tenant no incluye el ajeno.
        despues.Values.Where(m => m.Clientes is not null).SelectMany(m => m.Clientes!)
            .Should().NotContain(_clienteDelOtroTenant, "la conversión no cruza Tenants");
    }

    private string Nombre(Guid usuario) =>
        usuario == _gestorConUnCliente ? "Gestor CAE con un Cliente"
        : usuario == _gestorConOtroCliente ? "Gestor CAE con otro Cliente"
        : usuario == _gestorSinCartera ? "Gestor CAE sin cartera"
        : usuario == _gestorYaUniversal ? "Gestor CAE ya universal"
        : usuario == _gestorCaducado ? "Gestor CAE con la cartera caducada"
        : usuario == _gestorDesactivado ? "Gestor CAE desactivado"
        : usuario == _gestorDeOtroTenant ? "Gestor CAE de otro Tenant"
        : usuario == _coordinadorConEquipo ? "Coordinador CAE con equipo"
        : usuario == _coordinadorSinCartera ? "Coordinador CAE sin cartera en su equipo"
        : usuario == _coordinadorDelDesactivado ? "Coordinador CAE de un Gestor CAE desactivado"
        : usuario == _direccion ? "Dirección CAE"
        : usuario == _administrador ? "Administrador"
        : usuario == _consulta ? "Consulta"
        : usuario.ToString();

    // ── Medición ───────────────────────────────────────────────────────────

    private async Task<Dictionary<Guid, Medida>> MedirTodosAsync()
    {
        var medidas = new Dictionary<Guid, Medida>();
        foreach (var (usuario, rol) in new (Guid, string)[]
                 {
                     (_gestorConUnCliente, Roles.GestorCae), (_gestorConOtroCliente, Roles.GestorCae),
                     (_gestorSinCartera, Roles.GestorCae), (_gestorYaUniversal, Roles.GestorCae),
                     (_gestorCaducado, Roles.GestorCae), (_gestorDesactivado, Roles.GestorCae),
                     (_gestorDeOtroTenant, Roles.GestorCae),
                     (_coordinadorConEquipo, Roles.CoordinadorCae), (_coordinadorSinCartera, Roles.CoordinadorCae),
                     (_coordinadorDelDesactivado, Roles.CoordinadorCae),
                     (_direccion, Roles.DireccionCae), (_administrador, Roles.Administrador), (_consulta, Roles.Consulta),
                 })
        {
            // El Gestor CAE del otro Tenant se mide en _tenant, donde no tiene ninguna cartera.
            medidas[usuario] = await MedirAsync(usuario, rol, _tenant);
        }

        return medidas;
    }

    private async Task<Medida> MedirAsync(Guid usuario, string rol, Tenant tenant)
    {
        await using var contexto = NuevoContexto(tenant.Id);
        var servicio = new AlcanceDatosService(
            contexto, new CurrentUserServiceFalso(usuario, rol, tenantOrigenId: tenant.Id),
            new TenantActualAmbiental { TenantId = tenant.Id }, new SesionPrivilegiadaAusente());

        return new Medida(
            await servicio.TieneAccesoTotalAsync(),
            await servicio.ObtenerClienteIdsVisiblesAsync(),
            await servicio.ObtenerCentroIdsVisiblesAsync());
    }

    // ── Estado previo ──────────────────────────────────────────────────────

    private async Task SembrarTenantAsync()
    {
        var ahora = DateTime.UtcNow;
        await using var contexto = NuevoContexto(_tenant.Id);

        var raiz = AsignacionOperacion.Raiz(_tenant.Id, ServicioCae.Outbound, ahora.AddDays(-60), ahora.AddDays(-60));
        contexto.AsignacionesOperacion.Add(raiz);

        // Seis Clientes empresariales: tres con un Gestor CAE responsable, uno para la cartera caducada y dos sin
        // nadie (el reparto por Cliente empresarial admite un solo responsable vigente por Cliente).
        var clientes = Enumerable.Range(0, 6)
            .Select(i => Empresa.CrearComoCliente($"Cliente de la medición {i}", DatosPruebaSeeder.GenerarCifValido(8_100_000 + i), false, null, null))
            .ToList();
        var propia = new Empresa("Empresa propia de la medición", DatosPruebaSeeder.GenerarCifValido(8_199_999));
        contexto.Empresas.AddRange(clientes);
        contexto.Empresas.Add(propia);
        await contexto.SaveChangesAsync();

        var centros = clientes.Select(c => new Centro(c.Id, propia.Id, $"Centro de {c.RazonSocial}")).ToList();
        contexto.Centros.AddRange(centros);
        await contexto.SaveChangesAsync();
        _clientes = clientes.Select(c => c.Id).ToArray();
        _centros = centros.Select(c => c.Id).ToArray();

        var cuentas = new List<ApplicationUser>();
        foreach (var (id, coordinador, desactivado) in new (Guid, Guid?, bool)[]
                 {
                     (_gestorConUnCliente, _coordinadorConEquipo, false), (_gestorConOtroCliente, _coordinadorConEquipo, false),
                     (_gestorSinCartera, _coordinadorSinCartera, false), (_gestorYaUniversal, null, false),
                     (_gestorCaducado, null, false), (_gestorDesactivado, _coordinadorDelDesactivado, true),
                     (_coordinadorConEquipo, null, false), (_coordinadorSinCartera, null, false),
                     (_coordinadorDelDesactivado, null, false),
                     (_direccion, null, false), (_administrador, null, false), (_consulta, null, false),
                 })
        {
            var usuario = new ApplicationUser
            {
                Id = id,
                TenantId = _tenant.Id,
                UserName = $"u-{id:N}@medicion.test",
                Email = $"u-{id:N}@medicion.test",
                CoordinadorUsuarioId = coordinador,
            };
            if (desactivado) usuario.Desactivar();
            cuentas.Add(usuario);
        }
        await SiembraDeCuentasEnEsquemaAnterior.InsertarAsync(contexto, [.. cuentas]);

        AsignacionCartera PorCliente(Guid usuario, int cliente, DateTime? hasta = null, DateTime? desde = null) =>
            CarteraLegadaPorCliente.Interna(raiz, usuario, _clientes[cliente], desde ?? ahora.AddDays(-30), hasta, ahora);

        contexto.AsignacionesCartera.AddRange(
            PorCliente(_gestorConUnCliente, 0),
            PorCliente(_gestorConOtroCliente, 1),
            PorCliente(_gestorDesactivado, 2),
            PorCliente(_gestorCaducado, 4, hasta: ahora.AddDays(-1)),
            // El Gestor CAE ya universal: el Tenant entero antes y después, y sin cartera por Cliente.
            AsignacionCartera.Interna(raiz, _gestorYaUniversal, AmbitoAsignacion.Universal, ahora.AddDays(-10), null, ahora));
        await contexto.SaveChangesAsync();
    }

    private async Task SembrarOtroTenantAsync()
    {
        var ahora = DateTime.UtcNow;
        await using var contexto = NuevoContexto(_otro.Id);

        var raiz = AsignacionOperacion.Raiz(_otro.Id, ServicioCae.Outbound, ahora.AddDays(-60), ahora.AddDays(-60));
        contexto.AsignacionesOperacion.Add(raiz);
        var cliente = Empresa.CrearComoCliente("Cliente del otro Tenant", DatosPruebaSeeder.GenerarCifValido(8_188_888), false, null, null);
        contexto.Empresas.Add(cliente);
        await contexto.SaveChangesAsync();
        _clienteDelOtroTenant = cliente.Id;

        await SiembraDeCuentasEnEsquemaAnterior.InsertarAsync(contexto, new ApplicationUser
        {
            Id = _gestorDeOtroTenant,
            TenantId = _otro.Id,
            UserName = "otro@medicion.test",
            Email = "otro@medicion.test",
        });
        contexto.AsignacionesCartera.Add(CarteraLegadaPorCliente.Interna(
            raiz, _gestorDeOtroTenant, cliente.Id, ahora.AddDays(-30), null, ahora));
        await contexto.SaveChangesAsync();
    }

    private CaeManagerDbContext NuevoContexto(Guid? tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadena, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual), new ConcurrenciaOptimistaInterceptor())
            .Options;
        // Esta clase retrocede a un esquema anterior a la columna EsPrincipal: ver ContextoAnteriorALaMarcaDePrincipal.
        return new ContextoAnteriorALaMarcaDePrincipal(opciones, new EphemeralDataProtectionProvider(), tenantActual);
    }

    /// <summary>El contexto real: EF solo descubre las migraciones con el tipo exacto del contexto.</summary>
    private CaeManagerDbContext ContextoParaMigrar()
    {
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadena, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), new TenantActualAmbiental { TenantId = null });
    }
}
