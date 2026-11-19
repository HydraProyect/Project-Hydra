using CaeManager.Application.Centros.Commands.CrearCentro;
using CaeManager.Application.Comercial.LineaOutbound;
using CaeManager.Application.Common;
using CaeManager.Application.DependencyInjection;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Commands.CrearDelegacionTenant;
using CaeManager.Application.Tenants.Commands.MarcarParticipanteTec;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using CaeManager.Infrastructure.Plataforma;
using CaeManager.Web.Services;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Comercial;

/// <summary>
/// D3 (decisiones de Chris del 2026-11-19) contra PostgreSQL real, con el pipeline de producción
/// (<see cref="IMediator"/>, <c>AddApplication()</c>, el <see cref="TenantRlsConnectionInterceptor"/> y la elevación):
/// el Tenant TEC marcado sin línea Outbound no ejecuta <see cref="CrearCentroCommand"/> ni
/// <see cref="CrearDelegacionTenantCommand"/>; con la línea Outbound vigente, sí; la línea de otro Tenant no abre; y el
/// marcador solo lo escribe <see cref="MarcarTenantComoParticipanteTecCommand"/> bajo <c>cae_app_aprovisionamiento</c>,
/// nunca el runtime. Los handlers de los dos Commands de alta se sustituyen por uno que solo anota que llegó: aquí se prueba
/// la guarda con la línea leída bajo RLS, no el alta.
/// </summary>
public class LimiteTenantTecBajoRuntimeTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _actorPlataforma = Guid.NewGuid();
    private readonly Guid _administrador = Guid.NewGuid();
    private readonly Tenant _plataforma = new("Organización de plataforma de prueba", esPlataforma: true);
    private readonly Tenant _tec = new("Tenant TEC de prueba");
    private readonly Tenant _sinMarcar = new("Tenant TEC por marcar de prueba");
    private readonly Tenant _otro = new("Otro Tenant de prueba");
    private readonly Tenant _operador = new("Operador CAE externo de prueba");

    private Guid _sesionDeAprovisionamiento;

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);
        await using (var contexto = CrearContexto(_sinMarcar.Id, new Seleccion(_sinMarcar.Id, null)))
        {
            var ahora = DateTime.UtcNow;
            contexto.Tenants.AddRange(_plataforma, _tec, _sinMarcar, _otro, _operador);

            var aprovisionamiento = ConcesionPrivilegio.SobreTenants(
                _actorPlataforma, CapacidadPrivilegio.Aprovisionamiento, [_sinMarcar.Id],
                vigenciaDesde: ahora.AddMinutes(-10), vigenciaHasta: ahora.AddHours(4));
            contexto.ConcesionesPrivilegio.Add(aprovisionamiento);
            var sesion = SesionPrivilegiada.Abrir(
                aprovisionamiento, _sinMarcar.Id, "Marcar como participante TEC", ahora,
                ventana: TimeSpan.FromHours(1), usuarioSimuladoId: null, ticket: null);
            contexto.SesionesPrivilegiadas.Add(sesion);

            await contexto.SaveChangesAsync();
            _sesionDeAprovisionamiento = sesion.Id;
        }

        // El marcador de la siembra, como propietario: el trigger solo deja cambiarlo a él y a cae_app_aprovisionamiento.
        await EjecutarComoPropietarioAsync($"""UPDATE "Tenants" SET "EsParticipanteTec" = true WHERE "Id" = '{_tec.Id}';""");
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task El_Tenant_TEC_marcado_sin_linea_Outbound_no_crea_Centros_ni_delegaciones()
    {
        var (centro, llegoCentro) = await CrearCentroAsync(_tec.Id);
        centro.Error!.Codigo.Should().Be(ErroresLimiteTenantTec.OutboundCerrado.Codigo);
        llegoCentro.Should().BeFalse();

        var (delegacion, llegoDelegacion) = await CrearDelegacionAsync(_tec.Id);
        delegacion.Error!.Codigo.Should().Be(ErroresLimiteTenantTec.OutboundCerrado.Codigo);
        llegoDelegacion.Should().BeFalse();
    }

    [Fact]
    public async Task Con_la_linea_Outbound_vigente_el_Tenant_TEC_crea_Centros_y_delegaciones()
    {
        await EjecutarComoPropietarioAsync(InsertOutbound(_tec.Id));

        var (centro, llegoCentro) = await CrearCentroAsync(_tec.Id);
        centro.EsExitoso.Should().BeTrue(centro.EsFallido ? $"código: {centro.Error.Codigo}" : string.Empty);
        llegoCentro.Should().BeTrue();

        var (delegacion, llegoDelegacion) = await CrearDelegacionAsync(_tec.Id);
        delegacion.EsExitoso.Should().BeTrue(delegacion.EsFallido ? $"código: {delegacion.Error.Codigo}" : string.Empty);
        llegoDelegacion.Should().BeTrue();
    }

    [Fact]
    public async Task La_linea_Outbound_finalizada_no_abre()
    {
        await EjecutarComoPropietarioAsync(InsertOutbound(_tec.Id, finalizado: true));

        var (centro, llego) = await CrearCentroAsync(_tec.Id);

        centro.Error!.Codigo.Should().Be(ErroresLimiteTenantTec.OutboundCerrado.Codigo);
        llego.Should().BeFalse();
    }

    [Fact]
    public async Task La_linea_Outbound_de_otro_Tenant_no_abre()
    {
        await EjecutarComoPropietarioAsync(InsertOutbound(_otro.Id));

        var (centro, llego) = await CrearCentroAsync(_tec.Id);

        centro.Error!.Codigo.Should().Be(ErroresLimiteTenantTec.OutboundCerrado.Codigo,
            "la línea de otro Tenant no la ve la RLS ni la consulta, que filtra por el Tenant beneficiario");
        llego.Should().BeFalse();
    }

    [Fact]
    public async Task Un_Tenant_sin_marcar_crea_Centros_sin_linea_Outbound()
    {
        var (centro, llego) = await CrearCentroAsync(_otro.Id);

        centro.EsExitoso.Should().BeTrue(centro.EsFallido ? $"código: {centro.Error.Codigo}" : string.Empty);
        llego.Should().BeTrue("D3 solo restringe a los Tenants marcados");
    }

    [Fact]
    public async Task El_comando_de_plataforma_marca_el_Tenant_bajo_aprovisionamiento()
    {
        (await MarcadorComoPropietarioAsync(_sinMarcar.Id)).Should().BeFalse("control");

        await using var proveedor = ConstruirProveedor(_sinMarcar.Id, _sesionDeAprovisionamiento, rol: null, _plataforma.Id);
        var resultado = await proveedor.GetRequiredService<IMediator>().Send(new MarcarTenantComoParticipanteTecCommand());

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? $"código: {resultado.Error.Codigo}" : string.Empty);
        (await MarcadorComoPropietarioAsync(_sinMarcar.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task El_Administrador_sin_Sesion_Privilegiada_no_se_marca()
    {
        await using var proveedor = ConstruirProveedor(_sinMarcar.Id, sesion: null, rol: "Administrador", _sinMarcar.Id);
        var resultado = await proveedor.GetRequiredService<IMediator>().Send(new MarcarTenantComoParticipanteTecCommand());

        resultado.EsExitoso.Should().BeFalse();
        (await MarcadorComoPropietarioAsync(_sinMarcar.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task El_runtime_no_cambia_el_marcador_con_SQL_directo()
    {
        var (sqlState, _) = await SqlStateComoAsync("cae_app_runtime", _sinMarcar.Id,
            $"""UPDATE "Tenants" SET "EsParticipanteTec" = true WHERE "Id" = '{_sinMarcar.Id}';""");

        sqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        (await MarcadorComoPropietarioAsync(_sinMarcar.Id)).Should().BeFalse();
    }

    private async Task<(Result<Guid> Resultado, bool LlegoAlHandler)> CrearCentroAsync(Guid tenantId)
    {
        var handler = new HandlerQueAnota<CrearCentroCommand>();
        await using var proveedor = ConstruirProveedor(tenantId, sesion: null, rol: "Administrador", tenantId,
            s => s.AddSingleton<IRequestHandler<CrearCentroCommand, Result<Guid>>>(handler));
        var resultado = await proveedor.GetRequiredService<IMediator>().Send(
            new CrearCentroCommand(Guid.NewGuid(), Guid.NewGuid(), "Centro de prueba", null, null, null, null));
        return (resultado, handler.Llego);
    }

    private async Task<(Result<Guid> Resultado, bool LlegoAlHandler)> CrearDelegacionAsync(Guid tenantId)
    {
        var handler = new HandlerQueAnota<CrearDelegacionTenantCommand>();
        await using var proveedor = ConstruirProveedor(tenantId, sesion: null, rol: "Administrador", tenantId,
            s => s.AddSingleton<IRequestHandler<CrearDelegacionTenantCommand, Result<Guid>>>(handler));
        var resultado = await proveedor.GetRequiredService<IMediator>().Send(
            new CrearDelegacionTenantCommand(_operador.Id, tenantId));
        return (resultado, handler.Llego);
    }

    private static string InsertOutbound(Guid tenantId, bool finalizado = false) =>
        $"""
        INSERT INTO "ProductosContratados" ("Id", "Linea", "PagadorStripeCustomerId", "Tramo", "Modalidad",
            "VigenteDesdeUtc", "VigenteHastaUtc", "Estado", "TenantId", "Version", "CreadoEnUtc", "EstaEliminado")
        VALUES ('{Guid.NewGuid()}', 'OutboundGestionPropia', 'cus_X', 'De1A10', 'Anual', now() - interval '1 day',
            {(finalizado ? "now()" : "NULL")}, '{(finalizado ? "Finalizado" : "Vigente")}', '{tenantId}', '{Guid.NewGuid()}',
            now(), false);
        """;

    private ServiceProvider ConstruirProveedor(
        Guid tenantId, Guid? sesion, string? rol, Guid tenantOrigenId, Action<IServiceCollection>? sustituir = null)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var usuario = sesion is null ? _administrador : _actorPlataforma;
        var currentUser = new CurrentUserServiceFalso(usuario, rol: rol, tenantOrigenId: tenantOrigenId,
            papel: PapelActivo.TenantDeOrigen);
        var seleccion = new Seleccion(tenantId, sesion);
        var actorAuditoria = new ActorAuditoriaDesdeSesion(currentUser, seleccion, currentUser);
        var dbContext = CrearContexto(tenantId, seleccion, currentUser, actorAuditoria);

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
        servicios.AddSingleton<ITenantsQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IEncargosAdministracionQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        // Lo que lee la guarda de D3: la línea Outbound bajo la RLS del Tenant activo y, sobre una delegación, la delegación.
        servicios.AddSingleton<ILineaOutboundQueryContext>(sp => sp.GetRequiredService<CaeManagerDbContext>());
        servicios.AddSingleton<IDelegacionTenantRepository>(sp => new DelegacionTenantRepository(sp.GetRequiredService<CaeManagerDbContext>()));
        servicios.AddSingleton<ITenantRepository>(sp => new TenantRepository(sp.GetRequiredService<CaeManagerDbContext>()));
        servicios.AddSingleton<ITransaccionDeComando>(sp => new TransaccionDeComando(sp.GetRequiredService<CaeManagerDbContext>()));
        servicios.AddSingleton<IActorAuditoria>(actorAuditoria);

        sustituir?.Invoke(servicios);
        return servicios.BuildServiceProvider();
    }

    private CaeManagerDbContext CrearContexto(
        Guid tenantId, IClienteActivoSeleccionado seleccion, ICurrentUserService? currentUserService = null,
        IActorAuditoria? actorAuditoria = null)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var rls = new TenantRlsConnectionInterceptor(
            tenantActual, seleccion,
            currentUserService ?? new CurrentUserServiceFalso(_actorPlataforma, rol: null, tenantOrigenId: tenantId),
            BaseDatosPostgresDePruebas.FirmanteContextoRls);
        var constructor = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"));
        if (actorAuditoria is null)
            constructor.AddInterceptors(new TenantSelladoInterceptor(tenantActual), rls);
        else
            constructor.AddInterceptors(
                new AuditoriaInterceptor(actorAuditoria), rls, new TenantSelladoInterceptor(tenantActual),
                new ConcurrenciaOptimistaInterceptor());

        return new CaeManagerDbContext(constructor.Options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private async Task<bool> MarcadorComoPropietarioAsync(Guid tenantId)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(
            $"""SELECT "EsParticipanteTec" FROM "Tenants" WHERE "Id" = '{tenantId}';""", conexion);
        return (bool)(await comando.ExecuteScalarAsync())!;
    }

    private async Task EjecutarComoPropietarioAsync(string sql)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = new NpgsqlCommand(sql, conexion);
        await comando.ExecuteNonQueryAsync();
    }

    /// <summary>Ejecuta <paramref name="sql"/> como <paramref name="rol"/>, con el contexto firmado, y lo deshace.</summary>
    private async Task<(string? SqlState, int Filas)> SqlStateComoAsync(string rol, Guid tenantContexto, string sql)
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

        try
        {
            await using var comando = conexion.CreateCommand();
            comando.Transaction = transaccion;
            comando.CommandText = sql;
            return (null, await comando.ExecuteNonQueryAsync());
        }
        catch (PostgresException ex)
        {
            return (ex.SqlState, -1);
        }
        finally
        {
            await transaccion.RollbackAsync();
        }
    }

    private sealed class HandlerQueAnota<TCommand> : IRequestHandler<TCommand, Result<Guid>>
        where TCommand : IRequest<Result<Guid>>
    {
        public bool Llego { get; private set; }

        public Task<Result<Guid>> Handle(TCommand request, CancellationToken cancellationToken)
        {
            Llego = true;
            return Task.FromResult(Result.Exito(Guid.NewGuid()));
        }
    }

    private sealed class Seleccion(Guid tenantSeleccionado, Guid? sesionPrivilegiadaId) : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => tenantSeleccionado;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => sesionPrivilegiadaId;
    }
}
