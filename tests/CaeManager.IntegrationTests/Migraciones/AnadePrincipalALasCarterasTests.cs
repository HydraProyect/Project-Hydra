using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Configurations;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace CaeManager.IntegrationTests.Migraciones;

/// <summary>
/// ADR-011 § 2.7, enmienda del 2026-10-08 (incremento I1): la migración <c>AnadePrincipalALasCarteras</c>
/// añade la marca de principal a la Asignación de Cartera y marca, una sola vez, los datos que ya existen:
/// la Asignación de Operación con <b>una sola</b> cartera viva de Gestor CAE la marca; la que tiene varias
/// no marca ninguna.
///
/// <para>
/// <b>Capa</b>: PostgreSQL real. La base se lleva a la migración anterior, se siembra el estado previo con
/// el esquema de entonces (<see cref="ContextoAnteriorALaMarcaDePrincipal"/>) y se aplica la migración de
/// verdad. Cada Asignación de Operación externa universal vive en su propio Tenant propietario porque el
/// esquema no admite dos sobre el mismo.
/// </para>
/// </summary>
public class AnadePrincipalALasCarterasTests : IAsyncLifetime
{
    private const string MigracionDelCambio = "20261008172124_AnadePrincipalALasCarteras";

    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Tenant _operador = new("Operador CAE externo de la marca");

    // Una cartera por caso; el usuario identifica el caso.
    private readonly Guid _unicoGestor = Guid.NewGuid();
    private readonly Guid _unoDeDos = Guid.NewGuid();
    private readonly Guid _otroDeDos = Guid.NewGuid();
    private readonly Guid _gestorJuntoAOtrosRoles = Guid.NewGuid();
    private readonly Guid _consultaJuntoAlGestor = Guid.NewGuid();
    private readonly Guid _coordinadorJuntoAlGestor = Guid.NewGuid();
    private readonly Guid _gestorYaCerrado = Guid.NewGuid();
    private readonly Guid _unicoSuspendido = Guid.NewGuid();
    private readonly Guid _vigenteJuntoASuspendido = Guid.NewGuid();
    private readonly Guid _suspendidoJuntoAVigente = Guid.NewGuid();
    private readonly Guid _soloCoordinador = Guid.NewGuid();
    private readonly Guid _gestorInterno = Guid.NewGuid();
    private readonly Guid _administradorInterno = Guid.NewGuid();
    private readonly Guid _gestorInternoConDosRoles = Guid.NewGuid();

