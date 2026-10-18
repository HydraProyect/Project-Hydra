using CaeManager.Application.Empresas.Commands.EliminarDelegacionEmpresaUOficio;
using CaeManager.Application.Empresas.Commands.GuardarDelegacionEmpresa;
using CaeManager.Application.Empresas.Commands.GuardarOficioEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerCatalogoTrabajadoresEmpresa;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Trabajadores;
using CaeManager.Application.Trabajadores.Commands.FijarDelegacionEmpresaYOficioTrabajador;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Empresas;

/// <summary>
/// Delegación y Oficio de Empresa (decisión de Chris, 2026-10-18): CRUD desde Empresa 360 con alcance de gestión sobre
/// la Empresa, y asignación al Trabajador solo con el catálogo de su propia Empresa.
/// </summary>
public class DelegacionYOficioEmpresaTests
{
    private readonly Empresa _empresa = Empresa.CrearComoCliente("Montajes Repro S.L.", "B10380392", false, null, null);
    private readonly Empresa _otraEmpresa = new("Otra S.L.", "B87654323");
    private readonly EmpresaRepositorioFalso _empresas = new();
    private readonly CatalogoFalso _catalogo = new();
    private readonly TrabajadorRepositorioFalso _trabajadores = new();
    private readonly UnitOfWorkFalso _unitOfWork = new();

    public DelegacionYOficioEmpresaTests()
    {
        _empresas.Agregar(_empresa);
        _empresas.Agregar(_otraEmpresa);
    }

    private AlcanceDatosServiceFalso EnCartera() => new(tieneAccesoTotal: false, empresaIdsVisibles: [_empresa.Id]);

    // Fuera de cartera: no gestiona la Empresa aunque tenga un rol con escritura.
    private static AlcanceDatosServiceFalso FueraDeCartera() => new(tieneAccesoTotal: false, empresaIdsVisibles: []);

    private GuardarDelegacionEmpresaCommandHandler Delegaciones(AlcanceDatosServiceFalso alcance) =>
        new(_empresas, _catalogo, alcance, _unitOfWork);

    private GuardarOficioEmpresaCommandHandler Oficios(AlcanceDatosServiceFalso alcance) =>
        new(_empresas, _catalogo, alcance, _unitOfWork);

    [Fact]
    public async Task Crea_la_delegacion_con_su_contacto_de_reclamacion()
    {
        var resultado = await Delegaciones(EnCartera()).Handle(
            new(_empresa.Id, null, "  Sevilla  ", "Ana Ruiz", "ana@example.test", "600 000 000", null), default);

        resultado.EsExitoso.Should().BeTrue();
        var delegacion = _catalogo.DelegacionesEmpresa.Single();
        delegacion.Nombre.Should().Be("Sevilla");
        delegacion.ContactoReclamacionCorreo.Should().Be("ana@example.test");
        _unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Fuera_de_cartera_ningun_comando_encuentra_la_empresa_ni_guarda()
    {
        var delegacion = new DelegacionEmpresa(_empresa.Id, "Sevilla");
        var oficio = new OficioEmpresa(_empresa.Id, "Soldador");
        _catalogo.Agregar(delegacion);
        _catalogo.Agregar(oficio);
        var trabajador = Trabajador.DeEmpresa(_empresa.Id, "Ana", "Ruiz", "12345678Z");
        _trabajadores.Agregar(trabajador);
        var fuera = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [], trabajadorIdsVisibles: [trabajador.Id]);

        (await Delegaciones(FueraDeCartera()).Handle(new(_empresa.Id, null, "Bilbao", null, null, null, null), default))
            .Error.Codigo.Should().Be("Empresa.NoEncontrada");
        (await Oficios(FueraDeCartera()).Handle(new(_empresa.Id, null, "Pintor", null), default))
            .Error.Codigo.Should().Be("Empresa.NoEncontrada");
        (await new EliminarDelegacionEmpresaUOficioCommandHandler(_empresas, _catalogo, FueraDeCartera(), null!, _unitOfWork)
                .Handle(new(_empresa.Id, CatalogoTrabajadoresEmpresa.OficioEmpresa, oficio.Id, oficio.Version), default))
            .Error.Codigo.Should().Be("Empresa.NoEncontrada");
        (await new FijarDelegacionEmpresaYOficioTrabajadorCommandHandler(_trabajadores, _catalogo, fuera, _unitOfWork)
                .Handle(new(trabajador.Id, delegacion.Id, oficio.Id, trabajador.Version), default))
            .Error.Codigo.Should().Be("Trabajador.NoEncontrado");
        (await new ObtenerCatalogoTrabajadoresEmpresaQueryHandler(_catalogo, new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: []))
                .Handle(new(_empresa.Id), default))
            .Should().Be(CatalogoTrabajadoresEmpresaDto.Vacio);

