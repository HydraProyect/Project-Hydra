using CaeManager.Application.Common;
using CaeManager.Web.Documentos;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Hallazgo de la auditoría del 2026-11-02 (OWASP API4:2023, ASVS 4.0.3 V12.1.1): dos pantallas leían un archivo
/// subido a memoria sin reservar del presupuesto compartido entre Tenants. El helper reserva ANTES de leer, lee con
/// tope en lo declarado y deja reservado solo lo que el contenido retiene.
/// </summary>
public class ArchivoSubidoReservadoTests
{
    private readonly PresupuestoMemoriaDocumentos _presupuesto = new(
        Options.Create(new OpcionesPresupuestoMemoriaDocumentos { MaximoBytes = 1_000_000, EsperaMaxima = TimeSpan.FromMilliseconds(50) }),
        TimeProvider.System);

    private static byte[] Bytes(int longitud) => Enumerable.Range(0, longitud).Select(i => (byte)(i % 251)).ToArray();

    [Fact]
    public async Task Reserva_size_por_el_factor_antes_de_abrir_el_flujo_y_deja_solo_el_tamano_al_terminar()
    {
        var contenido = Bytes(1_000);
        long reservadoAlAbrir = -1;
        var archivo = new ArchivoNavegadorFalso("a.pdf", contenido) { AlAbrir = () => reservadoAlAbrir = _presupuesto.Reservado };

        using var lectura = await ArchivoSubidoReservado.LeerAsync(archivo, _presupuesto);

        reservadoAlAbrir.Should().Be(1_000L * ArchivoSubidoReservado.FactorMemoriaLectura, "la reserva existe antes de leer un solo byte");
        lectura.Contenido.Should().Equal(contenido);
        _presupuesto.Reservado.Should().Be(1_000, "pasado el pico de la lectura solo queda el búfer retenido");
        lectura.Reserva.Bytes.Should().Be(1_000);
    }

    [Fact]
    public async Task Dispose_devuelve_la_reserva_y_es_idempotente()
    {
        var lectura = await ArchivoSubidoReservado.LeerAsync(new ArchivoNavegadorFalso("a.pdf", Bytes(500)), _presupuesto);

        lectura.Dispose();
        lectura.Dispose();

        _presupuesto.Reservado.Should().Be(0);
    }

    [Fact]
    public async Task Sin_hueco_lanza_el_aviso_sin_abrir_el_flujo_ni_tocar_lo_reservado()
    {
        using var ocupado = await _presupuesto.ReservarAsync(_presupuesto.Maximo - 100);
        var archivo = new ArchivoNavegadorFalso("a.pdf", Bytes(1_000));

        var accion = () => ArchivoSubidoReservado.LeerAsync(archivo, _presupuesto);

        await accion.Should().ThrowAsync<PresupuestoMemoriaAgotadoException>();
        archivo.Aperturas.Should().Be(0, "sin reserva no se lee");
        _presupuesto.Reservado.Should().Be(ocupado.Bytes);
    }

    [Fact]
    public async Task Un_flujo_que_trae_mas_de_lo_declarado_se_rechaza_y_devuelve_la_reserva()
    {
        var archivo = new ArchivoNavegadorFalso("a.pdf", Bytes(1_000)) { TamanoDeclarado = 900 };

        var accion = () => ArchivoSubidoReservado.LeerAsync(archivo, _presupuesto);

        await accion.Should().ThrowAsync<IOException>();
        _presupuesto.Reservado.Should().Be(0);
    }

    [Fact]
    public async Task Un_flujo_que_trae_menos_de_lo_declarado_se_rechaza_y_devuelve_la_reserva()
    {
        var archivo = new ArchivoNavegadorFalso("a.pdf", Bytes(900)) { TamanoDeclarado = 1_000 };

        var accion = () => ArchivoSubidoReservado.LeerAsync(archivo, _presupuesto);

        await accion.Should().ThrowAsync<IOException>();
        _presupuesto.Reservado.Should().Be(0);
    }

    [Fact]
    public async Task Un_archivo_por_encima_del_maximo_se_rechaza_sin_reservar_ni_abrir_el_flujo()
    {
        var archivo = new ArchivoNavegadorFalso("a.pdf", Bytes(10)) { TamanoDeclarado = LimitesArchivoSubido.TamanoMaximoBytes + 1L };

        var accion = () => ArchivoSubidoReservado.LeerAsync(archivo, _presupuesto);

        await accion.Should().ThrowAsync<IOException>();
        archivo.Aperturas.Should().Be(0);
        _presupuesto.Reservado.Should().Be(0);
    }

    [Fact]
    public async Task Cancelar_mientras_se_lee_devuelve_la_reserva()
    {
        using var cancelacion = new CancellationTokenSource();
        var retener = new TaskCompletionSource();
        var archivo = new ArchivoNavegadorFalso("a.pdf", Bytes(1_000)) { Retener = retener };

        var lectura = ArchivoSubidoReservado.LeerAsync(archivo, _presupuesto, cancelacion.Token);
        _presupuesto.Reservado.Should().Be(1_000L * ArchivoSubidoReservado.FactorMemoriaLectura);
        await cancelacion.CancelAsync();

        await ((Func<Task>)(() => lectura)).Should().ThrowAsync<OperationCanceledException>();
        _presupuesto.Reservado.Should().Be(0);
    }
}
