using CaeManager.Application.Common;
using CaeManager.Application.HerramientasPdf;
using CaeManager.Infrastructure.FileStorage;
using CaeManager.Infrastructure.MultiTenancy;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests.HerramientasPdf;

/// <summary>
/// Guardar en TALVEG de Dividir PDF retiene su reserva mientras el almacenamiento pide otra, con espera, para cifrar cada
/// tramo. La pantalla solo empieza si las dos caben juntas (<see cref="LimitesDivisionPdf.MemoriaGuardarTramos"/>), y eso
/// vale lo que valga <see cref="LimitesDivisionPdf.MemoriaCifradoTramo"/>: aquí se mide contra lo que reserva de verdad
/// <see cref="DiskFileStorageService"/> al guardar un tramo del tamaño máximo del alta.
/// </summary>
public sealed class ReservaDelCifradoDeUnTramoTests : IDisposable
{
    private readonly string _ruta = Path.Combine(Path.GetTempPath(), $"talveg-cifrado-tramo-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_ruta)) Directory.Delete(_ruta, recursive: true);
    }

    [Fact]
    public async Task Lo_que_el_almacenamiento_reserva_para_cifrar_un_tramo_maximo_no_pasa_de_lo_que_Dividir_cuenta()
    {
        var presupuesto = new PresupuestoQueMideElPico(new PresupuestoMemoriaDocumentos(
            Options.Create(new OpcionesPresupuestoMemoriaDocumentos { MaximoBytes = 512L * 1024 * 1024 }), TimeProvider.System));
        var almacen = new DiskFileStorageService(
            Options.Create(new DiskFileStorageServiceOptions { Ruta = _ruta }), null!,
            new TenantActualAmbiental { TenantId = Guid.NewGuid() }, new EphemeralDataProtectionProvider(), new AlertaInerte(),
            NullLogger<DiskFileStorageService>.Instance, presupuesto);

        // Como el tramo que guarda Dividir: un MemoryStream de solo lectura sobre los bytes recibidos del proceso aparte.
        using var tramo = new MemoryStream(new byte[LimitesArchivoSubido.TamanoMaximoBytes], writable: false);
        await almacen.GuardarAsync(tramo, "paquete-pag-1-1.pdf");

        presupuesto.Pico.Should().BeGreaterThan(0, "si no mide nada, el test no prueba la cota");
        presupuesto.Pico.Should().BeLessThanOrEqualTo(LimitesDivisionPdf.MemoriaCifradoTramo,
            "si el almacenamiento reserva más, la comprobación de Guardar dejaría pasar un lote cuyo cifrado espera a la reserva de la pantalla");
    }

    /// <summary>Lo más que las reservas del almacenamiento llegan a retener a la vez.</summary>
    private sealed class PresupuestoQueMideElPico(PresupuestoMemoriaDocumentos interno) : IPresupuestoMemoriaDocumentos
    {
        public long Pico { get; private set; }

        public long Maximo => interno.Maximo;

        public async ValueTask<ReservaMemoriaDocumentos> ReservarAsync(long bytes, CancellationToken cancellationToken = default)
        {
            var reserva = await interno.ReservarAsync(bytes, cancellationToken);
            Pico = Math.Max(Pico, interno.Reservado);
            return reserva;
        }

        public bool TryReservar(long bytes, out ReservaMemoriaDocumentos reserva)
        {
            var reservada = interno.TryReservar(bytes, out reserva);
            Pico = Math.Max(Pico, interno.Reservado);
            return reservada;
        }
    }

    private sealed class AlertaInerte : IAlertaOperativa
    {
        public void Emitir(string mensaje, NivelAlertaOperativa nivel) { }
        public void CapturarExcepcion(Exception excepcion) { }
        public void DejarMigaDePan(string mensaje) { }
        public IDisposable IniciarAmbitoDeCaptura() => new SinAmbito();

        private sealed class SinAmbito : IDisposable
        {
            public void Dispose() { }
        }
    }
}
