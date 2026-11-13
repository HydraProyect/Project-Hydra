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

    private DiskFileStorageService Almacen(ITenantActual tenant, IPresupuestoMemoriaDocumentos presupuesto) =>
        new(Options.Create(new DiskFileStorageServiceOptions { Ruta = _ruta }), null!,
            tenant, new EphemeralDataProtectionProvider(), new AlertaInerte(),
            NullLogger<DiskFileStorageService>.Instance, presupuesto);

    [Fact]
    public async Task Lo_que_el_almacenamiento_reserva_para_cifrar_y_verificar_un_tramo_maximo_no_pasa_de_lo_que_Dividir_cuenta()
    {
        var presupuesto = new PresupuestoQueMideElPico(new PresupuestoMemoriaDocumentos(
            Options.Create(new OpcionesPresupuestoMemoriaDocumentos { MaximoBytes = 512L * 1024 * 1024 }), TimeProvider.System));
        var proteccion = new EphemeralDataProtectionProvider();
        var tenant = new TenantActualAmbiental { TenantId = Guid.NewGuid() };
        var almacen = new DiskFileStorageService(
            Options.Create(new DiskFileStorageServiceOptions { Ruta = _ruta }), null!, tenant, proteccion, new AlertaInerte(),
            NullLogger<DiskFileStorageService>.Instance, presupuesto);

        // Como el tramo que guarda Dividir: un MemoryStream de solo lectura sobre los bytes recibidos del proceso aparte.
        using var tramo = new MemoryStream(new byte[LimitesArchivoSubido.TamanoMaximoBytes], writable: false);
        var identificador = await almacen.GuardarAsync(tramo, "paquete-pag-1-1.pdf");
        var picoAlGuardar = presupuesto.Pico;

        // El alta lo vuelve a abrir para verificarlo mientras la pantalla sigue reteniendo su reserva.
        presupuesto.Reiniciar();
        await using (await almacen.AbrirAsync(identificador))
        {
        }

        picoAlGuardar.Should().BeGreaterThan(0, "si no mide nada, el test no prueba la cota");
        presupuesto.Pico.Should().BeGreaterThan(0);
        new[] { picoAlGuardar, presupuesto.Pico }.Should().OnlyContain(pico => pico <= LimitesDivisionPdf.MemoriaCifradoTramo,
            "si el almacenamiento reserva más, la comprobación de Guardar dejaría pasar un lote cuya segunda reserva espera a la de la pantalla");
    }

    [Fact]
    public async Task El_almacenamiento_lee_el_Tenant_activo_antes_de_su_primera_espera()
    {
        // Guardar en TALVEG comprueba el Tenant esperado y llama a GuardarAsync sin ninguna espera entre medias: eso solo
        // protege si el almacenamiento lee el Tenant al entrar. Aquí el Tenant cambia en cuanto el guardado espera su reserva.
        var esperado = Guid.NewGuid();
        var tenant = new TenantActualAmbiental { TenantId = esperado };
        var presupuesto = new PresupuestoQueMideElPico(new PresupuestoMemoriaDocumentos(
            Options.Create(new OpcionesPresupuestoMemoriaDocumentos { MaximoBytes = 512L * 1024 * 1024 }), TimeProvider.System))
        {
            AlReservar = () => tenant.TenantId = Guid.NewGuid(),
        };

        using var tramo = new MemoryStream(new byte[1024], writable: false);
        var identificador = await Almacen(tenant, presupuesto).GuardarAsync(tramo, "paquete-pag-1-1.pdf");

        identificador.Should().StartWith(esperado.ToString("N") + "/", "el archivo se escribe con el Tenant que había al entrar");
    }

    /// <summary>Lo más que las reservas del almacenamiento llegan a retener a la vez.</summary>
    private sealed class PresupuestoQueMideElPico(PresupuestoMemoriaDocumentos interno) : IPresupuestoMemoriaDocumentos
    {
        public long Pico { get; private set; }

        /// <summary>Lo que pasa mientras se espera una reserva.</summary>
        public Action? AlReservar { get; init; }

        public long Maximo => interno.Maximo;

        public void Reiniciar() => Pico = 0;

        public async ValueTask<ReservaMemoriaDocumentos> ReservarAsync(long bytes, CancellationToken cancellationToken = default)
        {
            AlReservar?.Invoke();
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
