using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.AlertasCriticas;
using CaeManager.Application.Comunicaciones.Deteccion;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Plataforma;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace CaeManager.IntegrationTests.Comunicaciones;

public class AlertasCriticasConversacionTests : IAsyncLifetime
{
    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _persona = Guid.NewGuid();
    private Guid _conversacion;

    public async Task InitializeAsync()
    {
        await using var db = CrearContexto(_tenantA);
        await db.Database.MigrateAsync();
        var conversacion = new Conversacion("Requerimiento en triaje");
        var mensaje = conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo,
            "empresa@example.test", "Inspección de Trabajo", DateTime.UtcNow);
        db.Conversaciones.Add(conversacion);
        db.AlertasCriticasConversacion.Add(new(mensaje, mensaje.CuerpoHtml, "inspección de trabajo", _tenantA, DateTime.UtcNow));
        await db.SaveChangesAsync();
        _conversacion = conversacion.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    [Fact]
    public async Task El_repositorio_no_lee_la_alerta_de_otro_Tenant()
    {
        await using var db = CrearContexto(_tenantB);
        var repositorio = new AlertaCriticaRepository(db, new TenantActualAmbiental { TenantId = _tenantB });
        (await repositorio.ObtenerPorConversacionAsync(_conversacion, default)).Should().BeNull();
        await using var control = CrearContexto(_tenantA);
        (await control.AlertasCriticasConversacion.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData("DireccionCae", true)]
    [InlineData("CoordinadorCae", true)]
    [InlineData("Administrador", false)]
    [InlineData("GestorCae", false)]
    [InlineData("Consulta", false)]
    [InlineData("Cliente", false)]
    public async Task El_comando_aplica_la_autorizacion_y_audita_la_decision(string rol, bool permitido)
    {
        await using var db = CrearContexto(_tenantA);
        var resultado = await CrearHandler(db, _tenantA, rol).Handle(new(_conversacion, true), default);
        resultado.EsExitoso.Should().Be(permitido);
        await using var lectura = CrearContexto(_tenantA);
        var alerta = await lectura.AlertasCriticasConversacion.SingleAsync();
        alerta.Estado.Should().Be(permitido ? EstadoAlertaCritica.ConfirmadaYDerivada : EstadoAlertaCritica.Pendiente);
        alerta.DecisionPorUsuarioId.Should().Be(permitido ? _persona : null);
    }

    [Fact]
    public async Task Descartar_persiste_la_decision_sin_enviar_correo()
    {
        await using var db = CrearContexto(_tenantA);
        (await CrearHandler(db, _tenantA, "DireccionCae").Handle(new(_conversacion, false), default)).EsExitoso.Should().BeTrue();
        await using var lectura = CrearContexto(_tenantA);
        var alerta = await lectura.AlertasCriticasConversacion.SingleAsync();
        alerta.Estado.Should().Be(EstadoAlertaCritica.Descartada);
        alerta.DecisionPorUsuarioId.Should().Be(_persona);
        alerta.DecisionEnUtc.Should().NotBeNull();
        (await lectura.Mensajes.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Un_Coordinador_Cae_sin_cartera_no_puede_resolver()
    {
        await using var db = CrearContexto(_tenantA);
        var alcance = new AlcanceDatosServiceFalso(clienteIds: []);
        // Una conversación identificada fuera de cartera, distinta de triaje.
        var conversacion = await db.Conversaciones.SingleAsync();
        conversacion.AsignarCliente(Guid.NewGuid());
        await db.SaveChangesAsync();
        (await CrearHandler(db, _tenantA, "CoordinadorCae", alcance).Handle(new(_conversacion, false), default)).EsFallido.Should().BeTrue();
    }

    [Fact]
    public async Task La_escritura_y_la_clave_foranea_no_atraviesan_Tenants()
    {
        await using var db = CrearContexto(_tenantB);
        (await CrearHandler(db, _tenantB, "DireccionCae").Handle(new(_conversacion, false), default)).EsFallido.Should().BeTrue();
        var mensaje = new Mensaje(_conversacion, DireccionMensaje.Entrante, CanalConversacion.Correo, "empresa@example.test", "denuncia", DateTime.UtcNow);
        db.AlertasCriticasConversacion.Add(new(mensaje, "denuncia", "denuncia", _tenantB, DateTime.UtcNow));
        Func<Task> guardar = () => db.SaveChangesAsync();
        await guardar.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Dos_decisiones_concurrentes_no_se_sobrescriben()
    {
        await using var primero = CrearContexto(_tenantA);
        await using var segundo = CrearContexto(_tenantA);
        var a = await primero.AlertasCriticasConversacion.SingleAsync();
        var b = await segundo.AlertasCriticasConversacion.SingleAsync();
        a.Resolver(true, _persona, DateTime.UtcNow);
        b.Resolver(false, Guid.NewGuid(), DateTime.UtcNow);
        await primero.SaveChangesAsync();
        Func<Task> guardar = () => segundo.SaveChangesAsync();
        await guardar.Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    [Fact]
    public async Task Rls_oculta_la_alerta_y_rechaza_cambiar_su_Tenant()
    {
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SET ROLE cae_app_runtime";
        await comando.ExecuteNonQueryAsync();
        comando.CommandText = "SELECT set_config('app.tenant_id', @tenant, false)";
        comando.Parameters.AddWithValue("tenant", _tenantA.ToString());
        await comando.ExecuteScalarAsync();
        comando.Parameters.Clear();
        comando.CommandText = "SELECT count(*) FROM \"AlertasCriticasConversacion\"";
        ((long)(await comando.ExecuteScalarAsync())!).Should().Be(1);
        comando.CommandText = "UPDATE \"AlertasCriticasConversacion\" SET \"TenantId\" = @otro, \"OperadorCaeTenantId\" = @otro";
        comando.Parameters.AddWithValue("otro", _tenantB);
        Func<Task> modificar = async () => await comando.ExecuteNonQueryAsync();
        (await modificar.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
        comando.Parameters.Clear();
        comando.CommandText = "SELECT set_config('app.tenant_id', @tenant, false)";
        comando.Parameters.AddWithValue("tenant", _tenantB.ToString());
        await comando.ExecuteScalarAsync();
        comando.Parameters.Clear();
        comando.CommandText = "SELECT count(*) FROM \"AlertasCriticasConversacion\"";
        ((long)(await comando.ExecuteScalarAsync())!).Should().Be(0);
    }

    private ResolverAlertaCriticaCommandHandler CrearHandler(CaeManagerDbContext db, Guid tenantId, string rol, IAlcanceDatosService? alcance = null)
    {
        var tenant = new TenantActualAmbiental { TenantId = tenantId };
        var repositorio = new AlertaCriticaRepository(db, tenant);
        var directorio = new Directorio();
        var acceso = new AccesoAlertaCritica(new CurrentUserServiceFalso(_persona, rol, tenantId), new Sesion(), tenant,
            db, alcance ?? new AlcanceDatosServiceFalso(), directorio);
        var avisos = new AlertaCriticaService(repositorio, new NotificacionUsuarioRepository(db), directorio, tenant,
            new Opciones(), new Opinion(), NullLogger<AlertaCriticaService>.Instance);
        return new(repositorio, acceso, avisos, db);
    }

    private CaeManagerDbContext CrearContexto(Guid tenantId)
    {
        var tenant = new TenantActualAmbiental { TenantId = tenantId };
        return new(new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadena, p => p.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenant), new ConcurrenciaOptimistaInterceptor()).Options,
            new EphemeralDataProtectionProvider(), tenant);
    }
    private sealed class Sesion : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken ct = default) => Task.FromResult<SesionPrivilegiadaActiva?>(null);
        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken ct = default) => ObtenerAsync(ct);
    }
    private sealed class Directorio : IDirectorioUsuariosService
    {
        public Task<bool> EsVisibleEnTenantActualAsync(Guid id, CancellationToken ct = default) => Task.FromResult(true);
        public Task<Guid?> ObtenerTenantDeUsuarioAsync(Guid id, CancellationToken ct = default) => Task.FromResult<Guid?>(null);
        public Task<IReadOnlyDictionary<Guid, string>> ObtenerNombresVisiblesAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default) => Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());
        public Task<bool> EsCuentaActivaConRolAsync(Guid id, Guid tenant, string rol, CancellationToken ct = default) => Task.FromResult(true);
        public Task<IReadOnlyList<Guid>> ObtenerCuentasActivasConRolAsync(Guid tenant, string rol, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Guid>>([]);
    }
    private sealed class Opinion : ISegundaOpinionCriticaIa
    {
        public Task<SegundaOpinionCritica?> EvaluarAsync(string texto, CancellationToken ct) => Task.FromResult<SegundaOpinionCritica?>(null);
    }
    private sealed class Opciones : IOptionsMonitor<AlertasCriticasOptions>
    {
        public AlertasCriticasOptions CurrentValue { get; } = new();
        public AlertasCriticasOptions Get(string? nombre) => CurrentValue;
        public IDisposable? OnChange(Action<AlertasCriticasOptions, string?> listener) => null;
    }
}
