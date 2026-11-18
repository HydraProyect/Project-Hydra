using CaeManager.Application.Common;
using CaeManager.Application.Retencion;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Retencion;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Retencion;

/// <summary>
/// El barrido que detecta qué ha cumplido plazo. Lo que importa comprobar es
/// que <b>propone y no destruye</b>, y que el criterio de fechas es el que
/// decidió el propietario del producto (2026-07-31).
/// </summary>
public class DeteccionPurgaTests : IAsyncLifetime
{
    private const int CincoAnios = 5;

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly DateOnly _hoy = new(2031, 6, 1);

    private Guid _clienteId;
    private Guid _tipoDocumentoId;
    private Guid _centroId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var cliente = Empresa.CrearComoCliente("Purgable S.L.", "B12345674", false, null, null);
        contexto.Empresas.Add(cliente);

        var empresa = new Empresa("Contrata Purgable S.L.", "B87654323");
        contexto.Empresas.Add(empresa);

        var tipo = new TipoDocumento("Seguro RC", 12, aplicaVencimientoAutomatico: true, 1, AmbitoAplicacion.Cliente, requerido: RequisitoDocumental.Si);
        contexto.TiposDocumento.Add(tipo);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro Purgable", "Calle Falsa 1");
        contexto.Centros.Add(centro);
        await contexto.SaveChangesAsync();

