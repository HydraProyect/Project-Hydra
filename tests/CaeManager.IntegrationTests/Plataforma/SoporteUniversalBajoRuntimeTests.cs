using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Plataforma.Commands.AbrirSesionPrivilegiada;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Plataforma;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;
using PlataformaWriter = CaeManager.Infrastructure.Plataforma.PlataformaWriter;
using SesionPrivilegiadaActual = CaeManager.Infrastructure.Plataforma.SesionPrivilegiadaActual;

namespace CaeManager.IntegrationTests.Plataforma;

/// <summary>
/// Soporte TALVEG universal (ADR-011 § 8.9) contra PostgreSQL real y con la
/// identidad de conexión de producción, <c>cae_app_runtime</c> — nunca como
/// propietario ni como superusuario.
///
/// <para>
/// La tesis que se ataca: <b>universal no es directo</b>. Una concesión global
/// de SoporteLectura quita pedir acceso Tenant a Tenant, pero cada entrada sigue
/// siendo una sesión sobre un único Tenant objetivo, y la lectura sigue acotada
/// por RLS a ese Tenant con el rol de solo lectura. Si la concesión global
/// ensanchara la RLS —el error que esta línea tiene prohibido—, la sesión sobre
/// A vería las filas de B y estos tests se pondrían rojos.
/// </para>
///
/// <para>
/// Y la otra mitad: el invariante de alcance global vive también en la base.
/// El <c>WITH CHECK</c> de RLS admite cualquier concesión que nombre a
/// <c>app.usuario_id</c> como beneficiario, así que sin los CHECK de la
/// migración <c>SoporteLecturaGlobalConCheckDeAlcance</c> una escritura que no
/// pasara por el dominio podría acuñar una concesión global de escritura.
/// </para>
/// </summary>
public class SoporteUniversalBajoRuntimeTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantPlataforma = Guid.NewGuid();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _tecnico = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        // Se siembra como propietario, que es lo que hace el migrador: el rol
        // de runtime no crea Tenants.
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);

        await using (var contexto = CrearContexto(_tenantPlataforma, cadena: _cadenaConexion))
        {
            contexto.Tenants.Add(CrearTenant(_tenantPlataforma, "Plataforma", esPlataforma: true));
            contexto.Tenants.Add(CrearTenant(_tenantA, "Tenant A S.L."));
            contexto.Tenants.Add(CrearTenant(_tenantB, "Tenant B S.L."));
            await contexto.SaveChangesAsync();
        }

        await SembrarEmpresaAsync(_tenantA, "Empresa de A S.L.", "B12345674");
        await SembrarEmpresaAsync(_tenantB, "Empresa de B S.L.", "B87654323");
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Bajo_runtime_una_concesion_global_abre_una_sesion_por_tenant_y_cada_una_ve_solo_el_suyo()
    {
        var concesionId = await AutoconcederGlobalComoRuntimeAsync();

        var sesionA = await AbrirComoRuntimeAsync(concesionId, _tenantA);
        var sesionB = await AbrirComoRuntimeAsync(concesionId, _tenantB);

        (await ResolverComoRuntimeAsync(_tenantA, sesionA)).Should().NotBeNull(
            "la sesión sobre A resuelve bajo una concesión global, sin filas de alcance");
        (await ResolverComoRuntimeAsync(_tenantB, sesionB)).Should().NotBeNull();

        (await LeerEmpresasComoSoporteAsync(_tenantA, sesionA)).Should().ContainSingle()
            .Which.Should().Be("Empresa de A S.L.", "la concesión global no ensancha la RLS: la sesión sobre A solo ve A");
        (await LeerEmpresasComoSoporteAsync(_tenantB, sesionB)).Should().ContainSingle()
            .Which.Should().Be("Empresa de B S.L.");
    }

    [Fact]
    public async Task Bajo_runtime_una_sesion_no_se_reutiliza_en_otro_tenant()
    {
        // El token de la sesión nombra A; presentarlo con B como Tenant no la
        // resuelve. La concesión global cubriría B, pero la sesión es de A.
        var concesionId = await AutoconcederGlobalComoRuntimeAsync();
        var sesionA = await AbrirComoRuntimeAsync(concesionId, _tenantA);

        (await ResolverComoRuntimeAsync(_tenantB, sesionA)).Should().BeNull();
    }

    [Fact]
    public async Task Bajo_runtime_la_sesion_sigue_sin_poder_escribir()
    {
        var concesionId = await AutoconcederGlobalComoRuntimeAsync();
        var sesionA = await AbrirComoRuntimeAsync(concesionId, _tenantA);

        await using var contexto = CrearContexto(_tenantA, sesionPrivilegiadaId: sesionA);
        (await contexto.Empresas.CountAsync()).Should().Be(1, "control positivo: la sesión de soporte está abierta");

        contexto.Empresas.Add(Empresa.CrearComoCliente(
            "Escrito por soporte S.L.", "B66666678", esCritico: false, notas: null, ejecutivoUsuarioId: null));
        var guardar = async () => await contexto.SaveChangesAsync();

        (await guardar.Should().ThrowAsync<DbUpdateException>())
            .Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Theory]
    [InlineData(CapacidadPrivilegio.Aprovisionamiento)]
    [InlineData(CapacidadPrivilegio.BreakGlass)]
    [InlineData(CapacidadPrivilegio.Impersonacion)]
    public async Task La_base_rechaza_una_concesion_global_de_una_capacidad_que_no_la_admite(
        CapacidadPrivilegio capacidad)
    {
        // Por debajo del dominio: la fila nace acotada y se le fuerza el alcance
        // global en el change tracker, que es lo que haría cualquier escritura
        // que no pasara por las fábricas. RLS la dejaría pasar —nombra a
        // app.usuario_id—; tiene que pararla el CHECK.
        var concesion = ConcesionPrivilegio.SobreTenants(
            _tecnico, capacidad, [_tenantA], DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddDays(1),
            concedidaPorUsuarioId: _tecnico);

        var excepcion = await GuardarForzandoAsync(concesion, entrada =>
            entrada.Property(nameof(ConcesionPrivilegio.EsAlcanceGlobal)).CurrentValue = true);

        excepcion.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        excepcion.ConstraintName.Should().Be("CK_ConcesionesPrivilegio_AlcanceGlobalSoloCapacidadesAdmitidas");
    }

    [Fact]
    public async Task La_base_rechaza_una_concesion_global_de_SoporteLectura_sin_fin_de_vigencia()
    {
        var concesion = ConcesionPrivilegio.SoporteLecturaGlobal(
            _tecnico, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddDays(1), concedidaPorUsuarioId: _tecnico);

        var excepcion = await GuardarForzandoAsync(concesion, entrada =>
            entrada.Property(nameof(ConcesionPrivilegio.VigenciaHasta)).CurrentValue = null);

        excepcion.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        excepcion.ConstraintName.Should().Be("CK_ConcesionesPrivilegio_SoporteGlobalConVigenciaFinita");
    }

    // ── Andamiaje ──────────────────────────────────────────────────────────

    private async Task<Guid> AutoconcederGlobalComoRuntimeAsync()
    {
        // Control positivo de los CHECK: la forma admitida entra bajo runtime.
        await using var contexto = CrearContexto(_tenantPlataforma);
        var ahora = DateTime.UtcNow;
        var concesion = ConcesionPrivilegio.SoporteLecturaGlobal(
            _tecnico, ahora.AddMinutes(-10), ahora.AddDays(30), concedidaPorUsuarioId: _tecnico);
        contexto.ConcesionesPrivilegio.Add(concesion);
        await contexto.SaveChangesAsync();
        return concesion.Id;
    }

    private async Task<Guid> AbrirComoRuntimeAsync(Guid concesionId, Guid tenantObjetivo)
    {
        await using var contexto = CrearContexto(_tenantPlataforma);
        var handler = new AbrirSesionPrivilegiadaCommandHandler(
            contexto, contexto, new PlataformaWriter(contexto), UsuarioTecnico(), contexto);

        var resultado = await handler.Handle(
            new AbrirSesionPrivilegiadaCommand(concesionId, tenantObjetivo, "Revisar la incidencia 42", HorasDeVentana: 1),
            CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : "");
        return resultado.Valor;
    }

    private async Task<SesionPrivilegiadaActiva?> ResolverComoRuntimeAsync(Guid tenantDelToken, Guid sesionId)
    {
        await using var contexto = CrearContexto(tenantDelToken, sesionPrivilegiadaId: sesionId);
        var resolutor = new SesionPrivilegiadaActual(
            contexto, new ClienteActivoSeleccionadoFalso(tenantDelToken, sesionId), UsuarioTecnico());
        return await resolutor.ObtenerAsync();
    }

    private async Task<List<string>> LeerEmpresasComoSoporteAsync(Guid tenant, Guid sesionId)
    {
        await using var contexto = CrearContexto(tenant, sesionPrivilegiadaId: sesionId);
        return await contexto.Empresas.Select(e => e.RazonSocial).ToListAsync();
    }

    private async Task<PostgresException> GuardarForzandoAsync(
        ConcesionPrivilegio concesion,
        Action<Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<ConcesionPrivilegio>> forzar)
    {
        await using var contexto = CrearContexto(_tenantPlataforma);
        contexto.ConcesionesPrivilegio.Add(concesion);
        forzar(contexto.Entry(concesion));

        var guardar = async () => await contexto.SaveChangesAsync();

        return (await guardar.Should().ThrowAsync<DbUpdateException>())
            .Which.InnerException.Should().BeOfType<PostgresException>().Subject;
    }

    private async Task SembrarEmpresaAsync(Guid tenant, string razonSocial, string cif)
    {
        await using var contexto = CrearContexto(tenant, cadena: _cadenaConexion);
        contexto.Empresas.Add(Empresa.CrearComoCliente(
            razonSocial, cif, esCritico: false, notas: null, ejecutivoUsuarioId: null));
        await contexto.SaveChangesAsync();
    }

    private CurrentUserServiceFalso UsuarioTecnico() =>
        new(_tecnico, rol: null, tenantOrigenId: _tenantPlataforma, tieneDobleFactorActivo: true);

    /// <summary>
    /// Por defecto conecta como <c>cae_app_runtime</c>: la identidad de
    /// producción. Solo la siembra pasa la cadena del propietario.
    /// </summary>
    private CaeManagerDbContext CrearContexto(Guid tenantId, Guid? sesionPrivilegiadaId = null, string? cadena = null)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var seleccion = new ClienteActivoSeleccionadoFalso(
            sesionPrivilegiadaId is null ? null : tenantId, sesionPrivilegiadaId);
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(
                cadena ?? BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion),
                npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantActual),
                new TenantRlsConnectionInterceptor(
                    tenantActual, seleccion, UsuarioTecnico(), BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private static Domain.Tenants.Tenant CrearTenant(Guid id, string nombre, bool esPlataforma = false)
    {
        var tenant = new Domain.Tenants.Tenant(nombre);
        typeof(Domain.Common.Entity).GetProperty(nameof(Domain.Common.Entity.Id))!.SetValue(tenant, id);
        if (esPlataforma) tenant.MarcarComoPlataforma();
        return tenant;
    }

    private sealed class ClienteActivoSeleccionadoFalso(Guid? tenantId, Guid? sesionPrivilegiadaId)
        : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => tenantId;

        public Guid? AsignacionOperacionIdSeleccionada => null;

        public Guid? SesionPrivilegiadaIdSeleccionada => sesionPrivilegiadaId;
    }
}
