using System.Text;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.FileStorage;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Web.Tests.HerramientasPdf;

/// <summary>
/// <see cref="DiskFileStorageService.AbrirHastaAsync"/> sobre disco real (carpeta temporal, claves de Data Protection
/// efímeras): lo que Unir PDF necesita de él es rechazar por el tamaño almacenado antes de reservar o descifrar nada, y no
/// esperar hueco en el presupuesto mientras la unión retiene otras reservas. No hay proyecto de tests unitarios de
/// Infrastructure; se aloja aquí junto a los de Unir PDF, que ya referencian ese ensamblado.
/// </summary>
public sealed class AlmacenEnDiscoAbrirHastaTests : IDisposable
{
    private readonly string _carpeta = Path.Combine(Path.GetTempPath(), "talveg-abrir-hasta-" + Guid.NewGuid().ToString("N"));
    private readonly TenantMutable _tenant = new() { TenantId = Guid.NewGuid() };
    private readonly PresupuestoQueCuenta _presupuesto = new(new PresupuestoMemoriaDocumentos(
        Options.Create(new OpcionesPresupuestoMemoriaDocumentos { MaximoBytes = 512L * 1024 * 1024, EsperaMaxima = TimeSpan.FromMilliseconds(100) }),
        TimeProvider.System));
    private readonly DiskFileStorageService _almacen;
    private readonly byte[] _contenido = Encoding.ASCII.GetBytes("%PDF-1.4 ").Concat(Enumerable.Repeat((byte)'x', 20_000)).ToArray();

    public AlmacenEnDiscoAbrirHastaTests()
    {
        Directory.CreateDirectory(_carpeta);
        _almacen = new DiskFileStorageService(
            Options.Create(new DiskFileStorageServiceOptions { Ruta = Path.Combine(_carpeta, "docs") }),
            new Entorno(_carpeta), _tenant,
            DataProtectionProvider.Create(new DirectoryInfo(Path.Combine(_carpeta, "claves"))),
            new AlertaNula(), NullLogger<DiskFileStorageService>.Instance, _presupuesto);
    }

    public void Dispose()
    {
        try { Directory.Delete(_carpeta, recursive: true); }
        catch (IOException) { /* temporal: si Windows aún lo retiene, no es del test */ }
    }

    private async Task<string> GuardarAsync()
    {
        await using var origen = new MemoryStream(_contenido);
        var id = await _almacen.GuardarAsync(origen, "doc.pdf");
        _presupuesto.Reiniciar();
        return id;
    }

    private bool PresupuestoLibre()
    {
        if (!_presupuesto.Interno.TryReservar(_presupuesto.Maximo, out var todo))
            return false;
        todo.Dispose();
        return true;
    }

    [Fact]
    public async Task Un_archivo_que_pasa_del_tope_se_rechaza_por_su_tamano_antes_de_reservar_memoria()
    {
        var id = await GuardarAsync();

        var abrir = async () => await _almacen.AbrirHastaAsync(id, maximoBytes: 1000);

        await abrir.Should().ThrowAsync<ArchivoAlmacenadoDemasiadoGrandeException>();
        _presupuesto.Reservas.Should().Be(0, "el tamaño en disco decide antes de reservar, leer o descifrar");
        PresupuestoLibre().Should().BeTrue();
    }

    [Fact]
    public async Task Un_archivo_que_cabe_se_entrega_integro_sin_esperar_hueco_y_devuelve_la_reserva_al_cerrarse()
    {
        var id = await GuardarAsync();

        await using (var flujo = await _almacen.AbrirHastaAsync(id, maximoBytes: 1024 * 1024))
        {
            using var copia = new MemoryStream();
            await flujo.CopyToAsync(copia);
            copia.ToArray().Should().Equal(_contenido);
            PresupuestoLibre().Should().BeFalse("mientras el flujo está abierto retiene su reserva");
        }

        _presupuesto.ReservasSinEspera.Should().Be(1, "con tope no se espera hueco reteniendo otras reservas");
        _presupuesto.ReservasConEspera.Should().Be(0);
        PresupuestoLibre().Should().BeTrue();
    }

    [Fact]
    public async Task Sin_hueco_en_el_presupuesto_falla_en_cerrado_sin_esperar()
    {
        var id = await GuardarAsync();
        _presupuesto.NoCabeNada = true;

        var abrir = async () => await _almacen.AbrirHastaAsync(id, maximoBytes: 1024 * 1024);

        await abrir.Should().ThrowAsync<PresupuestoMemoriaAgotadoException>();
        _presupuesto.ReservasConEspera.Should().Be(0);
    }

    [Fact]
    public async Task Abrir_sin_tope_sigue_esperando_hueco_como_antes()
    {
        var id = await GuardarAsync();

        await using (await _almacen.AbrirAsync(id))
        {
        }

        _presupuesto.ReservasConEspera.Should().Be(1);
    }

    [Fact]
    public async Task Un_archivo_de_otro_Tenant_responde_como_si_no_existiera()
    {
        var id = await GuardarAsync();
        _tenant.TenantId = Guid.NewGuid();

        var abrir = async () => await _almacen.AbrirHastaAsync(id, maximoBytes: 1024 * 1024);

        await abrir.Should().ThrowAsync<FileNotFoundException>();
    }

    private sealed class TenantMutable : ITenantActual
    {
        public Guid? TenantId { get; set; }
    }

    private sealed class Entorno(string raiz) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = raiz;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class AlertaNula : IAlertaOperativa
    {
        public void Emitir(string mensaje, NivelAlertaOperativa nivel) { }
        public void CapturarExcepcion(Exception excepcion) { }
        public void DejarMigaDePan(string mensaje) { }
        public IDisposable IniciarAmbitoDeCaptura() => new Nada();

        private sealed class Nada : IDisposable
        {
            public void Dispose() { }
        }
    }

    /// <summary>El presupuesto real, contando cuántas reservas se piden y de qué forma; con <see cref="NoCabeNada"/> rechaza las inmediatas.</summary>
    private sealed class PresupuestoQueCuenta(PresupuestoMemoriaDocumentos interno) : IPresupuestoMemoriaDocumentos
    {
        public PresupuestoMemoriaDocumentos Interno { get; } = interno;
        public bool NoCabeNada { get; set; }
        public int ReservasConEspera { get; private set; }
        public int ReservasSinEspera { get; private set; }
        public int Reservas => ReservasConEspera + ReservasSinEspera;

        public long Maximo => Interno.Maximo;

        public void Reiniciar()
        {
            ReservasConEspera = 0;
            ReservasSinEspera = 0;
        }

        public ValueTask<ReservaMemoriaDocumentos> ReservarAsync(long bytes, CancellationToken cancellationToken = default)
        {
            ReservasConEspera++;
            return Interno.ReservarAsync(bytes, cancellationToken);
        }

        public bool TryReservar(long bytes, out ReservaMemoriaDocumentos reserva)
        {
            ReservasSinEspera++;
            if (NoCabeNada)
            {
                reserva = null!;
                return false;
            }

            return Interno.TryReservar(bytes, out reserva);
        }
    }
}
