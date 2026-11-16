using CaeManager.Application.Common;
using CaeManager.Application.Inbound.Requisitos;
using CaeManager.Application.Inbound.Requisitos.Commands.RetirarPlantillaRequisitos;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Inbound.Requisitos;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Inbound.Persistence;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Inbound.Requisitos;

/// <summary>
/// Plantillas de requisitos del Tenant TCE (B-14) bajo <c>cae_app_runtime</c> con contexto firmado: RLS aísla las dos tablas
/// por Tenant sin ayuda del filtro de EF y no deja escribir filas de otro Tenant, el runtime no borra (se retiran), las FK
/// compuestas no dejan colgar un requisito de un tipo de documento ajeno, la base sostiene una sola plantilla activa por
/// referencia, y los requisitos de base A1 y ETT llegan inactivos y pendientes de revisión legal (R-14).
/// </summary>
public class PlantillasRequisitosBajoRuntimeTests : IAsyncLifetime
{
    private static readonly AtributosRequisito Basicos = new(
        OrigenRequisito.PropioTenant, ModoVigenciaRequisito.LaPoneElDocumento, VigenciaMeses: null, ToleranciaDias: null,
        BloqueaAcceso: true, Criterio: "Vigente", DestinatarioRequisito.Todos, TiposContratacion.Todos, ["Equivalente extranjero"],
        AmbitoDocumentoRequisito.GeneralTec);

    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Tenant _propietario = new("Tenant TCE propietario");
    private readonly Tenant _ajeno = new("Tenant TCE ajeno");
    private readonly Guid _administrador = Guid.NewGuid();
    private readonly Guid _administradorAjeno = Guid.NewGuid();
    private Guid _centro;
    private Guid _plantilla;
    private Guid _requisito;
    private Guid _plantillaAjena;
    private TipoDocumento _tipoAjeno = null!;

