using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Empresas;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Trabajadores.Commands.CrearTrabajador;
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
    public async Task Un_catalogo_rechazado_no_deja_el_resto_de_la_ficha_a_medio_cambiar()
    {
        // El DbContext del circuito Blazor sobrevive al comando: si la ficha se hubiera actualizado antes de validar el
        // catálogo, el siguiente guardado del circuito persistiría un Puesto que el usuario vio rechazado.
        var ajeno = new OficioEmpresa(_otraEmpresa.Id, "Electricista");
        _catalogo.Agregar(ajeno);
        var versionAntes = _trabajador.Version;

        var resultado = await Handler().Handle(Orden(new(null, ajeno.Id)), default);

        resultado.EsFallido.Should().BeTrue();
        _trabajador.Puesto.Should().BeNull("la orden traía «Puesto antiguo», pero el comando falló antes de aplicarla");
        _trabajador.Version.Should().Be(versionAntes);
    }

    [Fact]
    public async Task Con_el_trabajador_visible_pero_su_empresa_fuera_de_gestion_responde_no_encontrado_y_no_guarda()
    {
        var oficio = new OficioEmpresa(_empresa.Id, "Soldador");
        _catalogo.Agregar(oficio);
        var handler = new EditarFichaTrabajadorCommandHandler(_trabajadores, _catalogo,
            new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [_empresa.Id],
                trabajadorIdsVisibles: [_trabajador.Id], empresaIdsParaGestion: []),
            PoliticaNotaInternaPruebas.Con("GestorCae"), _unitOfWork);

        var resultado = await handler.Handle(Orden(new(null, oficio.Id)), default);

        resultado.Error.Codigo.Should().Be("Trabajador.NoEncontrado");
        _trabajador.OficioEmpresaId.Should().BeNull();
        _trabajador.Puesto.Should().BeNull();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Asignar_un_oficio_nuevo_renueva_su_version_y_repetir_el_mismo_no()
    {
        // La versión renovada hace chocar por concurrencia una eliminación simultánea del Oficio
        // (EliminarDelegacionEmpresaUOficioCommand comprueba que no lo use ningún Trabajador vivo).
        var oficio = new OficioEmpresa(_empresa.Id, "Soldador");
        _catalogo.Agregar(oficio);
        var versionInicial = oficio.Version;

        (await Handler().Handle(Orden(new(null, oficio.Id)), default)).EsExitoso.Should().BeTrue();
        var versionTrasAsignar = oficio.Version;
        versionTrasAsignar.Should().NotBe(versionInicial);

        (await Handler().Handle(Orden(new(null, oficio.Id)), default)).EsExitoso.Should().BeTrue();
        oficio.Version.Should().Be(versionTrasAsignar, "repetir el mismo Oficio no es una asignación nueva");
    }

    [Fact]
    public void El_alta_de_un_trabajador_de_subcontrata_no_admite_delegacion_ni_oficio()
    {
        var alta = new CrearTrabajadorCommand(null, Guid.NewGuid(), "Sara", "Ruiz", "12345678Z", null, null, null);
        var validador = new CrearTrabajadorCommandValidator();

        validador.Validate(alta).IsValid.Should().BeTrue();
        validador.Validate(alta with { OficioEmpresaId = Guid.NewGuid() }).Errors
            .Should().ContainSingle(e => e.ErrorMessage == "Solo un trabajador de empresa tiene delegación y oficio.");
        validador.Validate(alta with { DelegacionEmpresaId = Guid.NewGuid() }).Errors
            .Should().ContainSingle(e => e.ErrorMessage == "Solo un trabajador de empresa tiene delegación y oficio.");
        validador.Validate(alta with { EmpresaId = Guid.NewGuid(), SubcontrataId = null, OficioEmpresaId = Guid.NewGuid() })
            .IsValid.Should().BeTrue();
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
