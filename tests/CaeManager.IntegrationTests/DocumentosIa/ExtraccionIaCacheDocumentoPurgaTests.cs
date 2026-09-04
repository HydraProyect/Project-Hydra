using CaeManager.Application.Common;
using CaeManager.Application.Retencion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.DocumentosIa;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Retencion;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.DocumentosIa;

/// <summary>
/// REC-036/DEC-34: el vínculo durable entre <see cref="ExtraccionIaCache"/> y
/// <c>Documento</c>, y su purga en cascada cuando <see cref="EjecucionPurgaService"/>
/// anonimiza el Documento por retención (HO-036-01 § 7 y § 13).
///
/// <b>Medición del § 6 del handoff (HO-036-01), aquí por escrito.</b> La
/// pregunta era cuántas entradas de caché tienen hoy más de un Documento con
/// el mismo hash. Medido 2026-09-03 contra la base de desarrollo local
/// (<c>caemanager</c>, la misma cadena de <c>appsettings.json</c>): 5 filas en
/// <c>AuditoriasExtraccionIa</c>, 0 con <c>DocumentoId</c> no nulo, y 0 filas
/// en <c>ExtraccionesIaCache</c> — no hay datos reales que contar (coherente
/// con que el tenant real solo tiene datos de demo, ver
/// hydra-carteras-vacias-en-produccion). <b>Un resultado vacío no es una
/// ausencia</b>: no se puede leer como "en la práctica siempre es uno a uno",
/// porque el instrumento no tenía nada que observar, no porque observara un
/// cero. La medición que sí es un dato, no una opinión, es estructural: la
/// clave única de <see cref="ExtraccionIaCache"/> es
/// (TenantId, HashSha256, TipoEsperado, VersionPipeline) — nada en ella
/// distingue un Documento de otro — y de los cuatro sitios que llaman a
/// <c>DocumentAIRouterService.ProcesarAsync</c>, tres (detección de campos al
/// subir, adjunto de correo/WhatsApp, detección de campos de Plantilla) no
/// conocen ningún <c>documentoId</c> por diseño: no es que se les olvide
/// pasarlo, es que se ejecutan antes de que el Documento exista o, en el caso
/// de Plantilla, nunca hay uno. El único llamador que sí lo conoce
/// (<c>VerificacionIaDocumentoService</c>, vía
/// <c>RouterExtraccionMetadatosDocumentoIaService</c>) usa como
/// <c>TipoEsperado</c> el nombre exacto del <c>TipoDocumento</c> — distinto
/// del literal genérico que usa el triage — así que ni siquiera coincide con
/// la entrada que el triage pudo haber creado antes: son claves de caché
/// distintas. La forma N-documentos-por-entrada que el comentario de
/// <see cref="ExtraccionIaCache"/> describe ("el mismo certificado para dos
/// trabajadores") no es una posibilidad remota que haya que descartar por
/// ausencia de fila en una base sin clientes reales: es la consecuencia
/// directa y comprobada por código de cómo se calcula la clave. Por eso la
/// entrada elegida es la <b>tabla de vínculo</b> (opción del § 6 que conserva
/// la deduplicación), no "una entrada por Documento" — esa segunda tiraría
/// justo el valor que la caché existe para dar, y § 8 del handoff excluye
/// tocar el mecanismo de deduplicación.
/// </summary>
public class ExtraccionIaCacheDocumentoPurgaTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly DateOnly _hoy = new(2031, 6, 1);
    private readonly Guid _usuarioAutorizador = Guid.NewGuid();

    private Guid _clienteId;
    private Guid _tipoDocumentoId;

    // Contenido sintético — nunca datos reales (HO-036-01 § 9): el JSON no
    // representa ningún documento real, solo ejercita que la fila viaja y se
    // borra donde toca.
    private static readonly string HashSintetico = new('a', ExtraccionIaCache.LongitudHash);
    private const string TipoEsperadoSintetico = "Certificado sintético de prueba";
    private const string JsonSintetico = """{"campo":"valor-sintetico-de-prueba"}""";

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var cliente = Empresa.CrearComoCliente("Vínculo Caché IA S.L.", "B12345674", false, null, null);
        contexto.Empresas.Add(cliente);

        var tipo = new TipoDocumento(
            "Certificado sintético REC-036", 12, aplicaVencimientoAutomatico: true, 1,
            AmbitoAplicacion.Cliente, requerido: RequisitoDocumental.Si);
        contexto.TiposDocumento.Add(tipo);
        await contexto.SaveChangesAsync();

        _clienteId = cliente.Id;
        _tipoDocumentoId = tipo.Id;
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    /// <summary>Criterio de aceptación § 13.3: borrar (anonimizar por retención) el único Documento vinculado se lleva su extracción.</summary>
    [Fact]
    public async Task Anonimizar_el_unico_Documento_vinculado_borra_tambien_su_entrada_de_cache()
    {
        Guid cacheId;
        Guid solicitudId;

        await using (var contexto = CrearContexto())
        {
            var documento = Documento.DeCliente(_clienteId, _tipoDocumentoId, _hoy.AddYears(-7), _hoy.AddYears(-6));
            contexto.Documentos.Add(documento);
            await contexto.SaveChangesAsync();

            var cache = ExtraccionIaCache.Crear(HashSintetico, TipoEsperadoSintetico, JsonSintetico);
            contexto.ExtraccionesIaCache.Add(cache);
            await contexto.SaveChangesAsync();
            cacheId = cache.Id;

            contexto.ExtraccionesIaCacheDocumentos.Add(ExtraccionIaCacheDocumento.Crear(cacheId, documento.Id));

            var solicitud = new SolicitudPurga(TipoDatoPurgable.Documentos, 1, _hoy.AddYears(-5));
            contexto.SolicitudesPurga.Add(solicitud);
            await contexto.SaveChangesAsync();

            solicitud.Programar(_hoy, _usuarioAutorizador, _hoy);
            await contexto.SaveChangesAsync();
            solicitudId = solicitud.Id;
        }

        await using (var contextoEjecucion = CrearContexto())
        {
            var servicio = CrearServicioEjecucion(contextoEjecucion);
            (await servicio.EjecutarAsync(solicitudId, _hoy)).Should().Be(1);
        }

        await using var verificacion = CrearContexto();

        (await verificacion.ExtraccionesIaCache.AnyAsync(c => c.Id == cacheId)).Should().BeFalse(
            "sin cachés huérfanas (DEC-34): su único Documento se anonimizó, la entrada no debe sobrevivirlo");
        (await verificacion.ExtraccionesIaCacheDocumentos.AnyAsync(v => v.ExtraccionIaCacheId == cacheId)).Should().BeFalse(
            "el vínculo tampoco debe sobrevivir a la entrada que enlazaba");
    }

    /// <summary>
    /// Criterio de aceptación § 13.4: una entrada compartida por dos
    /// Documentos NO desaparece al borrar (anonimizar) solo uno — el
    /// escenario central del § 6 del handoff ("el mismo certificado para dos
    /// trabajadores").
    /// </summary>
    [Fact]
    public async Task Anonimizar_un_Documento_no_borra_una_entrada_de_cache_que_otro_Documento_activo_sigue_usando()
    {
        Guid documentoViejoId, documentoRecienteId, cacheId, solicitudId;

        await using (var contexto = CrearContexto())
        {
            var documentoViejo = Documento.DeCliente(_clienteId, _tipoDocumentoId, _hoy.AddYears(-7), _hoy.AddYears(-6));
            // FechaEmision tiene que ser una fecha real ya pasada (la guarda
            // de Documento.Renovar compara contra DateTime.UtcNow real, no
            // contra el "_hoy" ficticio de 2031) — el vencimiento sí puede
            // proyectarse sobre ese "_hoy" ficticio, que es lo que decide si
            // la retención lo alcanza.
            var documentoReciente = Documento.DeCliente(_clienteId, _tipoDocumentoId, new DateOnly(2025, 6, 1), _hoy.AddYears(1));
            contexto.Documentos.AddRange(documentoViejo, documentoReciente);
            await contexto.SaveChangesAsync();
            documentoViejoId = documentoViejo.Id;
            documentoRecienteId = documentoReciente.Id;

            var cache = ExtraccionIaCache.Crear(HashSintetico, TipoEsperadoSintetico, JsonSintetico);
            contexto.ExtraccionesIaCache.Add(cache);
            await contexto.SaveChangesAsync();
            cacheId = cache.Id;

            // La MISMA entrada, referenciada por los dos Documentos: mismo
            // archivo sintético subido dos veces bajo el mismo tipo esperado.
            contexto.ExtraccionesIaCacheDocumentos.AddRange(
                ExtraccionIaCacheDocumento.Crear(cacheId, documentoViejoId),
                ExtraccionIaCacheDocumento.Crear(cacheId, documentoRecienteId));

            // Fecha de corte que solo alcanza al Documento viejo.
            var solicitud = new SolicitudPurga(TipoDatoPurgable.Documentos, 1, _hoy.AddYears(-5));
            contexto.SolicitudesPurga.Add(solicitud);
            await contexto.SaveChangesAsync();

            solicitud.Programar(_hoy, _usuarioAutorizador, _hoy);
            await contexto.SaveChangesAsync();
            solicitudId = solicitud.Id;
        }

        await using (var contextoEjecucion = CrearContexto())
        {
            var servicio = CrearServicioEjecucion(contextoEjecucion);
            (await servicio.EjecutarAsync(solicitudId, _hoy)).Should().Be(1, "solo el Documento viejo cumple el plazo de retención");
        }

        await using var verificacion = CrearContexto();

        var documentoViejo_ = await verificacion.Documentos.SingleAsync(d => d.Id == documentoViejoId);
        var documentoReciente_ = await verificacion.Documentos.SingleAsync(d => d.Id == documentoRecienteId);
        documentoViejo_.EstaAnonimizado.Should().BeTrue();
        documentoReciente_.EstaAnonimizado.Should().BeFalse("no ha cumplido su plazo de retención");

        (await verificacion.ExtraccionesIaCache.AnyAsync(c => c.Id == cacheId)).Should().BeTrue(
            "el Documento reciente todavía usa esta entrada: borrarla sería destruir el derivado de un Documento que sigue existiendo");

        var vinculosRestantes = await verificacion.ExtraccionesIaCacheDocumentos
            .Where(v => v.ExtraccionIaCacheId == cacheId)
            .ToListAsync();

        vinculosRestantes.Should().ContainSingle()
            .Which.DocumentoId.Should().Be(documentoRecienteId,
                "el vínculo del Documento anonimizado se retira; el del que sigue activo permanece");
    }

    /// <summary>Control positivo del caso anterior: sin vínculo alguno, la purga no debe tocar entradas de caché ajenas a los Documentos purgados.</summary>
    [Fact]
    public async Task Purgar_documentos_sin_vinculos_no_toca_entradas_de_cache_de_otros_tenants_o_sin_relacion()
    {
        Guid cacheAjenaId;
        Guid solicitudId;

        await using (var contexto = CrearContexto())
        {
            var documento = Documento.DeCliente(_clienteId, _tipoDocumentoId, _hoy.AddYears(-7), _hoy.AddYears(-6));
            contexto.Documentos.Add(documento);

            // Entrada de caché sin ningún vínculo — nunca debe tocarla una
            // purga que no la referencia.
            var cacheAjena = ExtraccionIaCache.Crear(HashSintetico, TipoEsperadoSintetico, JsonSintetico);
            contexto.ExtraccionesIaCache.Add(cacheAjena);
            await contexto.SaveChangesAsync();
            cacheAjenaId = cacheAjena.Id;

            var solicitud = new SolicitudPurga(TipoDatoPurgable.Documentos, 1, _hoy.AddYears(-5));
            contexto.SolicitudesPurga.Add(solicitud);
            await contexto.SaveChangesAsync();

            solicitud.Programar(_hoy, _usuarioAutorizador, _hoy);
            await contexto.SaveChangesAsync();
            solicitudId = solicitud.Id;
        }

        await using (var contextoEjecucion = CrearContexto())
        {
            var servicio = CrearServicioEjecucion(contextoEjecucion);
            (await servicio.EjecutarAsync(solicitudId, _hoy)).Should().Be(1);
        }

        await using var verificacion = CrearContexto();
        (await verificacion.ExtraccionesIaCache.AnyAsync(c => c.Id == cacheAjenaId)).Should().BeTrue(
            "ningún Documento la vinculaba: no es huérfana producida por esta purga");
    }

    private EjecucionPurgaService CrearServicioEjecucion(CaeManagerDbContext contexto) =>
        new(
            contexto, contexto, contexto,
            new SolicitudPurgaRepository(contexto),
            new ExtraccionIaCacheRepository(contexto),
            new AlmacenamientoArchivosFalso(),
            new TenantActualAmbiental { TenantId = _tenant },
            contexto,
            new AlertaOperativaFalsa(),
            NullLogger<EjecucionPurgaService>.Instance);

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class AlmacenamientoArchivosFalso : IFileStorageService
    {
        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class AlertaOperativaFalsa : IAlertaOperativa
    {
        public List<(string Mensaje, NivelAlertaOperativa Nivel)> Alertas { get; } = [];

        public void Emitir(string mensaje, NivelAlertaOperativa nivel) => Alertas.Add((mensaje, nivel));

        public void CapturarExcepcion(Exception excepcion)
        {
        }

        public void DejarMigaDePan(string mensaje)
        {
        }

        public IDisposable IniciarAmbitoDeCaptura() => new AmbitoVacio();

        private sealed class AmbitoVacio : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}
