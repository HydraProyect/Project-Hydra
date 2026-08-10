using CaeManager.Application.Comunicaciones.Deteccion;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Comunicaciones;

/// <summary>
/// Decisión pura de ResolverRepeticiones (ronda de reducción de ruido en Comunicaciones) — un
/// ítem de gestión es repetición cuando existe una ReclamacionDocumentalDocumento formal para el
/// mismo (Trabajador, TipoDocumento), nunca por sospecha de la IA sin ese respaldo.
/// </summary>
public class ClasificacionRuidoMensajeServiceTests
{
    [Fact]
    public void Un_item_sin_evidencia_formal_no_se_marca_como_repeticion()
    {
        var trabajadorId = Guid.NewGuid();
        var tipoDocumentoId = Guid.NewGuid();
        var detalles = new[] { new ClasificacionRuidoMensajeService.DetalleParaClasificarDto(Guid.NewGuid(), trabajadorId, tipoDocumentoId) };

        var resultado = ClasificacionRuidoMensajeService.ResolverRepeticiones(detalles, documentosReclamados: []);

        resultado.Should().BeEmpty();
    }

    [Fact]
    public void Un_item_con_reclamacion_formal_del_mismo_par_se_marca_como_repeticion()
    {
        var trabajadorId = Guid.NewGuid();
        var tipoDocumentoId = Guid.NewGuid();
        var detalleId = Guid.NewGuid();
        var reclamacionDocumentoId = Guid.NewGuid();

        var detalles = new[] { new ClasificacionRuidoMensajeService.DetalleParaClasificarDto(detalleId, trabajadorId, tipoDocumentoId) };
        var documentosReclamados = new[]
        {
            new ClasificacionRuidoMensajeService.DocumentoReclamadoDto(reclamacionDocumentoId, trabajadorId, tipoDocumentoId),
        };

        var resultado = ClasificacionRuidoMensajeService.ResolverRepeticiones(detalles, documentosReclamados);

        resultado.Should().ContainSingle();
        resultado[detalleId].Should().Be(reclamacionDocumentoId);
    }

    [Fact]
    public void Un_mensaje_con_un_item_repetido_y_uno_nuevo_solo_marca_el_repetido()
    {
        var trabajadorA = Guid.NewGuid();
        var trabajadorC = Guid.NewGuid();
        var tipoDocumentoId = Guid.NewGuid();
        var detalleAId = Guid.NewGuid();
        var detalleCId = Guid.NewGuid();
        var reclamacionDocumentoId = Guid.NewGuid();

        // A ya se reclamó formalmente; C está recién detectado, sin ninguna reclamación previa —
        // el caso real que motivó esta fase: notificaciones en bloque mezclan ambos.
        var detalles = new[]
        {
            new ClasificacionRuidoMensajeService.DetalleParaClasificarDto(detalleAId, trabajadorA, tipoDocumentoId),
            new ClasificacionRuidoMensajeService.DetalleParaClasificarDto(detalleCId, trabajadorC, tipoDocumentoId),
        };
        var documentosReclamados = new[]
        {
            new ClasificacionRuidoMensajeService.DocumentoReclamadoDto(reclamacionDocumentoId, trabajadorA, tipoDocumentoId),
        };

        var resultado = ClasificacionRuidoMensajeService.ResolverRepeticiones(detalles, documentosReclamados);

        resultado.Should().ContainSingle();
        resultado.Should().ContainKey(detalleAId);
        resultado.Should().NotContainKey(detalleCId);
    }

    [Fact]
    public void Un_item_sin_trabajador_o_tipo_documento_resuelto_nunca_correlaciona()
    {
        var tipoDocumentoId = Guid.NewGuid();
        var detalles = new[] { new ClasificacionRuidoMensajeService.DetalleParaClasificarDto(Guid.NewGuid(), null, tipoDocumentoId) };
        var documentosReclamados = new[]
        {
            new ClasificacionRuidoMensajeService.DocumentoReclamadoDto(Guid.NewGuid(), null, tipoDocumentoId),
        };

        var resultado = ClasificacionRuidoMensajeService.ResolverRepeticiones(detalles, documentosReclamados);

        resultado.Should().BeEmpty();
    }

    [Fact]
    public void Un_par_distinto_de_trabajador_y_tipo_documento_no_correlaciona()
    {
        var trabajadorId = Guid.NewGuid();
        var tipoDocumentoA = Guid.NewGuid();
        var tipoDocumentoB = Guid.NewGuid();
        var detalles = new[] { new ClasificacionRuidoMensajeService.DetalleParaClasificarDto(Guid.NewGuid(), trabajadorId, tipoDocumentoA) };
        var documentosReclamados = new[]
        {
            new ClasificacionRuidoMensajeService.DocumentoReclamadoDto(Guid.NewGuid(), trabajadorId, tipoDocumentoB),
        };

        var resultado = ClasificacionRuidoMensajeService.ResolverRepeticiones(detalles, documentosReclamados);

        resultado.Should().BeEmpty();
    }
}
