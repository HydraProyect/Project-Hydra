using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Application.Tests.Reportes;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Application.Vehiculos;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Vehiculos;
using FluentAssertions;

namespace CaeManager.Application.Tests.Documentos;

public class FiltroGrupoDocumentalQueryTests
{
    private sealed class VehiculosVacios : IVehiculosQueryContext
    {
        public IQueryable<Vehiculo> Vehiculos => new TestAsyncQueryable<Vehiculo>(Array.Empty<Vehiculo>().AsQueryable());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Filtra_antes_de_paginar_y_los_recuentos_no_incluyen_documentos_ajenos(bool sinCartera)
    {
        var documentos = new DocumentosQueryContextFalso();
        var trabajadores = new TrabajadoresQueryContextFalso();
        var tipos = new TiposDocumentoQueryContextFalso();
        var configuracion = new ConfiguracionQueryContextFalso();
        configuracion.ListaParametrosSistema.Add(new ParametroSistema(30, 7));
        var propio = Trabajador.DeEmpresa(Guid.NewGuid(), "Ana", "García", "77189989B");
        var ajeno = Trabajador.DeEmpresa(Guid.NewGuid(), "Eva", "Pérez", "12345678Z");
        trabajadores.ListaTrabajadores.AddRange([propio, ajeno]);
        var formacion = new TipoDocumento("Formación PRL de convenio", null, false, 1, AmbitoAplicacion.Trabajador);
        var archivado = new TipoDocumento("Formación EPIs", null, false, 2, AmbitoAplicacion.Trabajador);
        var epi = new TipoDocumento("Entrega de EPI", null, false, 3, AmbitoAplicacion.Trabajador);
        tipos.ListaTiposDocumento.AddRange([formacion, archivado, epi]);
        foreach (var tipo in tipos.ListaTiposDocumento)
            documentos.ListaDocumentos.Add(Documento.DeTrabajador(propio.Id, tipo.Id, new DateOnly(2026, 10, 1), VigenciaDocumento.NoCaduca));
        documentos.ListaDocumentos.Add(Documento.DeTrabajador(ajeno.Id, formacion.Id, new DateOnly(2026, 10, 1), VigenciaDocumento.NoCaduca));
        var handler = new ObtenerDocumentosQueryHandler(configuracion, documentos, new EmpresasQueryContextFalso(),
            new ProyectosQueryContextFalso(), tipos, trabajadores, new VehiculosVacios(),
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: sinCartera ? [] : [propio.Id]),
            new CentrosQueryContextFalso(), new ProveedoresPlataformaCaeQueryContextFalso());
        var resultado = await handler.Handle(new ObtenerDocumentosQuery(null, null, null,
            TamanoPagina: 1, Grupos: ["Formación PRL"], ConRecuentosPorGrupo: true, ConRecuentosPorEstado: true), default);
        resultado.TotalElementos.Should().Be(sinCartera ? 0 : 2);
        resultado.Elementos.Should().HaveCount(sinCartera ? 0 : 1);
        if (!sinCartera) resultado.Elementos.Should().OnlyContain(d => d.PropietarioNombre == "Ana García" && d.Grupo == "Formación PRL");
        resultado.RecuentosGruposDocumentales!.Sum(g => g.Documentos).Should().Be(sinCartera ? 0 : 3);
        resultado.RecuentosGruposDocumentales!.Sum(g => g.Archivados).Should().Be(sinCartera ? 0 : 1);
        resultado.RecuentosPorEstado!.Values.Sum().Should().Be(sinCartera ? 0 : 2);
        var vacio = await handler.Handle(new ObtenerDocumentosQuery(null, null, null, Grupos: ["No existe"]), default);
        vacio.TotalElementos.Should().Be(0);
        var todos = await handler.Handle(new ObtenerDocumentosQuery(null, null, null, Grupos: []), default);
        todos.TotalElementos.Should().Be(sinCartera ? 0 : 3);
    }
}
