using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Empresas;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Trabajadores.Commands.EditarFichaTrabajador;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Trabajadores;

/// <summary>
/// El Oficio y la Delegación que la edición de la ficha fija junto con el resto (decisión de Chris, 2026-10-18): atómico con
/// el guardado de la ficha, solo con el catálogo de la Empresa del Trabajador, y <c>null</c> = no se cambian.
/// </summary>
public class DelegacionEmpresaYOficioEnLaFichaTests
{
    private readonly Empresa _empresa = Empresa.CrearComoCliente("Montajes Repro S.L.", "B10380392", false, null, null);
    private readonly Empresa _otraEmpresa = new("Otra S.L.", "B87654323");
    private readonly DelegacionEmpresaYOficioEmpresaTests.CatalogoFalso _catalogo = new();
    private readonly TrabajadorRepositorioFalso _trabajadores = new();
    private readonly UnitOfWorkFalso _unitOfWork = new();
    private readonly Trabajador _trabajador;

    public DelegacionEmpresaYOficioEnLaFichaTests()
    {
        _trabajador = Trabajador.DeEmpresa(_empresa.Id, "Ana", "Ruiz", "12345678Z");
        _trabajadores.Agregar(_trabajador);
    }

    private EditarFichaTrabajadorCommandHandler Handler() => new(_trabajadores, _catalogo,
        new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [_empresa.Id], trabajadorIdsVisibles: [_trabajador.Id]),
        PoliticaNotaInternaPruebas.Con("GestorCae"), _unitOfWork);

    private EditarFichaTrabajadorCommand Orden(DelegacionEmpresaYOficioDeTrabajador? delegacionYOficio) =>
        new(_trabajador.Id, _trabajador.Version, null, "Puesto antiguo", null, null, null, null, null, null, null, null, null,
            DelegacionEmpresaYOficio: delegacionYOficio);

    [Fact]
    public async Task Fija_la_delegacion_y_el_oficio_y_el_oficio_pasa_a_ser_el_puesto()
    {
        var delegacion = new DelegacionEmpresa(_empresa.Id, "Sevilla");
        var oficio = new OficioEmpresa(_empresa.Id, "Soldador");
        _catalogo.Agregar(delegacion);
        _catalogo.Agregar(oficio);

        var resultado = await Handler().Handle(Orden(new(delegacion.Id, oficio.Id)), default);

        resultado.EsExitoso.Should().BeTrue();
        _trabajador.DelegacionEmpresaId.Should().Be(delegacion.Id);
        _trabajador.OficioEmpresaId.Should().Be(oficio.Id);
        _trabajador.Puesto.Should().Be("Soldador");
        _unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Un_oficio_de_otra_empresa_se_rechaza_y_no_guarda()
    {
        var ajeno = new OficioEmpresa(_otraEmpresa.Id, "Electricista");
        _catalogo.Agregar(ajeno);

        var resultado = await Handler().Handle(Orden(new(null, ajeno.Id)), default);

        resultado.Error.Codigo.Should().Be("Oficio.NoEncontrado");
        _trabajador.OficioEmpresaId.Should().BeNull();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Sin_el_objeto_no_se_tocan_la_delegacion_ni_el_oficio()
    {
        var oficio = new OficioEmpresa(_empresa.Id, "Soldador");
        _catalogo.Agregar(oficio);
        _trabajador.FijarDelegacionEmpresaYOficio(null, oficio);

        var resultado = await Handler().Handle(Orden(null), default);

        resultado.EsExitoso.Should().BeTrue();
        _trabajador.OficioEmpresaId.Should().Be(oficio.Id);
    }

    [Fact]
    public async Task Con_ids_vacios_quita_la_delegacion_y_el_oficio_sin_borrar_el_puesto()
    {
        var oficio = new OficioEmpresa(_empresa.Id, "Soldador");
        _catalogo.Agregar(oficio);
        _trabajador.FijarDelegacionEmpresaYOficio(null, oficio);

        var resultado = await Handler().Handle(Orden(new(null, null)), default);

        resultado.EsExitoso.Should().BeTrue();
        _trabajador.OficioEmpresaId.Should().BeNull();
        _trabajador.Puesto.Should().Be("Puesto antiguo");
    }
}
