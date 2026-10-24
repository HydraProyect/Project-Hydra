using CaeManager.Application.Common;
using CaeManager.Application.Subcontratas;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratas;
using CaeManager.Application.Tests.Centros;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Proyectos;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.RelacionesEmpresariales;
using FluentAssertions;

namespace CaeManager.Application.Tests.Subcontratas;

public class ObtenerSubcontratasContratantesTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Solo_anade_empresas_propias_visibles_con_relacion_vigente_sin_cambiar_el_universo(bool visible)
    {
        var db = new EmpresasQueryContextFalso();
        var subcontrata = Empresa.CrearComoSubcontrata("Subcontratada", null, "Gestionada");
        var empresa = new Empresa("Contratante", null);
        var cerrada = new Empresa("Relación cerrada", null);
        var contraparte = Empresa.CrearComoCliente("Cliente empresarial", "B12345674", false, null, null);
        db.ListaEmpresas.AddRange([subcontrata, empresa, cerrada, contraparte]);
        var anterior = RelacionEmpresarial.Crear(subcontrata.Id, cerrada.Id, DateTime.UtcNow.AddDays(-2));
        anterior.Cerrar(DateTime.UtcNow.AddDays(-1));
        db.ListaRelacionesEmpresariales.AddRange([
            RelacionEmpresarial.Crear(subcontrata.Id, empresa.Id, DateTime.UtcNow), anterior,
            RelacionEmpresarial.Crear(subcontrata.Id, contraparte.Id, DateTime.UtcNow)]);
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false,
            subcontrataIdsVisibles: [subcontrata.Id], empresaIdsVisibles: visible ? [empresa.Id, cerrada.Id] : []);
        var handler = new ObtenerSubcontratasQueryHandler(
            db, alcance, new SinDocumentos(), new TrabajadoresQueryContextFalso(), new CalculoDocumentalFalso(),
            new RequisitosObligatoriosSinIncumplidos());

        var resultado = await handler.Handle(new(null, ConRecuentosPorEstado: true), default);

        resultado.TotalElementos.Should().Be(1);
        var fila = resultado.Elementos.Should().ContainSingle().Subject;
        fila.Id.Should().Be(subcontrata.Id);
        fila.Contratantes.Select(e => e.Id).Should().Equal(visible ? [empresa.Id] : Array.Empty<Guid>());
    }

    private sealed class SinDocumentos : ICalculoEstadoSubcontrataService
    {
        public Task<IReadOnlyDictionary<Guid, ResumenEstadoSubcontrata>> CalcularResumenAsync(IReadOnlyList<Guid> ids, CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<Guid, ResumenEstadoSubcontrata>>(new Dictionary<Guid, ResumenEstadoSubcontrata>());
        public Task<IReadOnlyDictionary<Guid, FraccionCumplimiento>> CalcularCumplimientoAsync(IReadOnlyList<Guid> ids, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<IncidenciaSubcontrataDto>>> CalcularAsync(IReadOnlyList<Guid> ids, CancellationToken ct) => throw new NotSupportedException();
        public IQueryable<Guid> SinVoluntariosInactivos(IReadOnlyList<Guid> trabajadorIds) => trabajadorIds.AsQueryable();
    }
}
