using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using CaeManager.Application.Inbound.Comercial.Commands.ContratarLineaInbound;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Comercial;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Configurations;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using CaeManager.Infrastructure.Plataforma;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Inbound.Comercial;

/// <summary>
/// B-04a: la contratación de la línea Inbound (<see cref="ContratarLineaInboundCommand"/>, decisiones D-B y D-C del
/// 2026-11-15) contra PostgreSQL real, componiendo el flujo como <c>EncargoAdministracionPorAprovisionamientoTests</c>:
/// <see cref="IMediator"/> real, <see cref="ISesionPrivilegiadaActual"/> real, la elevación real y el
/// <see cref="TenantRlsConnectionInterceptor"/> real.
///
/// <para>
/// Lo que solo esta composición prueba: que el handler escribe como <c>cae_app_aprovisionamiento</c> con el único
/// privilegio que la migración le concede (INSERT, sin SELECT), que el INSERT de EF no necesita leer, que el Tenant de la
/// fila es el de la Sesión Privilegiada, y que el índice único parcial impone un Producto Contratado de la línea Inbound
/// vigente por Tenant. Y la otra mitad, con SQL directo bajo cada rol: el runtime del propio Tenant no puede concederse la
/// línea, y el rol elevado no escribe fuera del Tenant de su contexto firmado ni lee ni cambia lo que inserta.
/// </para>
/// </summary>
public class ContratarLineaInboundPorAprovisionamientoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _actorPlataforma = Guid.NewGuid();
    private readonly Tenant _plataforma = new("Organización de plataforma de prueba", esPlataforma: true);
    private readonly Tenant _tce = new("Tenant TCE de prueba");
    private readonly Tenant _otro = new("Otro Tenant de prueba");

    private Guid _sesionDeAprovisionamiento;
    private Guid _sesionDeLectura;

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);
        await using var contexto = CrearContexto(_tce.Id, new Seleccion(_tce.Id, null));

        var ahora = DateTime.UtcNow;
        contexto.Tenants.AddRange(_plataforma, _tce, _otro);

        var aprovisionamiento = ConcesionPrivilegio.SobreTenants(
            _actorPlataforma, CapacidadPrivilegio.Aprovisionamiento, [_tce.Id],
            vigenciaDesde: ahora.AddMinutes(-10), vigenciaHasta: ahora.AddHours(4));
        var lectura = ConcesionPrivilegio.SobreTenants(
            _actorPlataforma, CapacidadPrivilegio.SoporteLectura, [_tce.Id],
            vigenciaDesde: ahora.AddMinutes(-10), vigenciaHasta: ahora.AddHours(4));
        contexto.ConcesionesPrivilegio.AddRange(aprovisionamiento, lectura);

        var sesionDeAprovisionamiento = SesionPrivilegiada.Abrir(
            aprovisionamiento, _tce.Id, "Alta de la línea Inbound", ahora,
            ventana: TimeSpan.FromHours(1), usuarioSimuladoId: null, ticket: null);
        var sesionDeLectura = SesionPrivilegiada.Abrir(
            lectura, _tce.Id, "Consulta de soporte", ahora,
            ventana: TimeSpan.FromHours(1), usuarioSimuladoId: null, ticket: null);
        contexto.SesionesPrivilegiadas.AddRange(sesionDeAprovisionamiento, sesionDeLectura);

        await contexto.SaveChangesAsync();
        _sesionDeAprovisionamiento = sesionDeAprovisionamiento.Id;
        _sesionDeLectura = sesionDeLectura.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Una_sesion_de_Aprovisionamiento_contrata_la_linea_Inbound_una_sola_vez_por_el_pipeline_completo()
    {
        // Sin este control el test no distinguiría la elevación: si el rol de lectura de la sesión pudiera insertar, la
        // contratación saldría bien aunque el comando no llegara a elevarse.
        (await TienePrivilegioAsync("cae_app_soporte", "INSERT")).Should().BeFalse("control: la sesión lee como cae_app_soporte");
        (await TienePrivilegioAsync("cae_app_aprovisionamiento", "SELECT")).Should().BeFalse(
            "control: el rol elevado no lee la tabla, así que el alta no puede depender de una lectura previa");

        var primera = await EnviarAsync(_sesionDeAprovisionamiento, rol: null,
            new ContratarLineaInboundCommand("cus_PagadorTce", TramoTrabajadoresActivos.De41A80, ModalidadSuscripcionAlta.Anual));
        primera.EsExitoso.Should().BeTrue(primera.EsFallido ? $"código: {primera.Error.Codigo}" : string.Empty);

        var segunda = await EnviarAsync(_sesionDeAprovisionamiento, rol: null,
            new ContratarLineaInboundCommand("cus_OtroPagador", TramoTrabajadoresActivos.De1A10, ModalidadSuscripcionAlta.Mensual));
        segunda.Error.Should().Be(ErroresContratarLineaInbound.YaContratada, "un solo Producto Contratado Inbound vigente por Tenant (D-B)");

        var filas = await FilasComoPropietarioAsync();
        var fila = filas.Should().ContainSingle().Subject;
        fila.Id.Should().Be(primera.Valor);
        fila.TenantId.Should().Be(_tce.Id, "el Tenant beneficiario es el objetivo de la Sesión Privilegiada");
        fila.Linea.Should().Be(LineaProductoContratado.Inbound);
        fila.PagadorStripeCustomerId.Should().Be("cus_PagadorTce");
        fila.Tramo.Should().Be(TramoTrabajadoresActivos.De41A80);
        fila.Modalidad.Should().Be(ModalidadSuscripcionAlta.Anual);
        fila.Estado.Should().Be(EstadoProductoContratado.Vigente);
    }

    [Fact]
    public async Task Una_sesion_de_soporte_de_solo_lectura_no_contrata_la_linea_Inbound()
    {
        var resultado = await EnviarAsync(_sesionDeLectura, rol: null,
            new ContratarLineaInboundCommand("cus_PagadorTce", TramoTrabajadoresActivos.De41A80, ModalidadSuscripcionAlta.Anual));

        // Lo deniega AutorizacionEscrituraBehavior antes del handler: SoporteLectura no tiene camino de escritura.
        resultado.Error.Codigo.Should().Be("Autorizacion.SesionPrivilegiadaSoloLectura",
            "la capacidad de la sesión no es Aprovisionamiento");
        (await FilasComoPropietarioAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Un_Administrador_del_propio_Tenant_sin_Sesion_Privilegiada_no_se_concede_la_linea_Inbound()
    {
        // AutorizacionEscrituraBehavior deja pasar el rol con escritura; el handler deniega, y aunque no lo hiciera,
        // el runtime no tiene INSERT (lo prueba el test de SQL directo de abajo).
        var resultado = await EnviarAsync(sesion: null, rol: "Administrador",
            new ContratarLineaInboundCommand("cus_PagadorTce", TramoTrabajadoresActivos.De41A80, ModalidadSuscripcionAlta.Anual));

        resultado.Error.Should().Be(ErroresContratarLineaInbound.NoAutorizado);
        (await FilasComoPropietarioAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Con_SQL_directo_el_runtime_no_inserta_y_el_rol_elevado_solo_inserta_en_su_Tenant_y_una_vez()
    {
        // El runtime con el contexto firmado del propio Tenant: sin INSERT, aunque el WITH CHECK lo admitiría.
        (await SqlStateComoAsync("cae_app_runtime", _tce.Id, InsertInbound(_tce.Id))).SqlState
            .Should().Be(PostgresErrorCodes.InsufficientPrivilege, "un Tenant no se concede su propia línea Inbound");

        // Control positivo del mismo INSERT con el rol elevado: sin él, los rechazos de abajo pasarían igual con un rol
        // que no pudiera insertar nada.
        (await SqlStateComoAsync("cae_app_aprovisionamiento", _tce.Id, InsertInbound(_tce.Id)))
            .Should().Be(((string?)null, 1, (string?)null));

        // Fuera del Tenant del contexto firmado: el WITH CHECK de aislamiento_tenant lo rechaza.
        (await SqlStateComoAsync("cae_app_aprovisionamiento", _tce.Id, InsertInbound(_otro.Id))).SqlState
            .Should().Be(PostgresErrorCodes.InsufficientPrivilege);

        // Dos vigentes del mismo Tenant: el índice único parcial. Uno finalizado no cuenta.
        var dosVigentes = await SqlStateComoAsync("cae_app_aprovisionamiento", _tce.Id,
            InsertInbound(_tce.Id) + InsertInbound(_tce.Id));
        dosVigentes.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        dosVigentes.Restriccion.Should().Be(ProductoContratadoConfiguration.IndiceInboundVigentePorTenant);
        (await SqlStateComoAsync("cae_app_aprovisionamiento", _tce.Id,
                InsertInbound(_tce.Id, finalizado: true) + InsertInbound(_tce.Id)))
            .SqlState.Should().BeNull("una fila finalizada no ocupa la plaza de la vigente");

        // Ni lee ni cambia lo que inserta.
        foreach (var sql in new[]
                 {
                     """SELECT count(*) FROM "ProductosContratados";""",
                     """UPDATE "ProductosContratados" SET "Tramo" = 'De1A10';""",
                     """DELETE FROM "ProductosContratados";""",
                 })
            (await SqlStateComoAsync("cae_app_aprovisionamiento", _tce.Id, sql)).SqlState
                .Should().Be(PostgresErrorCodes.InsufficientPrivilege, sql);
    }

    private static string InsertInbound(Guid tenantId, bool finalizado = false) =>
        $"""
        INSERT INTO "ProductosContratados" ("Id", "Linea", "PagadorStripeCustomerId", "Tramo", "Modalidad",
            "VigenteDesdeUtc", "VigenteHastaUtc", "Estado", "TenantId", "Version", "CreadoEnUtc", "EstaEliminado")
        VALUES ('{Guid.NewGuid()}', 'Inbound', 'cus_X', 'De1A10', 'Anual', now() - interval '1 day',
            {(finalizado ? "now()" : "NULL")}, '{(finalizado ? "Finalizado" : "Vigente")}', '{tenantId}', '{Guid.NewGuid()}', now(), false);
        """;

    private async Task<Result<Guid>> EnviarAsync(Guid? sesion, string? rol, ContratarLineaInboundCommand comando)
    {
        await using var proveedor = ConstruirProveedor(sesion, rol);
        return await proveedor.GetRequiredService<IMediator>().Send(comando);
    }

    /// <summary>Las filas como superusuario de pruebas: la comprobación no depende de los privilegios que se prueban.</summary>
    private async Task<List<ProductoContratado>> FilasComoPropietarioAsync()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>().UseNpgsql(_cadenaConexion).Options;
        await using var contexto = new CaeManagerDbContext(
            options, new EphemeralDataProtectionProvider(), new TenantActualAmbiental { TenantId = _tce.Id });
        return await contexto.ProductosContratados.IgnoreQueryFilters().ToListAsync();
    }

    /// <summary>
    /// Ejecuta <paramref name="sql"/> con <paramref name="rol"/> y el contexto firmado de <paramref name="tenantContexto"/>,
    /// dentro de una transacción que se deshace. Devuelve el SQLSTATE y la restricción del rechazo o, si entró,
    /// <c>null</c> y las filas afectadas.
    /// </summary>
    private async Task<(string? SqlState, int Filas, string? Restriccion)> SqlStateComoAsync(string rol, Guid tenantContexto, string sql)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var transaccion = await conexion.BeginTransactionAsync();

        var contextoFirmado = await ContextoRlsDePruebas.FirmarAsync(conexion, tenantId: tenantContexto);
        await using (var preparar = conexion.CreateCommand())
        {
            preparar.Transaction = transaccion;
            preparar.CommandText = $"SELECT set_config('app.contexto', @contexto, true); SET LOCAL ROLE {rol};";
            preparar.Parameters.AddWithValue("contexto", contextoFirmado);
            await preparar.ExecuteNonQueryAsync();
        }

        await using (var comprobarRol = conexion.CreateCommand())
        {
            comprobarRol.Transaction = transaccion;
            comprobarRol.CommandText = "SELECT current_user::text;";
            ((string)(await comprobarRol.ExecuteScalarAsync())!).Should().Be(rol);
        }

        try
        {
            await using var comando = conexion.CreateCommand();
            comando.Transaction = transaccion;
            comando.CommandText = sql;
            return (null, await comando.ExecuteNonQueryAsync(), null);
        }
        catch (PostgresException ex)
        {
            return (ex.SqlState, -1, ex.ConstraintName);
        }
        finally
        {
            await transaccion.RollbackAsync();
        }
    }

    private async Task<bool> TienePrivilegioAsync(string rol, string privilegio)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT has_table_privilege(@rol, 'public.\"ProductosContratados\"', @privilegio);";
        comando.Parameters.AddWithValue("rol", rol);
        comando.Parameters.AddWithValue("privilegio", privilegio);
        return (bool)(await comando.ExecuteScalarAsync())!;
    }

    private ServiceProvider ConstruirProveedor(Guid? sesionSeleccionada, string? rol)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tce.Id };
        // Con Sesión Privilegiada, el Actor de Plataforma es de la organización de plataforma; sin ella, el usuario es del
        // propio Tenant TCE.
        var currentUser = sesionSeleccionada is null
            ? new CurrentUserServiceFalso(_actorPlataforma, rol: rol, tenantOrigenId: _tce.Id)
            : new CurrentUserServiceFalso(_actorPlataforma, rol: rol, tenantOrigenId: _plataforma.Id);
        var seleccion = new Seleccion(_tce.Id, sesionSeleccionada);
        var dbContext = CrearContexto(_tce.Id, seleccion, currentUser);

        var servicios = new ServiceCollection();
        servicios.AddLogging();
        servicios.AddApplication();

        servicios.AddSingleton<ICurrentUserService>(currentUser);
        servicios.AddSingleton<ITenantActual>(tenantActual);
        servicios.AddSingleton<IClienteActivoSeleccionado>(seleccion);
        servicios.AddSingleton(dbContext);
        servicios.AddSingleton(TimeProvider.System);

        servicios.AddSingleton<IPlataformaQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<ISesionPrivilegiadaActual>(sp => new SesionPrivilegiadaActual(
            sp.GetRequiredService<IPlataformaQueryContext>(), seleccion, currentUser));
        servicios.AddSingleton<IElevacionEscrituraPrivilegiada>(sp =>
            new ElevacionEscrituraPrivilegiada(sp.GetRequiredService<CaeManagerDbContext>()));

        servicios.AddSingleton<IUnitOfWork>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        // GateComercialTenantBehavior lee el Tenant por aquí; el resto, como en EncargoAdministracionPorAprovisionamientoTests.
        servicios.AddSingleton<ITenantsQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IEncargosAdministracionQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IProductoContratadoRepository, ProductoContratadoRepository>();
        servicios.AddSingleton<IActorAuditoria>(new ActorFijo(_actorPlataforma));

        return servicios.BuildServiceProvider();
    }

    private CaeManagerDbContext CrearContexto(
        Guid tenantId, IClienteActivoSeleccionado seleccion, ICurrentUserService? currentUserService = null)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantActual),
                new TenantRlsConnectionInterceptor(
                    tenantActual, seleccion,
                    currentUserService ?? new CurrentUserServiceFalso(_actorPlataforma, rol: null, tenantOrigenId: tenantId),
                    BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class Seleccion(Guid tenantSeleccionado, Guid? sesionPrivilegiadaId) : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => tenantSeleccionado;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => sesionPrivilegiadaId;
    }

    private sealed class ActorFijo(Guid actorReal) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(ActorAuditoria.Normal(actorReal));
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => ActorAuditoria.Normal(actorReal);
    }
}
