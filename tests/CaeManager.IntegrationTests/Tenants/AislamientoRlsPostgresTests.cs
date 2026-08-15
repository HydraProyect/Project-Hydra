using CaeManager.Domain.ApiKeys;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Contactos;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Plantillas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// Prueba las políticas RLS de la migración <c>HabilitarRlsPostgres</c>
/// contra el rol restringido real (<c>cae_app_runtime</c>), no contra el
/// propietario de las tablas — RLS nunca restringe al propietario ni a un
/// superusuario (ver RUNBOOK-RLS.md), así que un test que solo usara el rol
/// por defecto (como el resto de los tests de este proyecto, ver
/// <see cref="BaseDatosPostgresDePruebas"/>) pasaría igual aunque las
/// políticas estuvieran completamente rotas o ni se hubieran aplicado.
///
/// <c>SET ROLE</c> (en vez de abrir una conexión de login nueva) basta para
/// probarlo: el superusuario de los tests ya puede asumir cualquier rol sin
/// contraseña, y una vez asumido, Postgres aplica RLS exactamente igual que
/// lo haría una conexión real bajo ese rol.
/// </summary>
public class AislamientoRlsPostgresTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenantA };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        // Migra y siembra como el rol propietario de los tests (postgres) a
        // propósito: RLS no debe interferir en absoluto con ese rol, solo
        // con cae_app_runtime — es justo lo que estos tests verifican.
        await using var dbContext = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
        await dbContext.Database.MigrateAsync();

        var cliente = new Cliente("RENDELSUR", "B12345674", esCritico: false);
        dbContext.Clientes.Add(cliente);
        dbContext.ClavesApi.Add(new ClaveApi("Integración de prueba", "cae_abcd", "hash-de-prueba", Guid.NewGuid()));

        var tipoDocumento = new TipoDocumento("Certificado", 12, aplicaVencimientoAutomatico: true, 1, AmbitoAplicacion.Cliente, esObligatorio: true);
        dbContext.TiposDocumento.Add(tipoDocumento);
        var documento = Documento.DeCliente(cliente.Id, tipoDocumento.Id, DateOnly.FromDateTime(DateTime.UtcNow), null);
        dbContext.Documentos.Add(documento);
        dbContext.FirmasEnCampoDocumento.Add(new FirmaEnCampoDocumento(
            documento.Id, Guid.NewGuid(), "Juan Pérez", "GestorCae", DateTime.UtcNow, null, new string('a', 64)));

        dbContext.FirmasGuardadasUsuario.Add(new FirmaGuardadaUsuario(Guid.NewGuid(), "url/firma.png", DateTime.UtcNow));
        var empresa = new Empresa("Empresa de prueba", "B12345674");
        dbContext.Empresas.Add(empresa);
        dbContext.SellosEmpresa.Add(new SelloEmpresa(empresa.Id, "url/sello.png", DateTime.UtcNow));

        var contacto = ContactoAgenda.DeEmpresa(empresa.Id, "Juan Pérez", "juan@example.com");
        contacto.EstablecerRoles([RolContacto.ResponsablePrl]);
        dbContext.ContactosAgenda.Add(contacto);

        var plantilla = new PlantillaDocumento(
            OrigenPlantilla.Externa, "Ficha de acceso al centro", AmbitoAplicacion.Trabajador, FormatoOrigenPlantilla.PdfVisual);
        dbContext.PlantillasDocumento.Add(plantilla);
        var plantillaVersion = new PlantillaDocumentoVersion(plantilla.Id, 1, "url/plantilla.pdf", new string('a', 64));
        plantillaVersion.EstablecerElementos([
            new PlantillaElemento(plantillaVersion.Id, TipoElementoPlantilla.Texto, 1, 0, 0, 100, 20, "Razón social",
                fuenteDato: FuenteDatoPlantilla.EmpresaRazonSocial)
        ]);
        dbContext.PlantillasDocumentoVersion.Add(plantillaVersion);

        await dbContext.SaveChangesAsync();
    }

    public async Task DisposeAsync() =>
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private async Task<NpgsqlConnection> AbrirComoRolRestringidoAsync()
    {
        var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        await using var setRol = conexion.CreateCommand();
        setRol.CommandText = "SET ROLE cae_app_runtime;";
        await setRol.ExecuteNonQueryAsync();

        return conexion;
    }

    private static async Task FijarTenantDeSesionAsync(NpgsqlConnection conexion, Guid? tenantId)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT set_config('app.tenant_id', @valor, false);";
        comando.Parameters.AddWithValue("valor", tenantId?.ToString() ?? string.Empty);
        await comando.ExecuteNonQueryAsync();
    }

    private static async Task<long> ContarClientesAsync(NpgsqlConnection conexion)
    {
        await using var consulta = conexion.CreateCommand();
        consulta.CommandText = "SELECT count(*) FROM \"Clientes\";";
        return (long)(await consulta.ExecuteScalarAsync())!;
    }

    private static async Task<long> ContarClavesApiAsync(NpgsqlConnection conexion)
    {
        await using var consulta = conexion.CreateCommand();
        consulta.CommandText = "SELECT count(*) FROM \"ClavesApi\";";
        return (long)(await consulta.ExecuteScalarAsync())!;
    }

    private static async Task<long> ContarFirmasEnCampoAsync(NpgsqlConnection conexion)
    {
        await using var consulta = conexion.CreateCommand();
        consulta.CommandText = "SELECT count(*) FROM \"FirmasEnCampoDocumento\";";
        return (long)(await consulta.ExecuteScalarAsync())!;
    }

    private static async Task<long> ContarAsync(NpgsqlConnection conexion, string tabla)
    {
        await using var consulta = conexion.CreateCommand();
        consulta.CommandText = $"SELECT count(*) FROM \"{tabla}\";";
        return (long)(await consulta.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task El_rol_restringido_no_ve_ninguna_fila_sin_tenant_de_sesion_fijado()
    {
        await using var conexion = await AbrirComoRolRestringidoAsync();

        var total = await ContarClientesAsync(conexion);

        total.Should().Be(0, "sin app.tenant_id fijado la política debe ocultar todas las filas, no solo las de otros tenants");
    }

    [Fact]
    public async Task El_rol_restringido_solo_ve_las_filas_del_tenant_fijado_en_la_sesion()
    {
        await using var conexion = await AbrirComoRolRestringidoAsync();

        await FijarTenantDeSesionAsync(conexion, _tenantA);
        (await ContarClientesAsync(conexion)).Should().Be(1);

        await FijarTenantDeSesionAsync(conexion, _tenantB);
        (await ContarClientesAsync(conexion)).Should().Be(0);
    }

    [Fact]
    public async Task El_rol_propietario_de_las_tablas_no_esta_restringido_por_rls()
    {
        // Control negativo: sin SET ROLE, la misma consulta (como el rol con
        // el que migran hoy todos los entornos) debe ver la fila igual que
        // antes de esta migración — confirma que RLS es hoy inerte para el
        // propietario, tal como documenta RUNBOOK-RLS.md, y no una regresión
        // que rompería el arranque/las queries normales.
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();

        (await ContarClientesAsync(conexion)).Should().Be(1);
    }

    [Fact]
    public async Task El_rol_restringido_solo_ve_las_claves_api_del_tenant_fijado_en_la_sesion()
    {
        // ClavesApi (P3-29) se creó después de HabilitarRlsPostgres y se
        // quedó fuera de la lista original — verificado aquí para que el
        // gap no vuelva a colarse en la siguiente tabla nueva.
        await using var conexion = await AbrirComoRolRestringidoAsync();

        await FijarTenantDeSesionAsync(conexion, _tenantA);
        (await ContarClavesApiAsync(conexion)).Should().Be(1);

        await FijarTenantDeSesionAsync(conexion, _tenantB);
        (await ContarClavesApiAsync(conexion)).Should().Be(0);
    }

    [Fact]
    public async Task El_rol_restringido_solo_ve_las_firmas_en_campo_del_tenant_fijado_en_la_sesion()
    {
        // FirmasEnCampoDocumento (Fase A de firma en campo) se creó después de
        // HabilitarRlsPostgres — verificado aquí igual que ClavesApi, para que
        // el hueco no vuelva a colarse en la tabla nueva.
        await using var conexion = await AbrirComoRolRestringidoAsync();

        await FijarTenantDeSesionAsync(conexion, _tenantA);
        (await ContarFirmasEnCampoAsync(conexion)).Should().Be(1);

        await FijarTenantDeSesionAsync(conexion, _tenantB);
        (await ContarFirmasEnCampoAsync(conexion)).Should().Be(0);
    }

    [Fact]
    public async Task El_rol_restringido_solo_ve_la_firma_guardada_del_tenant_fijado_en_la_sesion()
    {
        await using var conexion = await AbrirComoRolRestringidoAsync();

        await FijarTenantDeSesionAsync(conexion, _tenantA);
        (await ContarAsync(conexion, "FirmasGuardadasUsuario")).Should().Be(1);

        await FijarTenantDeSesionAsync(conexion, _tenantB);
        (await ContarAsync(conexion, "FirmasGuardadasUsuario")).Should().Be(0);
    }

    [Fact]
    public async Task El_rol_restringido_solo_ve_el_sello_de_empresa_del_tenant_fijado_en_la_sesion()
    {
        await using var conexion = await AbrirComoRolRestringidoAsync();

        await FijarTenantDeSesionAsync(conexion, _tenantA);
        (await ContarAsync(conexion, "SellosEmpresa")).Should().Be(1);

        await FijarTenantDeSesionAsync(conexion, _tenantB);
        (await ContarAsync(conexion, "SellosEmpresa")).Should().Be(0);
    }

    [Fact]
    public async Task El_rol_restringido_solo_ve_el_rol_de_contacto_del_tenant_fijado_en_la_sesion()
    {
        await using var conexion = await AbrirComoRolRestringidoAsync();

        await FijarTenantDeSesionAsync(conexion, _tenantA);
        (await ContarAsync(conexion, "ContactosAgendaRoles")).Should().Be(1);

        await FijarTenantDeSesionAsync(conexion, _tenantB);
        (await ContarAsync(conexion, "ContactosAgendaRoles")).Should().Be(0);
    }

    [Theory]
    [InlineData("PlantillasDocumento")]
    [InlineData("PlantillasDocumentoVersion")]
    [InlineData("PlantillasElemento")]
    public async Task El_rol_restringido_solo_ve_las_plantillas_del_tenant_fijado_en_la_sesion(string tabla)
    {
        await using var conexion = await AbrirComoRolRestringidoAsync();

        await FijarTenantDeSesionAsync(conexion, _tenantA);
        (await ContarAsync(conexion, tabla)).Should().Be(1);

        await FijarTenantDeSesionAsync(conexion, _tenantB);
        (await ContarAsync(conexion, tabla)).Should().Be(0);
    }

    [Fact]
    public async Task El_rol_restringido_no_puede_insertar_una_fila_de_otro_tenant_que_el_fijado_en_sesion()
    {
        await using var conexion = await AbrirComoRolRestringidoAsync();
        await FijarTenantDeSesionAsync(conexion, _tenantA);

        await using var insertar = conexion.CreateCommand();
        insertar.CommandText =
            "INSERT INTO \"Clientes\" (\"Id\", \"RazonSocial\", \"Cif\", \"EsCritico\", \"TenantId\", \"Version\", \"CreadoEnUtc\", \"EstaEliminado\") " +
            "VALUES (@id, 'Intento cruzado', 'B99999999', false, @tenantOtro, @version, now(), false);";
        insertar.Parameters.AddWithValue("id", Guid.NewGuid());
        insertar.Parameters.AddWithValue("tenantOtro", _tenantB);
        insertar.Parameters.AddWithValue("version", Guid.NewGuid());

        var accion = async () => await insertar.ExecuteNonQueryAsync();

        await accion.Should().ThrowAsync<PostgresException>(
            "WITH CHECK debe rechazar una fila cuyo TenantId no coincide con app.tenant_id de la sesión, aunque el INSERT venga de fuera de EF");
    }
}