        _unitOfWork.VecesGuardado.Should().Be(0);
        _catalogo.DelegacionesEmpresa.Should().ContainSingle();
        _catalogo.Oficios.Should().ContainSingle();
        trabajador.DelegacionEmpresaId.Should().BeNull();
    }

    [Fact]
    public void El_correo_del_contacto_se_valida_como_en_la_ficha_de_Empresa()
    {
        var validador = new GuardarDelegacionEmpresaCommandValidator();

        validador.Validate(new GuardarDelegacionEmpresaCommand(_empresa.Id, null, "Sevilla", null, "no-es-correo", null, null))
            .Errors.Should().ContainSingle(e => e.ErrorMessage == "Introduce un correo electrónico válido.");
        validador.Validate(new GuardarDelegacionEmpresaCommand(_empresa.Id, null, "Sevilla", null, null, null, null))
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public async Task Crear_un_oficio_que_ya_existe_sin_distinguir_mayusculas_ni_espacios_devuelve_el_existente()
    {
        var existente = new OficioEmpresa(_empresa.Id, "Soldador");
        _catalogo.Agregar(existente);

        var resultado = await Oficios(EnCartera()).Handle(new(_empresa.Id, null, "  SOLDADOR ", null), default);

        resultado.Valor.Should().Be(existente.Id);
        _catalogo.Oficios.Should().ContainSingle();
    }

    [Fact]
    public async Task Editar_con_una_version_vieja_es_conflicto()
    {
        var delegacion = new DelegacionEmpresa(_empresa.Id, "Sevilla");
        _catalogo.Agregar(delegacion);

        var resultado = await Delegaciones(EnCartera()).Handle(
            new(_empresa.Id, delegacion.Id, "Sevilla Norte", null, null, null, Guid.NewGuid()), default);

        resultado.EsExitoso.Should().BeFalse();
        delegacion.Nombre.Should().Be("Sevilla");
    }

    [Fact]
    public async Task No_se_elimina_un_oficio_que_tiene_trabajadores()
    {
        var oficio = new OficioEmpresa(_empresa.Id, "Soldador");
        _catalogo.Agregar(oficio);
        _catalogo.TrabajadoresPorOficio[oficio.Id] = 2;

        var resultado = await new EliminarDelegacionEmpresaUOficioCommandHandler(_empresas, _catalogo, EnCartera(), null!, _unitOfWork)
            .Handle(new(_empresa.Id, CatalogoTrabajadoresEmpresa.OficioEmpresa, oficio.Id, oficio.Version), default);

        resultado.Error.Codigo.Should().Be("Catalogo.EnUso");
        oficio.EstaEliminado.Should().BeFalse();
    }

    [Fact]
    public async Task Al_trabajador_solo_se_le_asigna_el_catalogo_de_su_empresa_y_el_oficio_copia_el_puesto()
    {
        var trabajador = Trabajador.DeEmpresa(_empresa.Id, "Ana", "Ruiz", "12345678Z");
        _trabajadores.Agregar(trabajador);
        var propio = new OficioEmpresa(_empresa.Id, "Soldador");
        var ajeno = new OficioEmpresa(_otraEmpresa.Id, "Pintor");
        _catalogo.Agregar(propio);
        _catalogo.Agregar(ajeno);
        var handler = new FijarDelegacionEmpresaYOficioTrabajadorCommandHandler(_trabajadores, _catalogo, EnCarteraConTrabajador(trabajador), _unitOfWork);

        (await handler.Handle(new(trabajador.Id, null, ajeno.Id, trabajador.Version), default))
            .Error.Codigo.Should().Be("Oficio.NoEncontrado");
        trabajador.OficioEmpresaId.Should().BeNull();

        (await handler.Handle(new(trabajador.Id, null, propio.Id, trabajador.Version), default)).EsExitoso.Should().BeTrue();
        trabajador.OficioEmpresaId.Should().Be(propio.Id);
        trabajador.Puesto.Should().Be("Soldador");
    }

    private AlcanceDatosServiceFalso EnCarteraConTrabajador(Trabajador trabajador) =>
        new(tieneAccesoTotal: false, empresaIdsVisibles: [_empresa.Id], trabajadorIdsVisibles: [trabajador.Id]);

    private sealed class CatalogoFalso : ICatalogoTrabajadoresEmpresaRepository
    {
        public List<DelegacionEmpresa> DelegacionesEmpresa { get; } = [];
        public List<OficioEmpresa> Oficios { get; } = [];
        public Dictionary<Guid, int> TrabajadoresPorOficio { get; } = [];

        public Task<DelegacionEmpresa?> ObtenerDelegacionEmpresaAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(DelegacionesEmpresa.FirstOrDefault(d => d.Id == id && !d.EstaEliminado));

        public Task<OficioEmpresa?> ObtenerOficioAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Oficios.FirstOrDefault(o => o.Id == id && !o.EstaEliminado));

        public Task<IReadOnlyList<DelegacionEmpresa>> ListarDelegacionesEmpresaAsync(Guid empresaId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DelegacionEmpresa>>([.. DelegacionesEmpresa.Where(d => d.EmpresaId == empresaId && !d.EstaEliminado)]);

        public Task<IReadOnlyList<OficioEmpresa>> ListarOficiosAsync(Guid empresaId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OficioEmpresa>>([.. Oficios.Where(o => o.EmpresaId == empresaId && !o.EstaEliminado)]);

        public Task<int> ContarTrabajadoresDeDelegacionEmpresaAsync(Guid delegacionId, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        public Task<int> ContarTrabajadoresDeOficioAsync(Guid oficioId, CancellationToken cancellationToken = default) =>
            Task.FromResult(TrabajadoresPorOficio.GetValueOrDefault(oficioId));

        public void Agregar(DelegacionEmpresa delegacion) => DelegacionesEmpresa.Add(delegacion);

        public void Agregar(OficioEmpresa oficio) => Oficios.Add(oficio);
    }
}
