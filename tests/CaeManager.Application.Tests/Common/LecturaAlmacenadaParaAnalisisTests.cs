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
    public async Task Un_flujo_sin_longitud_se_reserva_por_el_peor_caso_y_si_no_cabe_se_rechaza()
    {
        var almacen = new AlmacenFalso(new byte[Longitud]) { SinLongitud = true };
        var peorCaso = (long)LimitesArchivoSubido.TamanoMaximoBytes * LecturaAlmacenadaParaAnalisis.FactorMemoriaAnalisis;

        // Presupuesto que admite el peor caso: se reserva entero, no cero.
        var amplio = new PresupuestoMemoriaDocumentos(
            Options.Create(new OpcionesPresupuestoMemoriaDocumentos { MaximoBytes = peorCaso * 2, EsperaMaxima = TimeSpan.FromMilliseconds(100) }),
            TimeProvider.System);
        using (var lectura = await LecturaAlmacenadaParaAnalisis.LeerAsync(almacen, amplio, "a.pdf"))
        {
            lectura.Contenido.Should().HaveCount(Longitud);
            amplio.Reservado.Should().Be(peorCaso);
        }

        // Presupuesto menor que el peor caso: no se recorta, se rechaza.
        var pequeno = CrearPresupuesto();
        var leer = async () => await LecturaAlmacenadaParaAnalisis.LeerAsync(almacen, pequeno, "a.pdf");
        await leer.Should().ThrowExactlyAsync<PresupuestoMemoriaAgotadoException>();
        pequeno.Reservado.Should().Be(0);
    }

    [Fact]
    public async Task Un_flujo_sin_longitud_que_entrega_mas_que_el_maximo_reservado_se_corta_y_no_deja_nada_reservado()
    {
        // Reservó cuatro veces el máximo supuesto (10 MiB) y el flujo entrega un byte más: la copia se corta en vez de
        // crecer en memoria por encima de lo que el presupuesto cuenta.
        var peorCaso = (long)LimitesArchivoSubido.TamanoMaximoBytes * LecturaAlmacenadaParaAnalisis.FactorMemoriaAnalisis;
        var amplio = new PresupuestoMemoriaDocumentos(
            Options.Create(new OpcionesPresupuestoMemoriaDocumentos { MaximoBytes = peorCaso * 2, EsperaMaxima = TimeSpan.FromMilliseconds(100) }),
            TimeProvider.System);
        var almacen = new AlmacenFalso(new byte[LimitesArchivoSubido.TamanoMaximoBytes + 1]) { SinLongitud = true };

        var leer = async () => await LecturaAlmacenadaParaAnalisis.LeerAsync(almacen, amplio, "a.pdf");

        await leer.Should().ThrowExactlyAsync<PresupuestoMemoriaAgotadoException>();
        amplio.Reservado.Should().Be(0, "el corte devuelve la reserva");
        almacen.FlujoAbierto.Should().BeFalse();
    }

    [Fact]
    public async Task Un_flujo_posicionable_que_declara_menos_de_lo_que_entrega_se_corta_y_no_deja_nada_reservado()
    {
        var presupuesto = CrearPresupuesto();
        var almacen = new AlmacenFalso(new byte[2 * Longitud]) { LongitudDeclarada = Longitud };

        var leer = async () => await LecturaAlmacenadaParaAnalisis.LeerAsync(almacen, presupuesto, "a.pdf");

        await leer.Should().ThrowExactlyAsync<PresupuestoMemoriaAgotadoException>();
        presupuesto.Reservado.Should().Be(0);
        almacen.FlujoAbierto.Should().BeFalse();
    }

    [Fact]
    public async Task Un_flujo_sin_longitud_que_entrega_exactamente_el_maximo_reservado_se_copia_entero()
    {
        var peorCaso = (long)LimitesArchivoSubido.TamanoMaximoBytes * LecturaAlmacenadaParaAnalisis.FactorMemoriaAnalisis;
        var amplio = new PresupuestoMemoriaDocumentos(
            Options.Create(new OpcionesPresupuestoMemoriaDocumentos { MaximoBytes = peorCaso * 2, EsperaMaxima = TimeSpan.FromMilliseconds(100) }),
            TimeProvider.System);
        var almacen = new AlmacenFalso(new byte[LimitesArchivoSubido.TamanoMaximoBytes]) { SinLongitud = true };

        using var lectura = await LecturaAlmacenadaParaAnalisis.LeerAsync(almacen, amplio, "a.pdf");

        lectura.Contenido.Should().HaveCount(LimitesArchivoSubido.TamanoMaximoBytes);
        amplio.Reservado.Should().Be(peorCaso);
    }

    [Fact]
    public async Task Si_el_analisis_no_cabe_junto_al_flujo_abierto_se_rechaza_en_vez_de_recortarse()
    {
        // 4 x 80.000 no cabe en 100.000: recortar la reserva a lo que queda dejaría seguir con una cobertura menor que
        // la estimación. Se rechaza, sin esperar y sin dejar nada reservado.
        var presupuesto = CrearPresupuesto(TimeSpan.FromMinutes(1));
        var almacen = new AlmacenFalso(new byte[80_000]);

        var leer = async () => await LecturaAlmacenadaParaAnalisis.LeerAsync(almacen, presupuesto, "a.pdf");

        await leer.Should().ThrowExactlyAsync<PresupuestoMemoriaAgotadoException>();
        presupuesto.Reservado.Should().Be(0);
        presupuesto.EnEspera.Should().Be(0, "no espera reteniendo la reserva de su flujo");
        almacen.FlujoAbierto.Should().BeFalse();
        almacen.FlujoCopiado.Should().BeFalse();
    }

    [Fact]
    public async Task Sin_hueco_no_espera_aunque_la_espera_maxima_sea_larga()
    {
        var presupuesto = CrearPresupuesto(TimeSpan.FromMinutes(1));
        using var ocupado = await presupuesto.ReservarAsync(Maximo - 10);
        var almacen = new AlmacenFalso(new byte[Longitud]);

        var reloj = System.Diagnostics.Stopwatch.StartNew();
        var leer = async () => await LecturaAlmacenadaParaAnalisis.LeerAsync(almacen, presupuesto, "a.pdf");

        await leer.Should().ThrowExactlyAsync<PresupuestoMemoriaAgotadoException>();
        reloj.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5));
        presupuesto.Reservado.Should().Be(Maximo - 10);
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
        public long? LongitudDeclarada { get; init; }
        public bool FlujoAbierto => _flujo is { Cerrado: false };
        public bool FlujoCopiado => _flujo is { Leido: true };

        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default)
        {
            if (NoExiste) throw new FileNotFoundException("No existe.");
            _flujo = new FlujoFalso(contenido, SinLongitud, FallarAlCopiar, LongitudDeclarada);
            return Task.FromResult<Stream>(_flujo);
        }

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FlujoFalso(byte[] contenido, bool sinLongitud, bool fallarAlLeer, long? longitudDeclarada)
        : MemoryStream(contenido, writable: false)
    {
        public bool Cerrado { get; private set; }
        public bool Leido { get; private set; }

        // Un flujo posicionable que declara menos de lo que entrega: la reserva se hizo por lo declarado.
        public override long Length => longitudDeclarada ?? base.Length;

        public override bool CanSeek => !sinLongitud && base.CanSeek;

        // La lectura acotada pasa por ReadAsync: ahí se anota que se pidió leer y se simula el fallo.
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Leido = true;
            if (fallarAlLeer) throw new IOException("Fallo de lectura de la prueba.");
            return base.ReadAsync(buffer, cancellationToken);
        }
        protected override void Dispose(bool disposing)
        {
            Cerrado = true;
            base.Dispose(disposing);
        }
    }
}
