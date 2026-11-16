using CaeManager.Application.Common.Archivos;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// El criterio de borrado del barrido del outbox (issue #2123, decisión de 2026-11-16): solo lo que lleva más de 24 h
/// apuntado y ningún Documento ni versión de plantilla referencia; lo referenciado pierde la fila y conserva el archivo.
/// </summary>
public class BarridoArchivosPendientesDeReferenciaTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    private readonly ArchivosPendientesDeReferenciaFalso _pendientes = new();
    private readonly FileStorageServiceFalso _almacen = new();

    private BarridoArchivosPendientesDeReferencia Barrido() =>
        new(_pendientes, _almacen, new RelojFijo(Ahora), NullLogger<BarridoArchivosPendientesDeReferencia>.Instance);

    private async Task<string> GuardarAsync(string nombre)
    {
        using var flujo = new MemoryStream("%PDF-x"u8.ToArray());
        return await _almacen.GuardarAsync(flujo, nombre);
    }

    [Fact]
    public async Task Borra_el_huerfano_de_mas_de_24_h_y_su_fila()
    {
        var clave = await GuardarAsync("huerfano.pdf");
        _pendientes.Sembrar(clave, Ahora.UtcDateTime.AddHours(-25));

        var resultado = await Barrido().BarrerAsync(CancellationToken.None);

        resultado.Borrados.Should().Be(1);
        _almacen.ArchivosGuardados.Should().Be(0, "ningún Documento ni versión de plantilla lo referencia");
        _pendientes.Filas.Should().BeEmpty();
    }

    [Fact]
    public async Task Conserva_el_archivo_referenciado_y_solo_quita_su_fila()
    {
        var clave = await GuardarAsync("referenciado.pdf");
        _pendientes.Sembrar(clave, Ahora.UtcDateTime.AddHours(-48));
        _pendientes.Referenciadas.Add(clave);

        var resultado = await Barrido().BarrerAsync(CancellationToken.None);

        resultado.Should().Be(new ResultadoBarridoArchivosPendientes(0, 1, 0));
        _almacen.ArchivosGuardados.Should().Be(1, "un commit que sí confirmó el Documento conserva su PDF");
        _pendientes.Filas.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task No_toca_lo_apuntado_hace_menos_de_24_h(bool laConsultaLoDevuelve)
    {
        // La consulta del repositorio ya filtra por la hora; con laConsultaLoDevuelve se prueba además que la ventana
        // la impone el propio servicio.
        _pendientes.DevolverTambienNoVencidas = laConsultaLoDevuelve;
        var clave = await GuardarAsync("reciente.pdf");
        _pendientes.Sembrar(clave, Ahora.UtcDateTime.AddHours(-23));

        var resultado = await Barrido().BarrerAsync(CancellationToken.None);

        resultado.Should().Be(new ResultadoBarridoArchivosPendientes(0, 0, 0));
        _almacen.ArchivosGuardados.Should().Be(1, "un alta de hace 23 h puede no haberse confirmado aún");
        _pendientes.Filas.Should().ContainSingle();
    }

    [Fact]
    public async Task Un_fallo_al_borrar_conserva_la_fila_para_la_siguiente_pasada_y_sigue_con_las_demas()
    {
        var primera = await GuardarAsync("a.pdf");
        var segunda = await GuardarAsync("b.pdf");
        _pendientes.Sembrar(primera, Ahora.UtcDateTime.AddHours(-30));
        _pendientes.Sembrar(segunda, Ahora.UtcDateTime.AddHours(-29));
        _pendientes.Referenciadas.Add(segunda);
        _almacen.ExcepcionAlEliminar = new IOException("disco");

        var resultado = await Barrido().BarrerAsync(CancellationToken.None);

        resultado.Should().Be(new ResultadoBarridoArchivosPendientes(0, 1, 1));
        _pendientes.Filas.Should().ContainSingle().Which.Clave.Should().Be(primera);
    }

    private sealed class RelojFijo(DateTimeOffset ahora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => ahora;
    }
}
