using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.IntegrationTests.Migraciones;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Empresas;

/// <summary>
/// <c>DelegacionesEmpresa</c> y <c>OficiosEmpresa</c> bajo <c>cae_app_runtime</c>, el rol de producción: otro Tenant no
/// ve nada, el <c>WITH CHECK</c> no deja regalar una fila a otro Tenant, y la FK compuesta
/// <c>(TenantId, EmpresaId, …)</c> de Trabajadores no deja asignar el catálogo de otra Empresa. Además, la migración de
/// datos que convierte cada Puesto distinto de cada Empresa en un Oficio.
/// </summary>
public class DelegacionYOficioEmpresaRlsTests : IAsyncLifetime
{
    private const string MigracionAnterior = "20261018182902_AnadeContactoEmergenciaATrabajador";

    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionDeBaseVacia();
    private readonly Guid _tenantA = Guid.NewGuid();
    private readonly Guid _tenantB = Guid.NewGuid();
    private readonly Guid _tenantC = Guid.NewGuid();
    private Guid _empresaA;
    private Guid _empresaC;
    private Guid _otraEmpresaA;
    private Guid _subcontrataA;
    private Guid _empresaB;

    public static TheoryData<string> Tablas => ["DelegacionesEmpresa", "OficiosEmpresa"];

