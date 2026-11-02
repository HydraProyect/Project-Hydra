using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Commands.CrearAsignacionOperadorDelegado;
using CaeManager.Application.Tenants.Commands.RevocarAsignacionOperadorDelegado;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Operaciones;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// RLS de <c>AsignacionesOperadorDelegado</c> (políticas <c>extremos_de_la_delegacion</c> y
/// <c>organizacion_de_origen_lee_sus_asignaciones</c>) con los roles restringidos reales.
/// Escenario: un Operador CAE externo con delegación hacia los Tenants propietarios A y B, cada una
/// con su Gestor CAE, y una delegación ajena entre otros dos Tenants.
///
/// <para>
/// La fila no lleva Tenant propio: sus dos extremos son los de la delegación a la que apunta. La
/// lectura se abre por el Tenant activo o por el Tenant de origen, igual que en
/// <c>DelegacionesTenant</c>, para que los lectores con join sigan devolviendo lo mismo. Insertar,
/// modificar y borrar, solo por el Tenant activo.
/// </para>
/// </summary>
public class RlsAsignacionesOperadorDelegadoTests : IAsyncLifetime
{
    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _operador = Guid.NewGuid();
    private readonly Guid _a = Guid.NewGuid();
    private readonly Guid _b = Guid.NewGuid();
    private readonly Guid _tercero = Guid.NewGuid();
    private readonly Guid _gestorEnA = Guid.NewGuid();
    private readonly Guid _gestorEnB = Guid.NewGuid();
    private Guid _haciaA;
    private Guid _haciaB;
    private Guid _ajena;
    private Guid _asignacionEnA;
    private Guid _asignacionEnB;
    private Guid _asignacionAjena;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
        var haciaA = new DelegacionTenant(_operador, _a);
        var haciaB = new DelegacionTenant(_operador, _b);
        var ajena = new DelegacionTenant(_tercero, Guid.NewGuid());
        contexto.DelegacionesTenant.AddRange(haciaA, haciaB, ajena);
        var enA = new AsignacionOperadorDelegado(haciaA.Id, _gestorEnA, "GestorCae");
        var enB = new AsignacionOperadorDelegado(haciaB.Id, _gestorEnB, "GestorCae");
        var enAjena = new AsignacionOperadorDelegado(ajena.Id, Guid.NewGuid(), "GestorCae");
        contexto.AsignacionesOperadorDelegadoConRevocadas.AddRange(enA, enB, enAjena);
        await contexto.SaveChangesAsync();
        (_haciaA, _haciaB, _ajena) = (haciaA.Id, haciaB.Id, ajena.Id);
        (_asignacionEnA, _asignacionEnB, _asignacionAjena) = (enA.Id, enB.Id, enAjena.Id);
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    [Fact]
    public async Task Crear_desde_un_Tenant_no_extremo_falla_antes_de_insertar_bajo_runtime()
    {
        await using var conexion = await AbrirAsync("cae_app_runtime", tenant: _a, origen: _operador);
        await using var contexto = CrearContexto(conexion);
        var actual = new TenantActualAmbiental { TenantId = _a };
        var usuario = new CurrentUserServiceFalso(Guid.NewGuid(), tenantOrigenId: _operador);
        var handler = new CrearAsignacionOperadorDelegadoCommandHandler(
            new AsignacionOperadorDelegadoRepository(contexto), new DelegacionTenantRepository(contexto),
            null!, new AsignacionesOperativasWriter(contexto, actual, usuario), new Autoriza(), usuario, contexto, contexto, actual);

        var resultado = await handler.Handle(new(_haciaB, Guid.NewGuid(), "Consulta"), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("AsignacionOperadorDelegado.NoAutorizado");
        contexto.ChangeTracker.HasChanges().Should().BeFalse();
        await using var verificacion = CrearContexto();
        (await verificacion.AsignacionesOperadorDelegadoConRevocadas.CountAsync(a => a.DelegacionTenantId == _haciaB)).Should().Be(1);
    }

    /// <summary>
    /// El Tenant activo es un extremo y la autoridad está resuelta, pero con esta posición
    /// posicion_en_la_asignacion no deja localizar la Asignación de Operación: sin la guarda
    /// el comando acabaría en una excepción de AbrirCarteraOperadorAsync.
    /// </summary>
    [Fact]
    public async Task Crear_con_el_Operador_CAE_activo_y_origen_en_el_propietario_falla_antes_de_escribir_bajo_runtime()
    {
        await using (var siembra = CrearContexto())
        {
            var ahora = DateTime.UtcNow;
            siembra.AsignacionesOperacion.Add(AsignacionOperacion.Externa(
                _b, _operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora));
            await siembra.SaveChangesAsync();
        }

        await using var conexion = await AbrirAsync("cae_app_runtime", tenant: _operador, origen: _b);
        await using var contexto = CrearContexto(conexion);
        (await contexto.AsignacionesOperacion.CountAsync(o => o.PropietarioTenantId == _b))
            .Should().Be(0, "control: esta posición no alcanza la Asignación de Operación");

        var actual = new TenantActualAmbiental { TenantId = _operador };
        var usuario = new CurrentUserServiceFalso(Guid.NewGuid(), tenantOrigenId: _b);
        var handler = new CrearAsignacionOperadorDelegadoCommandHandler(
            new AsignacionOperadorDelegadoRepository(contexto), new DelegacionTenantRepository(contexto),
            null!, new AsignacionesOperativasWriter(contexto, actual, usuario), new Autoriza(), usuario, contexto, contexto, actual);

        var resultado = await handler.Handle(new(_haciaB, Guid.NewGuid(), "Consulta"), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("AsignacionOperadorDelegado.RequiereTenantPropietario");
        contexto.ChangeTracker.HasChanges().Should().BeFalse();
        await using var verificacion = CrearContexto();
        (await verificacion.AsignacionesOperadorDelegadoConRevocadas.CountAsync(a => a.DelegacionTenantId == _haciaB)).Should().Be(1);
    }

    [Fact]
    public async Task Revocar_desde_el_Tenant_de_origen_con_cartera_vigente_falla_sin_escribir_bajo_runtime()
    {
        Guid carteraId;
        await using (var siembra = CrearContexto())
        {
            var ahora = DateTime.UtcNow;
            var operacion = AsignacionOperacion.Externa(_a, _operador, ServicioCae.Outbound,
                AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora);
            var cartera = AsignacionCartera.Externa(operacion, _gestorEnA, "GestorCae",
                AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora);
            siembra.AsignacionesOperacion.Add(operacion);
            siembra.AsignacionesCartera.Add(cartera);
            await siembra.SaveChangesAsync();
            carteraId = cartera.Id;
        }

        await using (var conexion = await AbrirAsync("cae_app_runtime", tenant: _operador, origen: _operador))
        await using (var contexto = CrearContexto(conexion))
        {
            var actual = new TenantActualAmbiental { TenantId = _operador };
            var usuario = new CurrentUserServiceFalso(tenantOrigenId: _operador);
            var handler = new RevocarAsignacionOperadorDelegadoCommandHandler(
                new AsignacionOperadorDelegadoRepository(contexto), new DelegacionTenantRepository(contexto), usuario,
                new AsignacionesOperativasWriter(contexto, actual, usuario), contexto, actual, new Autoriza());
            var resultado = await handler.Handle(new(_asignacionEnA), CancellationToken.None);
            resultado.Error.Codigo.Should().Be("AsignacionOperadorDelegado.RequiereTenantPropietario");
            contexto.ChangeTracker.HasChanges().Should().BeFalse();
        }

        await using var verificacion = CrearContexto();
        (await verificacion.AsignacionesOperadorDelegadoConRevocadas.AnyAsync(a => a.Id == _asignacionEnA)).Should().BeTrue();
        (await verificacion.AsignacionesCartera.SingleAsync(c => c.Id == carteraId)).Estado.Should().Be(EstadoAsignacion.Vigente);
    }

    private sealed class Autoriza : IAutorizacionDelegacionTenant
    {
        public Task<bool> PuedeGestionarDelegacionesAsync(Guid usuarioId, Guid tenantClienteDeleganteId, CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    [Theory]
    [InlineData("cae_app_runtime")]
    [InlineData("cae_app_soporte")]
    public async Task Desde_su_Tenant_el_Operador_CAE_ve_las_asignaciones_de_sus_delegaciones_y_ninguna_ajena(string rol)
    {
        await using var conexion = await AbrirAsync(rol, tenant: _operador, origen: _operador);
        await using var contexto = CrearContexto(conexion);
        (await Visibles(contexto)).Should().BeEquivalentTo([_asignacionEnA, _asignacionEnB]);
    }

    [Theory]
    [InlineData("cae_app_runtime")]
    [InlineData("cae_app_soporte")]
    public async Task Desde_su_Tenant_el_Tenant_propietario_ve_solo_la_asignacion_de_la_delegacion_hacia_el(string rol)
    {
        await using var conexion = await AbrirAsync(rol, tenant: _a, origen: _a);
        await using var contexto = CrearContexto(conexion);
        (await Visibles(contexto)).Should().BeEquivalentTo([_asignacionEnA]);
    }

    /// <summary>
    /// Soporte TALVEG entra con un Tenant de origen que no es extremo de estas delegaciones: la única
    /// coordenada que abre filas es el Tenant del contexto.
    /// </summary>
    [Fact]
    public async Task Bajo_cae_app_soporte_solo_se_ve_el_Tenant_del_contexto()
    {
        var plataforma = Guid.NewGuid();
        await using (var conexion = await AbrirAsync("cae_app_soporte", tenant: _a, origen: plataforma))
        await using (var contexto = CrearContexto(conexion))
        {
            (await Visibles(contexto)).Should().BeEquivalentTo([_asignacionEnA]);
        }

        await using (var conexion = await AbrirAsync("cae_app_soporte", tenant: _b, origen: plataforma))
        await using (var contexto = CrearContexto(conexion))
        {
            (await Visibles(contexto)).Should().BeEquivalentTo([_asignacionEnB]);
        }
    }

    /// <summary>
    /// El caso real de Soporte TALVEG: el Tenant de origen es el de plataforma, extremo de sus propias
    /// delegaciones de soporte. La política de origen le deja leer esas asignaciones, que son de su
    /// propio personal, hacia cualquier Tenant, igual que las filas de <c>DelegacionesTenant</c>. Lo
    /// que no abre son las asignaciones de un Operador CAE externo en otro Tenant que el del contexto.
    /// </summary>
    [Fact]
    public async Task Soporte_con_origen_plataforma_lee_las_asignaciones_de_su_organizacion_y_no_las_de_un_Operador_CAE_ajeno()
    {
        var plataforma = Guid.NewGuid();
        Guid soporteEnA, soporteEnB;
        await using (var bootstrap = CrearContexto())
        {
            var haciaA = DelegacionTenant.ParaSoporte(plataforma, _a);
            var haciaB = DelegacionTenant.ParaSoporte(plataforma, _b);
            bootstrap.DelegacionesTenant.AddRange(haciaA, haciaB);
            var enA = new AsignacionOperadorDelegado(haciaA.Id, Guid.NewGuid(), "Consulta");
            var enB = new AsignacionOperadorDelegado(haciaB.Id, Guid.NewGuid(), "Consulta");
            bootstrap.AsignacionesOperadorDelegadoConRevocadas.AddRange(enA, enB);
            await bootstrap.SaveChangesAsync();
            (soporteEnA, soporteEnB) = (enA.Id, enB.Id);
        }

        await using var conexion = await AbrirAsync("cae_app_soporte", tenant: _a, origen: plataforma);
        await using var contexto = CrearContexto(conexion);
        (await Visibles(contexto)).Should().BeEquivalentTo([_asignacionEnA, soporteEnA, soporteEnB]);
    }

    [Fact]
    public async Task Un_tercero_no_lee_modifica_ni_borra_las_asignaciones_ajenas()
    {
        await using (var conexion = await AbrirAsync("cae_app_runtime", tenant: _tercero, origen: _tercero))
        await using (var contexto = CrearContexto(conexion))
        {
            (await Visibles(contexto)).Should().BeEquivalentTo([_asignacionAjena]);
            var ajena = contexto.AsignacionesOperadorDelegadoConRevocadas.Where(a => a.Id == _asignacionEnA);
            (await ajena.ExecuteUpdateAsync(s => s.SetProperty(a => a.Rol, "Consulta"))).Should().Be(0);
            (await ajena.ExecuteDeleteAsync()).Should().Be(0);
        }

        await using var bootstrap = CrearContexto();
        var fila = await bootstrap.AsignacionesOperadorDelegadoConRevocadas.SingleAsync(a => a.Id == _asignacionEnA);
        fila.Rol.Should().Be("GestorCae");
    }

    [Fact]
    public async Task Un_tercero_no_inserta_asignaciones_en_delegaciones_ajenas()
    {
        await using var conexion = await AbrirAsync("cae_app_runtime", tenant: _tercero, origen: _tercero);
        await using var contexto = CrearContexto(conexion);
        await InsertarRechazado(contexto, _haciaA);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cada_extremo_desde_su_Tenant_inserta_y_retira_asignaciones_de_la_delegacion(bool desdeElOperador)
    {
        var tenant = desdeElOperador ? _operador : _a;
        await using var conexion = await AbrirAsync("cae_app_runtime", tenant: tenant, origen: tenant);
        await using var contexto = CrearContexto(conexion);
        contexto.AsignacionesOperadorDelegadoConRevocadas.Add(
            new AsignacionOperadorDelegado(_haciaA, Guid.NewGuid(), "Consulta"));
        (await contexto.SaveChangesAsync()).Should().Be(1);
        (await contexto.AsignacionesOperadorDelegadoConRevocadas.Where(a => a.Id == _asignacionEnA)
            .ExecuteDeleteAsync()).Should().Be(1);
    }

    /// <summary>
    /// La lectura por el Tenant de origen va en una política solo SELECT: si fuera un OR más de la
    /// política de todas las órdenes, su USING dejaría borrar desde el workspace de A la asignación
    /// en B.
    /// </summary>
    [Fact]
    public async Task Desde_el_workspace_de_A_se_lee_la_asignacion_en_B_pero_no_se_borra_ni_se_inserta()
    {
        await using (var conexion = await AbrirAsync("cae_app_runtime", tenant: _a, origen: _operador))
        await using (var contexto = CrearContexto(conexion))
        {
            (await Visibles(contexto)).Should().BeEquivalentTo([_asignacionEnA, _asignacionEnB]);
            var enB = contexto.AsignacionesOperadorDelegadoConRevocadas.Where(a => a.Id == _asignacionEnB);
            (await enB.ExecuteDeleteAsync()).Should().Be(0);
            (await enB.ExecuteUpdateAsync(s => s.SetProperty(a => a.Rol, "Consulta"))).Should().Be(0);
        }

        await using (var conexion = await AbrirAsync("cae_app_runtime", tenant: _a, origen: _operador))
        await using (var contexto = CrearContexto(conexion))
        {
            await InsertarRechazado(contexto, _haciaB);
        }

        await using var bootstrap = CrearContexto();
        var fila = await bootstrap.AsignacionesOperadorDelegadoConRevocadas.SingleAsync(a => a.Id == _asignacionEnB);
        fila.Rol.Should().Be("GestorCae");
    }

    /// <summary>
    /// El caso que justifica la política del Tenant de origen: <c>/cuenta/cliente-activo</c> y la
    /// revalidación de cada petición autorizan el cambio de A a B por la vía heredada, un join entre
    /// esta tabla y <c>DelegacionesTenant</c>. Sin esa política, el Gestor CAE perdería B al entrar en A.
    /// </summary>
    [Fact]
    public async Task La_via_heredada_autoriza_bajo_runtime_con_el_Tenant_de_origen_firmado()
    {
        await using var conexion = await AbrirAsync("cae_app_runtime", tenant: _a, origen: _operador);
        await using var contexto = CrearContexto(conexion);
        (await TenantsBeneficiariosAutorizados.AutorizadoPorViaHeredadaAsync(
            contexto, _gestorEnB, _b, DateTime.UtcNow, CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task La_via_heredada_no_autoriza_a_un_Tenant_de_origen_que_no_es_extremo()
    {
        await using var conexion = await AbrirAsync("cae_app_runtime", tenant: _tercero, origen: _tercero);
        await using var contexto = CrearContexto(conexion);
        (await TenantsBeneficiariosAutorizados.AutorizadoPorViaHeredadaAsync(
            contexto, _gestorEnB, _b, DateTime.UtcNow, CancellationToken.None)).Should().BeFalse();
    }

    [Theory]
    [InlineData("cae_app_runtime")]
    [InlineData("cae_app_soporte")]
    public async Task Sin_Tenant_activo_ni_de_origen_no_se_ve_ninguna_asignacion(string rol)
    {
        await using var conexion = await AbrirAsync(rol, tenant: null, origen: null);
        await using var contexto = CrearContexto(conexion);
        (await contexto.AsignacionesOperadorDelegadoConRevocadas.CountAsync()).Should().Be(0);
    }

    /// <summary>
    /// Sin clave foránea, nada obliga a EF a insertar antes la delegación. El WITH CHECK busca esa
    /// fila, así que la asignación que se adelanta se rechaza: por eso
    /// <c>CrearClienteDeleganteCommand</c> guarda la delegación antes de añadirla.
    /// </summary>
    [Fact]
    public async Task Insertar_una_asignacion_antes_que_su_delegacion_se_rechaza()
    {
        await using var conexion = await AbrirAsync("cae_app_runtime", tenant: _a, origen: _a);
        await using var contexto = CrearContexto(conexion);
        await InsertarRechazado(contexto, Guid.NewGuid());
    }

    [Fact]
    public async Task No_se_puede_reapuntar_una_asignacion_a_una_delegacion_de_la_que_el_Tenant_activo_no_es_extremo()
    {
        await using var conexion = await AbrirAsync("cae_app_runtime", tenant: _a, origen: _a);
        await using var contexto = CrearContexto(conexion);
        var accion = async () => await contexto.AsignacionesOperadorDelegadoConRevocadas
            .Where(a => a.Id == _asignacionEnA)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.DelegacionTenantId, _ajena));
        (await accion.Should().ThrowAsync<PostgresException>()).Which.SqlState
            .Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Soporte_sigue_sin_poder_escribir_aunque_sea_un_extremo()
    {
        await using var conexion = await AbrirAsync("cae_app_soporte", tenant: _a, origen: _a);
        await using var contexto = CrearContexto(conexion);
        var accion = async () => await contexto.AsignacionesOperadorDelegadoConRevocadas
            .Where(a => a.Id == _asignacionEnA)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Rol, "Consulta"));
        (await accion.Should().ThrowAsync<PostgresException>()).Which.SqlState
            .Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task La_identidad_de_bootstrap_conserva_la_lectura_de_todas_las_asignaciones()
    {
        await using var contexto = CrearContexto();
        (await contexto.AsignacionesOperadorDelegadoConRevocadas.CountAsync()).Should().Be(3);
    }

    private static Task<List<Guid>> Visibles(CaeManagerDbContext contexto) =>
        contexto.AsignacionesOperadorDelegadoConRevocadas.Select(a => a.Id).ToListAsync();

    private static async Task InsertarRechazado(CaeManagerDbContext contexto, Guid delegacionId)
    {
        contexto.AsignacionesOperadorDelegadoConRevocadas.Add(
            new AsignacionOperadorDelegado(delegacionId, Guid.NewGuid(), "Consulta"));
        var accion = async () => await contexto.SaveChangesAsync();
        (await accion.Should().ThrowAsync<DbUpdateException>()).Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    private async Task<NpgsqlConnection> AbrirAsync(string rol, Guid? tenant, Guid? origen)
    {
        var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await ContextoRlsDePruebas.FijarAsync(conexion, tenantId: tenant, tenantOrigenId: origen);
        await using var comando = conexion.CreateCommand();
        comando.CommandText = $"SET ROLE {rol};";
        await comando.ExecuteNonQueryAsync();
        return conexion;
    }

    private CaeManagerDbContext CrearContexto(NpgsqlConnection? conexion = null)
    {
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>();
        if (conexion is null)
            opciones.UseNpgsql(_cadena, n => n.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"));
        else
            opciones.UseNpgsql(conexion, n => n.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"));
        return new CaeManagerDbContext(opciones.Options, new EphemeralDataProtectionProvider(),
            new TenantActualAmbiental());
    }
}
