using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Trabajadores.Queries.ObtenerDocumentacionPorCentroDeTrabajador;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Coherencia;

/// <summary>
/// «En tolerancia» (aprobado 2026-10-03) es un estado DE CONTEXTO: el Documento sigue Vencido, pero en un Centro cuya tolerancia
/// (la suya, o la de su Cliente empresarial) aún no se agotó se rotula «Vencido · en tolerancia hasta dd/MM». Lo producen las dos
/// vistas con contexto de Centro (Centro 360 y Trabajador 360 por Centro) con la misma resolución y la misma comparación que la
/// regla de acceso; no cambia el porcentaje de cumplimiento (que un vencido en tolerancia cuente como al día es el incremento 2
/// del porcentaje). Cada prueba mide las dos vistas sobre el mismo escenario: una sola regla, dos superficies.
/// </summary>
public class EstadoEnToleranciaPorContextoTests : IAsyncLifetime
{
    private const int DiasVencido = 5;

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _clienteId;
    private Guid _centroId;
    private Guid _trabajadorId;
    private Guid _tipoId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        if (await contexto.ParametrosSistema.SingleOrDefaultAsync() is null)
            contexto.ParametrosSistema.Add(new ParametroSistema(30, 15));

        var cliente = Empresa.CrearComoCliente("Cliente en tolerancia S.L.", "B12345674", false, null, null);
        var empresa = new Empresa("Empresa en tolerancia S.L.", "B87654323");
        contexto.Empresas.AddRange(cliente, empresa);
        var tipo = new TipoDocumento("Formación PRL", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
        contexto.TiposDocumento.Add(tipo);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro en tolerancia");
        contexto.Centros.Add(centro);
        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Rosa", "Tolerada", "12345678Z");
        contexto.Trabajadores.Add(trabajador);
        await contexto.SaveChangesAsync();

        contexto.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, DiaDeNegocio.Hoy().AddYears(-1)));
        var hoy = DiaDeNegocio.Hoy();
        contexto.Documentos.Add(Documento.DeTrabajador(trabajador.Id, tipo.Id, hoy.AddYears(-1), VigenciaDocumento.VenceEl(hoy.AddDays(-DiasVencido))));
        await contexto.SaveChangesAsync();

        _clienteId = cliente.Id;
        _centroId = centro.Id;
        _trabajadorId = trabajador.Id;
        _tipoId = tipo.Id;
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private async Task ConfigurarAsync(int? delCliente = null, int? delCentro = null, int? periodicidadMeses = null)
    {
        await using var contexto = CrearContexto();
        if (delCliente is { } dias)
            contexto.ToleranciasDocumentoClienteEmpresarial.Add(new ToleranciaDocumentoClienteEmpresarial(_clienteId, _tipoId, dias));
        if (delCentro is not null || periodicidadMeses is not null)
            contexto.TiposDocumentoCentros.Add(new TipoDocumentoCentro(
                _tipoId, _centroId, incluido: true, periodicidadEspecialMeses: periodicidadMeses, toleranciaDias: delCentro));
        await contexto.SaveChangesAsync();
    }

    private async Task<(DocumentoRequeridoDto EnCentro360, DocumentoRequeridoDto EnTrabajador360, FraccionCumplimiento Cumplimiento)> MedirAsync()
    {
        await using var contexto = CrearContexto();
        var alcance = new AlcanceDatosServiceFalso();

        var centro360 = await new ObtenerAsignacionesDocumentacionPorCentroQueryHandler(
                contexto, contexto, contexto, contexto, contexto, contexto, alcance, new CaeManager.Application.Documentos.SituacionEnCentro.SituacionDocumentosEnCentrosService(contexto, contexto, contexto, contexto, contexto, alcance, new CurrentUserServiceMutable { Rol = "Administrador" }))
            .Handle(new ObtenerAsignacionesDocumentacionPorCentroQuery(_centroId), CancellationToken.None);
        var trabajador360 = await new ObtenerDocumentacionPorCentroDeTrabajadorQueryHandler(
                contexto, contexto, contexto, contexto, contexto, contexto, alcance, new CaeManager.Application.Documentos.SituacionEnCentro.SituacionDocumentosEnCentrosService(contexto, contexto, contexto, contexto, contexto, alcance, new CurrentUserServiceMutable { Rol = "Administrador" }))
            .Handle(new ObtenerDocumentacionPorCentroDeTrabajadorQuery(_trabajadorId), CancellationToken.None);

        var enCentro = centro360.Should().ContainSingle().Subject;
        var enTrabajador = trabajador360.Should().ContainSingle().Subject;
        enCentro.Cumplimiento.Should().Be(enTrabajador.Cumplimiento, "las dos vistas miden el mismo par exigido");
        return (
            enCentro.Documentos.Should().ContainSingle().Subject,
            enTrabajador.Documentos.Should().ContainSingle().Subject,
            enCentro.Cumplimiento);
    }

    [Fact]
    public async Task Sin_tolerancia_el_vencido_se_ve_vencido_en_las_dos_vistas()
    {
        var (centro360, trabajador360, _) = await MedirAsync();

        centro360.Estado.Should().Be(EstadoDocumento.Vencido);
        trabajador360.Estado.Should().Be(EstadoDocumento.Vencido);
        centro360.EnToleranciaHasta.Should().BeNull();
        trabajador360.EnToleranciaHasta.Should().BeNull();
    }

    [Fact]
    public async Task Con_la_tolerancia_del_Cliente_empresarial_el_vencido_esta_en_tolerancia_hasta_su_ultimo_dia_en_las_dos_vistas()
    {
        await ConfigurarAsync(delCliente: 10);
        var hasta = DiaDeNegocio.Hoy().AddDays(-DiasVencido + 10);

        var (centro360, trabajador360, _) = await MedirAsync();

        foreach (var documento in new[] { centro360, trabajador360 })
        {
            documento.Estado.Should().Be(EstadoDocumento.EnTolerancia);
            documento.EnToleranciaHasta.Should().Be(hasta);
            documento.FechaVencimiento.Should().Be(DiaDeNegocio.Hoy().AddDays(-DiasVencido), "la fecha de vencimiento del Documento no cambia");
        }
    }

    [Fact]
    public async Task La_tolerancia_no_entra_en_el_porcentaje_el_documento_sigue_sin_estar_al_dia()
    {
        var (_, _, sinTolerancia) = await MedirAsync();
        await ConfigurarAsync(delCliente: 10);

        var (centro360, _, conTolerancia) = await MedirAsync();

        centro360.Estado.Should().Be(EstadoDocumento.EnTolerancia, "control: la tolerancia está aplicada");
        conTolerancia.Should().Be(sinTolerancia, "el porcentaje mide el estado de vigencia real; la tolerancia entra en el incremento 2 del porcentaje");
        conTolerancia.AlDia.Should().Be(0);
        conTolerancia.Requeridos.Should().Be(1);
    }

    [Fact]
    public async Task La_tolerancia_del_Centro_manda_sobre_la_del_Cliente_empresarial_en_los_dos_sentidos()
    {
        // El Centro la pone a 0 aunque el Cliente empresarial conceda 10: no hay tolerancia en ese Centro.
        await ConfigurarAsync(delCliente: 10, delCentro: 0);
        var (centroACero, trabajadorACero, _) = await MedirAsync();
        centroACero.Estado.Should().Be(EstadoDocumento.Vencido);
        trabajadorACero.Estado.Should().Be(EstadoDocumento.Vencido);
    }

    [Fact]
    public async Task El_Centro_puede_conceder_tolerancia_aunque_el_Cliente_empresarial_no_la_conceda()
    {
        await ConfigurarAsync(delCentro: 30);

        var (centro360, trabajador360, _) = await MedirAsync();

        centro360.Estado.Should().Be(EstadoDocumento.EnTolerancia);
        trabajador360.Estado.Should().Be(EstadoDocumento.EnTolerancia);
        centro360.EnToleranciaHasta.Should().Be(DiaDeNegocio.Hoy().AddDays(-DiasVencido + 30));
    }

    [Fact]
    public async Task Agotada_la_tolerancia_vuelve_a_verse_vencido()
    {
        // Venció hace 5 días y la tolerancia es de 4: el último día que valió fue ayer.
        await ConfigurarAsync(delCliente: DiasVencido - 1);

        var (centro360, trabajador360, _) = await MedirAsync();

        centro360.Estado.Should().Be(EstadoDocumento.Vencido);
        trabajador360.Estado.Should().Be(EstadoDocumento.Vencido);
        centro360.EnToleranciaHasta.Should().BeNull();
    }

    [Fact]
    public async Task El_ultimo_dia_de_tolerancia_todavia_esta_en_tolerancia()
    {
        // Venció hace 5 días y la tolerancia es de 5: hoy es el último día que vale.
        await ConfigurarAsync(delCliente: DiasVencido);

        var (centro360, _, _) = await MedirAsync();

        centro360.Estado.Should().Be(EstadoDocumento.EnTolerancia);
        centro360.EnToleranciaHasta.Should().Be(DiaDeNegocio.Hoy());
    }

    [Fact]
    public async Task La_tolerancia_se_cuenta_desde_el_vencimiento_efectivo_del_Centro_cuando_impone_su_propia_periodicidad()
    {
        // El documento (emitido hace un año) dice que venció hace 5 días; el Centro impone 11 meses desde la emisión: para
        // ESE Centro venció hace un mes, y la tolerancia de 40 días se cuenta desde ahí.
        var hoy = DiaDeNegocio.Hoy();
        var vencioEnElCentro = hoy.AddYears(-1).AddMonths(11);
        await ConfigurarAsync(delCliente: 40, periodicidadMeses: 11);

        var (centro360, trabajador360, _) = await MedirAsync();

        foreach (var documento in new[] { centro360, trabajador360 })
        {
            documento.Estado.Should().Be(EstadoDocumento.EnTolerancia);
            documento.EnToleranciaHasta.Should().Be(vencioEnElCentro.AddDays(40));
        }
    }

    [Fact]
    public async Task La_tolerancia_de_otro_Tipo_de_documento_no_se_aplica()
    {
        Guid otroTipo;
        await using (var contexto = CrearContexto())
        {
            var tipo = new TipoDocumento("Reconocimiento médico", null, aplicaVencimientoAutomatico: false, 2, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.No);
            contexto.TiposDocumento.Add(tipo);
            await contexto.SaveChangesAsync();
            contexto.ToleranciasDocumentoClienteEmpresarial.Add(new ToleranciaDocumentoClienteEmpresarial(_clienteId, tipo.Id, 30));
            await contexto.SaveChangesAsync();
            otroTipo = tipo.Id;
        }

        var (centro360, _, _) = await MedirAsync();

        otroTipo.Should().NotBe(_tipoId);
        centro360.Estado.Should().Be(EstadoDocumento.Vencido, "la tolerancia se fija por Tipo de documento");
    }

    [Fact]
    public async Task Un_documento_vigente_no_pasa_a_en_tolerancia()
    {
        await ConfigurarAsync(delCliente: 365);
        await using (var contexto = CrearContexto())
        {
            var hoy = DiaDeNegocio.Hoy();
            contexto.Documentos.Add(Documento.DeTrabajador(_trabajadorId, _tipoId, hoy, VigenciaDocumento.VenceEl(hoy.AddYears(1))));
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var centro360 = await new ObtenerAsignacionesDocumentacionPorCentroQueryHandler(
                lectura, lectura, lectura, lectura, lectura, lectura, new AlcanceDatosServiceFalso(), new CaeManager.Application.Documentos.SituacionEnCentro.SituacionDocumentosEnCentrosService(lectura, lectura, lectura, lectura, lectura, new AlcanceDatosServiceFalso(), new CurrentUserServiceMutable { Rol = "Administrador" }))
            .Handle(new ObtenerAsignacionesDocumentacionPorCentroQuery(_centroId), CancellationToken.None);

        var documentos = centro360.Single().Documentos;
        documentos.Should().Contain(d => d.Estado == EstadoDocumento.Vigente && d.EnToleranciaHasta == null);
        documentos.Where(d => d.Estado == EstadoDocumento.Vigente).Should().OnlyContain(d => d.EnToleranciaHasta == null);
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
