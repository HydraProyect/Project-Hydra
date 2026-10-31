using CaeManager.Domain.Contactos;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Migrations.PostgreSQL;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// M-27 (ASVS 4.0.3 V4.1.3, V4.2.1): la base rechaza una fila hija colgada de un padre de otro Tenant, y la migración que
/// compuso las FKs aborta si ya hubiera filas así. La conexión es de superusuario a propósito: sin RLS de por medio, lo
/// único que puede parar la inserción es la FK compuesta. Cada intento tiene su control con el mismo padre desde su Tenant,
/// para que el 23503 no pueda venir de un padre inexistente.
/// </summary>
public class ClavesForaneasCompuestasConTenantTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();

    private Empresa _empresaA = null!;
    private ContactoAgenda _contactoA = null!;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(_tenantA);
        await contexto.Database.MigrateAsync();

        _empresaA = new Empresa("Empresa del Tenant A S.L.", "B87654323");
        contexto.Empresas.Add(_empresaA);
        await contexto.SaveChangesAsync();

        _contactoA = ContactoAgenda.DeEmpresa(_empresaA.Id, "Contacto A", "contacto@a.test");
        contexto.ContactosAgenda.Add(_contactoA);
        await contexto.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Un_hijo_de_coleccion_no_se_cuelga_de_un_padre_de_otro_Tenant()
    {
        await EsperarRechazoEnBAsync(contexto => contexto.ContactosAgendaRoles.Add(new ContactoAgendaRol(_contactoA.Id, RolContacto.ContactoCae)));

        await using var delTenantA = CrearContexto(_tenantA);
        delTenantA.ContactosAgendaRoles.Add(new ContactoAgendaRol(_contactoA.Id, RolContacto.ContactoCae));
        await delTenantA.SaveChangesAsync();
    }

    [Fact]
    public async Task Una_FK_obligatoria_sin_navegacion_no_apunta_a_una_Empresa_de_otro_Tenant()
    {
        var ahora = DateTime.UtcNow;
        await EsperarRechazoEnBAsync(contexto => contexto.SellosEmpresa.Add(new SelloEmpresa(_empresaA.Id, "sello.png", ahora)));

        await using var delTenantA = CrearContexto(_tenantA);
        delTenantA.SellosEmpresa.Add(new SelloEmpresa(_empresaA.Id, "sello.png", ahora));
        await delTenantA.SaveChangesAsync();
    }

    [Fact]
    public async Task Una_FK_opcional_no_apunta_a_una_Empresa_de_otro_Tenant()
    {
        await EsperarRechazoEnBAsync(contexto =>
            contexto.ContactosAgenda.Add(ContactoAgenda.DeEmpresa(_empresaA.Id, "Contacto cruzado", "cruzado@b.test")));

        await using var delTenantA = CrearContexto(_tenantA);
        delTenantA.ContactosAgenda.Add(ContactoAgenda.DeEmpresa(_empresaA.Id, "Contacto propio", "propio@a.test"));
        await delTenantA.SaveChangesAsync();
    }

    /// <summary>
    /// El esquema migrado, relación por relación: exactamente una FK compuesta y ninguna simple, porque las FKs compuestas
    /// en SQL crudo que convivían con la simple desaparecen al pasar al modelo.
    /// </summary>
    [Fact]
    public async Task Las_relaciones_compuestas_tienen_una_FK_con_TenantId_y_ninguna_sin_el()
    {
        await using var conexion = await AbrirConexionAsync();

        foreach (var (hija, columna, padre) in ComprobacionTenantClavesForaneas.Relaciones)
        {
            await using var comando = conexion.CreateCommand();
            comando.CommandText = """
                SELECT count(*) FILTER (WHERE tenant.attnum = ANY (c.conkey)) AS compuestas,
                       count(*) FILTER (WHERE NOT tenant.attnum = ANY (c.conkey)) AS simples
                  FROM pg_constraint c
                  JOIN pg_attribute fk ON fk.attrelid = c.conrelid AND fk.attname = @columna
                  JOIN pg_attribute tenant ON tenant.attrelid = c.conrelid AND tenant.attname = 'TenantId'
                 WHERE c.contype = 'f'
                   AND c.conrelid = format('public.%I', @hija::text)::regclass
                   AND c.confrelid = format('public.%I', @padre::text)::regclass
                   AND fk.attnum = ANY (c.conkey);
                """;
            comando.Parameters.AddWithValue("hija", hija);
            comando.Parameters.AddWithValue("columna", columna);
            comando.Parameters.AddWithValue("padre", padre);

            await using var lector = await comando.ExecuteReaderAsync();
            (await lector.ReadAsync()).Should().BeTrue();
            lector.GetInt64(0).Should().Be(1, $"{hija}.{columna} debe tener una FK compuesta hacia {padre}");
            lector.GetInt64(1).Should().Be(0, $"{hija}.{columna} no debe conservar la FK simple hacia {padre}");
        }
    }

    [Fact]
    public async Task La_comprobacion_previa_pasa_sin_filas_cruzadas_y_aborta_nombrando_relacion_y_recuento()
    {
        await using var conexion = await AbrirConexionAsync();
        await EjecutarAsync(conexion, ComprobacionTenantClavesForaneas.SqlComprobacionPrevia);

        await using (var contexto = CrearContexto(_tenantA))
        {
            contexto.ContactosAgendaRoles.Add(new ContactoAgendaRol(_contactoA.Id, RolContacto.ContactoCae));
            await contexto.SaveChangesAsync();
        }

        // Fabrica la fila que la migración tiene que detectar: sin la FK, un rol del Tenant B en un contacto del Tenant A.
        await EjecutarAsync(conexion, """
            DO $$
            DECLARE r record;
            BEGIN
                FOR r IN SELECT conname FROM pg_constraint
                          WHERE contype = 'f' AND conrelid = 'public."ContactosAgendaRoles"'::regclass LOOP
                    EXECUTE format('ALTER TABLE public."ContactosAgendaRoles" DROP CONSTRAINT %I', r.conname);
                END LOOP;
            END $$;
            """);
        await EjecutarAsync(conexion, $"""UPDATE public."ContactosAgendaRoles" SET "TenantId" = '{_tenantB}'""");

        var excepcion = await Record.ExceptionAsync(() =>
            EjecutarAsync(conexion, ComprobacionTenantClavesForaneas.SqlComprobacionPrevia));

        var error = excepcion.Should().BeOfType<PostgresException>().Subject;
        error.MessageText.Should().Contain("ContactosAgendaRoles.ContactoAgendaId -> ContactosAgenda: 1 filas");

        await using var verificacion = conexion.CreateCommand();
        verificacion.CommandText = """SELECT count(*) FROM public."ContactosAgendaRoles" WHERE "TenantId" = @tenant""";
        verificacion.Parameters.AddWithValue("tenant", _tenantB);
        ((long)(await verificacion.ExecuteScalarAsync())!).Should().Be(1, "la comprobación no corrige nada");
    }

    [Fact]
    public async Task La_comprobacion_previa_aborta_con_un_rol_sujeto_a_RLS_en_vez_de_contar_cero()
    {
        await using var conexion = await AbrirConexionAsync();
        await EjecutarAsync(conexion, "SET ROLE cae_app_runtime");

        var excepcion = await Record.ExceptionAsync(() =>
            EjecutarAsync(conexion, ComprobacionTenantClavesForaneas.SqlComprobacionPrevia));

        excepcion.Should().BeOfType<PostgresException>().Which.MessageText.Should().Contain("no evita la RLS");
    }

    [Fact]
    public async Task Un_hijo_nuevo_por_navegacion_en_un_padre_cargado_se_persiste_con_su_Tenant()
    {
        await using (var contexto = CrearContexto(_tenantA))
        {
            var contacto = await contexto.ContactosAgenda.Include(c => c.Roles).SingleAsync(c => c.Id == _contactoA.Id);
            contacto.EstablecerRoles([RolContacto.ContactoCae]);
            await contexto.SaveChangesAsync();
        }

        await using var verificacion = CrearContexto(_tenantA);
        var rol = await verificacion.ContactosAgendaRoles.SingleAsync(r => r.ContactoAgendaId == _contactoA.Id);
        rol.TenantId.Should().Be(_tenantA);
    }

    private async Task EsperarRechazoEnBAsync(Action<CaeManagerDbContext> anadir)
    {
        await using var contexto = CrearContexto(_tenantB);
        anadir(contexto);

        var excepcion = await Record.ExceptionAsync(() => contexto.SaveChangesAsync());

        var error = excepcion.Should().BeOfType<DbUpdateException>()
            .Which.InnerException.Should().BeOfType<PostgresException>().Subject;
        error.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
        error.ConstraintName.Should().Contain("TenantId", "la que rechaza es la FK compuesta, no otra");
    }

    private async Task<NpgsqlConnection> AbrirConexionAsync()
    {
        var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        return conexion;
    }

    private static async Task EjecutarAsync(NpgsqlConnection conexion, string sql)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        await comando.ExecuteNonQueryAsync();
    }

    private CaeManagerDbContext CrearContexto(Guid tenant)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
