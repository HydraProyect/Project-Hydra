using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// RLS de <c>DelegacionesTenant</c> (políticas <c>extremos_de_la_delegacion</c> y
/// <c>organizacion_de_origen_lee_sus_delegaciones</c>) con los roles restringidos reales.
/// Escenario: un Operador CAE externo con delegación hacia los Tenants propietarios A y B, y una
/// delegación ajena entre otros dos Tenants.
///
/// <para>
/// La lectura se abre por el Tenant activo o por el Tenant de origen, cuando cualquiera de los dos
/// es un extremo: dentro del workspace de A el Tenant activo es A, y el selector de Tenant
/// beneficiario y <c>/cuenta/cliente-activo</c> necesitan seguir viendo la delegación hacia B.
/// Insertar, modificar y borrar, solo por el Tenant activo.
/// </para>
/// </summary>
public class RlsDelegacionesTenantTests : IAsyncLifetime
{
    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _operador = Guid.NewGuid();
    private readonly Guid _a = Guid.NewGuid();
    private readonly Guid _b = Guid.NewGuid();
    private readonly Guid _tercero = Guid.NewGuid();
    private Guid _haciaA;
    private Guid _haciaB;
    private Guid _ajena;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
        var haciaA = new DelegacionTenant(_operador, _a);
        var haciaB = new DelegacionTenant(_operador, _b);
        var ajena = new DelegacionTenant(_tercero, Guid.NewGuid());
        contexto.DelegacionesTenant.AddRange(haciaA, haciaB, ajena);
        await contexto.SaveChangesAsync();
        (_haciaA, _haciaB, _ajena) = (haciaA.Id, haciaB.Id, ajena.Id);
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    [Theory]
    [InlineData("cae_app_runtime")]
    [InlineData("cae_app_soporte")]
    public async Task Desde_su_Tenant_el_Operador_CAE_lee_todas_sus_delegaciones_y_ninguna_ajena(string rol)
    {
        await using var conexion = await AbrirAsync(rol, tenant: _operador, origen: _operador);
        await using var contexto = CrearContexto(conexion);
        (await contexto.DelegacionesTenant.Select(d => d.Id).ToListAsync()).Should().BeEquivalentTo([_haciaA, _haciaB]);
    }

    [Theory]
    [InlineData("cae_app_runtime")]
    [InlineData("cae_app_soporte")]
    public async Task Desde_su_Tenant_el_Tenant_propietario_lee_solo_la_delegacion_hacia_el(string rol)
    {
        await using var conexion = await AbrirAsync(rol, tenant: _a, origen: _a);
        await using var contexto = CrearContexto(conexion);
        (await contexto.DelegacionesTenant.Select(d => d.Id).ToListAsync()).Should().BeEquivalentTo([_haciaA]);
    }

    /// <summary>
    /// El caso que distingue esta política de una solo por Tenant activo: sin la rama del Tenant
    /// de origen, desde el workspace de A el usuario del Operador CAE dejaría de ver B en el
    /// selector y <c>/cuenta/cliente-activo</c> le negaría el cambio de A a B por vía heredada.
    /// </summary>
    [Fact]
    public async Task Desde_el_workspace_de_A_el_usuario_del_Operador_CAE_sigue_viendo_su_delegacion_hacia_B()
    {
        await using var conexion = await AbrirAsync("cae_app_runtime", tenant: _a, origen: _operador);
        await using var contexto = CrearContexto(conexion);
        (await contexto.DelegacionesTenant.Select(d => d.Id).ToListAsync()).Should().BeEquivalentTo([_haciaA, _haciaB]);
    }

    [Theory]
    [InlineData("cae_app_runtime")]
    [InlineData("cae_app_soporte")]
    public async Task Sin_Tenant_activo_ni_de_origen_no_se_ve_ninguna_delegacion(string rol)
    {
        await using var conexion = await AbrirAsync(rol, tenant: null, origen: null);
        await using var contexto = CrearContexto(conexion);
        (await contexto.DelegacionesTenant.CountAsync()).Should().Be(0);
    }

    /// <summary>
    /// Por el Tenant de origen se lee cualquier delegación de la que ese Tenant es extremo, sea el del
    /// Operador CAE o el del propietario, y ninguna más.
    /// </summary>
    [Fact]
    public async Task El_Tenant_de_origen_del_lado_propietario_solo_abre_la_delegacion_hacia_el()
    {
        await using var conexion = await AbrirAsync("cae_app_runtime", tenant: null, origen: _a);
        await using var contexto = CrearContexto(conexion);
        (await contexto.DelegacionesTenant.Select(d => d.Id).ToListAsync()).Should().BeEquivalentTo([_haciaA]);
    }

