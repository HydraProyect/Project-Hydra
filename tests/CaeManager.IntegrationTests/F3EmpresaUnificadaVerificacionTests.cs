using CaeManager.Domain.Centros;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Subcontratas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Verificación del modelo real de F3 (Empresa unificada) pedida
/// explícitamente por el propietario del producto tras la revisión
/// adversaria (<c>f3-revision-adversaria-2026-08-25.md</c>) — antes de
/// escribir el PR de esquema definitivo. Cubre, contra Postgres real (no
/// el proyecto de descarte de la revisión), las tres cosas que esa
/// revisión dejaba como huecos de evidencia:
///
/// 1. la migración <c>F3EmpresaUnificadaVerificacion</c> copia
///    Clientes/Subcontratas -> Empresas conservando Id y mapeando los
///    campos transitorios correctamente (incluida la traducción de
///    <see cref="NivelServicioSubcontrata"/>, guardado como entero, a
///    texto);
/// 2. el <c>CHECK</c> de no-autorreferencia rechaza de verdad una fila con
///    las dos FKs iguales, en las tres tablas puente;
/// 3. la FK compuesta <c>(TenantId, Id)</c> de las entidades multi-FK
///    rechaza una referencia a una Empresa de otro tenant.
/// </summary>
public class F3EmpresaUnificadaVerificacionTests : IAsyncLifetime
{
    private const string MigracionAnteriorAF3 = "EstadoBootstrapPlataforma";
    private const string MigracionF3 = "F3EmpresaUnificadaVerificacion";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    public async Task InitializeAsync()
    {
        // Migra solo hasta la migración anterior a F3 — el resto de cada
        // test decide cuándo aplicar F3, porque el test de copia de datos
        // necesita insertar Cliente/Subcontrata ANTES de que la migración
        // que los copia se ejecute.
        await using var contexto = CrearContexto(Guid.NewGuid());
        var migrador = contexto.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrador.MigrateAsync(MigracionAnteriorAF3);
    }