    public async Task InitializeAsync()
    {
        // Esquema anterior a la migración, con Puestos sembrados, para que la migración de datos tenga qué convertir.
        await using (var real = CrearContexto(_tenantA))
            await real.GetService<IMigrator>().MigrateAsync(MigracionAnterior);

        await using (var anterior = CrearContextoAnterior(_tenantA))
        {
            var empresa = new Empresa("Montajes A S.L.", "B12345674");
            var otra = new Empresa("Otra A S.L.", "B87654323");
            var subcontrata = new Empresa("Subcontrata A S.L.", "B10380186");
            anterior.Empresas.AddRange(empresa, otra, subcontrata);
            await anterior.SaveChangesAsync();
            anterior.Trabajadores.AddRange(
                Trabajador.DeEmpresa(empresa.Id, "Ana", "Uno", "12345678Z", puesto: "Soldador"),
                Trabajador.DeEmpresa(empresa.Id, "Luis", "Dos", "77189989B", puesto: "  SOLDADOR  "),
                Trabajador.DeEmpresa(empresa.Id, "Eva", "Tres", "00000000T", puesto: "Pintor"),
                Trabajador.DeSubcontrata(subcontrata.Id, "Sara", "Cuatro", "11111111H"));
            await anterior.SaveChangesAsync();
            (_empresaA, _otraEmpresaA, _subcontrataA) = (empresa.Id, otra.Id, subcontrata.Id);
        }

        // Tenant C: el mismo Puesto que en el Tenant A (cada Tenant tiene su Oficio), mayúsculas con tilde, un
        // eliminado y un anonimizado (fuera de la migración) y un Puesto con tabuladores escrito por debajo del dominio.
        await using (var anterior = CrearContextoAnterior(_tenantC))
        {
            var empresa = new Empresa("Montajes C S.L.", "B12345674");
            anterior.Empresas.Add(empresa);
            await anterior.SaveChangesAsync();
            var eliminado = Trabajador.DeEmpresa(empresa.Id, "Elio", "Borrado", "44444444A", puesto: "Gruista");
            anterior.Trabajadores.AddRange(
                Trabajador.DeEmpresa(empresa.Id, "Carla", "Uno", "12345678Z", puesto: "Soldador"),
                Trabajador.DeEmpresa(empresa.Id, "Pedro", "Dos", "22222222J", puesto: "Peón"),
                Trabajador.DeEmpresa(empresa.Id, "Pablo", "Tres", "33333333P", puesto: "PEÓN"),
                Trabajador.DeEmpresa(empresa.Id, "Tania", "Cuatro", "55555555K", puesto: "Peón"),
                Trabajador.DeEmpresa(empresa.Id, "Ane", "Cinco", "66666666Q", puesto: "Encofrador"),
                eliminado);
            await anterior.SaveChangesAsync();
            eliminado.MarcarComoEliminado(Guid.NewGuid());
            await anterior.SaveChangesAsync();
            _empresaC = empresa.Id;
        }

        await EjecutarComoMigradorAsync("""
            UPDATE "Trabajadores" SET "Puesto" = E'\t peón \t' WHERE "Dni" = '55555555K';
            UPDATE "Trabajadores" SET "AnonimizadoEnUtc" = now() WHERE "Dni" = '66666666Q';
            """);

        await using (var real = CrearContexto(_tenantA))
            await real.Database.MigrateAsync();

        await using (var a = CrearContexto(_tenantA))
        {
            a.DelegacionesEmpresa.Add(new DelegacionEmpresa(_empresaA, "Sevilla"));
            await a.SaveChangesAsync();
        }

        await using var b = CrearContexto(_tenantB);
        var empresaB = new Empresa("Montajes B S.L.", "B10380194");
        b.Empresas.Add(empresaB);
        await b.SaveChangesAsync();
        b.DelegacionesEmpresa.Add(new DelegacionEmpresa(empresaB.Id, "Bilbao"));
        b.OficiosEmpresa.Add(new OficioEmpresa(empresaB.Id, "Electricista"));
        await b.SaveChangesAsync();
        _empresaB = empresaB.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    [Fact]
    public async Task La_migracion_crea_un_oficio_por_puesto_distinto_de_cada_empresa_y_lo_asigna()
    {
        await using var db = CrearContexto(_tenantA);

        var oficios = await db.OficiosEmpresa.Where(o => o.EmpresaId == _empresaA).Select(o => o.Nombre).ToListAsync();
        oficios.Should().HaveCount(2, "«Soldador» y «  SOLDADOR  » son el mismo oficio").And.Contain("Pintor");

        var trabajadores = await db.Trabajadores.ToListAsync();
        trabajadores.Where(t => t.EmpresaId == _empresaA).Should().OnlyContain(t => t.OficioEmpresaId != null);
        trabajadores.Single(t => t.Dni == "77189989B").Puesto.Should().Be("SOLDADOR", "la migración no toca el Puesto (el dominio ya lo guardó sin espacios en los extremos)");
        trabajadores.Single(t => t.SubcontrataId == _subcontrataA).OficioEmpresaId.Should().BeNull();
        (await db.OficiosEmpresa.CountAsync(o => o.EmpresaId == _otraEmpresaA)).Should().Be(0);
    }

    [Fact]
    public async Task La_migracion_separa_tenants_ignora_mayusculas_y_tabuladores_y_deja_fuera_eliminados_y_anonimizados()
    {
        await using var c = CrearContexto(_tenantC);

        var oficios = await c.OficiosEmpresa.Where(o => o.EmpresaId == _empresaC).ToListAsync();
        oficios.Select(o => o.Nombre.ToLowerInvariant()).Should().BeEquivalentTo(["soldador", "peón"],
            "«Peón», «PEÓN» y «\\t peón \\t» son el mismo Oficio; el eliminado (Gruista) y el anonimizado (Encofrador) no crean ninguno");
        oficios.Should().OnlyContain(o => o.TenantId == _tenantC);

        await using var a = CrearContexto(_tenantA);
        var soldadorA = await a.OficiosEmpresa.SingleAsync(o => o.EmpresaId == _empresaA && o.Nombre.ToLower() == "soldador");
        soldadorA.Id.Should().NotBe(oficios.Single(o => o.Nombre == "Soldador").Id, "el mismo Puesto en dos Tenants da dos Oficios");

        var trabajadores = await c.Trabajadores.IgnoreQueryFilters().Where(t => t.TenantId == _tenantC).ToListAsync();
        var peon = oficios.Single(o => o.Nombre.ToLowerInvariant() == "peón").Id;
        trabajadores.Where(t => t.Dni is "22222222J" or "33333333P" or "55555555K").Should().OnlyContain(t => t.OficioEmpresaId == peon);
        trabajadores.Single(t => t.Dni == "44444444A").OficioEmpresaId.Should().BeNull("un Trabajador eliminado no recibe Oficio");
        trabajadores.Single(t => t.Dni == "66666666Q").OficioEmpresaId.Should().BeNull("un Trabajador anonimizado no recibe Oficio");
    }

    [Theory]
    [InlineData("DelegacionesEmpresa")]
    [InlineData("OficiosEmpresa")]
    public async Task El_indice_unico_rechaza_el_mismo_nombre_vivo_en_la_empresa_y_admite_uno_eliminado(string tabla)
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        await FijarTenantAsync(conexion, _tenantB);

        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            $"INSERT INTO \"{tabla}\" (\"Id\", \"TenantId\", \"EmpresaId\", \"Nombre\", \"Version\", \"CreadoEnUtc\", \"EstaEliminado\") " +
            "VALUES (gen_random_uuid(), @tenant, @empresa, @nombre, gen_random_uuid(), now(), @eliminado);";
        comando.Parameters.AddWithValue("tenant", _tenantB);
        comando.Parameters.AddWithValue("empresa", _empresaB);
        var nombre = comando.Parameters.AddWithValue("nombre", "Repetido");
        var eliminado = comando.Parameters.AddWithValue("eliminado", false);
        await comando.ExecuteNonQueryAsync();

        nombre.Value = "REPETIDO";
        (await FluentActions.Awaiting(() => comando.ExecuteNonQueryAsync()).Should().ThrowAsync<PostgresException>())
            .Which.Should().Match<PostgresException>(e =>
                e.SqlState == PostgresErrorCodes.UniqueViolation && e.ConstraintName == $"UX_{tabla}_TenantId_EmpresaId_Clave");

        // Control positivo: un nombre eliminado no bloquea volver a crearlo.
        eliminado.Value = true;
        (await comando.ExecuteNonQueryAsync()).Should().Be(1);
    }

