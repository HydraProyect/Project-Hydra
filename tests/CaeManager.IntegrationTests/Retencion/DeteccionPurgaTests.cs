using CaeManager.Application.Common;
using CaeManager.Application.Retencion;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
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
    /// P0-3 de docs/business/MATURITY_REVIEW.md: el filtro global
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

    private async Task<Guid> SembrarDocumentoAsync(DateOnly? vencimiento, DateOnly? emision = null)
    {
        await using var contexto = CrearContexto();

        // Un documento se emite antes de vencer: si no se indica la emisión,
        // se deriva del vencimiento para no construir uno imposible, que el
        // dominio rechaza con razón.
        var fechaEmision = emision ?? vencimiento?.AddYears(-1) ?? _hoy.AddYears(-8);

        var documento = Documento.DeCliente(
            _clienteId, _tipoDocumentoId, fechaEmision, vencimiento, "ruta.pdf");

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

    private DeteccionPurgaService CrearServicio(CaeManagerDbContext contexto, int? aniosTrabajadores = CincoAnios) =>
        new(contexto, contexto, contexto,
            new SolicitudPurgaRepository(contexto),
            Options.Create(new RetencionDatosOptions
            {
                AniosRetencionDocumentos = CincoAnios,
                AniosRetencionTrabajadores = aniosTrabajadores
            }),
            new TenantActualAmbiental { TenantId = _tenant },
            contexto);

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
