using CaeManager.Application.Alertas;
using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Subcontratas;
using CaeManager.Application.Subcontratas.Queries.ObtenerTrabajadoresDocumentacionPorSubcontrata;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Subcontratas;
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
    private Guid _tenantSesion, _tenantAjeno, _estadio, _dni, _seguro, _eventoVigente, _eventoProximo;
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

            (_estadio, _dni, _seguro, _eventoVigente, _eventoProximo) = (estadio.Id, dni.Id, seguro.Id, vigente.Id, proximo.Id);
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
        alertas.Should().Contain(a => a.TrabajadorId == _futuro && a.TipoDocumentoId == _dni && a.Estado == EstadoDocumento.Faltante
            && a.CentroId == _estadio, "el evento futuro ya aporta sus requisitos desde la asignación (opción B, 2026-10-16)");
        alertas.Should().NotContain(a => a.TrabajadorId == _ajena && a.TipoDocumentoId == _dni,
            "los requisitos del evento solo cuentan para quien está asignado a él");
    }

    [Fact]
    public async Task El_voluntario_inactivo_no_cuenta_en_el_estado_del_Centro()
    {
        await using var c = CrearRuntime(_tenantSesion);

        var estado = (await new CalculoEstadoCentroService(c, c, c, c, c, c, c, c, c).CalcularAsync([_estadio], CancellationToken.None))[_estadio];

        estado.Causas.Should().Contain(x => x.TrabajadorId == _ajena, "control positivo");
        estado.Causas.Should().NotContain(x => x.TrabajadorId == _inactivo);
    }

    [Fact]
    public async Task El_requisito_del_evento_vigente_cuenta_en_el_estado_y_el_cumplimiento_del_Centro_del_evento()
    {
        Guid sinAsignacion;
        using (AmbitoTenantExplicito.Establecer(_tenantSesion))
        {
            var club = await _propietario.Empresas.Select(e => e.Id).SingleAsync();
            var voluntario = Voluntario(club, "Nerea", "Lago Prieto", "33333333P");
            _propietario.Trabajadores.Add(voluntario);
            await _propietario.SaveChangesAsync();
            var hoy = DiaDeNegocio.Hoy();
            _propietario.ProyectosTecnicos.Add(new ProyectoTecnico(_eventoVigente, voluntario.Id, hoy.AddDays(-1)));
            _propietario.Documentos.Add(Documento.DeTrabajador(voluntario.Id, _dni, fechaEmision: hoy.AddDays(-10),
                vigencia: VigenciaDocumento.VenceEl(hoy.AddDays(365))));
            await _propietario.SaveChangesAsync();
            sinAsignacion = voluntario.Id;
        }
        await using var c = CrearRuntime(_tenantSesion);
        var servicio = new CalculoEstadoCentroService(c, c, c, c, c, c, c, c, c);

        var estado = (await servicio.CalcularAsync([_estadio], CancellationToken.None))[_estadio];
        estado.Causas.Should().ContainSingle(x => x.TrabajadorId == _vigente && x.TipoDocumentoId == _dni && x.Estado == EstadoDocumento.Faltante,
            "el DNI que pide el evento vigente le falta a su voluntario y cuenta en el estado del Centro del evento");
        estado.Causas.Should().ContainSingle(x => x.TrabajadorId == _futuro && x.TipoDocumentoId == _dni && x.Estado == EstadoDocumento.Faltante,
            "el evento futuro ya aporta sus requisitos desde la asignación (opción B)");
        estado.Causas.Should().NotContain(x => x.TipoDocumentoId == _dni && x.TrabajadorId == _ajena,
            "los del evento solo cuentan para quien está asignado a él");
        estado.Causas.Should().NotContain(x => x.TrabajadorId == sinAsignacion, "su DNI está vigente");

        var pares = await servicio.ObtenerParesExigidosAsync([_estadio], CancellationToken.None);
        pares.Should().ContainSingle(p => p.TrabajadorId == sinAsignacion && p.TipoDocumentoId == _dni)
            .Which.Estado.Should().Be(EstadoDocumento.Vigente, "el voluntario del evento cuenta en su Centro aunque no tenga Asignación");
        pares.Should().ContainSingle(p => p.TrabajadorId == _vigente && p.TipoDocumentoId == _dni)
            .Which.Estado.Should().Be(EstadoDocumento.Faltante);

        var cumplimiento = (await servicio.CalcularCumplimientoAsync([_estadio], CancellationToken.None))[_estadio];
        cumplimiento.Requeridos.Should().Be(3,
            "el Centro no incluye ningún tipo, así que solo cuentan los requisitos de los eventos: Iker sin DNI y Nerea con él en el vigente, Raúl sin DNI en el futuro");
        cumplimiento.AlDia.Should().Be(1);
    }

    /// <summary>
    /// Decisión 2026-10-16: los requisitos del evento cuentan para todos los asignados, no solo para los voluntarios. Un
    /// Trabajador de cuenta ajena de una Subcontrata asignado al evento vigente, sin Asignación al Centro, debe su DNI en los
    /// avisos, en el estado del Centro del evento y en el estado de su Subcontrata.
    /// </summary>
    [Fact]
    public async Task El_requisito_del_evento_cuenta_para_el_Trabajador_de_una_Subcontrata_asignado_a_el()
    {
        Guid subcontrataId, deSubcontrata, voluntarioInactivoDeSubcontrata;
        using (AmbitoTenantExplicito.Establecer(_tenantSesion))
        {
            var subcontrata = Empresa.CrearComoSubcontrata("Montajes Gradas S.L.", null, NivelServicioSubcontrata.Gestionada.ToString());
            _propietario.Empresas.Add(subcontrata);
            await _propietario.SaveChangesAsync();
            var operario = Trabajador.DeSubcontrata(subcontrata.Id, "Pablo", "Ferrer Soto", "44444444A");
            var inactivo = Trabajador.DeSubcontrata(subcontrata.Id, "Eva", "Rey Calvo", "55555555K");
            inactivo.CambiarTipoContratacion(TipoContratacion.Voluntario);
            _propietario.Trabajadores.AddRange(operario, inactivo);
            await _propietario.SaveChangesAsync();
            var hoy = DiaDeNegocio.Hoy();
            _propietario.ProyectosTecnicos.Add(new ProyectoTecnico(_eventoVigente, operario.Id, hoy.AddDays(-1)));
            // Voluntario inactivo de la Subcontrata con un vencido en un Centro que exige el seguro: no debe contar.
            _propietario.Asignaciones.Add(new Asignacion(inactivo.Id, _estadio, hoy.AddDays(-30)));
            _propietario.Documentos.Add(Documento.DeTrabajador(inactivo.Id, _seguro, fechaEmision: hoy.AddDays(-400),
                vigencia: VigenciaDocumento.VenceEl(hoy.AddDays(-5))));
            await _propietario.SaveChangesAsync();
            (subcontrataId, deSubcontrata, voluntarioInactivoDeSubcontrata) = (subcontrata.Id, operario.Id, inactivo.Id);
        }
        await using var c = CrearRuntime(_tenantSesion);

        (await Alertas(c)).Should().Contain(a => a.TrabajadorId == deSubcontrata && a.TipoDocumentoId == _dni
            && a.Estado == EstadoDocumento.Faltante && a.CentroId == _estadio, "cuenta ajena de Subcontrata, asignado al evento");

        var centro = (await new CalculoEstadoCentroService(c, c, c, c, c, c, c, c, c).CalcularAsync([_estadio], CancellationToken.None))[_estadio];
        centro.Causas.Should().ContainSingle(x => x.TrabajadorId == deSubcontrata && x.TipoDocumentoId == _dni && x.Estado == EstadoDocumento.Faltante);

        var subcontratas = new CalculoEstadoSubcontrataService(c, c, c, c, c, c, new AlcanceDatosServiceFalso(), c, c);
        var resumen = (await subcontratas.CalcularResumenAsync([subcontrataId], CancellationToken.None))[subcontrataId];
        resumen.Incidencias.Should().ContainSingle(i => i.TrabajadorId == deSubcontrata && i.TipoDocumentoId == _dni && i.Estado == EstadoDocumento.Faltante,
            "el requisito del evento cuenta en la Subcontrata aunque el Trabajador no tenga Asignación al Centro del evento");
        resumen.Incidencias.Should().NotContain(i => i.TrabajadorId == voluntarioInactivoDeSubcontrata,
            "el voluntario inactivo de la Subcontrata no cuenta, igual que en el Centro y en los avisos");
        resumen.Fraccion.Requeridos.Should().Be(1, "solo el DNI del evento: el voluntario inactivo no suma requeridos");

        // El acordeón de la Subcontrata mide el mismo universo que su fila: Pablo debe el DNI del evento y Eva sigue
        // listada sin nada exigido.
        var acordeon = await new ObtenerTrabajadoresDocumentacionPorSubcontrataQueryHandler(c, c, c, c, c, c, new AlcanceDatosServiceFalso(), c, c)
            .Handle(new ObtenerTrabajadoresDocumentacionPorSubcontrataQuery(subcontrataId), CancellationToken.None);
        acordeon.Single(t => t.TrabajadorId == deSubcontrata).Cumplimiento.Requeridos.Should().Be(1);
        acordeon.Single(t => t.TrabajadorId == voluntarioInactivoDeSubcontrata).Cumplimiento.Requeridos.Should().Be(0,
            "el voluntario inactivo sigue listado, pero no se le exige nada");
        FraccionCumplimiento.Sumar(acordeon.Select(t => t.Cumplimiento)).Should().Be(resumen.Fraccion);
    }

    [Fact]
    public async Task Una_participacion_dada_de_baja_no_aporta_los_requisitos_del_evento()
    {
        using (AmbitoTenantExplicito.Establecer(_tenantSesion))
        {
            var participacion = await _propietario.ProyectosTecnicos.SingleAsync(p => p.TrabajadorId == _futuro);
            participacion.DarDeBaja(DiaDeNegocio.Hoy());
            await _propietario.SaveChangesAsync();
        }
        await using var c = CrearRuntime(_tenantSesion);

        (await Alertas(c)).Should().NotContain(a => a.TrabajadorId == _futuro && a.TipoDocumentoId == _dni);
        (await Alertas(c)).Should().Contain(a => a.TrabajadorId == _vigente && a.TipoDocumentoId == _dni, "control positivo");
    }

    [Fact]
    public async Task Un_requisito_de_evento_que_no_es_de_ambito_Trabajador_no_cuenta_para_el_voluntario()
    {
        Guid rnt;
        using (AmbitoTenantExplicito.Establecer(_tenantSesion))
        {
            // Sembrado por debajo del comando, que ya lo rechaza: una fila heredada no debe convertirse en un faltante imposible.
            var tipo = new TipoDocumento("RNT del club", null, false, 3, AmbitoAplicacion.Empresa);
            _propietario.TiposDocumento.Add(tipo);
            await _propietario.SaveChangesAsync();
            _propietario.ProyectosRequisitos.Add(new ProyectoRequisito(_eventoVigente, tipo.Id));
            await _propietario.SaveChangesAsync();
            rnt = tipo.Id;
        }
        await using var c = CrearRuntime(_tenantSesion);
        var servicio = new CalculoEstadoCentroService(c, c, c, c, c, c, c, c, c);

        (await Alertas(c)).Should().NotContain(a => a.TipoDocumentoId == rnt, "un documento de Empresa no se le pide a la persona");
        (await servicio.CalcularAsync([_estadio], CancellationToken.None))[_estadio].Causas.Should().NotContain(x => x.TipoDocumentoId == rnt);
        var pares = await servicio.ObtenerParesExigidosAsync([_estadio], CancellationToken.None);
        pares.Should().NotContain(p => p.TipoDocumentoId == rnt);
        pares.Should().Contain(p => p.TrabajadorId == _vigente && p.TipoDocumentoId == _dni, "control positivo: el DNI del evento sí cuenta");
    }

    [Fact]
    public async Task En_un_Centro_sin_gestion_CAE_los_requisitos_del_evento_no_avisan()
    {
        using (AmbitoTenantExplicito.Establecer(_tenantSesion))
        {
            var estadio = await _propietario.Centros.SingleAsync(x => x.Id == _estadio);
            estadio.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);
            await _propietario.SaveChangesAsync();
        }
        await using var c = CrearRuntime(_tenantSesion);

        var alertas = await Alertas(c);

        alertas.Should().NotContain(a => a.TipoDocumentoId == _dni, "P1-X2: un Centro sin gestión CAE no exige nada, tampoco por sus eventos");
        alertas.Should().Contain(a => a.TrabajadorId == _ajena && a.TipoDocumentoId == _seguro && a.Estado == EstadoDocumento.Vencido,
            "control positivo: el vencido del propio documento sigue avisando");
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
        var handler = new ObtenerTrabajadoresQueryHandler(c, c, c, c, new AlcanceDatosServiceFalso(), new CalculoEstadoDocumentalService(c, c, c), c, c, new DocumentosFaltantesService(c, c, c));
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
