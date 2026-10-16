using CaeManager.Application.Alertas;
using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Trabajadores;

/// <summary>
/// Voluntario inactivo y requisitos de evento bajo PostgreSQL con RLS forzada (decisión 2026-10-15):
/// las subconsultas de <c>ActividadVoluntarios</c> se traducen a SQL, el voluntario sin evento vigente ni
/// futuro no genera avisos ni cuenta en el Centro, se reactiva al asignarlo a otro evento, los requisitos
/// del evento vigente cuentan como faltantes del voluntario, y nada de esto cruza el Tenant propietario.
/// </summary>
public class VoluntariosInactivosBajoRlsTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private CaeManagerDbContext _propietario = null!;
    private Guid _tenantSesion, _tenantAjeno, _estadio, _dni, _seguro, _eventoProximo;
    private Guid _inactivo, _vigente, _futuro, _ajena, _voluntarioAjeno;

    public async Task InitializeAsync()
    {
        var tenantPorAmbito = new TenantActualPorAmbito();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantPorAmbito))
            .Options;
        _propietario = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantPorAmbito);
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
            _propietario.Empresas.Add(club);
            _propietario.ParametrosSistema.Add(new ParametroSistema(30, 15));
            await _propietario.SaveChangesAsync();
            var estadio = new Centro(club.Id, club.Id, "Estadio municipal");
            var dni = new TipoDocumento("DNI del voluntario", null, false, 1, AmbitoAplicacion.Trabajador);
            var seguro = new TipoDocumento("Seguro de voluntariado", 12, true, 2, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
            _propietario.AddRange(estadio, dni, seguro);
            await _propietario.SaveChangesAsync();

            var pasado = Proyecto.Crear(club.Id, estadio.Id, "Jornada 1", hoy.AddDays(-20), hoy.AddDays(-19), null, TipoProyecto.Evento);
            var vigente = Proyecto.Crear(club.Id, estadio.Id, "Jornada 2", hoy.AddDays(-1), hoy.AddDays(1), null, TipoProyecto.Evento);
            var proximo = Proyecto.Crear(club.Id, estadio.Id, "Jornada 3", hoy.AddDays(5), hoy.AddDays(6), null, TipoProyecto.Evento);
            _propietario.Proyectos.AddRange(pasado, vigente, proximo);
            await _propietario.SaveChangesAsync();
            _propietario.ProyectosRequisitos.AddRange(
                new ProyectoRequisito(pasado.Id, dni.Id), new ProyectoRequisito(vigente.Id, dni.Id), new ProyectoRequisito(proximo.Id, dni.Id));

            var inactivo = Voluntario(club.Id, "Ana", "Vidal Ruiz", "45678912S");
            var enVigente = Voluntario(club.Id, "Iker", "Sanz Mora", "12345678Z");
            var enFuturo = Voluntario(club.Id, "Raúl", "Ortega Gil", "77189989B");
            var ajena = Trabajador.DeEmpresa(club.Id, "Marta", "Gómez Paz", "11111111H");
            _propietario.Trabajadores.AddRange(inactivo, enVigente, enFuturo, ajena);
            await _propietario.SaveChangesAsync();
            _propietario.ProyectosTecnicos.AddRange(
                new ProyectoTecnico(pasado.Id, inactivo.Id, hoy.AddDays(-20)),
                new ProyectoTecnico(vigente.Id, enVigente.Id, hoy.AddDays(-1)),
                new ProyectoTecnico(proximo.Id, enFuturo.Id, hoy));
            _propietario.Asignaciones.AddRange(
                new Asignacion(inactivo.Id, estadio.Id, hoy.AddDays(-30)),
                new Asignacion(enVigente.Id, estadio.Id, hoy.AddDays(-30)),
                new Asignacion(ajena.Id, estadio.Id, hoy.AddDays(-30)));
            var ahora = hoy;
            _propietario.Documentos.AddRange(
                Documento.DeTrabajador(inactivo.Id, seguro.Id, fechaEmision: ahora.AddDays(-400), vigencia: VigenciaDocumento.VenceEl(ahora.AddDays(-5))),
                Documento.DeTrabajador(ajena.Id, seguro.Id, fechaEmision: ahora.AddDays(-400), vigencia: VigenciaDocumento.VenceEl(ahora.AddDays(-5))));
            await _propietario.SaveChangesAsync();

            (_estadio, _dni, _seguro, _eventoProximo) = (estadio.Id, dni.Id, seguro.Id, proximo.Id);
            (_inactivo, _vigente, _futuro, _ajena) = (inactivo.Id, enVigente.Id, enFuturo.Id, ajena.Id);
        }

        using (AmbitoTenantExplicito.Establecer(_tenantAjeno))
        {
            var otra = new Empresa("Patronato Shelbyville", "G87654323");
            _propietario.Empresas.Add(otra);
            _propietario.ParametrosSistema.Add(new ParametroSistema(30, 15));
            await _propietario.SaveChangesAsync();
            var ajeno = Voluntario(otra.Id, "Lucía", "Pardo Ríos", "22222222J");
            _propietario.Trabajadores.Add(ajeno);
            await _propietario.SaveChangesAsync();
            _voluntarioAjeno = ajeno.Id;
        }
    }

    public async Task DisposeAsync()
    {
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    [Fact]
    public async Task El_listado_marca_y_filtra_al_voluntario_inactivo_en_SQL()
    {
        await using var c = CrearRuntime(_tenantSesion);

        var todos = await Listar(c, null);
        todos.Single(t => t.Id == _inactivo).VoluntarioInactivo.Should().BeTrue();
        todos.Where(t => t.Id != _inactivo).Should().OnlyContain(t => !t.VoluntarioInactivo,
            "el voluntario con evento vigente o futuro y el de cuenta ajena están activos");
        todos.Single(t => t.Id == _futuro).TipoContratacion.Should().Be(TipoContratacion.Voluntario);
        todos.Single(t => t.Id == _ajena).TipoContratacion.Should().Be(TipoContratacion.CuentaAjena);

        (await Listar(c, true)).Select(t => t.Id).Should().Equal(_inactivo);
        (await Listar(c, false)).Select(t => t.Id).Should().BeEquivalentTo([_vigente, _futuro, _ajena]);
    }

    [Fact]
    public async Task El_voluntario_inactivo_no_genera_avisos_y_el_evento_vigente_aporta_sus_requisitos()
    {
        await using var c = CrearRuntime(_tenantSesion);

        var alertas = await Alertas(c);

        alertas.Should().Contain(a => a.TrabajadorId == _ajena && a.TipoDocumentoId == _seguro && a.Estado == EstadoDocumento.Vencido,
            "control positivo: el vencido de cuenta ajena sí avisa");
        alertas.Should().NotContain(a => a.TrabajadorId == _inactivo, "ni su vencido ni sus faltantes avisan");
        alertas.Should().Contain(a => a.TrabajadorId == _vigente && a.TipoDocumentoId == _dni && a.Estado == EstadoDocumento.Faltante
            && a.CentroId == _estadio, "el requisito del evento vigente cuenta como faltante en el Centro del evento");
        alertas.Should().NotContain(a => a.TrabajadorId == _futuro && a.TipoDocumentoId == _dni,
            "el evento aún no ha empezado: sus requisitos no cuentan todavía (opción A)");
        alertas.Should().NotContain(a => a.TrabajadorId == _ajena && a.TipoDocumentoId == _dni,
            "los requisitos del evento solo cuentan para los voluntarios asignados");
    }

    [Fact]
    public async Task El_voluntario_inactivo_no_cuenta_en_el_estado_del_Centro()
    {
        await using var c = CrearRuntime(_tenantSesion);

        var estado = (await new CalculoEstadoCentroService(c, c, c, c, c, c, c, c).CalcularAsync([_estadio], CancellationToken.None))[_estadio];

        estado.Causas.Should().Contain(x => x.TrabajadorId == _ajena, "control positivo");
        estado.Causas.Should().NotContain(x => x.TrabajadorId == _inactivo);
    }

    [Fact]
    public async Task Se_reactiva_al_asignarlo_a_otro_evento()
    {
        using (AmbitoTenantExplicito.Establecer(_tenantSesion))
        {
            _propietario.ProyectosTecnicos.Add(new ProyectoTecnico(_eventoProximo, _inactivo, DiaDeNegocio.Hoy()));
            await _propietario.SaveChangesAsync();
        }
        await using var c = CrearRuntime(_tenantSesion);

        (await Listar(c, true)).Should().BeEmpty();
        (await Alertas(c)).Should().Contain(a => a.TrabajadorId == _inactivo && a.TipoDocumentoId == _seguro,
            "reactivado, su vencido vuelve a avisar");
    }

    [Fact]
    public async Task Cada_Tenant_propietario_solo_ve_a_sus_voluntarios_inactivos()
    {
        await using var sesion = CrearRuntime(_tenantSesion);
        await using var ajeno = CrearRuntime(_tenantAjeno);

        (await Listar(sesion, true)).Select(t => t.Id).Should().Equal(_inactivo);
        (await Listar(ajeno, true)).Select(t => t.Id).Should().Equal(_voluntarioAjeno);
    }

    private static Trabajador Voluntario(Guid empresaId, string nombre, string apellidos, string dni)
    {
        var t = Trabajador.DeEmpresa(empresaId, nombre, apellidos, dni);
        t.CambiarTipoContratacion(TipoContratacion.Voluntario);
        return t;
    }

    private static async Task<IReadOnlyList<TrabajadorListaDto>> Listar(CaeManagerDbContext c, bool? inactivo)
    {
        var handler = new ObtenerTrabajadoresQueryHandler(c, c, c, c, new AlcanceDatosServiceFalso(), new CalculoEstadoDocumentalService(c, c, c), c, c);
        return (await handler.Handle(new ObtenerTrabajadoresQuery(null, TamanoPagina: 50, VoluntarioInactivo: inactivo), CancellationToken.None)).Elementos;
    }

    private static Task<IReadOnlyList<AlertaDto>> Alertas(CaeManagerDbContext c) =>
        new ObtenerAlertasQueryHandler(c, c, c, c, c, c, c, new ResolverClientePrincipalService(c, c, c),
                new AlcanceDatosServiceFalso(), new DocumentosFaltantesService(c, c, c), c, c)
            .Handle(new ObtenerAlertasQuery(), CancellationToken.None);

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