    public async Task InitializeAsync()
    {
        await using (var contexto = Contexto(_propietario.Id))
        {
            await contexto.Database.MigrateAsync();
            contexto.Tenants.AddRange(_propietario, _ajeno);
            var roles = await contexto.Roles.ToDictionaryAsync(r => r.Name!, r => r.Id);
            Cuenta(contexto, _administrador, _propietario.Id, roles[Roles.Administrador]);
            Cuenta(contexto, _administradorAjeno, _ajeno.Id, roles[Roles.Administrador]);
            var cliente = Empresa.CrearComoCliente("Cliente empresarial titular", "B10380186", false, null, null);
            contexto.Empresas.Add(cliente);
            var centro = new Centro(cliente.Id, cliente.Id, "Centro de trabajo del titular");
            contexto.Centros.Add(centro);
            var tipo = new TipoDocumento("Formación del puesto", null, false, 0, AmbitoAplicacion.Trabajador);
            contexto.TiposDocumento.Add(tipo);
            await contexto.SaveChangesAsync();

            var plantilla = PlantillaRequisitos.ParaCentro(centro.Id);
            var requisito = plantilla.AnadirRequisito(tipo, null, Basicos);
            contexto.PlantillasRequisitos.Add(plantilla);
            await contexto.SaveChangesAsync();
            _centro = centro.Id; _plantilla = plantilla.Id; _requisito = requisito.Id;
        }
        await using (var contexto = Contexto(_ajeno.Id))
        {
            var cliente = Empresa.CrearComoCliente("Cliente empresarial ajeno", "B10380186", false, null, null);
            contexto.Empresas.Add(cliente);
            _tipoAjeno = new TipoDocumento("Formación del puesto ajena", null, false, 0, AmbitoAplicacion.Trabajador);
            contexto.TiposDocumento.Add(_tipoAjeno);
            await contexto.SaveChangesAsync();
            var plantilla = PlantillaRequisitos.ParaExigenciasDe(cliente.Id);
            plantilla.AnadirRequisito(_tipoAjeno, null, Basicos with { Origen = OrigenRequisito.ExigenciaN3 });
            contexto.PlantillasRequisitos.Add(plantilla);
            await contexto.SaveChangesAsync();
            _plantillaAjena = plantilla.Id;
        }
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    [Fact]
    public async Task Las_plantillas_y_los_requisitos_de_otro_Tenant_son_invisibles_aunque_se_quite_el_filtro_de_EF()
    {
        await using var contexto = Contexto(_propietario.Id, _administrador);
        (await contexto.Database.SqlQueryRaw<string>("SELECT current_user AS \"Value\"").SingleAsync()).Should().Be("cae_app_runtime");

        (await contexto.PlantillasRequisitos.IgnoreQueryFilters().Select(p => p.Id).ToListAsync())
            .Should().Equal([_plantilla], "RLS oculta la plantilla del Tenant ajeno y deja ver la propia");
        (await contexto.RequisitosPlantilla.IgnoreQueryFilters().Select(r => r.Id).ToListAsync())
            .Should().Equal([_requisito]);

        var leido = await contexto.PlantillasRequisitos.Include(p => p.Requisitos).SingleAsync(p => p.Id == _plantilla);
        leido.Requisitos.Should().ContainSingle().Which.Equivalencias.Should().Equal("Equivalente extranjero");

        await using var contextoAjeno = Contexto(_ajeno.Id, _administradorAjeno);
        (await contextoAjeno.PlantillasRequisitos.IgnoreQueryFilters().Select(p => p.Id).ToListAsync())
            .Should().Equal([_plantillaAjena], "y al revés");
    }

    [Theory]
    [InlineData("PlantillasRequisitos")]
    [InlineData("RequisitosPlantilla")]
    public async Task El_runtime_no_borra_y_no_escribe_filas_con_el_TenantId_de_otro(string tabla)
    {
        await using var contexto = Contexto(_propietario.Id, _administrador);

        // La tabla sale de InlineData, no de un usuario.
        var nombre = "\"" + tabla + "\"";
        var borrado = "DELETE FROM " + nombre + ";";
        var actualizacion = "UPDATE " + nombre + " SET \"Id\" = \"Id\";";
        var traslado = "UPDATE " + nombre + " SET \"TenantId\" = {0};";

        var borrar = () => contexto.Database.ExecuteSqlRawAsync(borrado);
        (await borrar.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, "la migración no concede DELETE a cae_app_runtime");

        (await contexto.Database.ExecuteSqlRawAsync(actualizacion))
            .Should().Be(1, "control positivo: el runtime actualiza la fila de su Tenant");

        var moverAlAjeno = () => contexto.Database.ExecuteSqlRawAsync(traslado, _ajeno.Id);
        (await moverAlAjeno.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, "WITH CHECK de aislamiento_tenant");
    }

    [Fact]
    public async Task Un_requisito_no_cuelga_de_un_tipo_de_documento_de_otro_Tenant()
    {
        await using var contexto = Contexto(_propietario.Id, _administrador);
        var plantilla = await contexto.PlantillasRequisitos.Include(p => p.Requisitos).SingleAsync(p => p.Id == _plantilla);

        plantilla.AnadirRequisito(_tipoAjeno, null, Basicos);
        var guardar = () => contexto.SaveChangesAsync();

        (await guardar.Should().ThrowAsync<DbUpdateException>())
            .WithInnerException<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation,
                "la FK compuesta (TenantId, TipoDocumentoId) contra TiposDocumento lo hace irrepresentable");
    }

    [Fact]
    public async Task La_base_sostiene_una_sola_plantilla_activa_por_Centro()
    {
        await using (var contexto = Contexto(_propietario.Id, _administrador))
        {
            contexto.PlantillasRequisitos.Add(PlantillaRequisitos.ParaCentro(_centro));
            var guardar = () => contexto.SaveChangesAsync();
            (await guardar.Should().ThrowAsync<DbUpdateException>())
                .WithInnerException<PostgresException>()
                .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation, "índice único parcial sobre las activas");
        }

