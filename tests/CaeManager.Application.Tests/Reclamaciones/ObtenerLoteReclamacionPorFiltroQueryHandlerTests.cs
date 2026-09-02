using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacionPorFiltro;
using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Reclamaciones;

/// <summary>
/// Cubre el dispatcher que traduce FiltroLoteDocumental (selector tipo ×
/// ámbito, DEC-7) a ObtenerLoteReclamacionQuery — con MediatorFalso para no
/// necesitar Postgres: el join real ya lo cubre ReclamacionDocumentalTests
/// (IntegrationTests), esto solo verifica la traducción del filtro.
/// </summary>
public class ObtenerLoteReclamacionPorFiltroQueryHandlerTests
{
    [Fact]
    public async Task Ambito_Trabajador_traduce_EntidadId_a_TrabajadorId_y_delega_en_ObtenerLoteReclamacionQuery()
    {
        var trabajadorId = Guid.NewGuid();
        var tipoDocumentoId = Guid.NewGuid();
        var clienteId = Guid.NewGuid();

        var mediator = new MediatorFalso
        {
            Respuesta = (IReadOnlyList<LoteReclamacionClienteDto>)
            [
                new LoteReclamacionClienteDto(clienteId, "Cliente de prueba", null, [])
            ]
        };
        var handler = new ObtenerLoteReclamacionPorFiltroQueryHandler(mediator);

        var filtro = new FiltroLoteDocumental(AmbitoAplicacion.Trabajador, [tipoDocumentoId], trabajadorId);
        var lotes = await handler.Handle(new ObtenerLoteReclamacionPorFiltroQuery(filtro), CancellationToken.None);

        var enviado = mediator.Enviados.Should().ContainSingle().Which.Should().BeOfType<ObtenerLoteReclamacionQuery>().Subject;
        enviado.TrabajadorId.Should().Be(trabajadorId);
        enviado.TipoDocumentoIds.Should().BeEquivalentTo([tipoDocumentoId]);
        enviado.ClienteId.Should().BeNull("el selector filtra por Trabajador, no por titular — EnviarReclamacionCommand sigue siendo uno por Cliente resuelto");
        enviado.CentroId.Should().BeNull();

        var lote = lotes.Should().ContainSingle().Which;
        lote.TitularId.Should().Be(clienteId);
        lote.TitularNombre.Should().Be("Cliente de prueba");
        lote.Ambito.Should().Be(AmbitoAplicacion.Trabajador);
    }

    [Fact]
    public async Task TipoDocumentoIds_vacio_se_traduce_a_null_para_no_filtrar_por_tipo()
    {
        var mediator = new MediatorFalso { Respuesta = (IReadOnlyList<LoteReclamacionClienteDto>)[] };
        var handler = new ObtenerLoteReclamacionPorFiltroQueryHandler(mediator);

        var filtro = new FiltroLoteDocumental(AmbitoAplicacion.Trabajador, [], EntidadId: null);
        await handler.Handle(new ObtenerLoteReclamacionPorFiltroQuery(filtro), CancellationToken.None);

        var enviado = mediator.Enviados.Should().ContainSingle().Which.Should().BeOfType<ObtenerLoteReclamacionQuery>().Subject;
        enviado.TipoDocumentoIds.Should().BeNull("vacío significa \"todos los tipos\", no \"ningún tipo\"");
        enviado.TrabajadorId.Should().BeNull("EntidadId null significa \"todos los trabajadores visibles\"");
    }

    [Theory]
    [InlineData(AmbitoAplicacion.Cliente)]
    [InlineData(AmbitoAplicacion.Empresa)]
    [InlineData(AmbitoAplicacion.Vehiculo)]
    [InlineData(AmbitoAplicacion.Proyecto)]
    public async Task Ambito_sin_camino_de_reclamacion_construido_falla_alto_en_vez_de_devolver_lista_vacia(AmbitoAplicacion ambito)
    {
        // Regresión deliberada del hueco declarado (DEC-7): un ámbito que
        // SelectorLoteDocumental no debería haber ofrecido nunca no puede
        // devolver silenciosamente "nada pendiente" — eso se confundiría con
        // ausencia real de datos.
        var handler = new ObtenerLoteReclamacionPorFiltroQueryHandler(new MediatorFalso());

        var filtro = new FiltroLoteDocumental(ambito, [], null);
        var accion = () => handler.Handle(new ObtenerLoteReclamacionPorFiltroQuery(filtro), CancellationToken.None);

        await accion.Should().ThrowAsync<NotSupportedException>();
    }
}
