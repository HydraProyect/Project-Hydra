using CaeManager.Application.Alertas;
using CaeManager.Application.Comunicaciones.Queries.ObtenerBorradorPedirPrioridad;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;

namespace CaeManager.Application.Tests.Comunicaciones;

public class ObtenerBorradorPedirPrioridadQueryHandlerTests
{
    [Theory]
    [InlineData("<script>alert(\"x\")</script> & 'Norte'", "&lt;script&gt;alert(&quot;x&quot;)&lt;/script&gt; &amp; &#39;Norte&#39;")]
    // El nombre se guarda tal cual lo escribió el usuario: un literal «&amp;» se codifica una vez, sin decodificarlo antes.
    [InlineData("R&amp;D", "R&amp;amp;D")]
    public async Task Codifica_los_datos_del_html_una_sola_vez_y_conserva_el_asunto(string nombre, string codificado)
    {
        var empresa = new Empresa(nombre, "B12345674");
        var centro = new Centro(Guid.NewGuid(), empresa.Id, nombre);
        var trabajador = Trabajador.DeEmpresa(empresa.Id, nombre, "Prueba", "12345678Z");
        var centros = new CentrosQueryContextFalso();
        centros.ListaCentros.Add(centro);
        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaEmpresas.Add(empresa);
        var trabajadores = new TrabajadoresQueryContextFalso();
        trabajadores.ListaTrabajadores.Add(trabajador);
        var asignaciones = new AsignacionesQueryContextFalso();
        asignaciones.ListaAsignaciones.Add(new Asignacion(trabajador.Id, centro.Id, DiaDeNegocio.Hoy()));
        var faltantes = new FaltantesFalso(new DocumentoFaltanteDto(
            trabajador.Id, trabajador.NombreCompleto, centro.Id, centro.Nombre, Guid.NewGuid(), nombre));
        var handler = new ObtenerBorradorPedirPrioridadQueryHandler(
            centros, asignaciones, trabajadores, empresas, new TiposDocumentoQueryContextFalso(),
            new IntegracionesQueryContextFalso(), new ComunicacionesQueryContextFalso(), faltantes, new AlcanceDatosServiceFalso());

        var resultado = await handler.Handle(new ObtenerBorradorPedirPrioridadQuery(centro.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Asunto.Should().Be($"Solicitud de validación prioritaria — {nombre}");
        resultado.Valor.CuerpoHtml.Should().Contain($"<strong>{codificado}</strong>");
        resultado.Valor.CuerpoHtml.Should().Contain($"<p><strong>{codificado}</strong></p><ul>");
        resultado.Valor.CuerpoHtml.Should().Contain($"<li>{codificado} Prueba: {codificado}</li>");
        resultado.Valor.CuerpoHtml.Should().NotContain("<script>");
    }

    private sealed class FaltantesFalso(DocumentoFaltanteDto faltante) : IDocumentosFaltantesService
    {
        public Task<IReadOnlyList<DocumentoFaltanteDto>> CalcularAsync(
            IReadOnlyList<ParejaTrabajadorCentro> parejas, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DocumentoFaltanteDto>>([faltante]);
    }
}