    public async Task DisposeAsync()
    {
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    [Fact]
    public async Task La_migracion_F3_copia_Cliente_y_Subcontrata_a_Empresas_conservando_Id_y_mapeando_campos_transitorios()
    {
        var tenantId = Guid.NewGuid();
        var empresaPropiaId = Guid.NewGuid();
        Guid clienteId, subcontrataId;

        // Estado previo a F3: una Empresa propia ya existente, un Cliente y
        // una Subcontrata con NivelServicio=Supervisada (el caso no-default,
        // el que de verdad ejercita el CASE WHEN de la migración).
        //
        // La Empresa se inserta por SQL directo con SOLO las columnas que
        // existen ANTES de F3 — usar contexto.Empresas.Add aquí fallaría:
        // el modelo compilado de CaeManagerDbContext ya es el de DESPUÉS de
        // F3 (EmpresaConfiguration ya declara EjecutivoUsuarioId/EsCritico/
        // Notas/NivelServicio), pero la base de datos todavía no tiene esas
        // columnas en este punto del test. Es exactamente la situación real
        // de una migración: el código nuevo se despliega, pero hasta que la
        // migración corre, la base de datos sigue en el esquema anterior.
        await using (var contexto = CrearContexto(tenantId))
        await using (var conexion = new NpgsqlConnection(_cadenaConexion))
        {
            await conexion.OpenAsync();
            await using (var comando = conexion.CreateCommand())
            {
                comando.CommandText = """
                    INSERT INTO "Empresas"
                        ("Id", "TenantId", "RazonSocial", "Cif", "EsActividadAnexoI", "CreadoEnUtc", "EstaEliminado", "Version")
                    VALUES (@id, @tenantId, 'Talveg Coordinación S.L.', 'B12345674', false, now(), false, @version);
                    """;
                comando.Parameters.AddWithValue("id", empresaPropiaId);
                comando.Parameters.AddWithValue("tenantId", tenantId);
                comando.Parameters.AddWithValue("version", Guid.NewGuid());
                await comando.ExecuteNonQueryAsync();
            }

            var cliente = new Cliente("Iberojet S.A.", "B10380194", esCritico: true, notas: "Cliente prioritario", ejecutivoUsuarioId: Guid.NewGuid());
            var subcontrata = new Subcontrata("Medición de Temperatura S.L.", "B10380186");
            subcontrata.CambiarNivelServicio(NivelServicioSubcontrata.Supervisada);

            contexto.Clientes.Add(cliente);
            contexto.Subcontratas.Add(subcontrata);
            await contexto.SaveChangesAsync();

            clienteId = cliente.Id;
            subcontrataId = subcontrata.Id;
        }

        // Aplica F3.
        await using (var contexto = CrearContexto(tenantId))
        {
            var migrador = contexto.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrador.MigrateAsync(MigracionF3);
        }

        // Verificación: la Empresa preexistente queda EsPropia=true (no el
        // default booleano de CLR), y aparecen dos filas nuevas EsPropia=false
        // con el mismo Id que el Cliente/Subcontrata de origen.
        await using (var contexto = CrearContexto(tenantId))
        {
            var empresaPropia = await contexto.Empresas.SingleAsync(e => e.Id == empresaPropiaId);
            empresaPropia.EsPropia.Should().BeTrue("una Empresa ya existente antes de F3 nunca fue una contraparte");

            var desdeCliente = await contexto.Empresas.SingleAsync(e => e.Id == clienteId);
            desdeCliente.EsPropia.Should().BeFalse();
            desdeCliente.RazonSocial.Should().Be("Iberojet S.A.");
            desdeCliente.Cif.Should().Be("B10380194");
            desdeCliente.EsCritico.Should().BeTrue();
            desdeCliente.Notas.Should().Be("Cliente prioritario");
            desdeCliente.EjecutivoUsuarioId.Should().NotBeNull();
            desdeCliente.NivelServicio.Should().BeNull("EsCritico/Notas/EjecutivoUsuarioId son de Cliente, NivelServicio no aplica");

            var desdeSubcontrata = await contexto.Empresas.SingleAsync(e => e.Id == subcontrataId);
            desdeSubcontrata.EsPropia.Should().BeFalse();
            desdeSubcontrata.RazonSocial.Should().Be("Medición de Temperatura S.L.");
            desdeSubcontrata.NivelServicio.Should().Be("Supervisada", "el CASE WHEN debe traducir el entero 1, no copiarlo tal cual");
            desdeSubcontrata.EsCritico.Should().BeNull("EsCritico es de Cliente, no aplica a una fila ex-Subcontrata");
        }
    }

    [Fact]
    public async Task El_CHECK_de_EmpresaCliente_rechaza_una_fila_autorreferente()
    {
        var tenantId = Guid.NewGuid();
        Guid empresaId;

        await using (var contexto = CrearContexto(tenantId))
        {
            var migrador = contexto.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrador.MigrateAsync(MigracionF3);

            var empresa = new Empresa("Refrielectric S.L.", "B87654323");
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();
            empresaId = empresa.Id;
        }

        // Inserción directa por SQL: el constructor de dominio de
        // EmpresaCliente no impide EmpresaId == ClienteId (no había motivo
        // para hacerlo antes de F3 — la imposibilidad era estructural, no
        // de validación). La defensa tiene que estar en la base de datos.
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """
            INSERT INTO "EmpresasClientes" ("Id", "TenantId", "EmpresaId", "ClienteId")
            VALUES (@id, @tenantId, @empresaId, @empresaId);
            """;
        comando.Parameters.AddWithValue("id", Guid.NewGuid());
        comando.Parameters.AddWithValue("tenantId", tenantId);
        comando.Parameters.AddWithValue("empresaId", empresaId);

        var accion = async () => await comando.ExecuteNonQueryAsync();

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.ConstraintName.Should().Be("CK_EmpresasClientes_NoAutorreferencia");
    }

    [Fact]
    public async Task El_CHECK_de_SubcontrataCliente_rechaza_una_fila_autorreferente()
    {
        var tenantId = Guid.NewGuid();
        Guid empresaId;

        await using (var contexto = CrearContexto(tenantId))
        {
            var migrador = contexto.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrador.MigrateAsync(MigracionF3);

            var empresa = new Empresa("Arcos SPA S.L.", "B10000016");
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();
            empresaId = empresa.Id;
        }

        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """
            INSERT INTO "SubcontratasClientes" ("Id", "TenantId", "SubcontrataId", "ClienteId")
            VALUES (@id, @tenantId, @empresaId, @empresaId);
            """;
        comando.Parameters.AddWithValue("id", Guid.NewGuid());
        comando.Parameters.AddWithValue("tenantId", tenantId);
        comando.Parameters.AddWithValue("empresaId", empresaId);

        var accion = async () => await comando.ExecuteNonQueryAsync();

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.ConstraintName.Should().Be("CK_SubcontratasClientes_NoAutorreferencia");
    }

    [Fact]
    public async Task El_CHECK_de_SubcontrataEmpresa_rechaza_una_fila_autorreferente()
    {
        var tenantId = Guid.NewGuid();
        Guid empresaId;

        await using (var contexto = CrearContexto(tenantId))
        {
            var migrador = contexto.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrador.MigrateAsync(MigracionF3);

            var empresa = new Empresa("Pepitos SPA S.L.", "B10000024");
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();
            empresaId = empresa.Id;
        }

        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """
            INSERT INTO "SubcontratasEmpresas" ("Id", "TenantId", "SubcontrataId", "EmpresaId")
            VALUES (@id, @tenantId, @empresaId, @empresaId);
            """;
        comando.Parameters.AddWithValue("id", Guid.NewGuid());
        comando.Parameters.AddWithValue("tenantId", tenantId);
        comando.Parameters.AddWithValue("empresaId", empresaId);

        var accion = async () => await comando.ExecuteNonQueryAsync();

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.ConstraintName.Should().Be("CK_SubcontratasEmpresas_NoAutorreferencia");
    }

    [Fact]
    public async Task La_FK_compuesta_de_Centro_rechaza_una_Empresa_de_otro_tenant()
    {
        var tenantPropietario = Guid.NewGuid();
        var tenantAjeno = Guid.NewGuid();
        Guid empresaDelTenantAjenoId;

        await using (var contexto = CrearContexto(tenantAjeno))
        {
            var migrador = contexto.GetInfrastructure().GetRequiredService<IMigrator>();
            await migrador.MigrateAsync(MigracionF3);

            var empresaAjena = new Empresa("Empresa de otro tenant S.L.", "B20000014");
            contexto.Empresas.Add(empresaAjena);
            await contexto.SaveChangesAsync();
            empresaDelTenantAjenoId = empresaAjena.Id;
        }

        // Fila de Centro en el tenant propietario, apuntando por SQL directo
        // a la Empresa del tenant ajeno como "titular" — exactamente el
        // ataque que la FK compuesta (TenantId, ClienteId) -> (TenantId, Id)
        // debe impedir de forma físicamente imposible, no solo por
        // validación de aplicación.
        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """
            INSERT INTO "Centros" ("Id", "TenantId", "ClienteId", "EmpresaId", "Nombre", "Version", "CreadoEnUtc", "EstaEliminado")
            VALUES (@id, @tenantPropietario, @empresaAjena, @empresaAjena, 'Centro de prueba', @version, now(), false);
            """;
        comando.Parameters.AddWithValue("id", Guid.NewGuid());
        comando.Parameters.AddWithValue("tenantPropietario", tenantPropietario);
        comando.Parameters.AddWithValue("empresaAjena", empresaDelTenantAjenoId);
        comando.Parameters.AddWithValue("version", Guid.NewGuid());

        var accion = async () => await comando.ExecuteNonQueryAsync();

        (await accion.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
    }

    private CaeManagerDbContext CrearContexto(Guid tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
