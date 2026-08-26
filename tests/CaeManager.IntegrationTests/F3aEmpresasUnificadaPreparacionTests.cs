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
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// Verificación de F3a (preparación física de Empresas unificada) contra
/// Postgres real. Alcance estricto de F3a
/// (f3-diseno-fisico-empresa-unificada-2026-08-25.md §8,
/// f3-comparativa-alcance-abcd-2026-08-25.md, camino D): crear columnas +
/// backfill + índices — SIN redirigir lectores, SIN repuntear FKs, SIN
/// retirar tablas antiguas. El repunteo de FKs y los CHECK anti-
/// autorreferencia son F3c — ver f3c-diseno-adversario-reconciliacion-2026-08-25.md.
///
/// Cada test siembra datos ANTES de que la migración F3a se aplique y
/// deja que sea la migración REAL (<see cref="IMigrator.MigrateAsync"/>
/// contra el nombre exacto de la migración) la que ejecute el backfill —
/// no una reimplementación del SQL dentro del test. Un test que re-declara
/// su propia copia del INSERT puede quedarse en verde después de que
/// alguien edite la migración real y no el test — exactamente el "falso
/// verde" que este diseño evita a propósito.
/// </summary>
public class F3aEmpresasUnificadaPreparacionTests : IAsyncLifetime
{
    private const string MigracionAnteriorAF3a = "EstadoBootstrapPlataforma";
    private const string MigracionF3a = "F3aEmpresasUnificadaPreparacion";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(Guid.NewGuid());
        var migrador = contexto.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrador.MigrateAsync(MigracionAnteriorAF3a);
    }

    public async Task DisposeAsync()
    {
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    [Fact]
    public async Task El_backfill_real_copia_un_Cliente_activo_sin_divergencia_de_ningun_campo()
    {
        var tenantId = Guid.NewGuid();
        var ejecutivoId = Guid.NewGuid();
        Cliente clienteOriginal;

        await using (var contexto = CrearContexto(tenantId))
        {
            clienteOriginal = new Cliente("Iberojet S.A.", "B10380194", esCritico: true, notas: "Cliente prioritario", ejecutivoUsuarioId: ejecutivoId);
            contexto.Clientes.Add(clienteOriginal);
            await contexto.SaveChangesAsync();
        }

        await AplicarMigracionF3aAsync(tenantId);

        await using var contextoVerificacion = CrearContexto(tenantId);
        var copia = await contextoVerificacion.Empresas.SingleAsync(e => e.Id == clienteOriginal.Id);

        copia.EsPropia.Should().BeFalse();
        copia.RazonSocial.Should().Be(clienteOriginal.RazonSocial);
        copia.Cif.Should().Be(clienteOriginal.Cif);
        copia.EsCritico.Should().Be(clienteOriginal.EsCritico);
        copia.Notas.Should().Be(clienteOriginal.Notas);
        copia.EjecutivoUsuarioId.Should().Be(clienteOriginal.EjecutivoUsuarioId);
        copia.NivelServicio.Should().BeNull("EsCritico/Notas/EjecutivoUsuarioId son de Cliente; NivelServicio no aplica a una fila ex-Cliente");
    }

    [Fact]
    public async Task El_backfill_real_copia_un_Cliente_soft_deleted_conservando_su_estado_de_borrado()
    {
        var tenantId = Guid.NewGuid();
        Guid clienteId;
        var usuarioQueElimino = Guid.NewGuid();

        await using (var contexto = CrearContexto(tenantId))
        {
            var cliente = new Cliente("Cliente a eliminar", "B12345674", esCritico: false);
            contexto.Clientes.Add(cliente);
            await contexto.SaveChangesAsync();
            clienteId = cliente.Id;

            cliente.MarcarComoEliminado(usuarioQueElimino);
            await contexto.SaveChangesAsync();
        }

        await AplicarMigracionF3aAsync(tenantId);

        // Lectura directa por SQL, sin pasar por el DbContext: el filtro
        // global de EF (HasQueryFilter) podría enmascarar el dato real si
        // algún día cambiase de forma — leer crudo confirma el estado
        // físico de la columna, no el efecto de un filtro.
        await using var conexion = new Npgsql.NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """SELECT "EstaEliminado", "EliminadoPorUsuarioId" FROM "Empresas" WHERE "Id" = @id""";
        comando.Parameters.AddWithValue("id", clienteId);
        await using var lector = await comando.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue();
        lector.GetBoolean(0).Should().BeTrue("una fila soft-deleted en Cliente debe llegar soft-deleted a la copia, o F3c encontraría una divergencia falsa");
        lector.GetGuid(1).Should().Be(usuarioQueElimino);
    }

    [Fact]
    public async Task El_backfill_real_traduce_NivelServicio_de_Subcontrata_del_entero_al_texto_esperado()
    {
        var tenantId = Guid.NewGuid();
        Subcontrata subcontrataOriginal;

        await using (var contexto = CrearContexto(tenantId))
        {
            subcontrataOriginal = new Subcontrata("Medición de Temperatura S.L.", "B10380186");
            subcontrataOriginal.CambiarNivelServicio(NivelServicioSubcontrata.Supervisada);
            contexto.Subcontratas.Add(subcontrataOriginal);
            await contexto.SaveChangesAsync();
        }

        await AplicarMigracionF3aAsync(tenantId);

        await using var contextoVerificacion = CrearContexto(tenantId);
        var copia = await contextoVerificacion.Empresas.SingleAsync(e => e.Id == subcontrataOriginal.Id);

        copia.NivelServicio.Should().Be("Supervisada", "el CASE WHEN debe traducir el entero 1, no copiarlo tal cual");
        copia.RazonSocial.Should().Be(subcontrataOriginal.RazonSocial);
        copia.Cif.Should().Be(subcontrataOriginal.Cif);
        copia.EsCritico.Should().BeNull("EsCritico es de Cliente, no aplica a una fila ex-Subcontrata");
    }

    [Fact]
    public async Task El_backfill_real_traduce_NivelServicio_Gestionada_por_defecto_no_solo_el_caso_Supervisada()
    {
        // El caso Gestionada (0) es el default de NivelServicioSubcontrata —
        // sin este test, un CASE WHEN que solo mapeara 1 y dejara 0 en NULL
        // por error habría pasado inadvertido (el otro test solo cubre el
        // valor no-default).
        var tenantId = Guid.NewGuid();
        Subcontrata subcontrataOriginal;

        await using (var contexto = CrearContexto(tenantId))
        {
            subcontrataOriginal = new Subcontrata("Subcontrata Gestionada S.L.", "B87654323");
            contexto.Subcontratas.Add(subcontrataOriginal);
            await contexto.SaveChangesAsync();
        }

        await AplicarMigracionF3aAsync(tenantId);

        await using var contextoVerificacion = CrearContexto(tenantId);
        var copia = await contextoVerificacion.Empresas.SingleAsync(e => e.Id == subcontrataOriginal.Id);
        copia.NivelServicio.Should().Be("Gestionada");
    }

    [Fact]
    public async Task Una_Empresa_ya_existente_antes_de_F3a_queda_EsPropia_true_tras_la_migracion_real()
    {
        var tenantId = Guid.NewGuid();
        Guid empresaId;

        await using (var contexto = CrearContexto(tenantId))
        {
            // Empresa sembrada ANTES de F3a — con SQL directo, porque el
            // modelo compilado de CaeManagerDbContext ya es el de DESPUÉS
            // de F3a (EmpresaConfiguration ya declara EsPropia), pero la
            // base de datos en este punto del test todavía no tiene esa
            // columna. Reproduce exactamente la situación real: código
            // nuevo desplegado, migración todavía no aplicada.
            await using var conexion = new Npgsql.NpgsqlConnection(_cadenaConexion);
            await conexion.OpenAsync();
            await using var comando = conexion.CreateCommand();
            empresaId = Guid.NewGuid();
            comando.CommandText = """
                INSERT INTO "Empresas"
                    ("Id", "TenantId", "RazonSocial", "Cif", "EsActividadAnexoI", "CreadoEnUtc", "EstaEliminado", "Version")
                VALUES (@id, @tenantId, 'Talveg Coordinación S.L.', 'B12345674', false, now(), false, @version);
                """;
            comando.Parameters.AddWithValue("id", empresaId);
            comando.Parameters.AddWithValue("tenantId", tenantId);
            comando.Parameters.AddWithValue("version", Guid.NewGuid());
            await comando.ExecuteNonQueryAsync();
        }

        await AplicarMigracionF3aAsync(tenantId);

        await using var conexionVerificacion = new Npgsql.NpgsqlConnection(_cadenaConexion);
        await conexionVerificacion.OpenAsync();
        await using var comandoVerificacion = conexionVerificacion.CreateCommand();
        comandoVerificacion.CommandText = """SELECT "EsPropia" FROM "Empresas" WHERE "Id" = @id""";
        comandoVerificacion.Parameters.AddWithValue("id", empresaId);
        var esPropia = (bool)(await comandoVerificacion.ExecuteScalarAsync())!;
        esPropia.Should().BeTrue("una Empresa ya existente antes de F3a nunca fue una contraparte");
    }

    [Fact]
    public async Task Los_indices_unicos_de_Cif_y_RazonSocial_siguen_activos_tras_F3a()
    {
        var tenantId = Guid.NewGuid();
        await AplicarMigracionF3aAsync(tenantId);

        await using var contexto = CrearContexto(tenantId);
        contexto.Empresas.Add(new Empresa("Duplicado S.L.", "B12345674"));
        await contexto.SaveChangesAsync();

        await using var contexto2 = CrearContexto(tenantId);
        contexto2.Empresas.Add(new Empresa("Duplicado S.L. (otro nombre)", "B12345674"));
        var accion = async () => await contexto2.SaveChangesAsync();

        await accion.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task F3a_no_toca_las_FKs_de_Centro_siguen_apuntando_a_Clientes_no_a_Empresas()
    {
        // Confirmación explícita de que F3a se quedó estrictamente dentro
        // de su alcance (f3-comparativa-alcance-abcd-2026-08-25.md): el
        // repunteo de FKs es F3c, no debe haber ocurrido todavía.
        var tenantId = Guid.NewGuid();
        await AplicarMigracionF3aAsync(tenantId);

        await using var conexion = new Npgsql.NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = """
            SELECT confrelid::regclass::text
            FROM pg_constraint
            WHERE conname = 'FK_Centros_Clientes_TenantId_ClienteId';
            """;
        var tablaReferenciada = (string?)await comando.ExecuteScalarAsync();
        tablaReferenciada.Should().Be("\"Clientes\"", "F3a no debe repuntear ninguna FK — eso es F3c");
    }

    private async Task AplicarMigracionF3aAsync(Guid tenantId)
    {
        await using var contexto = CrearContexto(tenantId);
        var migrador = contexto.GetInfrastructure().GetRequiredService<IMigrator>();
        await migrador.MigrateAsync(MigracionF3a);
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