    [Fact]
    public async Task El_repositorio_traduce_la_carrera_del_indice_unico_a_false_y_descarta_el_alta()
    {
        await using var b = CrearContexto(_tenantB);
        var repositorio = new Infrastructure.Persistence.Repositories.CatalogoTrabajadoresEmpresaRepository(b);
        repositorio.Agregar(new OficioEmpresa(_empresaB, "  ELECTRICISTA "));

        (await repositorio.GuardarCambiosAsync()).Should().BeFalse();
        b.ChangeTracker.Entries<OficioEmpresa>().Should().NotContain(e => e.State == EntityState.Added);

        repositorio.Agregar(new OficioEmpresa(_empresaB, "Fontanero"));
        (await repositorio.GuardarCambiosAsync()).Should().BeTrue("el contexto quedó limpio tras el choque");
    }

    [Fact]
    public async Task Asignar_un_oficio_mientras_otro_lo_elimina_choca_por_concurrencia()
    {
        Guid trabajadorId;
        await using (var siembra = CrearContexto(_tenantA))
        {
            var trabajador = Trabajador.DeEmpresa(_empresaA, "Rosa", "Carrera", "88888888Y");
            siembra.Trabajadores.Add(trabajador);
            await siembra.SaveChangesAsync();
            trabajadorId = trabajador.Id;
        }

        // Quien elimina ya leyó el Oficio y contó cero Trabajadores con él.
        await using var eliminador = CrearContexto(_tenantA);
        var oficioLeido = await eliminador.OficiosEmpresa.SingleAsync(o => o.EmpresaId == _empresaA && o.Nombre == "Pintor");

        // Entre medias, otra persona se lo asigna a un Trabajador.
        await using (var asignador = CrearContexto(_tenantA))
        {
            var oficio = await asignador.OficiosEmpresa.SingleAsync(o => o.Id == oficioLeido.Id);
            var trabajador = await asignador.Trabajadores.SingleAsync(t => t.Id == trabajadorId);
            oficio.RegistrarAsignacion();
            trabajador.FijarDelegacionEmpresaYOficio(null, oficio);
            await asignador.SaveChangesAsync();
        }

        oficioLeido.MarcarComoEliminado(Guid.NewGuid());
        await FluentActions.Awaiting(() => eliminador.SaveChangesAsync()).Should().ThrowAsync<DbUpdateConcurrencyException>();
    }

