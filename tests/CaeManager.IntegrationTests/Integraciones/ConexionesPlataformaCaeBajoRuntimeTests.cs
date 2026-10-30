using CaeManager.Application.Common;
using CaeManager.Application.Integraciones.ConectoresPlataformaCae;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Integraciones;

/// <summary>
/// Las conexiones con plataformas CAE de terceros contra PostgreSQL real y como
/// <c>cae_app_runtime</c>, porque como propietario las políticas no se evalúan (OWASP ASVS 4.0.3
/// V2.10.4, V4.1.3, V4.2.1, V7.2.2). La mitad positiva recorre lo legítimo: sin ella, los rechazos
/// pasarían igual con una política que lo prohibiera todo.
/// </summary>
public class ConexionesPlataformaCaeBajoRuntimeTests : IAsyncLifetime
{
    private const string Secreto = "SECRETO-API-PLATAFORMA-no-debe-aparecer-en-claro";

    // Sembrada por la migración de catálogo.
    private static readonly Guid Proveedor = Guid.Parse("60000000-0000-0000-0000-000000000001");

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _usuario = Guid.NewGuid();
    private readonly IDataProtectionProvider _dataProtection = new EphemeralDataProtectionProvider();

    private Guid _conexionA;

    public async Task InitializeAsync()
    {
        await BaseDatosPostgresDePruebas.MigrarAsync(_cadenaConexion);

        await using (var contexto = CrearContexto(_tenantA, _cadenaConexion))
        {
            contexto.Tenants.Add(CrearTenant(_tenantA, "Tenant A S.L."));
            contexto.Tenants.Add(CrearTenant(_tenantB, "Tenant B S.L."));
            await contexto.SaveChangesAsync();
        }

        await using var bajoRuntime = CrearContexto(_tenantA);
        var conexion = new ConexionPlataformaCae(Proveedor, "Acuerdo A");
        bajoRuntime.ConexionesPlataformaCae.Add(conexion);
        bajoRuntime.CredencialesConexionPlataformaCae.Add(new CredencialConexionPlataformaCae(conexion.Id, Secreto, DateTime.UtcNow));
        await bajoRuntime.SaveChangesAsync();
        _conexionA = conexion.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task La_credencial_va_cifrada_en_disco_y_vuelve_intacta_a_su_Tenant()
    {
        await using (var propietario = new NpgsqlConnection(_cadenaConexion))
        {
            await propietario.OpenAsync();
            await using var comando = propietario.CreateCommand();
            comando.CommandText = "SELECT \"SecretoApi\" FROM \"CredencialesConexionPlataformaCae\" WHERE \"ConexionPlataformaCaeId\" = @id";
            comando.Parameters.AddWithValue("id", _conexionA);
            ((string)(await comando.ExecuteScalarAsync())!).Should().NotContain(Secreto);
        }

        await using var contexto = CrearContexto(_tenantA);
        var credencial = await contexto.CredencialesConexionPlataformaCae.SingleAsync(c => c.ConexionPlataformaCaeId == _conexionA);
        credencial.SecretoApi.Should().Be(Secreto);
    }

    [Fact]
    public async Task El_secreto_no_llega_al_registro_de_auditoria()
    {
        await using var contexto = CrearContexto(_tenantA, _cadenaConexion, auditar: true);
        var conexion = new ConexionPlataformaCae(Proveedor, "Acuerdo auditado");
        contexto.ConexionesPlataformaCae.Add(conexion);
        contexto.CredencialesConexionPlataformaCae.Add(new CredencialConexionPlataformaCae(conexion.Id, Secreto, DateTime.UtcNow));
        await contexto.SaveChangesAsync();

        var filas = await contexto.Set<CaeManager.Domain.Auditoria.RegistroAuditoria>()
            .IgnoreQueryFilters().Where(r => r.EntidadTipo == nameof(CredencialConexionPlataformaCae)).ToListAsync();

        filas.Should().NotBeEmpty("control del instrumento: la credencial se audita, enmascarada");
        filas.Should().OnlyContain(r => r.DatosDespues == null || !r.DatosDespues.Contains(Secreto));
    }

    [Fact]
    public async Task Otro_Tenant_no_ve_la_conexion_ni_la_credencial_ni_con_el_filtro_apagado()
    {
        await using var contexto = CrearContexto(_tenantB);

        (await contexto.ConexionesPlataformaCae.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await contexto.CredencialesConexionPlataformaCae.IgnoreQueryFilters().CountAsync()).Should().Be(0);

        await using var propio = CrearContexto(_tenantA);
        (await propio.ConexionesPlataformaCae.CountAsync()).Should().Be(1, "control: el propio Tenant sí la ve");
    }

    [Fact]
    public async Task La_base_rechaza_colgar_la_credencial_de_la_conexion_de_otro_Tenant()
    {
        await using var propietario = new NpgsqlConnection(_cadenaConexion);
        await propietario.OpenAsync();
        await using var comando = propietario.CreateCommand();
        comando.CommandText = """
            INSERT INTO "CredencialesConexionPlataformaCae" ("Id", "ConexionPlataformaCaeId", "SecretoApi", "ActualizadaEnUtc", "TenantId")
            VALUES (gen_random_uuid(), @conexion, 'x', now(), @tenant);
            """;
        comando.Parameters.AddWithValue("conexion", _conexionA);
        comando.Parameters.AddWithValue("tenant", _tenantB);

        var insertar = () => comando.ExecuteNonQueryAsync();

        (await insertar.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23503");
    }

    [Fact]
    public async Task Un_uso_se_abre_y_se_cierra_una_vez_y_despues_no_se_reescribe_ni_se_borra()
    {
        var uso = UsoConexionPlataformaCae.Iniciar(
            _conexionA, OperacionConectorPlataformaCae.SubirDocumento, _usuario, Guid.NewGuid(), null, DateTime.UtcNow);

        await using (var contexto = CrearContexto(_tenantA))
        {
            contexto.UsosConexionPlataformaCae.Add(uso);
            await contexto.SaveChangesAsync();

            uso.FinalizarConExito(DateTime.UtcNow);
            await contexto.SaveChangesAsync();
        }

        await using var conexion = await AbrirComoRuntimeAsync(_tenantA);

        await using (var reescribir = conexion.CreateCommand())
        {
            reescribir.CommandText = "UPDATE \"UsosConexionPlataformaCae\" SET \"Resultado\" = 'Fallo', \"Motivo\" = 'Inesperado' WHERE \"Id\" = @id;";
            reescribir.Parameters.AddWithValue("id", uso.Id);
            (await reescribir.ExecuteNonQueryAsync()).Should().Be(0, "la política solo deja actualizar un uso EnCurso");
        }

        await DebeDenegarAsync(conexion, $"UPDATE \"UsosConexionPlataformaCae\" SET \"Operacion\" = 'LeerEstadoDocumento' WHERE \"Id\" = '{uso.Id}';");
        await DebeDenegarAsync(conexion, $"DELETE FROM \"UsosConexionPlataformaCae\" WHERE \"Id\" = '{uso.Id}';");
    }

    [Fact]
    public async Task Un_uso_abierto_no_se_puede_dejar_en_curso_con_otro_motivo()
    {
        var uso = UsoConexionPlataformaCae.Iniciar(
            _conexionA, OperacionConectorPlataformaCae.LeerEstadoDocumento, _usuario, null, null, DateTime.UtcNow);
        await using (var contexto = CrearContexto(_tenantA))
        {
            contexto.UsosConexionPlataformaCae.Add(uso);
            await contexto.SaveChangesAsync();
        }

        await using var conexion = await AbrirComoRuntimeAsync(_tenantA);
        await DebeDenegarAsync(conexion, $"UPDATE \"UsosConexionPlataformaCae\" SET \"Motivo\" = 'Inesperado' WHERE \"Id\" = '{uso.Id}';");
    }

    [Theory]
    [InlineData("ConexionesPlataformaCae")]
    [InlineData("UsosConexionPlataformaCae")]
    [InlineData("CredencialesConexionPlataformaCae")]
    public async Task Las_tres_tablas_tienen_RLS_forzada(string tabla)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT relrowsecurity AND relforcerowsecurity FROM pg_class WHERE oid = format('public.%I', @tabla::text)::regclass;";
        comando.Parameters.AddWithValue("tabla", tabla);

        ((bool)(await comando.ExecuteScalarAsync())!).Should().BeTrue();
    }

    [Theory]
    [InlineData("cae_app_aprovisionamiento", "CredencialesConexionPlataformaCae")]
    [InlineData("cae_app_aprovisionamiento", "ConexionesPlataformaCae")]
    [InlineData("cae_app_aprovisionamiento", "UsosConexionPlataformaCae")]
    public async Task Aprovisionamiento_no_tiene_nada_sobre_ellas(string rol, string tabla)
    {
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT has_any_column_privilege(@rol, format('public.%I', @tabla::text), 'SELECT');";
        comando.Parameters.AddWithValue("rol", rol);
        comando.Parameters.AddWithValue("tabla", tabla);

        ((bool)(await comando.ExecuteScalarAsync())!).Should().BeFalse();
    }

    [Fact]
    public async Task Bajo_runtime_la_puerta_niega_la_conexion_de_otro_Tenant_sin_llamar_y_deja_rastro_en_la_propia()
    {
        var conector = new ConectorQueCuenta();
        await using (var propietario = new NpgsqlConnection(_cadenaConexion))
        {
            await propietario.OpenAsync();
            await using var comando = propietario.CreateCommand();
            comando.CommandText = """
                UPDATE "ProveedoresPlataformaCae" SET "ConectorApiActivo" = true WHERE "Id" = @proveedor;
                UPDATE "Tenants" SET "AddOnSubidaAutomaticaActivo" = true, "EstadoComercial" = 'Activa' WHERE "Id" IN (@a, @b);
                """;
            comando.Parameters.AddWithValue("proveedor", Proveedor);
            comando.Parameters.AddWithValue("a", _tenantA);
            comando.Parameters.AddWithValue("b", _tenantB);
            await comando.ExecuteNonQueryAsync();

            await using var codigo = propietario.CreateCommand();
            codigo.CommandText = "SELECT \"Codigo\" FROM \"ProveedoresPlataformaCae\" WHERE \"Id\" = @proveedor;";
            codigo.Parameters.AddWithValue("proveedor", Proveedor);
            conector.CodigoProveedor = (string)(await codigo.ExecuteScalarAsync())!;
        }

        await using (var contextoB = CrearContexto(_tenantB))
        {
            var denegada = await CrearPuerta(contextoB, _tenantB, conector)
                .LeerEstadoDocumentoAsync(_conexionA, Guid.NewGuid());
            denegada.EsExitoso.Should().BeFalse();
        }

        conector.Llamadas.Should().Be(0);

        // Control positivo de todo lo anterior: en su propio Tenant la puerta encuentra la conexión,
        // pasa los interruptores, descifra la credencial y solo se detiene en el objeto, que no existe.
        await using var contextoA = CrearContexto(_tenantA);
        var sinObjeto = await CrearPuerta(contextoA, _tenantA, conector)
            .LeerEstadoDocumentoAsync(_conexionA, Guid.NewGuid());

        sinObjeto.EsExitoso.Should().BeFalse();
        conector.Llamadas.Should().Be(0);
        var uso = await contextoA.UsosConexionPlataformaCae.SingleAsync();
        uso.Resultado.Should().Be(ResultadoUsoConector.Denegado);
        uso.Motivo.Should().Be(MotivoUsoConector.ObjetoNoEncontrado);
    }

    private OperacionesConectorPlataformaCae CrearPuerta(CaeManagerDbContext contexto, Guid tenant, IConectorPlataformaCae conector) => new(
        new ConexionPlataformaCaeRepository(contexto),
        new ProveedorPlataformaCaeRepository(contexto),
        [conector],
        new SinSesionPrivilegiada(),
        new TenantActualAmbiental { TenantId = tenant },
        new CurrentUserServiceFalso(_usuario, tenantOrigenId: tenant),
        new SinArchivos(),
        contexto,
        NullLogger<OperacionesConectorPlataformaCae>.Instance);

    private async Task<NpgsqlConnection> AbrirComoRuntimeAsync(Guid tenant)
    {
        var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await ContextoRlsDePruebas.FijarAsync(conexion, tenantId: tenant, usuarioId: _usuario);

        await using var setRol = conexion.CreateCommand();
        setRol.CommandText = "SET ROLE cae_app_runtime;";
        await setRol.ExecuteNonQueryAsync();

        return conexion;
    }

    private static async Task DebeDenegarAsync(NpgsqlConnection conexion, string sql)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;

        var ejecutar = () => comando.ExecuteNonQueryAsync();

        (await ejecutar.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
    }

    private CaeManagerDbContext CrearContexto(Guid tenant, string? cadena = null, bool auditar = false)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenant };
        var builder = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(
                cadena ?? BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion),
                npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"));

