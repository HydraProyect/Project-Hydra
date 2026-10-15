using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.SituacionEnCentro;
using CaeManager.Application.Trabajadores.Queries.ObtenerDocumentacionPorCentroDeTrabajador;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Centros;

/// <summary>
/// Coherencia del «Pendiente en la plataforma» (decisión del propietario del 2026-10-10) entre el porcentaje del Centro, el
/// estado del Centro, el bloqueo de personas y la vista por Trabajador, leyendo <b>como <c>cae_app_runtime</c></b> bajo RLS
/// (mismo arnés que <see cref="PendienteEnPlataformaBajoRlsTests"/>). Cada test siembra el mínimo propio: un Centro con
/// acceso de plataforma y los Trabajadores y documentos que su caso necesita.
///
/// <list type="bullet">
/// <item>A: un pendiente de un tipo que el Centro NO marca «bloquea el acceso» no baja el porcentaje (el anillo cuenta lo
/// obligatorio); el de un tipo bloqueante sí.</item>
/// <item>B: el pendiente es el del documento efectivo del par, el mismo que mide el porcentaje: un duplicado pendiente de
/// otro documento no bloquea a quien el porcentaje da al día.</item>
/// <item>C: el documento cuenta como vigente según el requisito EN ESE Centro (periodicidad y tolerancia del Centro), no
/// según la fecha de vencimiento del documento, en los dos sentidos.</item>
/// <item>D: la vista por Trabajador del Centro mide igual que el porcentaje.</item>
/// <item>J: el sujeto Empresa también es una Subcontrata (Trabajador de subcontrata).</item>
/// </list>
/// </summary>
public class PendienteEnPlataformaCoherenciaBajoRlsTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private CaeManagerDbContext _propietario = null!;
    private CaeManagerDbContext _runtime = null!;
    private Guid _tenantId;
    private Guid _clienteId;
    private Guid _empresaId;
    private Guid _centroId;
    private Guid _canalId;
    private DateOnly _hoy;

    public async Task InitializeAsync()
    {
        var tenantPorAmbito = new TenantActualPorAmbito();
        var opcionesPropietario = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantPorAmbito))
            .Options;
        _propietario = new CaeManagerDbContext(opcionesPropietario, new EphemeralDataProtectionProvider(), tenantPorAmbito);
        await _propietario.Database.MigrateAsync();

        var tenant = new Tenant("Tenant de la coherencia");
        _propietario.Tenants.Add(tenant);
        await _propietario.SaveChangesAsync();
        _tenantId = tenant.Id;
        _hoy = DiaDeNegocio.Hoy();

        using (AmbitoTenantExplicito.Establecer(_tenantId))
        {
            var cliente = Empresa.CrearComoCliente("Cervezas Duff Ibérica", "B12345674", false, null, null);
            var empresa = new Empresa("Montajes Springfield S.L.", "B87654323");
            _propietario.Empresas.AddRange(cliente, empresa);
            _propietario.ParametrosSistema.Add(new ParametroSistema(umbralAmbarDias: 30, umbralRojoDias: 15));
            var proveedor = new ProveedorPlataformaCae($"PLAT-{Guid.NewGuid():N}"[..14], "Plataforma de prueba");
            _propietario.ProveedoresPlataformaCae.Add(proveedor);
            await _propietario.SaveChangesAsync();

            var centro = new Centro(cliente.Id, empresa.Id, "Fábrica de Springfield");
            _propietario.Centros.Add(centro);
            await _propietario.SaveChangesAsync();
            var canal = CanalGestionDocumental.DePlataforma(centro.Id, "Acceso de prueba", proveedor.Id, null, null, null);
            _propietario.CanalesGestionDocumental.Add(canal);
            await _propietario.SaveChangesAsync();
            _clienteId = cliente.Id;
            _empresaId = empresa.Id;
            _centroId = centro.Id;
            _canalId = canal.Id;
        }

        var tenantDeLaPeticion = new TenantActualDeLaPeticion(_tenantId);
        var usuario = new CurrentUserServiceFalso(Guid.NewGuid(), tenantOrigenId: _tenantId);
        var opcionesRuntime = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion))
            .AddInterceptors(
                new TenantSelladoInterceptor(tenantDeLaPeticion),
                new TenantRlsConnectionInterceptor(tenantDeLaPeticion, new SinClienteActivo(), usuario, BaseDatosPostgresDePruebas.FirmanteContextoRls))
            .Options;
        _runtime = new CaeManagerDbContext(opcionesRuntime, new EphemeralDataProtectionProvider(), tenantDeLaPeticion);
    }

    public async Task DisposeAsync()
    {
        await _runtime.DisposeAsync();
        await _propietario.DisposeAsync();
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    private CalculoEstadoCentroService Calculo() => new(_runtime, _runtime, _runtime, _runtime, _runtime, _runtime, _runtime);

    private EvaluacionDeAccesoPorCentroService Evaluacion() =>
        new(_runtime, _runtime, _runtime, _runtime, _runtime, new AlcanceDatosServiceFalso());

    // ---- Siembra mínima (como propietario de la base: no es lo que se mide) ----

    private async Task<Guid> TipoAsync(string nombre, AmbitoAplicacion ambito, RequisitoDocumental requerido, bool bloquea, int? periodicidadMeses = null)
    {
        using var ambitoTenant = AmbitoTenantExplicito.Establecer(_tenantId);
        var tipo = new TipoDocumento(nombre, null, aplicaVencimientoAutomatico: false, 1, ambito, requerido: requerido);
        _propietario.TiposDocumento.Add(tipo);
        await _propietario.SaveChangesAsync();
        _propietario.TiposDocumentoCentros.Add(new TipoDocumentoCentro(tipo.Id, _centroId, incluido: true, periodicidadMeses, bloqueaAcceso: bloquea));
        await _propietario.SaveChangesAsync();
        return tipo.Id;
    }

    private async Task<Guid> TrabajadorAsignadoAsync(string nombre, string dni, Guid? subcontrataId = null)
    {
        using var ambitoTenant = AmbitoTenantExplicito.Establecer(_tenantId);
        var trabajador = subcontrataId is { } s
            ? Trabajador.DeSubcontrata(s, nombre, "Prueba", dni)
            : Trabajador.DeEmpresa(_empresaId, nombre, "Prueba", dni);
        _propietario.Trabajadores.Add(trabajador);
        await _propietario.SaveChangesAsync();
        _propietario.Asignaciones.Add(new Asignacion(trabajador.Id, _centroId, _hoy));
        await _propietario.SaveChangesAsync();
        return trabajador.Id;
    }

    private async Task<Guid> DocumentoDeTrabajadorAsync(Guid trabajadorId, Guid tipoId, DateOnly emision, DateOnly? vencimiento) =>
        await GuardarAsync(Documento.DeTrabajador(trabajadorId, tipoId, emision, vencimiento is { } v ? VigenciaDocumento.VenceEl(v) : VigenciaDocumento.NoCaduca));

    private async Task<Guid> DocumentoDeEmpresaAsync(Guid empresaId, Guid tipoId, DateOnly emision, DateOnly vencimiento) =>
        await GuardarAsync(Documento.DeEmpresa(empresaId, tipoId, emision, VigenciaDocumento.VenceEl(vencimiento)));

    private async Task<Guid> GuardarAsync(Documento documento)
    {
        using var ambitoTenant = AmbitoTenantExplicito.Establecer(_tenantId);
        _propietario.Documentos.Add(documento);
        await _propietario.SaveChangesAsync();
        return documento.Id;
    }

    private enum Acreditacion { SinSubir, SubidaSinValidar, Validada }

    private async Task AcreditarAsync(Guid documentoId, Acreditacion estado)
    {
        using var ambitoTenant = AmbitoTenantExplicito.Establecer(_tenantId);
        var acreditacion = new AcreditacionDocumentoPlataforma(documentoId, _canalId);
        if (estado != Acreditacion.SinSubir) acreditacion.MarcarSubida();
        if (estado == Acreditacion.Validada) acreditacion.MarcarAceptada(VigenciaEnPlataforma.NoVenceAqui);
        _propietario.AcreditacionesDocumentoPlataforma.Add(acreditacion);
        await _propietario.SaveChangesAsync();
    }

    // ---- A ----

    [Fact]
    public async Task A_un_pendiente_de_un_tipo_sin_marca_de_bloqueo_no_baja_el_porcentaje_y_el_de_un_tipo_bloqueante_si()
    {
        var formacion = await TipoAsync("Formación 60h", AmbitoAplicacion.Trabajador, RequisitoDocumental.Si, bloquea: true);
        var iso = await TipoAsync("ISO personal", AmbitoAplicacion.Trabajador, RequisitoDocumental.No, bloquea: false);
        var ana = await TrabajadorAsignadoAsync("Ana", "77189989B");
        var beto = await TrabajadorAsignadoAsync("Beto", "12345678Z");
        foreach (var (trabajador, estadoFormacion, estadoIso) in new[]
                 {
                     (ana, Acreditacion.Validada, Acreditacion.SinSubir),   // la ISO opcional de Ana, sin subir
                     (beto, Acreditacion.SinSubir, Acreditacion.Validada)   // la Formación bloqueante de Beto, sin subir
                 })
        {
            await AcreditarAsync(await DocumentoDeTrabajadorAsync(trabajador, formacion, _hoy.AddDays(-5), _hoy.AddYears(1)), estadoFormacion);
            await AcreditarAsync(await DocumentoDeTrabajadorAsync(trabajador, iso, _hoy.AddDays(-5), _hoy.AddYears(1)), estadoIso);
        }

        var cumplimiento = (await Calculo().CalcularCumplimientoAsync([_centroId], CancellationToken.None))[_centroId];

        cumplimiento.Requeridos.Should().Be(4, "dos Trabajadores por dos tipos incluidos en el Centro");
        cumplimiento.AlDia.Should().Be(3,
            "solo la Formación bloqueante de Beto está pendiente; la ISO opcional de Ana, pendiente, no baja el porcentaje");

        var estado = (await Calculo().CalcularAsync([_centroId], CancellationToken.None))[_centroId];
        estado.Estado.Should().Be(EstadoCentro.Pendiente, "el Centro sigue Pendiente tenga o no marca de bloqueo el tipo");
        estado.Causas.Where(c => c.PendienteEnPlataforma).Should().HaveCount(2, "las dos acreditaciones sin subir");
        estado.Causas.Should().ContainSingle(c => c.PendienteEnPlataforma && c.TrabajadorId == beto).Which.BloqueaAcceso
            .Should().BeTrue("la Formación de Beto es un tipo que el Centro marca «bloquea el acceso»");
        estado.Causas.Should().ContainSingle(c => c.PendienteEnPlataforma && c.TrabajadorId == ana).Which.BloqueaAcceso
            .Should().BeFalse("la ISO de Ana no la marca");
    }

    // ---- B ----

    [Fact]
    public async Task B_un_duplicado_pendiente_no_bloquea_a_quien_el_porcentaje_da_al_dia_por_su_documento_efectivo()
    {
        var formacion = await TipoAsync("Formación 60h", AmbitoAplicacion.Trabajador, RequisitoDocumental.Si, bloquea: true);
        var ana = await TrabajadorAsignadoAsync("Ana", "77189989B");
        // El efectivo es el de emisión más reciente (DocumentoEfectivo): A, validado. B es un duplicado más antiguo, sin subir.
        var documentoA = await DocumentoDeTrabajadorAsync(ana, formacion, _hoy.AddDays(-5), _hoy.AddYears(1));
        var documentoB = await DocumentoDeTrabajadorAsync(ana, formacion, _hoy.AddDays(-40), _hoy.AddYears(1));
        await AcreditarAsync(documentoA, Acreditacion.Validada);
        await AcreditarAsync(documentoB, Acreditacion.SinSubir);

        var cumplimiento = (await Calculo().CalcularCumplimientoAsync([_centroId], CancellationToken.None))[_centroId];
        cumplimiento.Should().Be(new FraccionCumplimiento(1, 1), "control: el porcentaje mide el documento efectivo, que está validado");

        var evaluacion = await Evaluacion().EvaluarAsync(null, CancellationToken.None);
        evaluacion.Requisitos.Where(r => ReglaBloqueoDeAcceso.Bloquea(r.Resultado.Situacion)).Should().BeEmpty(
            "el pendiente es el del documento efectivo del par: Ana no está bloqueada por un duplicado");
        evaluacion.Requisitos.Should().ContainSingle(r => r.TrabajadorId == ana, "control positivo: el requisito de Ana sí se evalúa");

        var estado = (await Calculo().CalcularAsync([_centroId], CancellationToken.None))[_centroId];
        estado.Causas.Where(c => c.PendienteEnPlataforma).Should().BeEmpty("tampoco pone el Centro Pendiente");
        estado.Estado.Should().Be(EstadoCentro.Vigente);
    }

    // ---- C ----

    [Fact]
    public async Task C_si_el_documento_vence_en_ese_Centro_por_su_periodicidad_el_pendiente_no_cuenta()
    {
        // Periodicidad especial de 6 meses en este Centro: el documento, emitido hace ~10 meses y vigente a nivel documento
        // (vence dentro de 60 días), ya venció aquí hace ~4 meses.
        var formacion = await TipoAsync("Formación 60h", AmbitoAplicacion.Trabajador, RequisitoDocumental.Si, bloquea: true, periodicidadMeses: 6);
        var ana = await TrabajadorAsignadoAsync("Ana", "77189989B");
        var documento = await DocumentoDeTrabajadorAsync(ana, formacion, _hoy.AddMonths(-10), _hoy.AddDays(60));
        await AcreditarAsync(documento, Acreditacion.SinSubir);

        var estado = (await Calculo().CalcularAsync([_centroId], CancellationToken.None))[_centroId];
        estado.Causas.Where(c => c.PendienteEnPlataforma).Should().BeEmpty(
            "vencido en este Centro es causa más grave que estar sin subir: no se cuenta como Pendiente");

        var cumplimiento = (await Calculo().CalcularCumplimientoAsync([_centroId], CancellationToken.None))[_centroId];
        cumplimiento.Requeridos.Should().Be(1);

        var evaluacion = await Evaluacion().EvaluarAsync(null, CancellationToken.None);
        evaluacion.Requisitos.Should().ContainSingle().Which.Resultado.Situacion.Should().Be(
            SituacionDeRequisitoBloqueante.Vencido, "lo que dice la regla de vencido, no la de pendiente");
    }

    [Fact]
    public async Task C_si_el_documento_aun_vale_en_ese_Centro_por_su_periodicidad_el_pendiente_cuenta_aunque_el_documento_este_vencido()
    {
        // Periodicidad especial de 24 meses: emitido hace 14 meses y vencido a nivel documento (venció hace 30 días), pero aún
        // vale 10 meses más en este Centro.
        var formacion = await TipoAsync("Formación 60h", AmbitoAplicacion.Trabajador, RequisitoDocumental.Si, bloquea: true, periodicidadMeses: 24);
        var ana = await TrabajadorAsignadoAsync("Ana", "77189989B");
        var documento = await DocumentoDeTrabajadorAsync(ana, formacion, _hoy.AddMonths(-14), _hoy.AddDays(-30));
        await AcreditarAsync(documento, Acreditacion.SinSubir);

        var estado = (await Calculo().CalcularAsync([_centroId], CancellationToken.None))[_centroId];
        estado.Causas.Should().Contain(c => c.PendienteEnPlataforma && c.TrabajadorId == ana,
            "el documento vale en este Centro y está sin subir a su plataforma");

        var evaluacion = await Evaluacion().EvaluarAsync(null, CancellationToken.None);
        evaluacion.Requisitos.Should().ContainSingle().Which.Resultado.Situacion.Should().Be(
            SituacionDeRequisitoBloqueante.PendienteDeSubirAPlataforma);
    }

    // ---- D ----

    [Fact]
    public async Task D_la_vista_por_Trabajador_mide_el_pendiente_igual_que_el_porcentaje()
    {
        var formacion = await TipoAsync("Formación 60h", AmbitoAplicacion.Trabajador, RequisitoDocumental.Si, bloquea: true);
        var iso = await TipoAsync("ISO personal", AmbitoAplicacion.Trabajador, RequisitoDocumental.No, bloquea: false);
        var ana = await TrabajadorAsignadoAsync("Ana", "77189989B");
        var beto = await TrabajadorAsignadoAsync("Beto", "12345678Z");
        await AcreditarAsync(await DocumentoDeTrabajadorAsync(ana, formacion, _hoy.AddDays(-5), _hoy.AddYears(1)), Acreditacion.SinSubir);
        await AcreditarAsync(await DocumentoDeTrabajadorAsync(ana, iso, _hoy.AddDays(-5), _hoy.AddYears(1)), Acreditacion.Validada);
        await AcreditarAsync(await DocumentoDeTrabajadorAsync(beto, formacion, _hoy.AddDays(-5), _hoy.AddYears(1)), Acreditacion.Validada);
        await AcreditarAsync(await DocumentoDeTrabajadorAsync(beto, iso, _hoy.AddDays(-5), _hoy.AddYears(1)), Acreditacion.SinSubir);

        var alcance = new AlcanceDatosServiceFalso();
        var c = _runtime;
        var handler = new ObtenerAsignacionesDocumentacionPorCentroQueryHandler(
            c, c, c, c, c, c, alcance,
            new SituacionDocumentosEnCentrosService(c, c, c, c, c, alcance, new CurrentUserServiceMutable { Rol = "Administrador" }));

        var trabajadores = await handler.Handle(new ObtenerAsignacionesDocumentacionPorCentroQuery(_centroId), CancellationToken.None);

        trabajadores.Single(t => t.TrabajadorId == ana).Cumplimiento.Should().Be(new FraccionCumplimiento(1, 2),
            "la Formación bloqueante de Ana está sin subir: no está al día en este Centro");
        trabajadores.Single(t => t.TrabajadorId == beto).Cumplimiento.Should().Be(new FraccionCumplimiento(2, 2),
            "la ISO opcional de Beto, sin subir, no cuenta (el anillo mide lo obligatorio)");

        var porcentajeDelCentro = (await Calculo().CalcularCumplimientoAsync([_centroId], CancellationToken.None))[_centroId];
        FraccionCumplimiento.Sumar(trabajadores.Select(t => t.Cumplimiento)).Should().Be(porcentajeDelCentro,
            "la suma de las fracciones de los Trabajadores es la del Centro");
    }

    // ---- Ficha de Trabajador (ronda 3, hallazgo 1) ----

    [Fact]
    public async Task E_la_ficha_del_Trabajador_cuenta_en_su_Centro_el_mismo_pendiente_que_el_porcentaje_y_el_bloqueo()
    {
        var formacion = await TipoAsync("Formación 60h", AmbitoAplicacion.Trabajador, RequisitoDocumental.Si, bloquea: true);
        var iso = await TipoAsync("ISO personal", AmbitoAplicacion.Trabajador, RequisitoDocumental.No, bloquea: false);
        var ana = await TrabajadorAsignadoAsync("Ana", "77189989B");
        var beto = await TrabajadorAsignadoAsync("Beto", "12345678Z");
        await AcreditarAsync(await DocumentoDeTrabajadorAsync(ana, formacion, _hoy.AddDays(-5), _hoy.AddYears(1)), Acreditacion.SinSubir);
        await AcreditarAsync(await DocumentoDeTrabajadorAsync(ana, iso, _hoy.AddDays(-5), _hoy.AddYears(1)), Acreditacion.Validada);
        await AcreditarAsync(await DocumentoDeTrabajadorAsync(beto, formacion, _hoy.AddDays(-5), _hoy.AddYears(1)), Acreditacion.Validada);
        await AcreditarAsync(await DocumentoDeTrabajadorAsync(beto, iso, _hoy.AddDays(-5), _hoy.AddYears(1)), Acreditacion.SinSubir);

        var alcance = new AlcanceDatosServiceFalso();
        var c = _runtime;
        var handler = new ObtenerDocumentacionPorCentroDeTrabajadorQueryHandler(
            c, c, c, c, c, c, c, alcance,
            new SituacionDocumentosEnCentrosService(c, c, c, c, c, alcance, new CurrentUserServiceMutable { Rol = "Administrador" }));

        var deAna = (await handler.Handle(new ObtenerDocumentacionPorCentroDeTrabajadorQuery(ana), CancellationToken.None)).Single();
        var deBeto = (await handler.Handle(new ObtenerDocumentacionPorCentroDeTrabajadorQuery(beto), CancellationToken.None)).Single();

        deAna.CumplimientoEnElCentro.Should().Be(new FraccionCumplimiento(1, 2),
            "la Formación bloqueante de Ana está sin subir: no está al día en este Centro");
        deAna.Cumplimiento.Should().Be(new FraccionCumplimiento(2, 2),
            "control: el % del Trabajador mide su documentación y no cuenta la acreditación en la plataforma");
        deBeto.CumplimientoEnElCentro.Should().Be(new FraccionCumplimiento(2, 2),
            "la ISO opcional de Beto, sin subir, no cuenta (el anillo mide lo obligatorio)");

        var porcentajeDelCentro = (await Calculo().CalcularCumplimientoAsync([_centroId], CancellationToken.None))[_centroId];
        FraccionCumplimiento.Sumar([deAna.CumplimientoEnElCentro, deBeto.CumplimientoEnElCentro]).Should().Be(porcentajeDelCentro,
            "la suma de lo que aporta cada Trabajador es el % del Centro");

        var evaluacion = await Evaluacion().EvaluarAsync(null, CancellationToken.None);
        evaluacion.Requisitos.Where(r => ReglaBloqueoDeAcceso.Bloquea(r.Resultado.Situacion)).Select(r => r.TrabajadorId)
            .Should().BeEquivalentTo([ana], "el bloqueo coincide con el único Trabajador que la ficha da por no al día por un Pendiente");
    }

    // ---- J ----

    [Fact]
    public async Task J_un_pendiente_de_una_Subcontrata_bloquea_a_su_Trabajador_y_pone_el_Centro_Pendiente()
    {
        // El sujeto del requisito de Empresa es la Empresa del Trabajador, sea su empresa o su subcontrata.
        var subcontrata = Empresa.CrearComoSubcontrata("Montajes Shelbyville S.L.", null, "Estandar");
        using (AmbitoTenantExplicito.Establecer(_tenantId))
        {
            _propietario.Empresas.Add(subcontrata);
            await _propietario.SaveChangesAsync();
        }

        var rnt = await TipoAsync("RNT", AmbitoAplicacion.Empresa, RequisitoDocumental.Si, bloquea: true);
        var ana = await TrabajadorAsignadoAsync("Ana", "77189989B", subcontrata.Id);
        var otro = await TrabajadorAsignadoAsync("Otro", "12345678Z");
        var documento = await DocumentoDeEmpresaAsync(subcontrata.Id, rnt, _hoy.AddDays(-5), _hoy.AddYears(1));
        await AcreditarAsync(documento, Acreditacion.SubidaSinValidar);

        var evaluacion = await Evaluacion().EvaluarAsync(null, CancellationToken.None);
        var bloqueos = evaluacion.Requisitos.Where(r => ReglaBloqueoDeAcceso.Bloquea(r.Resultado.Situacion)).ToList();
        bloqueos.Should().ContainSingle(b => b.TrabajadorId == ana, "el RNT de la subcontrata de Ana está subido sin validar")
            .Which.Should().Match<RequisitoEvaluado>(b =>
                b.Ambito == AmbitoAplicacion.Empresa && b.EmpresaId == subcontrata.Id
                && b.Resultado.Situacion == SituacionDeRequisitoBloqueante.SinValidarEnPlataforma);
        bloqueos.Should().ContainSingle(b => b.TrabajadorId == otro).Which.Resultado.Situacion.Should().Be(
            SituacionDeRequisitoBloqueante.Ausente,
            "el otro Trabajador es de otra Empresa, sin RNT: bloquea por ausente, nunca por el pendiente de la subcontrata");

        var estado = (await Calculo().CalcularAsync([_centroId], CancellationToken.None))[_centroId];
        estado.Causas.Should().ContainSingle(c => c.PendienteEnPlataforma && c.Ambito == AmbitoCausa.Empresa).Which.Descripcion
            .Should().Be("RNT — Montajes Shelbyville S.L. — subido, sin validar en la plataforma", "la causa nombra la Empresa, no un «Empresa» genérico");

        // Y el mismo Pendiente baja el porcentaje del Centro (decisión del 2026-10-10): dos Empresas con Trabajadores allí
        // × el RNT que exige el Centro, ninguna al día (la de Ana, pendiente; la de Otro, sin documento).
        var cumplimiento = (await Calculo().CalcularCumplimientoAsync([_centroId], CancellationToken.None))[_centroId];
        cumplimiento.Should().Be(new FraccionCumplimiento(0, 2));
    }

    // ---- K ----

    [Fact]
    public async Task K_los_pares_de_Empresa_entran_en_el_porcentaje_del_Centro_y_el_pendiente_solo_si_el_tipo_bloquea()
    {
        var formacion = await TipoAsync("Formación 60h", AmbitoAplicacion.Trabajador, RequisitoDocumental.Si, bloquea: true);
        var rnt = await TipoAsync("RNT", AmbitoAplicacion.Empresa, RequisitoDocumental.Si, bloquea: true);
        var sello = await TipoAsync("Sello de calidad", AmbitoAplicacion.Empresa, RequisitoDocumental.Si, bloquea: false);
        Guid noExigido;
        using (AmbitoTenantExplicito.Establecer(_tenantId))
        {
            // Sin fila del Centro y no requerido por defecto: el Centro no lo exige, no entra en el universo.
            var tipo = new TipoDocumento("Memoria anual", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Empresa, requerido: RequisitoDocumental.No);
            _propietario.TiposDocumento.Add(tipo);
            await _propietario.SaveChangesAsync();
            noExigido = tipo.Id;
        }

        var ana = await TrabajadorAsignadoAsync("Ana", "77189989B");
        await AcreditarAsync(await DocumentoDeTrabajadorAsync(ana, formacion, _hoy.AddDays(-5), _hoy.AddYears(1)), Acreditacion.Validada);

        // Sin documentos de Empresa, faltan: Formación de Ana (al día) + RNT y Sello (Faltante) = 1 de 3. «Memoria anual» no entra.
        (await Calculo().CalcularCumplimientoAsync([_centroId], CancellationToken.None))[_centroId]
            .Should().Be(new FraccionCumplimiento(1, 3), "el universo suma los pares de Empresa exigidos por el Centro");

        var documentoRnt = await DocumentoDeEmpresaAsync(_empresaId, rnt, _hoy.AddDays(-5), _hoy.AddYears(1));
        var documentoSello = await DocumentoDeEmpresaAsync(_empresaId, sello, _hoy.AddDays(-5), _hoy.AddYears(1));
        await DocumentoDeEmpresaAsync(_empresaId, noExigido, _hoy.AddDays(-5), _hoy.AddDays(-1));
        (await Calculo().CalcularCumplimientoAsync([_centroId], CancellationToken.None))[_centroId]
            .Should().Be(new FraccionCumplimiento(3, 3), "con los dos al día todo cuenta, y un vencido de un tipo que el Centro no exige no baja nada");

        // Pendiente en la plataforma: el RNT, que el Centro marca bloqueante, baja el porcentaje; el Sello, no.
        await AcreditarAsync(documentoRnt, Acreditacion.SinSubir);
        await AcreditarAsync(documentoSello, Acreditacion.SubidaSinValidar);
        (await Calculo().CalcularCumplimientoAsync([_centroId], CancellationToken.None))[_centroId]
            .Should().Be(new FraccionCumplimiento(2, 3), "solo el pendiente de un tipo bloqueante baja el porcentaje");

        // Los pares de Trabajador (de los que salen los demás contextos) no cambian.
        (await Calculo().ObtenerParesExigidosAsync([_centroId], CancellationToken.None))
            .Should().ContainSingle(p => p.TrabajadorId == ana, "los porcentajes de Trabajador, Empresa y Cliente empresarial no reciben los pares de Empresa");
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    /// <summary>Como el <c>TenantActual</c> real: primero el ámbito explícito, luego el Tenant de la sesión.</summary>
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
