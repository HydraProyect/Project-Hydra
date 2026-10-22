using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Centros.Queries.ObtenerTrabajadoresAsignadosDeCentro;
using CaeManager.Application.Proyectos.Commands.CrearProyecto;
using CaeManager.Application.Proyectos.Commands.RequisitosEvento;
using CaeManager.Application.Proyectos.Queries.RequisitosEvento;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Proyectos;

/// <summary>
/// Proyecto de tipo Evento bajo <b><c>cae_app_runtime</c></b> con los interceptores de sellado y de sesión RLS
/// de producción (mismo arnés que <c>ConductoresDeVehiculoBajoRlsTests</c>): copia de requisitos desde otro
/// evento y desde una plantilla, aislamiento por Tenant propietario de las tres tablas nuevas, FK compuesta
/// contra un Tipo de Documento de otro Tenant, y la pestaña de Trabajadores del Centro, que no muestra a quien
/// solo entró por eventos terminados. La siembra va como propietario de la base, porque no es lo que se mide.
/// </summary>
public class EventosBajoRlsTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private CaeManagerDbContext _propietario = null!;
    private Guid _eventoOtroCliente, _obra;
    private CaeManagerDbContext _runtime = null!;
    private CaeManagerDbContext _runtimeAjeno = null!;
    private Guid _tenantSesion;
    private Guid _tenantAjeno;
    private Guid _cliente;
    private Guid _otroCliente;
    private Guid _estadio;
    private Guid _dni;
    private Guid _seguro;
    private Guid _tipoAjeno;
    private Guid _eventoPasado;
    private Guid _eventoProximo;
    private Guid _plantillaOtroCliente;
    private Guid _voluntarioSoloPasado;
    private Guid _voluntarioProximo;
    private Guid _asignadoYVoluntario;

    public async Task InitializeAsync()
    {
        var tenantPorAmbito = new TenantActualPorAmbito();
        var opcionesPropietario = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantPorAmbito))
            .Options;
        _propietario = new CaeManagerDbContext(opcionesPropietario, new EphemeralDataProtectionProvider(), tenantPorAmbito);
        await _propietario.Database.MigrateAsync();

        var tenantSesion = new Tenant("Tenant propietario que organiza partidos");
        var tenantAjeno = new Tenant("Tenant propietario ajeno");
        _propietario.Tenants.AddRange(tenantSesion, tenantAjeno);
        await _propietario.SaveChangesAsync();
        (_tenantSesion, _tenantAjeno) = (tenantSesion.Id, tenantAjeno.Id);

        var hoy = DiaDeNegocio.Hoy();
        using (AmbitoTenantExplicito.Establecer(_tenantSesion))
        {
            var club = new Empresa("Club Deportivo Springfield", "G10380186");
            var otro = new Empresa("Patronato Shelbyville", "G87654323");
            _propietario.Empresas.AddRange(club, otro);
            await _propietario.SaveChangesAsync();
            var estadio = new Centro(club.Id, club.Id, "Estadio municipal");
            var pabellonOtro = new Centro(otro.Id, otro.Id, "Pabellón");
            var dni = new TipoDocumento("DNI del voluntario", null, false, 1, AmbitoAplicacion.Trabajador);
            var seguro = new TipoDocumento("Seguro de voluntariado", 12, true, 2, AmbitoAplicacion.Trabajador);
            _propietario.AddRange(estadio, pabellonOtro, dni, seguro);
            await _propietario.SaveChangesAsync();

            var pasado = Proyecto.Crear(club.Id, estadio.Id, "Jornada 1", hoy.AddDays(-20), hoy.AddDays(-19), null, TipoProyecto.Evento);
            var proximo = Proyecto.Crear(club.Id, estadio.Id, "Jornada 3", hoy.AddDays(5), hoy.AddDays(6), null, TipoProyecto.Evento);
            var eventoOtro = Proyecto.Crear(otro.Id, pabellonOtro.Id, "Torneo del patronato", hoy.AddDays(-3), hoy.AddDays(-2), null, TipoProyecto.Evento);
            var obra = Proyecto.Crear(club.Id, estadio.Id, "Reforma de gradas", hoy.AddDays(-60), null, null);
            _propietario.Proyectos.AddRange(pasado, proximo, eventoOtro, obra);
            (_eventoOtroCliente, _obra) = (eventoOtro.Id, obra.Id);
            await _propietario.SaveChangesAsync();
            _propietario.ProyectosRequisitos.AddRange(new ProyectoRequisito(pasado.Id, dni.Id), new ProyectoRequisito(pasado.Id, seguro.Id));
            var plantillaOtro = PlantillaRequisitosEvento.Crear(otro.Id, "Plantilla del patronato", [dni.Id]);
            _propietario.PlantillasRequisitosEvento.Add(plantillaOtro);

            var ana = Trabajador.DeEmpresa(club.Id, "Ana", "Vidal Ruiz", "45678912S");
            var iker = Trabajador.DeEmpresa(club.Id, "Iker", "Sanz Mora", "12345678Z");
            var raul = Trabajador.DeEmpresa(club.Id, "Raúl", "Ortega Gil", "77189989B");
            _propietario.Trabajadores.AddRange(ana, iker, raul);
            await _propietario.SaveChangesAsync();
            _propietario.ProyectosTecnicos.AddRange(
                new ProyectoTecnico(pasado.Id, ana.Id, hoy.AddDays(-20)),
                new ProyectoTecnico(proximo.Id, iker.Id, hoy),
                new ProyectoTecnico(proximo.Id, raul.Id, hoy));
            _propietario.Asignaciones.Add(new Asignacion(raul.Id, estadio.Id, hoy.AddDays(-100)));
            await _propietario.SaveChangesAsync();

            (_cliente, _otroCliente, _estadio, _dni, _seguro) = (club.Id, otro.Id, estadio.Id, dni.Id, seguro.Id);
            (_eventoPasado, _eventoProximo, _plantillaOtroCliente) = (pasado.Id, proximo.Id, plantillaOtro.Id);
            (_voluntarioSoloPasado, _voluntarioProximo, _asignadoYVoluntario) = (ana.Id, iker.Id, raul.Id);
        }

        using (AmbitoTenantExplicito.Establecer(_tenantAjeno))
        {
            var tipoAjeno = new TipoDocumento("DNI ajeno", null, false, 1, AmbitoAplicacion.Trabajador);
            _propietario.Add(tipoAjeno);
            await _propietario.SaveChangesAsync();
            _tipoAjeno = tipoAjeno.Id;
        }

        _runtime = CrearRuntime(_tenantSesion);
        _runtimeAjeno = CrearRuntime(_tenantAjeno);
    }

    public async Task DisposeAsync()
    {
        await _runtimeAjeno.DisposeAsync();
        await _runtime.DisposeAsync();
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    [Fact]
    public async Task Crear_un_evento_copiando_de_otro_evento_copia_sus_requisitos_en_el_Tenant_de_la_sesion()
    {
        var resultado = await CrearEvento("Jornada 5", copiarDe: _eventoPasado);

        resultado.EsExitoso.Should().BeTrue(resultado.Error.Mensaje);
        var filas = await _propietario.ProyectosRequisitos.IgnoreQueryFilters().AsNoTracking()
            .Where(r => r.ProyectoId == resultado.Valor).ToListAsync();
        filas.Select(r => r.TipoDocumentoId).Should().BeEquivalentTo([_dni, _seguro]);
        filas.Should().OnlyContain(r => r.TenantId == _tenantSesion);
    }

    [Fact]
    public async Task Crear_un_evento_desde_una_plantilla_copia_sus_tipos()
    {
        var plantilla = await new GuardarPlantillaRequisitosEventoCommandHandler(
                new ProyectoRepository(_runtime), new ProyectoRequisitoRepository(_runtime), new PlantillaRequisitosEventoRepository(_runtime),
                new AlcanceDatosServiceFalso(), _runtime)
            .Handle(new GuardarPlantillaRequisitosEventoCommand(_eventoPasado, "Partidos de liga"), CancellationToken.None);
        plantilla.EsExitoso.Should().BeTrue(plantilla.Error.Mensaje);

        await using var otraPeticion = CrearRuntime(_tenantSesion);
        var resultado = await CrearEvento("Jornada 6", plantilla: plantilla.Valor, contexto: otraPeticion);

        resultado.EsExitoso.Should().BeTrue(resultado.Error.Mensaje);
        (await _propietario.ProyectosRequisitos.IgnoreQueryFilters().AsNoTracking()
                .Where(r => r.ProyectoId == resultado.Valor).Select(r => r.TipoDocumentoId).ToListAsync())
            .Should().BeEquivalentTo([_dni, _seguro]);
    }

    [Fact]
    public async Task La_plantilla_de_un_Cliente_fuera_del_alcance_no_sirve_de_origen_y_no_se_crea_nada()
    {
        var resultado = await CrearEvento("Jornada 7", plantilla: _plantillaOtroCliente,
            alcance: new AlcanceDatosServiceFalso(clienteIds: [_cliente]));

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Proyecto.OrigenRequisitosNoEncontrado");
        (await _propietario.Proyectos.IgnoreQueryFilters().CountAsync(p => p.Nombre == "Jornada 7")).Should().Be(0);
    }

    [Fact]
    public async Task Las_tres_tablas_nuevas_solo_las_ve_su_Tenant_propietario()
    {
        (await _runtime.ProyectosRequisitos.IgnoreQueryFilters().CountAsync()).Should().Be(2, "control positivo");
        (await _runtime.PlantillasRequisitosEvento.IgnoreQueryFilters().CountAsync()).Should().Be(1, "control positivo");
        (await _runtime.PlantillasRequisitosEventoTipos.IgnoreQueryFilters().CountAsync()).Should().Be(1, "control positivo");

        (await _runtimeAjeno.ProyectosRequisitos.IgnoreQueryFilters().CountAsync()).Should().Be(0,
            "sin filtro global de EF, solo RLS puede ocultar los requisitos al otro Tenant");
        (await _runtimeAjeno.PlantillasRequisitosEvento.IgnoreQueryFilters().CountAsync()).Should().Be(0);
        (await _runtimeAjeno.PlantillasRequisitosEventoTipos.IgnoreQueryFilters().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task La_base_rechaza_un_requisito_con_un_Tipo_de_Documento_de_otro_Tenant_aunque_el_handler_no_filtre()
    {
        _runtime.ProyectosRequisitos.Add(new ProyectoRequisito(_eventoProximo, _tipoAjeno));

        var guardar = () => _runtime.SaveChangesAsync();

        var fallo = await guardar.Should().ThrowAsync<DbUpdateException>();
        fallo.Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task El_handler_no_anade_un_Tipo_de_Documento_de_otro_Tenant()
    {
        var resultado = await new AnadirRequisitoEventoCommandHandler(
                new ProyectoRepository(_runtime), new ProyectoRequisitoRepository(_runtime), _runtime, new AlcanceDatosServiceFalso(), _runtime)
            .Handle(new AnadirRequisitoEventoCommand(_eventoProximo, _tipoAjeno), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Proyecto.TipoDocumentoNoEncontrado");
    }

    [Fact]
    public async Task El_handler_no_anade_un_Tipo_de_Documento_que_no_es_de_ambito_Trabajador()
    {
        Guid rnt;
        using (AmbitoTenantExplicito.Establecer(_tenantSesion))
        {
            var tipo = new TipoDocumento("RNT del club", null, false, 3, AmbitoAplicacion.Empresa);
            _propietario.TiposDocumento.Add(tipo);
            await _propietario.SaveChangesAsync();
            rnt = tipo.Id;
        }
        var handler = new AnadirRequisitoEventoCommandHandler(
            new ProyectoRepository(_runtime), new ProyectoRequisitoRepository(_runtime), _runtime, new AlcanceDatosServiceFalso(), _runtime);

        var resultado = await handler.Handle(new AnadirRequisitoEventoCommand(_eventoProximo, rnt), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue("un evento pide lo que aporta cada participante: un documento de Empresa no lo cumple nadie");
        resultado.Error.Codigo.Should().Be("Proyecto.RequisitoNoEsDeTrabajador");
        (await _runtime.ProyectosRequisitos.CountAsync(r => r.TipoDocumentoId == rnt)).Should().Be(0);
        (await handler.Handle(new AnadirRequisitoEventoCommand(_eventoProximo, _dni), CancellationToken.None)).EsExitoso
            .Should().BeTrue("control positivo: un tipo de ámbito Trabajador sí se añade");
    }

    [Fact]
    public async Task El_Centro_no_muestra_a_quien_solo_entro_por_un_evento_terminado_y_si_al_de_un_evento_vigente()
    {
        var filas = await new ObtenerTrabajadoresAsignadosDeCentroQueryHandler(_runtime, _runtime, _runtime, _runtime, new AlcanceDatosServiceFalso(), _runtime)
            .Handle(new ObtenerTrabajadoresAsignadosDeCentroQuery(_estadio), CancellationToken.None);

        filas.Select(f => f.TrabajadorId).Should().NotContain(_voluntarioSoloPasado,
            "solo entró por la Jornada 1, ya terminada: el Centro no acumula voluntarios");
        filas.Should().ContainSingle(f => f.TrabajadorId == _voluntarioProximo)
            .Which.EventoNombre.Should().Be("Jornada 3");
        filas.Should().ContainSingle(f => f.TrabajadorId == _asignadoYVoluntario,
                "con Asignación activa aparece una sola vez, como asignado")
            .Which.EventoNombre.Should().BeNull();
    }

    [Fact]
    public async Task Al_cerrar_el_evento_vigente_su_voluntario_deja_de_figurar_en_el_Centro()
    {
        using (AmbitoTenantExplicito.Establecer(_tenantSesion))
        {
            var evento = await _propietario.Proyectos.IgnoreQueryFilters().SingleAsync(p => p.Id == _eventoProximo);
            evento.Cerrar(evento.FechaInicio);
            await _propietario.SaveChangesAsync();
        }

        var filas = await new ObtenerTrabajadoresAsignadosDeCentroQueryHandler(_runtime, _runtime, _runtime, _runtime, new AlcanceDatosServiceFalso(), _runtime)
            .Handle(new ObtenerTrabajadoresAsignadosDeCentroQuery(_estadio), CancellationToken.None);

        filas.Select(f => f.TrabajadorId).Should().Equal(_asignadoYVoluntario);
    }

    /// <summary>El último día del evento todavía es vigente: quien entra por él figura en el Centro hasta ese día incluido.</summary>
    [Fact]
    public async Task El_voluntario_de_un_evento_que_termina_hoy_aun_figura_en_el_Centro()
    {
        var hoy = DiaDeNegocio.Hoy();
        using (AmbitoTenantExplicito.Establecer(_tenantSesion))
        {
            var jornada2 = Proyecto.Crear(_cliente, _estadio, "Jornada 2", hoy.AddDays(-1), hoy, null, TipoProyecto.Evento);
            _propietario.Proyectos.Add(jornada2);
            await _propietario.SaveChangesAsync();
            _propietario.ProyectosTecnicos.Add(new ProyectoTecnico(jornada2.Id, _voluntarioSoloPasado, hoy.AddDays(-1)));
            await _propietario.SaveChangesAsync();
        }

        var filas = await new ObtenerTrabajadoresAsignadosDeCentroQueryHandler(_runtime, _runtime, _runtime, _runtime, new AlcanceDatosServiceFalso(), _runtime)
            .Handle(new ObtenerTrabajadoresAsignadosDeCentroQuery(_estadio), CancellationToken.None);

        filas.Should().ContainSingle(f => f.TrabajadorId == _voluntarioSoloPasado).Which.EventoNombre.Should().Be("Jornada 2");
    }

    [Fact]
    public async Task Un_evento_de_un_Cliente_fuera_del_alcance_no_sirve_de_origen()
    {
        var resultado = await CrearEvento("Jornada 8", copiarDe: _eventoOtroCliente, alcance: new AlcanceDatosServiceFalso(clienteIds: [_cliente]));

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Proyecto.OrigenRequisitosNoEncontrado");
    }

    [Fact]
    public async Task Una_obra_no_sirve_de_origen_de_requisitos()
    {
        var resultado = await CrearEvento("Jornada 9", copiarDe: _obra);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Proyecto.OrigenRequisitosNoEncontrado");
    }

    /// <summary>Añadir, quitar y guardar plantilla pasan por la misma puerta: evento visible por su Cliente empresarial.</summary>
    [Fact]
    public async Task Los_comandos_de_requisitos_no_tocan_un_evento_fuera_del_alcance_ni_una_obra()
    {
        var fuera = new AlcanceDatosServiceFalso(clienteIds: [_otroCliente]);
        var repo = new ProyectoRepository(_runtime);
        var requisitos = new ProyectoRequisitoRepository(_runtime);

        var anadir = await new AnadirRequisitoEventoCommandHandler(repo, requisitos, _runtime, fuera, _runtime)
            .Handle(new AnadirRequisitoEventoCommand(_eventoPasado, _dni), CancellationToken.None);
        var quitar = await new QuitarRequisitoEventoCommandHandler(repo, requisitos, fuera, _runtime)
            .Handle(new QuitarRequisitoEventoCommand(_eventoPasado, _dni), CancellationToken.None);
        var plantilla = await new GuardarPlantillaRequisitosEventoCommandHandler(repo, requisitos, new PlantillaRequisitosEventoRepository(_runtime), fuera, _runtime)
            .Handle(new GuardarPlantillaRequisitosEventoCommand(_eventoPasado, "Copia ajena"), CancellationToken.None);
        var enObra = await new AnadirRequisitoEventoCommandHandler(repo, requisitos, _runtime, new AlcanceDatosServiceFalso(), _runtime)
            .Handle(new AnadirRequisitoEventoCommand(_obra, _dni), CancellationToken.None);

        new[] { anadir.Error.Codigo, quitar.Error.Codigo, plantilla.Error.Codigo }.Should().AllBe("Proyecto.NoEncontrado");
        enObra.Error.Codigo.Should().Be("Proyecto.NoEsEvento");
        (await _propietario.ProyectosRequisitos.IgnoreQueryFilters().CountAsync(r => r.ProyectoId == _eventoPasado)).Should().Be(2);
        (await _propietario.PlantillasRequisitosEvento.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Las_lecturas_de_requisitos_se_traducen_a_SQL_y_respetan_el_alcance()
    {
        var requisitos = await new ObtenerRequisitosEventoQueryHandler(_runtime, _runtime, _runtime, new AlcanceDatosServiceFalso())
            .Handle(new ObtenerRequisitosEventoQuery(_eventoPasado), CancellationToken.None);
        requisitos.Select(r => r.Nombre).Should().BeEquivalentTo("DNI del voluntario", "Seguro de voluntariado");

        var ajenos = await new ObtenerRequisitosEventoQueryHandler(_runtime, _runtime, _runtime, new AlcanceDatosServiceFalso(clienteIds: [_otroCliente]))
            .Handle(new ObtenerRequisitosEventoQuery(_eventoPasado), CancellationToken.None);
        ajenos.Should().BeEmpty("el evento es de un Cliente empresarial fuera del alcance");

        var origenes = await new ObtenerOrigenesRequisitosEventoQueryHandler(_runtime, _runtime, new AlcanceDatosServiceFalso())
            .Handle(new ObtenerOrigenesRequisitosEventoQuery(_cliente), CancellationToken.None);
        origenes.Eventos.Should().Contain(o => o.Id == _eventoPasado && o.Requisitos == 2);
        origenes.Eventos.Select(o => o.Id).Should().NotContain([_obra, _eventoOtroCliente], "solo eventos del mismo Cliente empresarial");
        origenes.Plantillas.Should().BeEmpty("la única plantilla es de otro Cliente empresarial");
    }

    private Task<Result<Guid>> CrearEvento(
        string nombre, Guid? copiarDe = null, Guid? plantilla = null, AlcanceDatosServiceFalso? alcance = null, CaeManagerDbContext? contexto = null)
    {
        var ctx = contexto ?? _runtime;
        var hoy = DiaDeNegocio.Hoy();
        return new CrearProyectoCommandHandler(
                new ProyectoRepository(ctx), ctx, alcance ?? new AlcanceDatosServiceFalso(), ctx, ctx, ctx, new ProyectoRequisitoRepository(ctx), ctx)
            .Handle(new CrearProyectoCommand(_cliente, _estadio, nombre, hoy.AddDays(30), hoy.AddDays(30), null,
                TipoProyecto.Evento, copiarDe, plantilla), CancellationToken.None);
    }

    private CaeManagerDbContext CrearRuntime(Guid tenant)
    {
        var tenantDeLaPeticion = new TenantActualDeLaPeticion(tenant);
        var usuario = new CurrentUserServiceFalso(Guid.NewGuid(), Roles.Administrador, tenantOrigenId: tenant);
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantDeLaPeticion),
                new TenantRlsConnectionInterceptor(tenantDeLaPeticion, new SinClienteActivo(), usuario, BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;
        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantDeLaPeticion);
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    private sealed class TenantActualDeLaPeticion(Guid tenantDeLaSesion) : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual ?? tenantDeLaSesion;
    }

    private sealed class SinClienteActivo : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }
}