    /// <summary>
    /// La lectura por el Tenant de origen va en una política solo SELECT: si fuera un OR más de la
    /// política de todas las órdenes, su USING dejaría borrar desde el workspace de B la delegación
    /// hacia A, o reapuntarla hacia B (el WITH CHECK aceptaría la fila nueva, que ya tiene a B de extremo).
    /// </summary>
    [Fact]
    public async Task Desde_el_workspace_de_B_no_se_borra_ni_se_reapunta_la_delegacion_hacia_A()
    {
        await using (var conexion = await AbrirAsync("cae_app_runtime", tenant: _b, origen: _operador))
        await using (var contexto = CrearContexto(conexion))
        {
            (await contexto.DelegacionesTenant.Where(d => d.Id == _haciaA).ExecuteDeleteAsync()).Should().Be(0);
            (await contexto.DelegacionesTenant.Where(d => d.Id == _haciaA)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.TenantClienteId, _b))).Should().Be(0);
        }

        await using var bootstrap = CrearContexto();
        var haciaA = await bootstrap.DelegacionesTenant.SingleAsync(d => d.Id == _haciaA);
        haciaA.TenantClienteId.Should().Be(_a);
    }

    [Fact]
    public async Task Un_tercero_no_lee_modifica_ni_borra_las_delegaciones_ajenas()
    {
        await using var conexion = await AbrirAsync("cae_app_runtime", tenant: _tercero, origen: _tercero);
        await using var contexto = CrearContexto(conexion);
        (await contexto.DelegacionesTenant.Select(d => d.Id).ToListAsync()).Should().BeEquivalentTo([_ajena]);
        (await contexto.DelegacionesTenant.Where(d => d.Id == _haciaA)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Activa, false))).Should().Be(0);
        (await contexto.DelegacionesTenant.Where(d => d.Id == _haciaA).ExecuteDeleteAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cada_extremo_desde_su_Tenant_inserta_y_actualiza_la_delegacion(bool desdeElOperador)
    {
        var tenant = desdeElOperador ? _operador : _a;
        await using var conexion = await AbrirAsync("cae_app_runtime", tenant: tenant, origen: tenant);
        await using var contexto = CrearContexto(conexion);
        contexto.DelegacionesTenant.Add(new DelegacionTenant(_operador, _a));
        (await contexto.SaveChangesAsync()).Should().Be(1);
        (await contexto.DelegacionesTenant.Where(d => d.Id == _haciaA)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Activa, false))).Should().Be(1);
    }

    [Fact]
    public async Task Un_tercero_no_inserta_delegaciones_ajenas()
    {
        await using var conexion = await AbrirAsync("cae_app_runtime", tenant: _tercero, origen: _tercero);
        await using var contexto = CrearContexto(conexion);
        contexto.DelegacionesTenant.Add(new DelegacionTenant(_operador, _a));
        var accion = async () => await contexto.SaveChangesAsync();
        var error = await accion.Should().ThrowAsync<DbUpdateException>();
        error.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    /// <summary>
    /// El Tenant de origen da lectura, no escritura: desde el workspace de B no se crea ni se
    /// modifica la delegación hacia A, aunque el Operador CAE sea extremo de las dos.
    /// </summary>
    [Fact]
    public async Task Desde_el_workspace_de_B_no_se_escribe_la_delegacion_hacia_A()
    {
        await using (var conexion = await AbrirAsync("cae_app_runtime", tenant: _b, origen: _operador))
        await using (var contexto = CrearContexto(conexion))
        {
            contexto.DelegacionesTenant.Add(new DelegacionTenant(_operador, _a));
            var alta = async () => await contexto.SaveChangesAsync();
            (await alta.Should().ThrowAsync<DbUpdateException>()).Which.InnerException.Should().BeOfType<PostgresException>()
                .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        }

        // La fila se lee por el Tenant de origen, pero la política de todas las órdenes no la deja
        // tocar: el UPDATE no la alcanza y no cambia nada.
        await using (var conexion = await AbrirAsync("cae_app_runtime", tenant: _b, origen: _operador))
        await using (var contexto = CrearContexto(conexion))
        {
            (await contexto.DelegacionesTenant.Where(d => d.Id == _haciaA)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Activa, false))).Should().Be(0);
        }

        await using var bootstrap = CrearContexto();
        (await bootstrap.DelegacionesTenant.SingleAsync(d => d.Id == _haciaA)).Activa.Should().BeTrue();
    }

    [Fact]
    public async Task No_se_puede_cambiar_un_extremo_para_dejar_la_fila_fuera_del_Tenant_activo()
    {
        await using var conexion = await AbrirAsync("cae_app_runtime", tenant: _a, origen: _a);
        await using var contexto = CrearContexto(conexion);
        var accion = async () => await contexto.DelegacionesTenant.Where(d => d.Id == _haciaA)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.TenantClienteId, _tercero));
        (await accion.Should().ThrowAsync<PostgresException>()).Which.SqlState
            .Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Soporte_sigue_sin_poder_escribir_aunque_sea_un_extremo()
    {
        await using var conexion = await AbrirAsync("cae_app_soporte", tenant: _a, origen: _a);
        await using var contexto = CrearContexto(conexion);
        var accion = async () => await contexto.DelegacionesTenant.Where(d => d.Id == _haciaA)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Activa, false));
        (await accion.Should().ThrowAsync<PostgresException>()).Which.SqlState
            .Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task La_identidad_de_bootstrap_conserva_la_lectura_de_todo_el_catalogo()
    {
        await using var contexto = CrearContexto();
        (await contexto.DelegacionesTenant.CountAsync()).Should().Be(3);
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
