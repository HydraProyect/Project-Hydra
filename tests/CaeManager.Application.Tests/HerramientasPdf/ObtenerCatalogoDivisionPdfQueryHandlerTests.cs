using CaeManager.Application.HerramientasPdf;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Application.Vehiculos;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Vehiculos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.HerramientasPdf;

public class ObtenerCatalogoDivisionPdfQueryHandlerTests
{
    private static readonly DateTime Ahora = new(2026, 1, 15, 9, 0, 0, DateTimeKind.Utc);

    private readonly EmpresasQueryContextFalso _empresas = new();
    private readonly TrabajadoresQueryContextFalso _trabajadores = new();
    private readonly VehiculosFalsos _vehiculos = new();
    private readonly TiposDocumentoQueryContextFalso _tipos = new();
    private readonly Empresa _propia = new("Montajes Norte S.L.");
    private readonly Empresa _otraPropia = new("Otra Empresa S.L.");
    private readonly Empresa _subcontrata = Empresa.CrearComoSubcontrata("Pintura Sur S.L.", null, "Gestionada");
    private readonly Empresa _subcontrataDeOtra = Empresa.CrearComoSubcontrata("Ajena S.L.", null, "Gestionada");

    public ObtenerCatalogoDivisionPdfQueryHandlerTests()
    {
        _empresas.ListaEmpresas.AddRange([_propia, _otraPropia, _subcontrata, _subcontrataDeOtra]);
        _empresas.ListaRelacionesEmpresariales.Add(RelacionEmpresarial.Crear(_subcontrata.Id, _propia.Id, Ahora));
        _empresas.ListaRelacionesEmpresariales.Add(RelacionEmpresarial.Crear(_subcontrataDeOtra.Id, _otraPropia.Id, Ahora));
    }

    private ObtenerCatalogoDivisionPdfQueryHandler Handler(AlcanceDatosServiceFalso? alcance = null) =>
        new(_empresas, _trabajadores, _vehiculos, _tipos, alcance ?? new AlcanceDatosServiceFalso());

    private Task<Domain.Common.Result<CatalogoDivisionPdf>> Leer(Guid empresaId, AlcanceDatosServiceFalso? alcance = null) =>
        Handler(alcance).Handle(new ObtenerCatalogoDivisionPdfQuery(empresaId), CancellationToken.None);

    [Fact]
    public async Task Solo_trae_lo_de_la_Empresa_gestionada_y_sus_Subcontratas_vigentes()
    {
        var propio = Trabajador.DeEmpresa(_propia.Id, "Ana", "Pérez", "12345678Z");
        var deSubcontrata = Trabajador.DeSubcontrata(_subcontrata.Id, "Luis", "Mora", "87654321X");
        var ajeno = Trabajador.DeEmpresa(_otraPropia.Id, "Eva", "Ruiz", "11111111H");
        var deSubcontrataAjena = Trabajador.DeSubcontrata(_subcontrataDeOtra.Id, "Iker", "Sanz", "22222222J");
        _trabajadores.ListaTrabajadores.AddRange([propio, deSubcontrata, ajeno, deSubcontrataAjena]);

        var furgoneta = Vehiculo.DeEmpresa(_propia.Id, "Furgoneta", "Kangoo", "1234 KLM");
        var camion = Vehiculo.DeSubcontrata(_subcontrata.Id, "Camión", "Actros", "5678 BCD");
        var ajeno2 = Vehiculo.DeEmpresa(_otraPropia.Id, "Grúa", "Liebherr", "9999 ZZZ");
        _vehiculos.Lista.AddRange([furgoneta, camion, ajeno2]);

        var catalogo = (await Leer(_propia.Id)).Valor;

        catalogo.EmpresaGestionada.Id.Should().Be(_propia.Id);
        catalogo.Subcontratas.Select(s => s.Id).Should().Equal(_subcontrata.Id);
        catalogo.Trabajadores.Select(t => (t.Id, t.EmpresaDuenaId)).Should().BeEquivalentTo(
            [(propio.Id, _propia.Id), (deSubcontrata.Id, _subcontrata.Id)]);
        catalogo.Vehiculos.Select(v => (v.Id, v.EmpresaDuenaId)).Should().BeEquivalentTo(
            [(furgoneta.Id, _propia.Id), (camion.Id, _subcontrata.Id)]);
    }

    [Fact]
    public async Task Una_Subcontrata_con_la_relacion_cerrada_ya_no_cuenta()
    {
        var relacion = _empresas.ListaRelacionesEmpresariales.Single(r => r.ProveedoraId == _subcontrata.Id);
        relacion.Cerrar(Ahora.AddDays(1));

        (await Leer(_propia.Id)).Valor.Subcontratas.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_Subcontrata_no_vale_como_Empresa_gestionada()
    {
        var resultado = await Leer(_subcontrata.Id);

        resultado.Error.Codigo.Should().Be(ObtenerCatalogoDivisionPdfQueryHandler.ErrorEmpresaNoGestionable);
    }

    [Fact]
    public async Task Una_Empresa_fuera_de_la_cartera_se_rechaza()
    {
        var resultado = await Leer(_propia.Id, new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, empresaIdsParaGestion: [_otraPropia.Id]));

        resultado.Error.Codigo.Should().Be(ObtenerCatalogoDivisionPdfQueryHandler.ErrorEmpresaNoGestionable);
    }

    [Fact]
    public async Task El_alcance_recorta_Subcontratas_Trabajadores_y_Vehiculos()
    {
        var visible = Trabajador.DeEmpresa(_propia.Id, "Ana", "Pérez", "12345678Z");
        var oculto = Trabajador.DeEmpresa(_propia.Id, "Luis", "Mora", "87654321X");
        _trabajadores.ListaTrabajadores.AddRange([visible, oculto]);
        var vehiculoVisible = Vehiculo.DeEmpresa(_propia.Id, "Furgoneta", "Kangoo", "1234 KLM");
        _vehiculos.Lista.AddRange([vehiculoVisible, Vehiculo.DeEmpresa(_propia.Id, "Grúa", "Liebherr", "9999 ZZZ")]);

        var catalogo = (await Leer(_propia.Id, new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, trabajadorIdsVisibles: [visible.Id], empresaIdsParaGestion: [_propia.Id],
            subcontrataIdsParaGestion: [], vehiculoIdsVisibles: [vehiculoVisible.Id]))).Valor;

        catalogo.Subcontratas.Should().BeEmpty();
        catalogo.Trabajadores.Select(t => t.Id).Should().Equal(visible.Id);
        catalogo.Vehiculos.Select(v => v.Id).Should().Equal(vehiculoVisible.Id);
    }

    [Fact]
    public async Task No_ofrece_tipos_archivados()
    {
        var vigente = new TipoDocumento("Formación", 12, true, 1, AmbitoAplicacion.Trabajador);
        var archivado = new TipoDocumento("Antiguo", 12, true, 2, AmbitoAplicacion.Trabajador);
        archivado.Archivar(sustituto: null);
        _tipos.ListaTiposDocumento.AddRange([vigente, archivado]);

        (await Leer(_propia.Id)).Valor.Tipos.Select(t => t.Id).Should().Equal(vigente.Id);
    }

    private sealed class VehiculosFalsos : IVehiculosQueryContext
    {
        public List<Vehiculo> Lista { get; } = [];

        public IQueryable<Vehiculo> Vehiculos => new TestAsyncQueryable<Vehiculo>(Lista.AsQueryable());
    }
}
