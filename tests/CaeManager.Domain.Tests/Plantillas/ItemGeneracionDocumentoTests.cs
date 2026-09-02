using CaeManager.Domain.Plantillas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Plantillas;

public class ItemGeneracionDocumentoTests
{
    [Fact]
    public void Constructor_nace_pendiente()
    {
        var item = new ItemGeneracionDocumento(Guid.NewGuid(), Guid.NewGuid());

        item.Estado.Should().Be(EstadoItemGeneracion.Pendiente);
        item.DocumentoGeneradoId.Should().BeNull();
        item.Error.Should().BeNull();
    }

    [Fact]
    public void Constructor_rechaza_lote_vacio()
    {
        var accion = () => new ItemGeneracionDocumento(Guid.Empty, Guid.NewGuid());

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_rechaza_trabajador_vacio()
    {
        var accion = () => new ItemGeneracionDocumento(Guid.NewGuid(), Guid.Empty);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MarcarCompletado_asigna_el_documento_generado()
    {
        var item = new ItemGeneracionDocumento(Guid.NewGuid(), Guid.NewGuid());
        var documentoGeneradoId = Guid.NewGuid();

        item.MarcarCompletado(documentoGeneradoId);

        item.Estado.Should().Be(EstadoItemGeneracion.Completado);
        item.DocumentoGeneradoId.Should().Be(documentoGeneradoId);
    }

    [Fact]
    public void MarcarFallido_guarda_el_mensaje_de_error()
    {
        var item = new ItemGeneracionDocumento(Guid.NewGuid(), Guid.NewGuid());

        item.MarcarFallido("No encontramos al trabajador.");

        item.Estado.Should().Be(EstadoItemGeneracion.Fallido);
        item.Error.Should().Be("No encontramos al trabajador.");
    }

    [Fact]
    public void MarcarFallido_con_mensaje_vacio_usa_un_mensaje_por_defecto()
    {
        var item = new ItemGeneracionDocumento(Guid.NewGuid(), Guid.NewGuid());

        item.MarcarFallido("  ");

        item.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void Rechaza_marcar_un_item_ya_procesado()
    {
        var item = new ItemGeneracionDocumento(Guid.NewGuid(), Guid.NewGuid());
        item.MarcarCompletado(Guid.NewGuid());

        var accion = () => item.MarcarFallido("error tardío");

        accion.Should().Throw<InvalidOperationException>();
    }
    /// <summary>
    /// DEC-5 (propietario, 2026-09-02): el ítem con avisos SÍ tiene documento —
    /// lo que cambia respecto de uno limpio es que queda señalado y nombra los
    /// campos que resolvieron vacíos.
    /// </summary>
    [Fact]
    public void MarcarCompletadoConAvisos_deja_documento_estado_propio_y_nombra_los_campos()
    {
        var item = new ItemGeneracionDocumento(Guid.NewGuid(), Guid.NewGuid());
        var documentoGeneradoId = Guid.NewGuid();

        item.MarcarCompletadoConAvisos(documentoGeneradoId, ["Mutua", "Número de póliza"]);

        item.Estado.Should().Be(EstadoItemGeneracion.CompletadoConAvisos);
        item.DocumentoGeneradoId.Should().Be(documentoGeneradoId);
        item.Error.Should().Be("Campos obligatorios sin dato: Mutua, Número de póliza.");
    }

    [Fact]
    public void MarcarCompletadoConAvisos_sin_ningun_campo_es_un_error_de_programacion()
    {
        var item = new ItemGeneracionDocumento(Guid.NewGuid(), Guid.NewGuid());

        var accion = () => item.MarcarCompletadoConAvisos(Guid.NewGuid(), []);

        accion.Should().Throw<ArgumentException>();
    }

    /// <summary>
    /// Con muchas etiquetas largas el texto no cabe en LongitudMaximaError.
    /// Cortar por caracteres partiría la última etiqueta por la mitad: el aviso
    /// dejaría de nombrar un campo para nombrar medio. Se nombra lo que cabe y
    /// se cuenta el resto.
    /// </summary>
    [Fact]
    public void MarcarCompletadoConAvisos_con_demasiadas_etiquetas_nombra_las_que_caben_y_cuenta_el_resto()
    {
        var item = new ItemGeneracionDocumento(Guid.NewGuid(), Guid.NewGuid());
        var campos = Enumerable.Range(1, 12).Select(i => new string((char)('a' + i), 100)).ToList();

        item.MarcarCompletadoConAvisos(Guid.NewGuid(), campos);

        item.Error!.Length.Should().BeLessThanOrEqualTo(ItemGeneracionDocumento.LongitudMaximaError);
        item.Error.Should().MatchRegex(@" y \d+ más\.$");
        item.Error.Should().Contain(campos[0], "la primera etiqueta se nombra entera, no a medias");
    }
}