        _clienteId = cliente.Id;
        _tipoDocumentoId = tipo.Id;
        _centroId = centro.Id;
    }

    public async Task DisposeAsync()
    {
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Un_documento_vencido_hace_mas_de_cinco_anios_genera_propuesta()
    {
        await SembrarDocumentoAsync(vencimiento: _hoy.AddYears(-CincoAnios).AddDays(-1));

        await using var contexto = CrearContexto();
        var creadas = await CrearServicio(contexto).DetectarAsync(_hoy);

        creadas.Should().Be(1);

        var solicitud = await contexto.SolicitudesPurga.SingleAsync();
        solicitud.TipoDato.Should().Be(TipoDatoPurgable.Documentos);
        solicitud.RegistrosAfectados.Should().Be(1);
        // Lo esencial: propone, no destruye.
        solicitud.Estado.Should().Be(EstadoSolicitudPurga.PendienteDeRevision);
    }

    [Fact]
    public async Task Un_documento_que_aun_no_cumple_plazo_no_genera_nada()
    {
        await SembrarDocumentoAsync(vencimiento: _hoy.AddYears(-CincoAnios).AddDays(1));

        await using var contexto = CrearContexto();

        (await CrearServicio(contexto).DetectarAsync(_hoy)).Should().Be(0);
        (await contexto.SolicitudesPurga.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Un_documento_sin_vencimiento_cuenta_desde_su_emision()
    {
        // Es lo que evita los documentos perpetuos.
        await SembrarDocumentoAsync(vencimiento: null, emision: _hoy.AddYears(-CincoAnios).AddDays(-1));

        await using var contexto = CrearContexto();

        (await CrearServicio(contexto).DetectarAsync(_hoy)).Should().Be(1);
    }

    [Fact]
    public async Task Un_documento_ya_anonimizado_no_vuelve_a_proponerse()
    {
        var documentoId = await SembrarDocumentoAsync(vencimiento: _hoy.AddYears(-10));

        await using (var contextoAnonimizar = CrearContexto())
        {
            var documento = await contextoAnonimizar.Documentos.FirstAsync(d => d.Id == documentoId);
            documento.Anonimizar(DateTime.UtcNow);
            await contextoAnonimizar.SaveChangesAsync();
        }

        await using var contexto = CrearContexto();

        (await CrearServicio(contexto).DetectarAsync(_hoy)).Should().Be(0);
    }

    [Fact]
    public async Task Repetir_el_barrido_no_duplica_la_propuesta()
    {
        // Si no, cada arranque llenaría la bandeja de propuestas idénticas.
        await SembrarDocumentoAsync(vencimiento: _hoy.AddYears(-10));

        await using var contexto = CrearContexto();
        await CrearServicio(contexto).DetectarAsync(_hoy);

        await using var segundoContexto = CrearContexto();
        (await CrearServicio(segundoContexto).DetectarAsync(_hoy)).Should().Be(0);

        (await segundoContexto.SolicitudesPurga.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Un_trabajador_de_baja_hace_mas_de_cinco_anios_genera_propuesta()
    {
        await SembrarTrabajadorAsync(fechaBaja: _hoy.AddYears(-CincoAnios).AddDays(-1));

        await using var contexto = CrearContexto();
        await CrearServicio(contexto).DetectarAsync(_hoy);

        var solicitud = await contexto.SolicitudesPurga
            .SingleAsync(s => s.TipoDato == TipoDatoPurgable.TrabajadoresDadosDeBaja);

        solicitud.RegistrosAfectados.Should().Be(1);
    }

    [Fact]
    public async Task Un_trabajador_todavia_de_alta_nunca_se_propone()
    {
        // Mientras dure la relación no hay hito del que contar.
        await SembrarTrabajadorAsync(fechaBaja: null);

        await using var contexto = CrearContexto();
        await CrearServicio(contexto).DetectarAsync(_hoy);

        (await contexto.SolicitudesPurga.CountAsync(s => s.TipoDato == TipoDatoPurgable.TrabajadoresDadosDeBaja))
            .Should().Be(0);
    }

    /// <summary>
    /// P0-3 de Project-Hydra-Negocio/MATURITY_REVIEW.md: el filtro global
    /// (!EstaEliminado) ocultaba también estas filas de la propia detección
    /// de retención — un trabajador dado de baja Y eliminado lógicamente (el
    /// caso más claro de "ya no hay relación") no entraba nunca en la purga.
    /// Decisión confirmada por el propietario: las filas EstaEliminado sí
    /// deben purgarse.
    /// </summary>
    [Fact]
    public async Task Un_trabajador_de_baja_y_eliminado_logicamente_tambien_se_propone()
    {
        var trabajadorId = await SembrarTrabajadorAsync(fechaBaja: _hoy.AddYears(-CincoAnios).AddDays(-1));

        await using (var contextoEliminar = CrearContexto())
        {
            var trabajador = await contextoEliminar.Trabajadores.FirstAsync(t => t.Id == trabajadorId);
            trabajador.MarcarComoEliminado(Guid.NewGuid());
            await contextoEliminar.SaveChangesAsync();
        }

        await using var contexto = CrearContexto();
        await CrearServicio(contexto).DetectarAsync(_hoy);

        var solicitud = await contexto.SolicitudesPurga
            .SingleAsync(s => s.TipoDato == TipoDatoPurgable.TrabajadoresDadosDeBaja);

        solicitud.RegistrosAfectados.Should().Be(1);
    }

    /// <summary>Mismo hallazgo P0-3, categoría Documentos.</summary>
    [Fact]
    public async Task Un_documento_vencido_y_eliminado_logicamente_tambien_se_propone()
    {
        var documentoId = await SembrarDocumentoAsync(vencimiento: _hoy.AddYears(-CincoAnios).AddDays(-1));

        await using (var contextoEliminar = CrearContexto())
        {
            var documento = await contextoEliminar.Documentos.FirstAsync(d => d.Id == documentoId);
            documento.MarcarComoEliminado(Guid.NewGuid());
            await contextoEliminar.SaveChangesAsync();
        }

        await using var contexto = CrearContexto();
        var creadas = await CrearServicio(contexto).DetectarAsync(_hoy);

        creadas.Should().Be(1);
        var solicitud = await contexto.SolicitudesPurga.SingleAsync(s => s.TipoDato == TipoDatoPurgable.Documentos);
        solicitud.RegistrosAfectados.Should().Be(1);
    }

    [Fact]
    public async Task Sin_plazo_configurado_no_se_purga_ningun_trabajador()
    {
        // Null desactiva la categoría sin tocar la de documentos: es la
        // salvaguarda para datos de salud si la revisión legal lo pide.
        await SembrarTrabajadorAsync(fechaBaja: _hoy.AddYears(-20));

        await using var contexto = CrearContexto();
        await CrearServicio(contexto, aniosTrabajadores: null).DetectarAsync(_hoy);

        (await contexto.SolicitudesPurga.CountAsync(s => s.TipoDato == TipoDatoPurgable.TrabajadoresDadosDeBaja))
            .Should().Be(0);
    }

    /// <summary>
    /// HO-084-01 (REC-084, DEC-35): el modo diagnóstico es lo que permite
    /// evaluar qué habría sin política de retención aprobada. Lo esencial —
    /// y lo que distingue este método de <see cref="DeteccionPurgaService.DetectarAsync"/>—
    /// es que cuenta sin dejar ningún rastro ejecutable: repetir el
    /// diagnóstico no debe acumular filas, porque no crea ninguna.
    /// </summary>
    [Fact]
    public async Task Diagnosticar_cuenta_lo_purgable_sin_crear_ninguna_solicitud()
    {
        await SembrarDocumentoAsync(vencimiento: _hoy.AddYears(-CincoAnios).AddDays(-1));
        await SembrarTrabajadorAsync(fechaBaja: _hoy.AddYears(-CincoAnios).AddDays(-1));

        await using var contexto = CrearContexto();
        var resultado = await CrearServicio(contexto).DiagnosticarAsync(_hoy);

        resultado.DocumentosPurgables.Should().Be(1);
        resultado.TrabajadoresPurgables.Should().Be(1);
        resultado.Total.Should().Be(2);

        // El recuento antes/después es el mismo: cero solicitudes, antes y
        // después de diagnosticar dos veces seguidas.
        (await contexto.SolicitudesPurga.CountAsync()).Should().Be(0);
        await CrearServicio(contexto).DiagnosticarAsync(_hoy);
        (await contexto.SolicitudesPurga.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Diagnosticar_sin_nada_purgable_devuelve_cero()
    {
        await SembrarDocumentoAsync(vencimiento: _hoy.AddYears(-CincoAnios).AddDays(1));

        await using var contexto = CrearContexto();
        var resultado = await CrearServicio(contexto).DiagnosticarAsync(_hoy);

        resultado.Total.Should().Be(0);
    }

    [Fact]
    public async Task Diagnosticar_respeta_el_plazo_de_trabajadores_desactivado()
    {
        // Mismo criterio que Sin_plazo_configurado_no_se_purga_ningun_trabajador,
        // aplicado al diagnóstico: null sigue significando "esta categoría no
        // se evalúa", no "todo es purgable".
        await SembrarTrabajadorAsync(fechaBaja: _hoy.AddYears(-20));

        await using var contexto = CrearContexto();
        var resultado = await CrearServicio(contexto, aniosTrabajadores: null).DiagnosticarAsync(_hoy);

        resultado.TrabajadoresPurgables.Should().Be(0);
    }

    /// <summary>
    /// Plazo efectivo = el del tipo ?? el del Tenant propietario, por el servicio real: con 2 años en el Tenant,
    /// se propone lo vencido hace 3 años de un tipo sin plazo propio y no lo de un tipo con 5; y una versión
    /// sustituida cuenta desde su sustitución (R3) aunque su propio vencimiento quede lejos.
    /// </summary>
    [Fact]
    public async Task Con_dos_anios_en_el_Tenant_y_cinco_en_un_tipo_se_propone_lo_que_toca()
    {
        Guid delTenantId, delTipoId, sustituidaId, sustitutaId;
        await using (var contexto = CrearContexto())
        {
            var parametros = new ParametroSistema(30, 15);
            parametros.FijarPlazoRetencionDocumentos(2);
            contexto.ParametrosSistema.Add(parametros);

            var tipoConPlazo = new TipoDocumento(
                "Contrato marco", 12, aplicaVencimientoAutomatico: true, 1, AmbitoAplicacion.Cliente, requerido: RequisitoDocumental.Si);
            tipoConPlazo.FijarPlazoRetencionDocumentos(5);
            contexto.TiposDocumento.Add(tipoConPlazo);
            await contexto.SaveChangesAsync();

            // Vencidos hace 3 años: cumplen 2 años, no 5.
            var delTenant = Documento.DeCliente(
                _clienteId, _tipoDocumentoId, _hoy.AddYears(-8), VigenciaDocumento.VenceEl(_hoy.AddYears(-3)), "tenant.pdf");
            var delTipo = Documento.DeCliente(
                _clienteId, tipoConPlazo.Id, _hoy.AddYears(-8), VigenciaDocumento.VenceEl(_hoy.AddYears(-3)), "tipo.pdf");

            // Vigentes hasta dentro de un año; la primera se sustituye el 2026-09-01, entre el corte de 5 años
            // (2026-06-01) y el de 2 (2029-06-01): solo la propone el plazo de 2 contado desde la sustitución.
            var sustituida = Documento.DeCliente(
                _clienteId, _tipoDocumentoId, new DateOnly(2024, 1, 1), VigenciaDocumento.VenceEl(_hoy.AddYears(1)), "v1.pdf");
            var sustituta = Documento.DeCliente(
                _clienteId, _tipoDocumentoId, new DateOnly(2026, 9, 1), VigenciaDocumento.VenceEl(_hoy.AddYears(1)), "v2.pdf");
            contexto.Documentos.AddRange(delTenant, delTipo, sustituida, sustituta);
            await contexto.SaveChangesAsync();

            sustituida.SustituirPor(
                sustituta, MotivoSustitucionDocumento.Renovacion, new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc));
            await contexto.SaveChangesAsync();

            (delTenantId, delTipoId, sustituidaId, sustitutaId) = (delTenant.Id, delTipo.Id, sustituida.Id, sustituta.Id);
        }

        await using var contextoDeteccion = CrearContexto();
        (await CrearServicio(contextoDeteccion).DetectarAsync(_hoy)).Should().Be(1);

        var solicitud = await contextoDeteccion.SolicitudesPurga.SingleAsync(s => s.TipoDato == TipoDatoPurgable.Documentos);
        solicitud.RegistrosAfectados.Should().Be(2);
        solicitud.FechaCorte.Should().Be(_hoy.AddYears(-2), "el corte informativo es el del plazo más corto aplicado");

        var propuestos = await contextoDeteccion.DocumentosPropuestosPurga
            .Where(p => p.SolicitudPurgaId == solicitud.Id)
            .Select(p => p.DocumentoId)
            .ToListAsync();
        propuestos.Should().BeEquivalentTo(
            new[] { delTenantId, sustituidaId },
            "{0} es de un tipo con 5 años y {1} es la versión vigente", delTipoId, sustitutaId);
    }

    /// <summary>
    /// El plazo de conservación es del Tenant propietario: el de 1 año de otro Tenant no adelanta nada en este, que
    /// sin configuración conserva 5 años (R2).
    /// </summary>
    [Fact]
    public async Task El_plazo_de_otro_Tenant_no_altera_lo_detectado_en_este()
    {
        var tenantB = Guid.NewGuid();
        Guid documentoB;
        await using (var contextoB = CrearContexto(tenantB))
        {
            var parametros = new ParametroSistema(30, 15);
            parametros.FijarPlazoRetencionDocumentos(1);
            contextoB.ParametrosSistema.Add(parametros);

            var clienteB = Empresa.CrearComoCliente("Cliente del Tenant B S.L.", "B12345674", false, null, null);
            contextoB.Empresas.Add(clienteB);
            var tipoB = new TipoDocumento("Seguro RC", 12, aplicaVencimientoAutomatico: true, 1, AmbitoAplicacion.Cliente, requerido: RequisitoDocumental.Si);
            contextoB.TiposDocumento.Add(tipoB);
            await contextoB.SaveChangesAsync();

            var documento = Documento.DeCliente(
                clienteB.Id, tipoB.Id, _hoy.AddYears(-8), VigenciaDocumento.VenceEl(_hoy.AddYears(-2)), "b.pdf");
            contextoB.Documentos.Add(documento);
            await contextoB.SaveChangesAsync();
            documentoB = documento.Id;
        }

        // Vencido hace 2 años: con el plazo de 1 año de B se propondría; con los 5 de A, no.
        await SembrarDocumentoAsync(vencimiento: _hoy.AddYears(-2), emision: _hoy.AddYears(-8));

        await using (var contextoA = CrearContexto())
        {
            (await CrearServicio(contextoA).DetectarAsync(_hoy)).Should().Be(0);
            (await contextoA.SolicitudesPurga.AnyAsync()).Should().BeFalse();
        }

        await using var contextoDeB = CrearContexto(tenantB);
        (await CrearServicio(contextoDeB, tenant: tenantB).DetectarAsync(_hoy)).Should().Be(1);
        var solicitudB = await contextoDeB.SolicitudesPurga.SingleAsync();
        (await contextoDeB.DocumentosPropuestosPurga
                .Where(p => p.SolicitudPurgaId == solicitudB.Id)
                .Select(p => p.DocumentoId)
                .ToListAsync())
            .Should().Equal(documentoB);
    }

    /// <summary>El CHECK de la base es la última barrera del rango de 1 a 5 años (R1), aunque se salte el dominio.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public async Task La_base_rechaza_un_plazo_del_Tenant_fuera_de_rango(int anios)
    {
        await using var contexto = CrearContexto();
        contexto.ParametrosSistema.Add(new ParametroSistema(30, 15));
        await contexto.SaveChangesAsync();

        var actualizar = () => contexto.Database.ExecuteSqlAsync(
            $"""UPDATE "ParametrosSistema" SET "AniosRetencionDocumentos" = {anios} WHERE "TenantId" = {_tenant}""");

        (await actualizar.Should().ThrowAsync<PostgresException>())
            .Which.ConstraintName.Should().Be("CK_ParametrosSistema_AniosRetencionDocumentos");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public async Task La_base_rechaza_un_plazo_del_tipo_fuera_de_rango(int anios)
    {
        await using var contexto = CrearContexto();

        var actualizar = () => contexto.Database.ExecuteSqlAsync(
            $"""UPDATE "TiposDocumento" SET "AniosRetencionDocumentos" = {anios} WHERE "Id" = {_tipoDocumentoId}""");

        (await actualizar.Should().ThrowAsync<PostgresException>())
            .Which.ConstraintName.Should().Be("CK_TiposDocumento_AniosRetencionDocumentos");
    }

    private async Task<Guid> SembrarDocumentoAsync(DateOnly? vencimiento, DateOnly? emision = null)
    {
        await using var contexto = CrearContexto();

        // Un documento se emite antes de vencer: si no se indica la emisión,
        // se deriva del vencimiento para no construir uno imposible, que el
        // dominio rechaza con razón.
        var fechaEmision = emision ?? vencimiento?.AddYears(-1) ?? _hoy.AddYears(-8);

        var documento = Documento.DeCliente(
            _clienteId, _tipoDocumentoId, fechaEmision,
            vencimiento is { } fecha ? VigenciaDocumento.VenceEl(fecha) : VigenciaDocumento.NoCaduca, "ruta.pdf");

        contexto.Documentos.Add(documento);
        await contexto.SaveChangesAsync();

        return documento.Id;
    }

    private async Task<Guid> SembrarTrabajadorAsync(DateOnly? fechaBaja)
    {
        await using var contexto = CrearContexto();

        var empresaId = await contexto.Empresas.Select(e => e.Id).FirstAsync();
        var trabajador = Trabajador.DeEmpresa(
            empresaId, "Manuel", "Moreno", "12345678Z", new DateOnly(1985, 1, 1), null, null, null);

        contexto.Trabajadores.Add(trabajador);
        await contexto.SaveChangesAsync();

        // El alta va antes que la baja, también cuando la baja es muy antigua.
        var fechaAlta = fechaBaja is { } b && b < _hoy.AddYears(-12) ? b.AddYears(-1) : _hoy.AddYears(-12);
        var asignacion = new Asignacion(trabajador.Id, _centroId, fechaAlta);
        if (fechaBaja is { } baja) asignacion.DarDeBaja(baja);

        contexto.Asignaciones.Add(asignacion);
        await contexto.SaveChangesAsync();

        return trabajador.Id;
    }

    private DeteccionPurgaService CrearServicio(
        CaeManagerDbContext contexto, int? aniosTrabajadores = CincoAnios, Guid? tenant = null) =>
        new(contexto, contexto, contexto, contexto, contexto,
            new SolicitudPurgaRepository(contexto),
            Options.Create(new RetencionDatosOptions
            {
                AniosRetencionTrabajadores = aniosTrabajadores
            }),
            new TenantActualAmbiental { TenantId = tenant ?? _tenant },
            contexto);

    private CaeManagerDbContext CrearContexto(Guid? tenant = null)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenant ?? _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