    [Fact]
    public async Task Restaurar_un_trabajador_le_quita_el_oficio_y_la_delegacion_eliminados_mientras_tanto()
    {
        Guid trabajadorId;
        await using (var siembra = CrearContexto(_tenantA))
        {
            var delegacion = await siembra.DelegacionesEmpresa.SingleAsync(d => d.EmpresaId == _empresaA);
            var oficio = new OficioEmpresa(_empresaA, "Andamiero");
            siembra.OficiosEmpresa.Add(oficio);
            var trabajador = Trabajador.DeEmpresa(_empresaA, "Iker", "Vuelta", "88888888Y");
            siembra.Trabajadores.Add(trabajador);
            await siembra.SaveChangesAsync();
            trabajador.FijarDelegacionEmpresaYOficio(delegacion, oficio);
            await siembra.SaveChangesAsync();

            // El Trabajador se elimina y, ya sin Trabajadores vivos, se eliminan su Delegación y su Oficio.
            trabajador.MarcarComoEliminado(Guid.NewGuid());
            await siembra.SaveChangesAsync();
            delegacion.MarcarComoEliminado(Guid.NewGuid());
            oficio.MarcarComoEliminado(Guid.NewGuid());
            await siembra.SaveChangesAsync();
            trabajadorId = trabajador.Id;
        }

        await using var contexto = CrearContexto(_tenantA);
        var handler = new Application.Trabajadores.Commands.RestaurarTrabajador.RestaurarTrabajadorCommandHandler(
            contexto, new TenantActualAmbiental { TenantId = _tenantA }, new AlcanceDatosServiceFalso(),
            new Infrastructure.Persistence.Repositories.CatalogoTrabajadoresEmpresaRepository(contexto), contexto);

        (await handler.Handle(new(trabajadorId), CancellationToken.None)).EsExitoso.Should().BeTrue();

        var restaurado = await contexto.Trabajadores.AsNoTracking().SingleAsync(t => t.Id == trabajadorId);
        restaurado.DelegacionEmpresaId.Should().BeNull();
        restaurado.OficioEmpresaId.Should().BeNull();
        restaurado.Puesto.Should().Be("Andamiero", "quitar el Oficio no borra el Puesto");
    }

    [Theory]
    [InlineData("cae_app_soporte")]
    [InlineData("cae_app_aprovisionamiento")]
    public async Task Soporte_y_aprovisionamiento_leen_el_catalogo_de_su_tenant_y_no_escriben(string rol)
    {
        // Aprovisionamiento: CrearTrabajadorCommand corre elevado a este rol y lee el catálogo para validar lo elegido.
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var transaccion = await conexion.BeginTransactionAsync();
        await using (var preparar = conexion.CreateCommand())
        {
            preparar.CommandText = $"SELECT set_config('app.tenant_id', @tenant, true); SET LOCAL ROLE {rol};";
            preparar.Parameters.AddWithValue("tenant", _tenantB.ToString());
            await preparar.ExecuteNonQueryAsync();
        }

        foreach (var tabla in new[] { "DelegacionesEmpresa", "OficiosEmpresa" })
        {
            await using var leer = conexion.CreateCommand();
            leer.CommandText = $"SELECT count(*) FROM \"{tabla}\";";
            ((long)(await leer.ExecuteScalarAsync())!).Should().Be(1, $"{rol} lee la fila del Tenant B en {tabla}");
        }

        await using var escribir = conexion.CreateCommand();
        escribir.CommandText = "UPDATE \"OficiosEmpresa\" SET \"Nombre\" = 'Cambiado';";
        (await FluentActions.Awaiting(() => escribir.ExecuteNonQueryAsync()).Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, $"{rol} no escribe en el catálogo");
    }

