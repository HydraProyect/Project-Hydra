using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Documentos;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.FileStorage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;

namespace CaeManager.Web.Tests;

public sealed class ReparacionAdjuntosTemporalesTests : IDisposable
{
    private readonly string raiz = Path.Combine(Path.GetTempPath(), $"hydra-reparacion-{Guid.NewGuid():N}");
    private const string Temporal = "tenant/documento.PDF.tmp-0123456789abcdef0123456789abcdef";
    private const string Definitivo = "tenant/documento.pdf";

    public ReparacionAdjuntosTemporalesTests() => Directory.CreateDirectory(Path.Combine(raiz, "tenant"));

    [Fact]
    public async Task DetectaReferenciadoEIgnoraHuerfano()
    {
        await using var db = await CrearContexto(Temporal);
        await Escribir(Temporal);
        await Escribir("tenant/huerfano.pdf.tmp-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        var resultado = await new ReparacionAdjuntosTemporales(db).EjecutarAsync(raiz);

        var encontrado = Assert.Single(resultado);
        Assert.Equal(new AdjuntoTemporalDefinitivo(Temporal, Definitivo, true), encontrado);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(raiz, "tenant")).Length);
    }

    [Fact]
    public async Task SimularPorDefectoNoCambiaNiDiscoNiBaseDeDatos()
    {
        await using var db = await CrearContexto(Temporal);
        await Escribir(Temporal);

        await new ReparacionAdjuntosTemporales(db).EjecutarAsync(raiz);

        Assert.True(File.Exists(Ruta(Temporal)));
        Assert.False(File.Exists(Ruta(Definitivo)));
        Assert.Single(Directory.GetFiles(raiz, "*", SearchOption.AllDirectories));
        db.ChangeTracker.Clear();
        Assert.Equal(Temporal, (await db.Adjuntos.SingleAsync()).ArchivoUrl);
    }

    [Fact]
    public async Task AplicarRenombraConservaContenidoYEsIdempotente()
    {
        await using var db = await CrearContexto(Temporal);
        await Escribir(Temporal);
        var servicio = new ReparacionAdjuntosTemporales(db);

        Assert.Single(await servicio.EjecutarAsync(raiz, aplicar: true));
        Assert.Empty(await servicio.EjecutarAsync(raiz, aplicar: true));

        Assert.False(File.Exists(Ruta(Temporal)));
        Assert.Equal("contenido intacto", await File.ReadAllTextAsync(Ruta(Definitivo)));
        db.ChangeTracker.Clear();
        Assert.Equal(Definitivo, (await db.Adjuntos.SingleAsync()).ArchivoUrl);
    }

    [Fact]
    public async Task SiRenombrarFallaNoTocaBaseNiSobrescribeDestino()
    {
        await using var db = await CrearContexto(Temporal);
        await Escribir(Temporal);
        await File.WriteAllTextAsync(Ruta(Definitivo), "no sobrescribir");

        await Assert.ThrowsAsync<IOException>(() => new ReparacionAdjuntosTemporales(db).EjecutarAsync(raiz, true));

        db.ChangeTracker.Clear();
        Assert.Equal(Temporal, (await db.Adjuntos.SingleAsync()).ArchivoUrl);
        Assert.Equal("contenido intacto", await File.ReadAllTextAsync(Ruta(Temporal)));
        Assert.Equal("no sobrescribir", await File.ReadAllTextAsync(Ruta(Definitivo)));
    }

    [Fact]
    public async Task SiBaseFallaRevierteRenombradoSinBorrar()
    {
        await using var db = await CrearContexto(Temporal);
        await Escribir(Temporal);
        db.FallarGuardado = true;

        await Assert.ThrowsAsync<DbUpdateException>(() => new ReparacionAdjuntosTemporales(db).EjecutarAsync(raiz, true));

        db.ChangeTracker.Clear();
        Assert.Equal(Temporal, (await db.Adjuntos.SingleAsync()).ArchivoUrl);
        Assert.Equal("contenido intacto", await File.ReadAllTextAsync(Ruta(Temporal)));
        Assert.False(File.Exists(Ruta(Definitivo)));
    }

    [Fact]
    public async Task ActualizaTodasLasReferenciasAlMismoFichero()
    {
        await using var db = await CrearContexto(Temporal, Temporal);
        await Escribir(Temporal);

        Assert.Single(await new ReparacionAdjuntosTemporales(db).EjecutarAsync(raiz, true));

        db.ChangeTracker.Clear();
        Assert.All(await db.Adjuntos.ToListAsync(), a => Assert.Equal(Definitivo, a.ArchivoUrl));
    }

    [Fact]
    public async Task RecuperaExtensionDelNombreOriginalCuandoElNombreEnDiscoLaPerdio()
    {
        var referencia = "tenant/identificador.tmp-0123456789abcdef0123456789abcdef";
        await using var db = await CrearContexto(referencia);
        db.Entry(await db.Adjuntos.SingleAsync()).Property("NombreArchivo").CurrentValue = "informe.PDF.tmp-1";
        using (AmbitoTenantExplicito.Establecer((await db.Adjuntos.SingleAsync()).TenantId))
            await db.SaveChangesAsync();
        await Escribir(referencia);

        Assert.Equal("tenant/identificador.pdf", Assert.Single(
            await new ReparacionAdjuntosTemporales(db).EjecutarAsync(raiz, true)).RutaNueva);
    }

    [Fact]
    public async Task NoReparaUnaReferenciaEliminadaSinNingunaVigente()
    {
        await using var db = await CrearContexto();
        var eliminada = Documento.DeTrabajador(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 10, 26), VigenciaDocumento.NoCaduca, Temporal);
        eliminada.MarcarComoEliminado(Guid.NewGuid());
        db.Documentos.Add(eliminada);
        db.Entry(eliminada).Property("TenantId").CurrentValue = Guid.NewGuid();
        await db.SaveChangesAsync();
        await Escribir(Temporal);

        Assert.Empty(await new ReparacionAdjuntosTemporales(db).EjecutarAsync(raiz, true));
        Assert.True(File.Exists(Ruta(Temporal)));
    }

    [Fact]
    public async Task ActualizaReferenciaEliminadaSiComparteBlobConUnaVigente()
    {
        await using var db = await CrearContexto(Temporal);
        var eliminada = Documento.DeTrabajador(Guid.NewGuid(), Guid.NewGuid(), new DateOnly(2026, 10, 26), VigenciaDocumento.NoCaduca, Temporal);
        eliminada.MarcarComoEliminado(Guid.NewGuid());
        db.Documentos.Add(eliminada);
        db.Entry(eliminada).Property("TenantId").CurrentValue = Guid.NewGuid();
        await db.SaveChangesAsync();
        await Escribir(Temporal);

        Assert.Single(await new ReparacionAdjuntosTemporales(db).EjecutarAsync(raiz, true));

        db.ChangeTracker.Clear();
        Assert.Equal(Definitivo, (await db.Documentos.SingleAsync()).ArchivoUrl);
        Assert.Equal(Definitivo, (await db.Adjuntos.SingleAsync()).ArchivoUrl);
    }

    [Fact]
    public async Task SiCommitFallaDespuesDeConfirmarseConservaAmbasRutas()
    {
        await using var db = await CrearContexto(Temporal);
        await Escribir(Temporal);
        db.FallarConfirmacion = true;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ReparacionAdjuntosTemporales(db).EjecutarAsync(raiz, true));

        Assert.Contains("incierta", error.Message);
        db.ChangeTracker.Clear();
        Assert.Equal(Definitivo, (await db.Adjuntos.SingleAsync()).ArchivoUrl);
        Assert.Equal("contenido intacto", await File.ReadAllTextAsync(Ruta(Temporal)));
        Assert.Equal("contenido intacto", await File.ReadAllTextAsync(Ruta(Definitivo)));
    }

    [Fact]
    public async Task RechazaReferenciasFueraDelAlmacen()
    {
        await using var db = await CrearContexto("../ajeno.pdf.tmp-0123456789abcdef0123456789abcdef");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ReparacionAdjuntosTemporales(db).EjecutarAsync(raiz, true));
        Assert.Equal("../ajeno.pdf.tmp-0123456789abcdef0123456789abcdef", (await db.Adjuntos.SingleAsync()).ArchivoUrl);
    }

    [Theory]
    [InlineData("tenant/documento.pdf.tmp-1")]
    [InlineData("tenant/documento.pdf.tmp-gggggggggggggggggggggggggggggggg")]
    [InlineData("tenant/documento.pdf.tmp-0123456789abcdef0123456789abcdef.extra")]
    [InlineData("tenant/documento.pdf")]
    public async Task IgnoraNombresQueNoSonElSufijoExacto(string referencia)
    {
        await using var db = await CrearContexto(referencia);
        await Escribir(referencia);
        Assert.Empty(await new ReparacionAdjuntosTemporales(db).EjecutarAsync(raiz, true));
        Assert.True(File.Exists(Ruta(referencia)));
    }

    [Fact]
    public async Task InformaReferenciaAusenteYAplicarFallaSinActualizar()
    {
        await using var db = await CrearContexto(Temporal);
        var servicio = new ReparacionAdjuntosTemporales(db);
        Assert.False(Assert.Single(await servicio.EjecutarAsync(raiz)).ExisteEnDisco);
        await Assert.ThrowsAsync<FileNotFoundException>(() => servicio.EjecutarAsync(raiz, true));
        db.ChangeTracker.Clear();
        Assert.Equal(Temporal, (await db.Adjuntos.SingleAsync()).ArchivoUrl);
    }

    [Theory]
    [InlineData("tenant/documento.PDF.TMP-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA.tmp-0123456789abcdef0123456789abcdef", "tenant/documento.pdf")]
    [InlineData("tenant/documento.tmp-0123456789abcdef0123456789abcdef", "tenant/documento")]
    public async Task UsaElMismoCriterioDeExtensionQueGuardar(string referencia, string destino)
    {
        await using var db = await CrearContexto(referencia);
        await Escribir(referencia);
        Assert.Equal(destino, Assert.Single(await new ReparacionAdjuntosTemporales(db).EjecutarAsync(raiz, true)).RutaNueva);
        Assert.True(File.Exists(Ruta(destino)));
    }

    [Fact]
    public async Task SinExtensionRecuperableNoLaInventaYAvisaConElIdSinNombres()
    {
        var referencia = "tenant/0123456789abcdef0123456789abcdef.tmp-0123456789abcdef0123456789abcdef";
        await using var db = await CrearContexto(referencia);
        var id = (await db.Adjuntos.SingleAsync()).Id;
        db.ChangeTracker.Clear();
        await Escribir(referencia);
        var avisos = new RegistroAvisos();

        var reparado = Assert.Single(await new ReparacionAdjuntosTemporales(db, avisos).EjecutarAsync(raiz, true));

        Assert.Equal("tenant/0123456789abcdef0123456789abcdef", reparado.RutaNueva);
        Assert.False(reparado.ExtensionRecuperada);
        var aviso = Assert.Single(avisos.Mensajes);
        Assert.Contains(nameof(AdjuntoMensaje), aviso);
        Assert.Contains(id.ToString(), aviso);
        Assert.DoesNotContain("tenant/", aviso);
        Assert.DoesNotContain("documento", aviso);
    }

    [Fact]
    public async Task ConExtensionRecuperadaNoAvisa()
    {
        await using var db = await CrearContexto(Temporal);
        await Escribir(Temporal);
        var avisos = new RegistroAvisos();

        Assert.True(Assert.Single(await new ReparacionAdjuntosTemporales(db, avisos).EjecutarAsync(raiz)).ExtensionRecuperada);
        Assert.Empty(avisos.Mensajes);
    }

    [Fact]
    public void ComandoExigeAplicarExplicito()
    {
        Assert.False(ReparacionAdjuntosTemporales.Aplicar([ReparacionAdjuntosTemporales.Argumento]));
        Assert.False(ReparacionAdjuntosTemporales.Aplicar(["--simular"]));
        Assert.True(ReparacionAdjuntosTemporales.Aplicar(["--aplicar"]));
        Assert.Throws<ArgumentException>(() => ReparacionAdjuntosTemporales.Aplicar(["--simular", "--aplicar"]));
    }

    private string Ruta(string referencia) => Path.Combine(raiz, referencia);
    private Task Escribir(string referencia) => File.WriteAllTextAsync(Ruta(referencia), "contenido intacto");

    private static async Task<ContextoPrueba> CrearContexto(params string[] referencias)
    {
        var conexion = new SqliteConnection("Data Source=:memory:");
        await conexion.OpenAsync();
        var db = new ContextoPrueba(new DbContextOptionsBuilder<ContextoPrueba>()
            .UseSqlite(conexion, contextOwnsConnection: true).Options);
        await db.Database.EnsureCreatedAsync();
        foreach (var referencia in referencias)
        {
            var adjunto = new AdjuntoMensaje(Guid.NewGuid(), "documento", "application/pdf", 10, referencia);
            db.Adjuntos.Add(adjunto);
            db.Entry(adjunto).Property("TenantId").CurrentValue = Guid.NewGuid();
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return db;
    }

    private sealed class ContextoPrueba(DbContextOptions<ContextoPrueba> opciones) : DbContext(opciones)
    {
        public DbSet<AdjuntoMensaje> Adjuntos => Set<AdjuntoMensaje>();
        public DbSet<Documento> Documentos => Set<Documento>();
        public bool FallarGuardado { get; set; }
        public bool FallarConfirmacion { get; set; }
        protected override void OnConfiguring(DbContextOptionsBuilder opciones) => opciones.AddInterceptors(new FalloConfirmacion(this));
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            foreach (var entrada in ChangeTracker.Entries<AdjuntoMensaje>().Where(e => e.State == EntityState.Modified))
                Assert.Equal(entrada.Entity.TenantId, AmbitoTenantExplicito.TenantIdActual);
            return FallarGuardado ? throw new DbUpdateException("Fallo de prueba") : base.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class RegistroAvisos : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Mensajes { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning) Mensajes.Add(formatter(state, exception));
        }
    }

    private sealed class FalloConfirmacion(ContextoPrueba db) : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default) => db.FallarConfirmacion
                ? throw new InvalidOperationException("Fallo después de COMMIT") : Task.CompletedTask;
    }

    public void Dispose() => Directory.Delete(raiz, recursive: true);
}
