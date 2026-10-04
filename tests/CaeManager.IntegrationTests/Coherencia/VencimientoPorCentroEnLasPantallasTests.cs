using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Documentos.Commands.VolverAPresentarDocumentoEnCentro;
using CaeManager.Application.Documentos.Presentaciones;
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
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Coherencia;

/// <summary>
/// El vencimiento por Centro (2026-10-04) tal como lo ven las dos pantallas con contexto de Centro (Centro 360 y Trabajador 360 por
/// Centro): la fecha «Vence en este Centro», la vigencia propia del documento, el estado y si se ofrece «Volver a presentar».
/// Reproduce el ejemplo del propietario con fechas relativas a hoy: una formación con vigencia de cuatro años que un Centro pide
/// presentar cada año. Cada prueba mide las dos vistas sobre el mismo escenario: una sola regla, dos superficies.
/// </summary>
public class VencimientoPorCentroEnLasPantallasTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly DateOnly _hoy = DiaDeNegocio.Hoy();
    private Guid _centroId;
    private Guid _trabajadorId;
    private Guid _tipoId;
    private Guid _empresaId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        if (await contexto.ParametrosSistema.SingleOrDefaultAsync() is null)
            contexto.ParametrosSistema.Add(new ParametroSistema(30, 15));

        var cliente = Empresa.CrearComoCliente("Cliente periodico S.L.", "B12345674", false, null, null);
        var empresa = new Empresa("Empresa periodica S.L.", "B87654323");
        contexto.Empresas.AddRange(cliente, empresa);
        var tipo = new TipoDocumento("Formación art. 19", null, aplicaVencimientoAutomatico: false, 1, AmbitoAplicacion.Trabajador, requerido: RequisitoDocumental.Si);
        contexto.TiposDocumento.Add(tipo);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro con periodicidad");
        contexto.Centros.Add(centro);
        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Rosa", "Periodica", "12345678Z");
        contexto.Trabajadores.Add(trabajador);
        await contexto.SaveChangesAsync();
        contexto.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, _hoy.AddYears(-5)));
        await contexto.SaveChangesAsync();

        _centroId = centro.Id;
        _trabajadorId = trabajador.Id;
        _tipoId = tipo.Id;
        _empresaId = empresa.Id;
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;
        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private async Task<Guid> SembrarAsync(int? periodicidadMeses, DateOnly emision, VigenciaDocumento vigencia)
    {
        await using var contexto = CrearContexto();
        if (periodicidadMeses is not null)
            contexto.TiposDocumentoCentros.Add(new TipoDocumentoCentro(_tipoId, _centroId, incluido: true, periodicidadEspecialMeses: periodicidadMeses));
        var documento = Documento.DeTrabajador(_trabajadorId, _tipoId, emision, vigencia);
        contexto.Documentos.Add(documento);
        await contexto.SaveChangesAsync();
        return documento.Id;
    }

    private async Task PresentarAsync(Guid documentoId, DateOnly fecha)
    {
        await using var contexto = CrearContexto();
        contexto.PresentacionesDocumentoEnCentro.Add(new PresentacionDocumentoEnCentro(
            documentoId, _centroId, fecha, OrigenPresentacionDocumentoEnCentro.VolverAPresentar, DateTime.UtcNow));
        await contexto.SaveChangesAsync();
    }

    private async Task<(DocumentoRequeridoDto EnCentro360, DocumentoRequeridoDto EnTrabajador360)> MedirAsync()
    {
        var (documentosCentro, documentosTrabajador) = await MedirListasAsync();
        return (
            documentosCentro.Should().ContainSingle().Subject,
            documentosTrabajador.Should().ContainSingle().Subject);
    }

    /// <summary>Lo que listan las dos vistas, sin exigir que el documento aparezca (un «No caduca» no se lista).</summary>
    private async Task<(IReadOnlyList<DocumentoRequeridoDto> EnCentro360, IReadOnlyList<DocumentoRequeridoDto> EnTrabajador360)> MedirListasAsync()
    {
        await using var contexto = CrearContexto();
        var alcance = new AlcanceDatosServiceFalso();

        var centro360 = await new ObtenerAsignacionesDocumentacionPorCentroQueryHandler(
                contexto, contexto, contexto, contexto, contexto, contexto, alcance)
            .Handle(new ObtenerAsignacionesDocumentacionPorCentroQuery(_centroId), CancellationToken.None);
        var trabajador360 = await new ObtenerDocumentacionPorCentroDeTrabajadorQueryHandler(
                contexto, contexto, contexto, contexto, contexto, contexto, alcance)
            .Handle(new ObtenerDocumentacionPorCentroDeTrabajadorQuery(_trabajadorId), CancellationToken.None);

        var enCentro = centro360.Should().ContainSingle().Subject;
        var enTrabajador = trabajador360.Should().ContainSingle().Subject;
        enCentro.Cumplimiento.Should().Be(enTrabajador.Cumplimiento, "las dos vistas miden el mismo par exigido");
        return (enCentro.Documentos, enTrabajador.Documentos);
    }

    private static void LasDosVistas(
        (DocumentoRequeridoDto Centro, DocumentoRequeridoDto Trabajador) vistas, Action<DocumentoRequeridoDto> comprobacion)
    {
        comprobacion(vistas.Centro);
        comprobacion(vistas.Trabajador);
    }

    [Fact]
    public async Task Ejemplo_del_propietario_el_primer_ano_vence_en_el_Centro_a_los_12_meses_de_la_emision_y_su_vigencia_propia_sigue_a_cuatro_anos()
    {
        // Formación emitida hoy, vigencia de cuatro años; el Centro la pide cada año.
        await SembrarAsync(12, _hoy, VigenciaDocumento.VenceEl(_hoy.AddYears(4)));

        LasDosVistas(await MedirAsync(), d =>
        {
            d.VenceEnElCentro.Should().Be(_hoy.AddYears(1));
            d.FechaVencimiento.Should().Be(_hoy.AddYears(4), "la vigencia propia del Documento no cambia");
            d.Estado.Should().Be(EstadoDocumento.Vigente);
            d.PuedeVolverAPresentar.Should().BeTrue();
        });
    }

    [Fact]
    public async Task Ejemplo_del_propietario_al_cumplirse_el_ano_sin_volver_a_presentar_vence_en_el_Centro_aunque_el_documento_siga_vigente()
    {
        // Emitida hace un año y un día, vigente tres años más; nunca se volvió a presentar: venció en el Centro ayer.
        await SembrarAsync(12, _hoy.AddYears(-1).AddDays(-1), VigenciaDocumento.VenceEl(_hoy.AddYears(3)));

        LasDosVistas(await MedirAsync(), d =>
        {
            d.Estado.Should().Be(EstadoDocumento.Vencido);
            d.VenceEnElCentro.Should().Be(_hoy.AddDays(-1));
            d.FechaVencimiento.Should().Be(_hoy.AddYears(3));
            d.PuedeVolverAPresentar.Should().BeTrue("sigue vigente por su propia fecha: se vuelve a presentar, no se renueva");
        });
    }

    [Fact]
    public async Task Ejemplo_del_propietario_volver_a_presentar_reinicia_el_plazo_un_ano_desde_la_presentacion()
    {
        var documentoId = await SembrarAsync(12, _hoy.AddYears(-1).AddDays(-1), VigenciaDocumento.VenceEl(_hoy.AddYears(3)));

        // Mismo comando que usa la pantalla, con los servicios reales.
        await using (var contexto = CrearContexto())
        {
            var handler = new VolverAPresentarDocumentoEnCentroCommandHandler(
                new DocumentoRepository(contexto), new AlcanceDatosServiceFalso(), contexto, contexto, contexto, contexto, contexto,
                new RegistroDePresentaciones(new PresentacionDocumentoEnCentroRepository(contexto), contexto), contexto);
            var resultado = await handler.Handle(new VolverAPresentarDocumentoEnCentroCommand(documentoId, _centroId), CancellationToken.None);
            resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : null);
        }

        LasDosVistas(await MedirAsync(), d =>
        {
            d.Estado.Should().Be(EstadoDocumento.Vigente);
            d.VenceEnElCentro.Should().Be(_hoy.AddYears(1), "12 meses desde la presentacion de hoy");
            d.FechaVencimiento.Should().Be(_hoy.AddYears(3));
        });

        await using var comprobacion = CrearContexto();
        var fila = await comprobacion.PresentacionesDocumentoEnCentro.SingleAsync();
        (fila.DocumentoId, fila.CentroId, fila.FechaPresentacion, fila.Origen).Should().Be(
            (documentoId, _centroId, _hoy, OrigenPresentacionDocumentoEnCentro.VolverAPresentar));
    }

    [Fact]
    public async Task Ejemplo_del_propietario_en_el_ultimo_tramo_el_plazo_del_Centro_no_pasa_de_la_vigencia_propia()
    {
        // Tercer ano de cuatro: la ultima presentacion fue hace dos dias y daria 12 meses mas, pero el documento vence en seis meses.
        var propia = _hoy.AddMonths(6);
        var documentoId = await SembrarAsync(12, _hoy.AddYears(-3).AddMonths(-6), VigenciaDocumento.VenceEl(propia));
        await PresentarAsync(documentoId, _hoy.AddDays(-2));

        LasDosVistas(await MedirAsync(), d =>
        {
            d.VenceEnElCentro.Should().Be(propia, "el Centro nunca alarga la vigencia propia: en ese momento hace falta un documento nuevo");
            d.Estado.Should().Be(EstadoDocumento.Vigente);
        });
    }

    [Fact]
    public async Task Un_documento_vencido_por_su_fecha_no_ofrece_volver_a_presentar_ni_lo_acepta_el_comando()
    {
        var documentoId = await SembrarAsync(12, _hoy.AddYears(-2), VigenciaDocumento.VenceEl(_hoy.AddDays(-3)));

        LasDosVistas(await MedirAsync(), d =>
        {
            d.Estado.Should().Be(EstadoDocumento.Vencido);
            d.PuedeVolverAPresentar.Should().BeFalse();
        });

        await using var contexto = CrearContexto();
        var handler = new VolverAPresentarDocumentoEnCentroCommandHandler(
            new DocumentoRepository(contexto), new AlcanceDatosServiceFalso(), contexto, contexto, contexto, contexto, contexto,
            new RegistroDePresentaciones(new PresentacionDocumentoEnCentroRepository(contexto), contexto), contexto);
        var resultado = await handler.Handle(new VolverAPresentarDocumentoEnCentroCommand(documentoId, _centroId), CancellationToken.None);
        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(VolverAPresentarDocumentoEnCentroCommandHandler.CodigoNoAplica);
        (await contexto.PresentacionesDocumentoEnCentro.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Sin_periodicidad_en_el_Centro_no_hay_segunda_fecha_ni_accion_aunque_haya_presentaciones()
    {
        var documentoId = await SembrarAsync(null, _hoy.AddYears(-1), VigenciaDocumento.VenceEl(_hoy.AddYears(2)));
        await PresentarAsync(documentoId, _hoy);

        LasDosVistas(await MedirAsync(), d =>
        {
            d.VenceEnElCentro.Should().BeNull();
            d.PuedeVolverAPresentar.Should().BeFalse();
            d.FechaVencimiento.Should().Be(_hoy.AddYears(2));
            d.Estado.Should().Be(EstadoDocumento.Vigente);
        });
    }

    [Fact]
    public async Task Un_documento_que_no_caduca_no_vence_en_el_Centro_aunque_tenga_periodicidad()
    {
        // Hueco declarado en la entrega: «No caduca» sigue sin vencer, tambien con periodicidad. Ni Centro 360 ni Trabajador 360 lo
        // listan (solo lista lo que tiene fecha o requiere atención), así que no hay fila que pueda ofrecer «Volver a presentar», y
        // el par cuenta como al día en las dos vistas (la misma fracción en ambas, comprobada en MedirListasAsync).
        await SembrarAsync(12, _hoy.AddYears(-10), VigenciaDocumento.NoCaduca);

        var (enCentro, enTrabajador) = await MedirListasAsync();

        enCentro.Should().BeEmpty("«No caduca» no se lista en Centro 360, tampoco con periodicidad en el Centro");
        enTrabajador.Should().BeEmpty("«No caduca» no se lista en Trabajador 360, tampoco con periodicidad en el Centro");
    }

    [Fact]
    public async Task Renovar_el_documento_crea_otro_que_no_hereda_las_presentaciones_y_empieza_en_su_emision()
    {
        var anteriorId = await SembrarAsync(12, _hoy.AddYears(-1).AddDays(-1), VigenciaDocumento.VenceEl(_hoy.AddMonths(1)));
        await PresentarAsync(anteriorId, _hoy);

        // La renovación sustituye al documento por uno NUEVO (otro Id): sus presentaciones son las suyas, no las del anterior.
        Guid nuevoId;
        await using (var contexto = CrearContexto())
        {
            var anterior = await contexto.Documentos.SingleAsync(d => d.Id == anteriorId);
            var nuevo = Documento.DeTrabajador(_trabajadorId, _tipoId, _hoy.AddMonths(-13), VigenciaDocumento.VenceEl(_hoy.AddYears(3)));
            contexto.Documentos.Add(nuevo);
            await contexto.SaveChangesAsync();
            anterior.SustituirPor(nuevo, MotivoSustitucionDocumento.Renovacion, DateTime.UtcNow);
            await contexto.SaveChangesAsync();
            nuevoId = nuevo.Id;
        }

        LasDosVistas(await MedirAsync(), d =>
        {
            d.DocumentoId.Should().Be(nuevoId);
            d.VenceEnElCentro.Should().Be(_hoy.AddMonths(-1), "sin presentaciones propias, el ancla es la emision del documento nuevo");
            d.Estado.Should().Be(EstadoDocumento.Vencido, "la presentacion del documento anterior no cuenta para el nuevo");
        });

        await using var comprobacion = CrearContexto();
        (await comprobacion.PresentacionesDocumentoEnCentro.AnyAsync(p => p.DocumentoId == nuevoId)).Should().BeFalse();
        (await comprobacion.PresentacionesDocumentoEnCentro.AnyAsync(p => p.DocumentoId == anteriorId)).Should().BeTrue("el historial del anterior se conserva");
    }

    [Fact]
    public async Task El_tope_de_120_meses_lo_impone_tambien_la_base_de_datos()
    {
        // El dominio ya lo rechaza (ArgumentException); este es el CHECK de la migración, que protege contra cualquier otro escritor.
        await SembrarAsync(12, _hoy, VigenciaDocumento.VenceEl(_hoy.AddYears(4)));

        await using var conexion = new NpgsqlConnection(_cadenaConexion);
        await conexion.OpenAsync();
        await using (var tenant = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false);", conexion))
        {
            tenant.Parameters.AddWithValue("t", _tenant.ToString());
            await tenant.ExecuteScalarAsync();
        }

        // Control positivo: con el Tenant puesto el UPDATE ve la fila y un valor valido se escribe.
        await using (var valido = new NpgsqlCommand("UPDATE \"TiposDocumentoCentros\" SET \"PeriodicidadEspecialMeses\" = 120", conexion))
            (await valido.ExecuteNonQueryAsync()).Should().Be(1);

        await using var fuera = new NpgsqlCommand("UPDATE \"TiposDocumentoCentros\" SET \"PeriodicidadEspecialMeses\" = 121", conexion);
        var accion = async () => await fuera.ExecuteNonQueryAsync();
        var excepcion = (await accion.Should().ThrowAsync<PostgresException>()).Which;
        excepcion.SqlState.Should().Be("23514", "violacion de CHECK");
        excepcion.ConstraintName.Should().Be("CK_TiposDocumentoCentros_PeriodicidadEspecialMeses");
    }
}