    private string MigracionAnterior
    {
        get
        {
            using var contexto = ContextoParaMigrar();
            var migraciones = contexto.Database.GetMigrations().ToList();
            var indice = migraciones.IndexOf(MigracionDelCambio);
            indice.Should().BeGreaterThan(0, "la migración del cambio existe en el ensamblado y no es la línea base");
            return migraciones[indice - 1];
        }
    }

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadena);
        await DeshacerElCambioAsync();

        await using (var contexto = NuevoContexto(null))
        {
            contexto.Tenants.Add(_operador);
            await contexto.SaveChangesAsync();
        }

        var ahora = DateTime.UtcNow;

        await SembrarExternaAsync("una sola cartera de Gestor CAE", ahora, o => [Cartera(o, _unicoGestor, Roles.GestorCae, ahora)]);

        await SembrarExternaAsync("dos carteras de Gestor CAE", ahora, o =>
            [Cartera(o, _unoDeDos, Roles.GestorCae, ahora), Cartera(o, _otroDeDos, Roles.GestorCae, ahora)]);

        await SembrarExternaAsync("un Gestor CAE entre otros roles y una cerrada", ahora, o =>
        {
            var cerrada = Cartera(o, _gestorYaCerrado, Roles.GestorCae, ahora);
            cerrada.Cerrar(MotivoCierreAsignacion.RetiradaPorElOperador, ahora);
            return
            [
                Cartera(o, _gestorJuntoAOtrosRoles, Roles.GestorCae, ahora),
                Cartera(o, _consultaJuntoAlGestor, Roles.Consulta, ahora),
                Cartera(o, _coordinadorJuntoAlGestor, Roles.CoordinadorCae, ahora),
                cerrada,
            ];
        });

        await SembrarExternaAsync("una sola, suspendida", ahora, o =>
        {
            var suspendida = Cartera(o, _unicoSuspendido, Roles.GestorCae, ahora);
            suspendida.Suspender();
            return [suspendida];
        });

        await SembrarExternaAsync("una vigente y una suspendida", ahora, o =>
        {
            var suspendida = Cartera(o, _suspendidoJuntoAVigente, Roles.GestorCae, ahora);
            suspendida.Suspender();
            return [Cartera(o, _vigenteJuntoASuspendido, Roles.GestorCae, ahora), suspendida];
        });

        await SembrarExternaAsync("solo un Coordinador CAE", ahora, o => [Cartera(o, _soloCoordinador, Roles.CoordinadorCae, ahora)]);

        await SembrarInternaAsync("raíz con un Gestor CAE", _gestorInterno, [Roles.GestorCae], ahora);
        await SembrarInternaAsync("raíz con un Administrador", _administradorInterno, [Roles.Administrador], ahora);
        await SembrarInternaAsync("raíz con dos roles", _gestorInternoConDosRoles, [Roles.GestorCae, Roles.DireccionCae], ahora);
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    [Fact]
    public async Task Control_positivo_la_base_previa_tiene_las_carteras_y_no_tiene_la_columna()
    {
        await using var contexto = NuevoContexto(null);

        (await contexto.AsignacionesCartera.CountAsync()).Should().Be(14);
        (await ExisteColumnaAsync(contexto)).Should().BeFalse("la siembra se hizo sobre el esquema anterior a la marca");
    }

    [Fact]
    public async Task La_operacion_con_una_sola_cartera_viva_de_Gestor_CAE_la_marca_como_principal()
    {
        await MigrarAsync();
        var principales = await PrincipalesAsync();

        principales.Should().Contain(_unicoGestor);
        principales.Should().Contain(_unicoSuspendido, "suspendida sigue viva: es la única que responde de ese Tenant");
    }

    [Fact]
    public async Task La_operacion_con_varias_carteras_vivas_de_Gestor_CAE_no_marca_ninguna()
    {
        await MigrarAsync();
        var principales = await PrincipalesAsync();

        principales.Should().NotContain([_unoDeDos, _otroDeDos], "no se inventa un responsable por antigüedad");
        principales.Should().NotContain(
            [_vigenteJuntoASuspendido, _suspendidoJuntoAVigente], "la suspendida también cuenta como viva");
    }

    [Fact]
    public async Task Solo_cuentan_las_carteras_vivas_de_Gestor_CAE_y_solo_ellas_se_marcan()
    {
        await MigrarAsync();
        var principales = await PrincipalesAsync();

        principales.Should().Contain(
            _gestorJuntoAOtrosRoles, "ni la de Consulta, ni la de Coordinador CAE, ni la cerrada cuentan como otra cartera de Gestor CAE");
        principales.Should().NotContain([_consultaJuntoAlGestor, _coordinadorJuntoAlGestor, _gestorYaCerrado]);
        principales.Should().NotContain(_soloCoordinador, "la migración solo marca carteras de Gestor CAE");
    }

    [Fact]
    public async Task Una_cartera_interna_sin_rol_propio_se_marca_solo_si_su_usuario_es_Gestor_CAE_y_nada_mas_en_Identity()
    {
        await MigrarAsync();
        var principales = await PrincipalesAsync();

        principales.Should().Contain(_gestorInterno);
        principales.Should().NotContain([_administradorInterno, _gestorInternoConDosRoles]);
    }

    [Fact]
    public async Task Tras_migrar_el_recuento_es_exacto_y_ninguna_operacion_tiene_dos_principales()
    {
        await MigrarAsync();
        await using var contexto = ContextoParaMigrar();

        var marcadas = await contexto.AsignacionesCartera.Where(c => c.EsPrincipal).ToListAsync();
        marcadas.Select(c => c.UsuarioId).Should().BeEquivalentTo(
            [_unicoGestor, _unicoSuspendido, _gestorJuntoAOtrosRoles, _gestorInterno]);
        marcadas.GroupBy(c => c.AsignacionOperacionId).Should().OnlyContain(g => g.Count() == 1);
        (await contexto.AsignacionesCartera.CountAsync()).Should().Be(14, "la migración no crea ni borra carteras");
    }

    [Fact]
    public async Task La_migracion_no_cambia_estado_rol_ni_ambito_de_ninguna_cartera()
    {
        List<(Guid Id, EstadoAsignacion Estado, string? Rol, bool Universal, DateTime? Hasta)> antes;
        await using (var contexto = NuevoContexto(null))
            antes = await InstantaneaAsync(contexto);

        await MigrarAsync();

        await using var despues = NuevoContexto(null);
        (await InstantaneaAsync(despues)).Should().BeEquivalentTo(antes, "la marca no concede nada: nadie gana ni pierde alcance");
    }

    [Fact]
    public async Task La_migracion_crea_el_indice_unico_parcial_y_el_CHECK_y_Down_los_retira_con_la_columna()
    {
        await MigrarAsync();

        await using (var contexto = NuevoContexto(null))
        {
            (await DefinicionDelIndiceAsync(contexto)).Should().NotBeNull()
                .And.Contain("UNIQUE")
                .And.Contain("\"AsignacionOperacionId\"")
                .And.Contain("WHERE").And.Contain("\"EsPrincipal\"").And.Contain("Cerrada");
            (await ExisteRestriccionAsync(contexto)).Should().BeTrue();
            (await ExisteColumnaAsync(contexto)).Should().BeTrue();
        }

        await DeshacerElCambioAsync();

        await using var verificacion = NuevoContexto(null);
        (await DefinicionDelIndiceAsync(verificacion)).Should().BeNull();
        (await ExisteRestriccionAsync(verificacion)).Should().BeFalse();
        (await ExisteColumnaAsync(verificacion)).Should().BeFalse();
        (await verificacion.AsignacionesCartera.CountAsync()).Should().Be(14, "Down no borra carteras");
    }

    // ── Siembra ────────────────────────────────────────────────────────────

    private static AsignacionCartera Cartera(AsignacionOperacion operacion, Guid usuario, string rol, DateTime ahora) =>
        AsignacionCartera.Externa(operacion, usuario, rol, AmbitoAsignacion.Universal, ahora.AddDays(-30), null, ahora);

    private async Task SembrarExternaAsync(
        string caso, DateTime ahora, Func<AsignacionOperacion, AsignacionCartera[]> carteras)
    {
        var propietario = new Tenant($"Tenant propietario: {caso}");
        await using (var contexto = NuevoContexto(null))
        {
            contexto.Tenants.Add(propietario);
            await contexto.SaveChangesAsync();
        }

        await using var delPropietario = NuevoContexto(propietario.Id);
        var operacion = AsignacionOperacion.Externa(
            propietario.Id, _operador.Id, ServicioCae.Outbound, AmbitoAsignacion.Universal, ahora.AddDays(-60), null, ahora.AddDays(-60));
        delPropietario.AsignacionesOperacion.Add(operacion);
        await delPropietario.SaveChangesAsync();

        delPropietario.AsignacionesCartera.AddRange(carteras(operacion));
        await delPropietario.SaveChangesAsync();
    }

    private async Task SembrarInternaAsync(string caso, Guid usuario, string[] roles, DateTime ahora)
    {
        var tenant = new Tenant($"Tenant interno: {caso}");
        await using (var contexto = NuevoContexto(null))
        {
            contexto.Tenants.Add(tenant);
            await contexto.SaveChangesAsync();
        }

        await using var propio = NuevoContexto(tenant.Id);
        propio.Users.Add(new ApplicationUser
        {
            Id = usuario,
            TenantId = tenant.Id,
            UserName = $"{usuario:N}@interno",
            Email = $"{usuario:N}@interno"
        });
        foreach (var nombre in roles)
        {
            var rol = await propio.Roles.SingleOrDefaultAsync(r => r.Name == nombre);
            if (rol is null)
            {
                rol = new IdentityRole<Guid> { Id = Guid.NewGuid(), Name = nombre, NormalizedName = nombre.ToUpperInvariant() };
                propio.Roles.Add(rol);
            }

            propio.UserRoles.Add(new IdentityUserRole<Guid> { UserId = usuario, RoleId = rol.Id });
        }

        var raiz = AsignacionOperacion.Raiz(tenant.Id, ServicioCae.Outbound, ahora.AddDays(-60), ahora.AddDays(-60));
        propio.AsignacionesOperacion.Add(raiz);
        await propio.SaveChangesAsync();

        propio.AsignacionesCartera.Add(
            AsignacionCartera.Interna(raiz, usuario, AmbitoAsignacion.Universal, ahora.AddDays(-30), null, ahora));
        await propio.SaveChangesAsync();
    }

    // ── Arnés ──────────────────────────────────────────────────────────────

    private async Task MigrarAsync()
    {
        await using var contexto = ContextoParaMigrar();
        await contexto.GetService<IMigrator>().MigrateAsync();
    }

    private async Task DeshacerElCambioAsync()
    {
        var anterior = MigracionAnterior;
        await using var contexto = ContextoParaMigrar();
        await contexto.GetService<IMigrator>().MigrateAsync(anterior);
    }

    /// <summary>Con el contexto real: solo él conoce la columna. Solo después de migrar.</summary>
    private async Task<List<Guid>> PrincipalesAsync()
    {
        await using var contexto = ContextoParaMigrar();
        return await contexto.AsignacionesCartera.Where(c => c.EsPrincipal).Select(c => c.UsuarioId).ToListAsync();
    }

    private static async Task<List<(Guid, EstadoAsignacion, string?, bool, DateTime?)>> InstantaneaAsync(CaeManagerDbContext contexto)
    {
        var filas = await contexto.AsignacionesCartera.AsNoTracking().OrderBy(c => c.Id).ToListAsync();
        return filas.Select(c => (c.Id, c.Estado, c.Rol, c.Ambito.EsUniversal, c.VigenciaHasta)).ToList();
    }

    private static Task<bool> ExisteColumnaAsync(CaeManagerDbContext contexto) =>
        ExisteAsync(contexto,
            "SELECT 1 FROM information_schema.columns WHERE table_name = 'AsignacionesCartera' AND column_name = 'EsPrincipal'");

    private static Task<bool> ExisteRestriccionAsync(CaeManagerDbContext contexto) =>
        ExisteAsync(contexto,
            $"SELECT 1 FROM pg_constraint WHERE conname = '{AsignacionCarteraConfiguration.RestriccionPrincipal}' AND contype = 'c'");

    private static async Task<bool> ExisteAsync(CaeManagerDbContext contexto, string sql) =>
        await EscalarAsync(contexto, sql) is not null;

    private static async Task<string?> DefinicionDelIndiceAsync(CaeManagerDbContext contexto) =>
        await EscalarAsync(contexto,
            $"SELECT indexdef FROM pg_indexes WHERE indexname = '{AsignacionCarteraConfiguration.IndicePrincipalPorOperacion}'") as string;

    private static async Task<object?> EscalarAsync(CaeManagerDbContext contexto, string sql)
    {
        var conexion = contexto.Database.GetDbConnection();
        if (conexion.State != System.Data.ConnectionState.Open)
            await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        var valor = await comando.ExecuteScalarAsync();
        return valor is DBNull ? null : valor;
    }

    private CaeManagerDbContext NuevoContexto(Guid? tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadena, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual), new ConcurrenciaOptimistaInterceptor())
            .Options;
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
