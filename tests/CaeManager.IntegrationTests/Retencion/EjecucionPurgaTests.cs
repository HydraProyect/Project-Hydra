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
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Retencion;

/// <summary>
/// La ejecución ya autorizada — el único punto que destruye datos
/// personales. P0-3 de docs/business/MATURITY_REVIEW.md: comprueba que
/// alcanza también a las filas soft-deleted (EstaEliminado), que antes del
/// fix quedaban invisibles tanto para detectar como para anonimizar.
/// </summary>
public class EjecucionPurgaTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly DateOnly _hoy = new(2031, 6, 1);
    private readonly Guid _usuarioAutorizador = Guid.NewGuid();

    private Guid _clienteId;
    private Guid _tipoDocumentoId;
    private Guid _centroId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var cliente = Empresa.CrearComoCliente("Purgable Ejecucion S.L.", "B12345674", false, null, null);
        contexto.Empresas.Add(cliente);

        var empresa = new Empresa("Contrata Purgable Ejecucion S.L.", "B87654323");
        contexto.Empresas.Add(empresa);

        var tipo = new TipoDocumento("Seguro RC Ejecucion", 12, aplicaVencimientoAutomatico: true, 1, AmbitoAplicacion.Cliente, requerido: RequisitoDocumental.Si);
        contexto.TiposDocumento.Add(tipo);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro Purgable Ejecucion", "Calle Falsa 2");
        contexto.Centros.Add(centro);
        await contexto.SaveChangesAsync();

        _clienteId = cliente.Id;
        _tipoDocumentoId = tipo.Id;
        _centroId = centro.Id;
    }

    public async Task DisposeAsync() =>
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Ejecutar_anonimiza_un_trabajador_dado_de_baja_y_eliminado_logicamente()
    {
        Guid trabajadorId;
        await using (var contextoSiembra = CrearContexto())
        {
            var empresaId = await contextoSiembra.Empresas.Select(e => e.Id).FirstAsync();
            var trabajador = Trabajador.DeEmpresa(
                empresaId, "Manuel", "Moreno", "12345678Z", new DateOnly(1985, 1, 1), null, null, null);
            contextoSiembra.Trabajadores.Add(trabajador);
            await contextoSiembra.SaveChangesAsync();

            var fechaBaja = _hoy.AddYears(-6);
            var asignacion = new Asignacion(trabajador.Id, _centroId, fechaBaja.AddYears(-1));
            asignacion.DarDeBaja(fechaBaja);
            contextoSiembra.Asignaciones.Add(asignacion);
            await contextoSiembra.SaveChangesAsync();

            // El caso central del hallazgo: además de dado de baja, eliminado lógicamente.
            trabajador.MarcarComoEliminado(Guid.NewGuid());
            await contextoSiembra.SaveChangesAsync();

            trabajadorId = trabajador.Id;
        }

        Guid solicitudId;
        await using (var contextoDeteccion = CrearContexto())
        {
            var servicioDeteccion = new DeteccionPurgaService(
                contextoDeteccion,
                contextoDeteccion,
                contextoDeteccion,
                new SolicitudPurgaRepository(contextoDeteccion),
                Microsoft.Extensions.Options.Options.Create(new RetencionDatosOptions
                {
                    AniosRetencionDocumentos = 5,
                    AniosRetencionTrabajadores = 5
                }),
                new TenantActualAmbiental { TenantId = _tenant },
                contextoDeteccion);

            (await servicioDeteccion.DetectarAsync(_hoy)).Should().Be(1);

            var solicitud = await contextoDeteccion.SolicitudesPurga
                .SingleAsync(s => s.TipoDato == TipoDatoPurgable.TrabajadoresDadosDeBaja);
            solicitudId = solicitud.Id;
        }

        await using (var contextoAutorizacion = CrearContexto())
        {
            var solicitud = await contextoAutorizacion.SolicitudesPurga.FirstAsync(s => s.Id == solicitudId);
            solicitud.Programar(_hoy, _usuarioAutorizador, _hoy);
            await contextoAutorizacion.SaveChangesAsync();
        }

        await using (var contextoEjecucion = CrearContexto())
        {
            var servicioEjecucion = new EjecucionPurgaService(
                contextoEjecucion,
                contextoEjecucion,
                contextoEjecucion,
                new SolicitudPurgaRepository(contextoEjecucion),
                new AlmacenamientoArchivosFalso(),
                new TenantActualAmbiental { TenantId = _tenant },
                contextoEjecucion,
                new AlertaOperativaFalsa(),
                NullLogger<EjecucionPurgaService>.Instance);

            var afectados = await servicioEjecucion.EjecutarAsync(solicitudId, _hoy);
            afectados.Should().Be(1);
        }

        await using var contextoVerificacion = CrearContexto();
        var trabajadorAnonimizado = await contextoVerificacion.Trabajadores
            .IgnoreQueryFilters()
            .FirstAsync(t => t.Id == trabajadorId);

        trabajadorAnonimizado.EstaAnonimizado.Should().BeTrue(
            "el trabajador estaba soft-deleted pero seguía dentro del plazo de retención — debía anonimizarse igual");
    }

    /// <summary>
    /// Siembra un Documento de Cliente ya vencido y con archivo adjunto, más
    /// la solicitud de purga que lo alcanza, ya autorizada. Devuelve los dos
    /// identificadores que los casos necesitan comprobar.
    /// </summary>
    private async Task<(Guid DocumentoId, Guid SolicitudId)> SembrarDocumentoPurgableAsync(string archivoUrl)
    {
        await using var contexto = CrearContexto();

        var documento = Documento.DeCliente(
            _clienteId, _tipoDocumentoId,
            fechaEmision: _hoy.AddYears(-7),
            fechaVencimiento: _hoy.AddYears(-6),
            archivoUrl: archivoUrl);
        contexto.Documentos.Add(documento);

        var solicitud = new SolicitudPurga(TipoDatoPurgable.Documentos, 1, _hoy.AddYears(-5));
        contexto.SolicitudesPurga.Add(solicitud);
        await contexto.SaveChangesAsync();

        solicitud.Programar(_hoy, _usuarioAutorizador, _hoy);
        await contexto.SaveChangesAsync();

        return (documento.Id, solicitud.Id);
    }

    [Fact]
    public async Task Si_no_se_puede_borrar_el_archivo_el_documento_no_se_da_por_purgado()
    {
        // El orden anterior anonimizaba primero y borraba después: al fallar el
        // borrado, la fila quedaba marcada como anonimizada y sin ArchivoUrl,
        // el PDF seguía en almacenamiento y ya nada sabía qué había que borrar.
        // Un reconocimiento médico conservado para siempre y declarado
        // suprimido. Este caso fija que un borrado fallido deja el documento
        // intacto y localizable, para que un reintento posterior lo alcance.
        var (documentoId, solicitudId) = await SembrarDocumentoPurgableAsync("tenant/medico.pdf");

        var alertas = new AlertaOperativaFalsa();
        await using (var contextoEjecucion = CrearContexto())
        {
            var servicioEjecucion = new EjecucionPurgaService(
                contextoEjecucion, contextoEjecucion, contextoEjecucion,
                new SolicitudPurgaRepository(contextoEjecucion),
                new AlmacenamientoArchivosFalso(fallaAlEliminar: true),
                new TenantActualAmbiental { TenantId = _tenant },
                contextoEjecucion,
                alertas,
                NullLogger<EjecucionPurgaService>.Instance);

            var afectados = await servicioEjecucion.EjecutarAsync(solicitudId, _hoy);
            afectados.Should().Be(0, "no se purgó nada: el archivo sigue estando");
        }

        await using var contextoVerificacion = CrearContexto();
        var documento = await contextoVerificacion.Documentos
            .IgnoreQueryFilters()
            .FirstAsync(d => d.Id == documentoId);

        documento.EstaAnonimizado.Should().BeFalse(
            "declararlo purgado con su PDF todavía en almacenamiento sería conformidad falsa");
        documento.ArchivoUrl.Should().Be("tenant/medico.pdf",
            "sin la referencia, ningún reintento posterior podría encontrar el archivo superviviente");

        alertas.Alertas.Should().ContainSingle()
            .Which.Nivel.Should().Be(NivelAlertaOperativa.Critica,
                "la solicitud ya quedó marcada como ejecutada, así que nadie reintentará esto solo");
    }

    [Fact]
    public async Task Un_documento_vencido_se_anonimiza_y_pierde_su_archivo()
    {
        // Control positivo del caso anterior: sin él, un servicio que no
        // purgara nunca nada también pasaría aquel.
        var (documentoId, solicitudId) = await SembrarDocumentoPurgableAsync("tenant/medico.pdf");

        var alertas = new AlertaOperativaFalsa();
        await using (var contextoEjecucion = CrearContexto())
        {
            var servicioEjecucion = new EjecucionPurgaService(
                contextoEjecucion, contextoEjecucion, contextoEjecucion,
                new SolicitudPurgaRepository(contextoEjecucion),
                new AlmacenamientoArchivosFalso(),
                new TenantActualAmbiental { TenantId = _tenant },
                contextoEjecucion,
                alertas,
                NullLogger<EjecucionPurgaService>.Instance);

            (await servicioEjecucion.EjecutarAsync(solicitudId, _hoy)).Should().Be(1);
        }

        await using var contextoVerificacion = CrearContexto();
        var documento = await contextoVerificacion.Documentos
            .IgnoreQueryFilters()
            .FirstAsync(d => d.Id == documentoId);

        documento.EstaAnonimizado.Should().BeTrue();
        documento.ArchivoUrl.Should().BeNull();
        alertas.Alertas.Should().BeEmpty();
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

    private sealed class AlmacenamientoArchivosFalso(bool fallaAlEliminar = false) : IFileStorageService
    {
        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) =>
            fallaAlEliminar
                ? throw new IOException("El almacenamiento no está disponible.")
                : Task.CompletedTask;
    }

    private sealed class AlertaOperativaFalsa : IAlertaOperativa
    {
        public List<(string Mensaje, NivelAlertaOperativa Nivel)> Alertas { get; } = [];

        public void Emitir(string mensaje, NivelAlertaOperativa nivel) => Alertas.Add((mensaje, nivel));

        public void CapturarExcepcion(Exception excepcion) { }

        public void DejarMigaDePan(string mensaje) { }

        public IDisposable IniciarAmbitoDeCaptura() => new AmbitoVacio();

        private sealed class AmbitoVacio : IDisposable
        {
            public void Dispose() { }
        }
    }
}