    [Fact]
    public async Task El_alta_de_un_trabajador_con_oficio_y_delegacion_funciona_como_aprovisionamiento()
    {
        // CrearTrabajadorCommand es IComandoDeAprovisionamiento: puede correr como cae_app_aprovisionamiento, que solo lee
        // el catálogo. El alta no renueva la versión del Oficio ni de la Delegación (eso sería un UPDATE con 42501).
        Guid oficioId, delegacionId, versionOficio, versionDelegacion;
        await using (var b = CrearContexto(_tenantB))
        {
            var oficio = await b.OficiosEmpresa.AsNoTracking().SingleAsync(o => o.EmpresaId == _empresaB);
            var delegacion = await b.DelegacionesEmpresa.AsNoTracking().SingleAsync(d => d.EmpresaId == _empresaB);
            (oficioId, versionOficio, delegacionId, versionDelegacion) = (oficio.Id, oficio.Version, delegacion.Id, delegacion.Version);
        }

        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using (var preparar = conexion.CreateCommand())
        {
            preparar.CommandText = "SELECT set_config('app.tenant_id', @tenant, false); SET ROLE cae_app_aprovisionamiento;";
            preparar.Parameters.AddWithValue("tenant", _tenantB.ToString());
            await preparar.ExecuteNonQueryAsync();
        }

        var tenant = new TenantActualAmbiental { TenantId = _tenantB };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(conexion, p => p.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenant), new ConcurrenciaOptimistaInterceptor())
            .Options;
        Guid trabajadorId;
        await using (var contexto = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenant))
        {
            var handler = new Application.Trabajadores.Commands.CrearTrabajador.CrearTrabajadorCommandHandler(
                new Infrastructure.Persistence.Repositories.TrabajadorRepository(contexto), contexto,
                new Infrastructure.Persistence.Repositories.CatalogoTrabajadoresEmpresaRepository(contexto),
                new AlcanceDatosServiceFalso(empresaIds: [_empresaB]), contexto);

            var resultado = await handler.Handle(new(_empresaB, null, "Olga", "Alta", "99999999R", null, null, null,
                OficioEmpresaId: oficioId, DelegacionEmpresaId: delegacionId), CancellationToken.None);

            resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : null);
            trabajadorId = resultado.Valor;
        }

        await using var verificar = CrearContexto(_tenantB);
        var creado = await verificar.Trabajadores.AsNoTracking().SingleAsync(t => t.Id == trabajadorId);
        creado.OficioEmpresaId.Should().Be(oficioId);
        creado.DelegacionEmpresaId.Should().Be(delegacionId);
        creado.Puesto.Should().Be("Electricista");
        (await verificar.OficiosEmpresa.AsNoTracking().SingleAsync(o => o.Id == oficioId)).Version.Should().Be(versionOficio);
        (await verificar.DelegacionesEmpresa.AsNoTracking().SingleAsync(d => d.Id == delegacionId)).Version.Should().Be(versionDelegacion);
    }

    [Theory]
    [MemberData(nameof(Tablas))]
    public async Task Sin_tenant_de_sesion_no_se_ve_nada_y_cada_tenant_solo_ve_lo_suyo(string tabla)
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        (await ContarAsync(conexion, tabla)).Should().Be(0);

        await FijarTenantAsync(conexion, _tenantB);
        (await ContarAsync(conexion, tabla)).Should().Be(1, "el Tenant B solo ve su fila, nunca las del Tenant A");
    }

    [Theory]
    [MemberData(nameof(Tablas))]
    public async Task El_with_check_rechaza_mover_una_fila_a_otro_tenant(string tabla)
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        await FijarTenantAsync(conexion, _tenantB);

        await using var comando = conexion.CreateCommand();
        comando.CommandText = $"UPDATE \"{tabla}\" SET \"TenantId\" = @destino;";
        comando.Parameters.AddWithValue("destino", _tenantA);

        (await FluentActions.Awaiting(() => comando.ExecuteNonQueryAsync()).Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);

        // Control positivo: lo que se rechaza es el cambio de dueño, no la escritura.
        comando.CommandText = $"UPDATE \"{tabla}\" SET \"Nombre\" = \"Nombre\" || ' (editada)';";
        comando.Parameters.Clear();
        (await comando.ExecuteNonQueryAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Un_insert_en_otro_tenant_lo_rechaza_el_with_check()
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        await FijarTenantAsync(conexion, _tenantB);

        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "INSERT INTO \"OficiosEmpresa\" (\"Id\", \"TenantId\", \"EmpresaId\", \"Nombre\", \"Version\", \"CreadoEnUtc\", \"EstaEliminado\") " +
            "VALUES (gen_random_uuid(), @ajeno, @empresa, 'Sembrado en ajeno', gen_random_uuid(), now(), false);";
        comando.Parameters.AddWithValue("ajeno", _tenantA);
        comando.Parameters.AddWithValue("empresa", _empresaA);

        (await FluentActions.Awaiting(() => comando.ExecuteNonQueryAsync()).Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Un_trabajador_no_puede_tener_la_delegacion_de_otra_empresa_ni_ser_de_subcontrata()
    {
        await using var conexion = await AbrirComoRuntimeAsync();
        await FijarTenantAsync(conexion, _tenantA);

        await using (var otra = conexion.CreateCommand())
        {
            otra.CommandText =
                "INSERT INTO \"DelegacionesEmpresa\" (\"Id\", \"TenantId\", \"EmpresaId\", \"Nombre\", \"Version\", \"CreadoEnUtc\", \"EstaEliminado\") " +
                "VALUES (gen_random_uuid(), @tenant, @otra, 'De la otra', gen_random_uuid(), now(), false);";
            otra.Parameters.AddWithValue("tenant", _tenantA);
            otra.Parameters.AddWithValue("otra", _otraEmpresaA);
            await otra.ExecuteNonQueryAsync();
        }

        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            "UPDATE \"Trabajadores\" SET \"DelegacionEmpresaId\" = (SELECT \"Id\" FROM \"DelegacionesEmpresa\" WHERE \"EmpresaId\" = @otra) " +
            "WHERE \"EmpresaId\" = @empresa;";
        comando.Parameters.AddWithValue("otra", _otraEmpresaA);
        comando.Parameters.AddWithValue("empresa", _empresaA);
        (await FluentActions.Awaiting(() => comando.ExecuteNonQueryAsync()).Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);

        comando.CommandText =
            "UPDATE \"Trabajadores\" SET \"OficioEmpresaId\" = (SELECT \"Id\" FROM \"OficiosEmpresa\" LIMIT 1) " +
            "WHERE \"SubcontrataId\" = @subcontrata;";
        comando.Parameters.Clear();
        comando.Parameters.AddWithValue("subcontrata", _subcontrataA);
        (await FluentActions.Awaiting(() => comando.ExecuteNonQueryAsync()).Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task El_repositorio_de_otro_tenant_no_lee_la_delegacion()
    {
        await using var a = CrearContexto(_tenantA);
        var delegacionA = await a.DelegacionesEmpresa.SingleAsync(d => d.EmpresaId == _empresaA);

        await using var b = CrearContexto(_tenantB);
        var repositorio = new Infrastructure.Persistence.Repositories.CatalogoTrabajadoresEmpresaRepository(b);
        (await repositorio.ObtenerDelegacionEmpresaAsync(delegacionA.Id)).Should().BeNull();
        (await repositorio.ListarDelegacionesEmpresaAsync(_empresaA)).Should().BeEmpty();
        (await repositorio.ListarOficiosAsync(_empresaB)).Should().ContainSingle();
    }

    private async Task<NpgsqlConnection> AbrirComoRuntimeAsync()
    {
        var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var setRol = conexion.CreateCommand();
        setRol.CommandText = "SET ROLE cae_app_runtime;";
        await setRol.ExecuteNonQueryAsync();
        return conexion;
    }

    private async Task EjecutarComoMigradorAsync(string sql)
    {
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        await comando.ExecuteNonQueryAsync();
    }

    private static async Task FijarTenantAsync(NpgsqlConnection conexion, Guid tenantId)
    {
        await using var comando = conexion.CreateCommand();
        comando.CommandText = "SELECT set_config('app.tenant_id', @valor, false);";
        comando.Parameters.AddWithValue("valor", tenantId.ToString());
        await comando.ExecuteNonQueryAsync();
    }

    private static async Task<long> ContarAsync(NpgsqlConnection conexion, string tabla)
    {
        await using var consulta = conexion.CreateCommand();
        consulta.CommandText = $"SELECT count(*) FROM \"{tabla}\";";
        return (long)(await consulta.ExecuteScalarAsync())!;
    }

    private DbContextOptions<CaeManagerDbContext> Opciones(TenantActualAmbiental tenant) =>
        new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadena, p => p.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenant), new ConcurrenciaOptimistaInterceptor())
            .Options;

    private CaeManagerDbContext CrearContexto(Guid tenantId)
    {
        var tenant = new TenantActualAmbiental { TenantId = tenantId };
        return new(Opciones(tenant), new EphemeralDataProtectionProvider(), tenant);
    }

    private ContextoAnteriorALaMarcaDePrincipal CrearContextoAnterior(Guid tenantId)
    {
        var tenant = new TenantActualAmbiental { TenantId = tenantId };
        return new(Opciones(tenant), new EphemeralDataProtectionProvider(), tenant);
    }
}