        await using (var contexto = Contexto(_propietario.Id, _administrador))
        {
            var vieja = await contexto.PlantillasRequisitos.SingleAsync(p => p.Id == _plantilla);
            vieja.Retirar();
            await contexto.SaveChangesAsync();
            contexto.PlantillasRequisitos.Add(PlantillaRequisitos.ParaCentro(_centro));
            await contexto.SaveChangesAsync();
            (await contexto.PlantillasRequisitos.CountAsync(p => p.CentroId == _centro))
                .Should().Be(2, "control positivo: retirada la anterior, cabe una nueva");
        }
    }

    [Fact]
    public async Task R14_A1_y_ETT_llegan_inactivos_y_pendientes_de_revision_legal()
    {
        await using var contexto = Contexto(_propietario.Id, _administrador);

        var sembrados = await contexto.CatalogoRequisitosBase
            .Where(r => r.Codigo == "a1-trabajador-desplazado" || r.Codigo == "puesta-a-disposicion-ett")
            .Select(r => new { r.Codigo, r.Activo, r.PendienteRevisionLegal })
            .ToListAsync();

        sembrados.Should().HaveCount(2);
        sembrados.Should().AllSatisfy(r =>
        {
            r.Activo.Should().BeFalse(r.Codigo);
            r.PendienteRevisionLegal.Should().BeTrue(r.Codigo);
        });
    }

    [Fact]
    public async Task Soporte_lee_las_plantillas_de_su_Tenant_y_no_las_escribe()
    {
        await using var conexion = new NpgsqlConnection(_cadena);
        await conexion.OpenAsync();
        await using var transaccion = await conexion.BeginTransactionAsync();
        var contextoFirmado = await ContextoRlsDePruebas.FirmarAsync(conexion, tenantId: _propietario.Id);
        await using (var preparar = conexion.CreateCommand())
        {
            preparar.CommandText = "SELECT set_config('app.contexto', @contexto, true); SET LOCAL ROLE cae_app_soporte;";
            preparar.Parameters.AddWithValue("contexto", contextoFirmado);
            await preparar.ExecuteNonQueryAsync();
        }
        await using (var leer = conexion.CreateCommand())
        {
            leer.CommandText = "SELECT count(*) FROM \"PlantillasRequisitos\";";
            ((long)(await leer.ExecuteScalarAsync())!).Should().Be(1, "Soporte TALVEG lee la plantilla de su Tenant y no la ajena");
        }
        await using var escribir = conexion.CreateCommand();
        escribir.CommandText = "UPDATE \"RequisitosPlantilla\" SET \"Activo\" = false;";
        (await FluentActions.Awaiting(() => escribir.ExecuteNonQueryAsync()).Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege, "la migración solo concede SELECT a cae_app_soporte");
    }

    [Fact]
    public async Task D1_un_acceso_total_en_su_Tenant_no_escribe_la_plantilla_de_otro_Tenant()
    {
        // D1 (DECISION_LOG 2026-11-15): las escrituras de plantillas se autorizan con IAlcanceDatosService hasta B-03, así que
        // un Gestor CAE con acceso total en su Tenant pasa la autorización del handler. El límite de Tenant lo sostienen el
        // filtro de EF y RLS (que el primer test de este fichero prueba sin el filtro): la plantilla ajena no se encuentra y
        // no se toca.
        await using (var contexto = Contexto(_propietario.Id, _administrador))
        {
            var handler = new RetirarPlantillaRequisitosCommandHandler(
                new PlantillaRequisitosRepository(contexto), new AlcanceDatosServiceFalso(), contexto);

            var resultado = await handler.Handle(new RetirarPlantillaRequisitosCommand(_plantillaAjena, Guid.NewGuid()), default);

            resultado.EsExitoso.Should().BeFalse();
            resultado.Error.Codigo.Should().Be(PlantillaRequisitosErrores.NoEncontrada.Codigo);
        }

        await using var contextoAjeno = Contexto(_ajeno.Id, _administradorAjeno);
        (await contextoAjeno.PlantillasRequisitos.SingleAsync(p => p.Id == _plantillaAjena)).Activa
            .Should().BeTrue("la plantilla del otro Tenant sigue activa");
    }

    private static void Cuenta(CaeManagerDbContext contexto, Guid id, Guid tenantId, Guid rolId)
    {
        contexto.Users.Add(new ApplicationUser
        {
            Id = id,
            TenantId = tenantId,
            UserName = $"{id:N}@caemanager.local",
            NormalizedUserName = $"{id:N}@CAEMANAGER.LOCAL",
            Email = $"{id:N}@caemanager.local",
            NormalizedEmail = $"{id:N}@CAEMANAGER.LOCAL",
            NombreCompleto = "Administrador",
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString()
        });
        contexto.UserRoles.Add(new IdentityUserRole<Guid> { UserId = id, RoleId = rolId });
    }

    private CaeManagerDbContext Contexto(Guid tenantId, Guid? usuario = null)
    {
        var tenant = new TenantActualAmbiental { TenantId = tenantId };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>().UseNpgsql(
            usuario is null ? _cadena : BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadena),
            n => n.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"));
        opciones.AddInterceptors(new TenantSelladoInterceptor(tenant));
        if (usuario is not null)
            opciones.AddInterceptors(new TenantRlsConnectionInterceptor(tenant, new SinTenantSeleccionado(),
                new CurrentUserServiceFalso(usuario.Value, Roles.Administrador, tenantOrigenId: tenantId), BaseDatosPostgresDePruebas.FirmanteContextoRls),
                new ConcurrenciaOptimistaInterceptor());
        return new(opciones.Options, new EphemeralDataProtectionProvider(), tenant);
    }

    private sealed class SinTenantSeleccionado : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }
}