        var usuario = new CurrentUserServiceFalso(_usuario, tenantOrigenId: tenant);
        if (auditar)
            builder.AddInterceptors(new AuditoriaInterceptor(new ActorFijo(_usuario)), new TenantSelladoInterceptor(tenantActual));
        else
            builder.AddInterceptors(
                new TenantSelladoInterceptor(tenantActual),
                new TenantRlsConnectionInterceptor(tenantActual, new SinClienteSeleccionado(tenant), usuario, BaseDatosPostgresDePruebas.FirmanteContextoRls));

        return new CaeManagerDbContext(builder.Options, _dataProtection, tenantActual);
    }

    private static Tenant CrearTenant(Guid id, string nombre)
    {
        var tenant = new Tenant(nombre);
        typeof(Entity).GetProperty(nameof(Entity.Id))!.SetValue(tenant, id);
        return tenant;
    }

    private sealed class SinClienteSeleccionado(Guid tenant) : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => tenant;

        public Guid? AsignacionOperacionIdSeleccionada => null;

        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    private sealed class ActorFijo(Guid usuario) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(ActorAuditoria.Normal(usuario));

        public ActorAuditoria? ObtenerSiYaEstaResuelto() => ActorAuditoria.Normal(usuario);
    }

    private sealed class SinSesionPrivilegiada : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SesionPrivilegiadaActiva?>(null);
    }

    private sealed class SinArchivos : IFileStorageService
    {
        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ConectorQueCuenta : IConectorPlataformaCae
    {
        public string CodigoProveedor { get; set; } = "";

        public int Llamadas { get; private set; }

        public Task<ResultadoConector<EstadoDocumentoEnPlataforma>> LeerEstadoDocumentoAsync(
            CredencialApiConector credencial, SolicitudLecturaEstado solicitud, CancellationToken cancellationToken)
        {
            Llamadas++;
            return Task.FromResult(ResultadoConector<EstadoDocumentoEnPlataforma>.Exito(EstadoDocumentoEnPlataforma.Validado));
        }

        public Task<ResultadoConector<ReferenciaEnPlataforma>> DarDeAltaTrabajadorAsync(
            CredencialApiConector credencial, SolicitudAltaTrabajador solicitud, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ResultadoConector<ReferenciaEnPlataforma>> SubirDocumentoAsync(
            CredencialApiConector credencial, SolicitudSubidaDocumento solicitud, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
