using CaeManager.Application.Common;
using CaeManager.Application.Common.Archivos;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// Revisión de Codex del 2026-11-01 (OWASP API4:2023, ASVS 4.0.3 V12.1.1 y V5.2.1): el adjunto copiado a un
/// <c>byte[]</c> para analizarlo sigue reservado en el presupuesto de memoria de documentos mientras dura el análisis,
/// se devuelve al desechar, también si la copia falla, y sin hueco falla cerrado sin copiar nada.
/// </summary>
public class LecturaAlmacenadaParaAnalisisTests
{
    private const long Maximo = 100_000;
    private const int Longitud = 10_000;

    private static PresupuestoMemoriaDocumentos CrearPresupuesto(TimeSpan? espera = null) =>
        new(Options.Create(new OpcionesPresupuestoMemoriaDocumentos
        {
            MaximoBytes = Maximo,
            EsperaMaxima = espera ?? TimeSpan.FromMilliseconds(100)
        }), TimeProvider.System);

    [Fact]
    public async Task La_reserva_sigue_viva_con_el_contenido_copiado_y_se_devuelve_al_desechar()
    {
        var presupuesto = CrearPresupuesto();
        var almacen = new AlmacenFalso(new byte[Longitud]);

        var lectura = await LecturaAlmacenadaParaAnalisis.LeerAsync(almacen, presupuesto, "a.pdf");

        lectura.Contenido.Should().HaveCount(Longitud);
        presupuesto.Reservado.Should().Be(Longitud * LecturaAlmacenadaParaAnalisis.FactorMemoriaAnalisis,
            "el flujo ya se cerró, pero el byte[] que lo sustituye sigue reservado");
        almacen.FlujoAbierto.Should().BeFalse();

        lectura.Dispose();
        lectura.Dispose();

        presupuesto.Reservado.Should().Be(0, "desechar devuelve la reserva, una sola vez");
    }

    [Fact]
    public async Task Sin_hueco_falla_cerrado_sin_copiar_ni_dejar_nada_reservado_y_cierra_el_flujo()
    {
        var presupuesto = CrearPresupuesto(TimeSpan.FromMilliseconds(50));
        using var ocupado = await presupuesto.ReservarAsync(Maximo - 10);
        var almacen = new AlmacenFalso(new byte[Longitud]);

        var leer = async () => await LecturaAlmacenadaParaAnalisis.LeerAsync(almacen, presupuesto, "a.pdf");

        await leer.Should().ThrowExactlyAsync<PresupuestoMemoriaAgotadoException>();
        presupuesto.Reservado.Should().Be(Maximo - 10, "el rechazo no descuenta nada");
        presupuesto.EnEspera.Should().Be(0);
        almacen.FlujoAbierto.Should().BeFalse("el flujo se cierra aunque no se pueda reservar");
        almacen.FlujoCopiado.Should().BeFalse("sin reserva no se copia el archivo a un byte[]");
    }

    [Fact]
    public async Task Si_la_copia_falla_la_reserva_se_devuelve_y_el_flujo_se_cierra()
    {
        var presupuesto = CrearPresupuesto();
        var almacen = new AlmacenFalso(new byte[Longitud]) { FallarAlCopiar = true };

        var leer = async () => await LecturaAlmacenadaParaAnalisis.LeerAsync(almacen, presupuesto, "a.pdf");

        await leer.Should().ThrowExactlyAsync<IOException>();
        presupuesto.Reservado.Should().Be(0, "una excepción tras reservar no pierde la reserva");
        almacen.FlujoAbierto.Should().BeFalse();
    }

    [Fact]
    public async Task Un_flujo_sin_longitud_se_reserva_por_el_peor_caso()
    {
        var presupuesto = CrearPresupuesto();
        var almacen = new AlmacenFalso(new byte[Longitud]) { SinLongitud = true };

        using var lectura = await LecturaAlmacenadaParaAnalisis.LeerAsync(almacen, presupuesto, "a.pdf");

        lectura.Contenido.Should().HaveCount(Longitud);
        // El peor caso supera el presupuesto de la prueba: se recorta al presupuesto entero, nunca a cero.
        presupuesto.Reservado.Should().Be(Maximo);
    }

    [Fact]
    public async Task La_reserva_nunca_pide_mas_de_lo_que_cabe_junto_al_flujo_abierto()
    {
        // El flujo del almacenamiento retiene su claro (+4 KiB) mientras se copia: pedir cuatro veces el archivo lo
        // dejaría esperando a la reserva de su propio flujo.
        var presupuesto = CrearPresupuesto();
        const int grande = 80_000;
        var almacen = new AlmacenFalso(new byte[grande]);

        using var lectura = await LecturaAlmacenadaParaAnalisis.LeerAsync(almacen, presupuesto, "a.pdf");

        presupuesto.Reservado.Should().Be(Maximo - grande - 4 * 1024);
        lectura.Contenido.Should().HaveCount(grande);
    }

    [Fact]
    public async Task Un_archivo_inexistente_no_reserva_nada()
    {
        var presupuesto = CrearPresupuesto();
        var almacen = new AlmacenFalso(new byte[Longitud]) { NoExiste = true };

        var leer = async () => await LecturaAlmacenadaParaAnalisis.LeerAsync(almacen, presupuesto, "a.pdf");

        await leer.Should().ThrowExactlyAsync<FileNotFoundException>();
        presupuesto.Reservado.Should().Be(0);
    }

    private sealed class AlmacenFalso(byte[] contenido) : IFileStorageService
    {
        private FlujoFalso? _flujo;

        public bool FallarAlCopiar { get; init; }
        public bool SinLongitud { get; init; }
        public bool NoExiste { get; init; }
        public bool FlujoAbierto => _flujo is { Cerrado: false };
        public bool FlujoCopiado => _flujo is { Leido: true };

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default)
        {
            if (NoExiste) throw new FileNotFoundException("No existe.");
            _flujo = new FlujoFalso(contenido, SinLongitud, FallarAlCopiar);
            return Task.FromResult<Stream>(_flujo);
        }

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FlujoFalso(byte[] contenido, bool sinLongitud, bool fallarAlLeer) : MemoryStream(contenido, writable: false)
    {
        public bool Cerrado { get; private set; }
        public bool Leido { get; private set; }

        public override bool CanSeek => !sinLongitud && base.CanSeek;

        // MemoryStream copia sin pasar por Read: lo que importa a la prueba es que se pidió copiar.
        public override Task CopyToAsync(Stream destination, int bufferSize, CancellationToken cancellationToken)
        {
            Leido = true;
            if (fallarAlLeer) throw new IOException("Fallo de lectura de la prueba.");
            return base.CopyToAsync(destination, bufferSize, cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            Cerrado = true;
            base.Dispose(disposing);
        }
    }
}
