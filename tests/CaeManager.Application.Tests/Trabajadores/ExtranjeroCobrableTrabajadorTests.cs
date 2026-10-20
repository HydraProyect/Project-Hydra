using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Common;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Trabajadores.Commands.EditarFichaTrabajador;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadorPorId;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Trabajadores;

/// <summary>
/// La casilla «Trabajador extranjero (cobrable)» (decisión de Chris, 2026-10-20) se guarda con la ficha y sigue la
/// política del contacto de emergencia: solo quien la lee la cambia; el Usuario de Cliente ni la ve ni la cambia.
/// </summary>
public class ExtranjeroCobrableTrabajadorTests
{
    private static Trabajador Amina() =>
        Trabajador.DeEmpresa(Guid.NewGuid(), "Amina", "El Idrissi", "AB1234567");

    private static EditarFichaTrabajadorCommand Ficha(Trabajador t, MarcaExtranjeroCobrableTrabajador? marca) =>
        new(t.Id, t.Version, null, "Electricista", null, null, null, null, null, null, null, null, null,
            ExtranjeroCobrable: marca);

    private static (EditarFichaTrabajadorCommandHandler Handler, UnitOfWorkFalso UnitOfWork) Montar(
        Trabajador trabajador, IPoliticaNotaInterna? politica = null, IAlcanceDatosService? alcance = null)
    {
        var repositorio = new TrabajadorRepositorioFalso();
        repositorio.Agregar(trabajador);
        var unitOfWork = new UnitOfWorkFalso();
        return (new EditarFichaTrabajadorCommandHandler(repositorio, new Empresas.DelegacionEmpresaYOficioEmpresaTests.CatalogoFalso(),
            alcance ?? new AlcanceDatosServiceFalso(), politica ?? PoliticaNotaInternaPruebas.Con("GestorCae"), unitOfWork), unitOfWork);
    }

    /// <summary>Ve al Trabajador (por ejemplo, por una Asignación) pero no gestiona su Empresa ni ninguna Subcontrata.</summary>
    private static AlcanceDatosServiceFalso SoloLectura(Trabajador t) =>
        new(tieneAccesoTotal: false, trabajadorIdsVisibles: [t.Id], empresaIdsVisibles: [t.EmpresaId!.Value],
            empresaIdsParaGestion: [], subcontrataIdsParaGestion: []);

    [Fact]
    public async Task Cambiar_la_marca_exige_gestionar_la_Empresa_del_Trabajador_no_solo_verlo()
    {
        var t = Amina();
        var (handler, unitOfWork) = Montar(t, alcance: SoloLectura(t));

        var resultado = await handler.Handle(Ficha(t, new(false)), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Trabajador.ExtranjeroCobrableSinGestion");
        t.MarcaExtranjeroCobrable.Should().BeNull();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Quien_solo_ve_al_Trabajador_guarda_la_ficha_si_la_marca_no_cambia()
    {
        var t = Amina();
        t.FijarMarcaExtranjeroCobrable(false);
        var (handler, _) = Montar(t, alcance: SoloLectura(t));

        (await handler.Handle(Ficha(t, new(false)), CancellationToken.None)).EsExitoso.Should().BeTrue();

        t.Puesto.Should().Be("Electricista");
    }

    [Fact]
    public async Task Cambiar_la_marca_de_un_Trabajador_de_Subcontrata_exige_gestionar_la_Subcontrata()
    {
        var subcontrata = Guid.NewGuid();
        var t = Trabajador.DeSubcontrata(subcontrata, "Oleg", "Ivanov", "AB7654321");
        var sinGestion = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [t.Id],
            subcontrataIdsVisibles: [subcontrata], subcontrataIdsParaGestion: []);
        var conGestion = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, trabajadorIdsVisibles: [t.Id],
            subcontrataIdsVisibles: [subcontrata], subcontrataIdsParaGestion: [subcontrata]);

        (await Montar(t, alcance: sinGestion).Handler.Handle(Ficha(t, new(false)), CancellationToken.None))
            .EsFallido.Should().BeTrue();
        t.MarcaExtranjeroCobrable.Should().BeNull();

        (await Montar(t, alcance: conGestion).Handler.Handle(Ficha(t, new(false)), CancellationToken.None))
            .EsExitoso.Should().BeTrue();
        t.MarcaExtranjeroCobrable.Should().BeFalse();
    }

    [Fact]
    public async Task La_ficha_fija_y_devuelve_al_documento_la_marca()
    {
        var t = Amina();
        var (handler, unitOfWork) = Montar(t);

        (await handler.Handle(Ficha(t, new(false)), CancellationToken.None)).EsExitoso.Should().BeTrue();
        t.MarcaExtranjeroCobrable.Should().BeFalse();

        (await handler.Handle(Ficha(t, new(null)), CancellationToken.None)).EsExitoso.Should().BeTrue();
        t.MarcaExtranjeroCobrable.Should().BeNull("una marca null devuelve la decisión al documento");
        unitOfWork.VecesGuardado.Should().Be(2);
    }

    [Fact]
    public async Task Sin_marca_en_la_orden_no_se_cambia()
    {
        var t = Amina();
        t.FijarMarcaExtranjeroCobrable(false);
        var (handler, _) = Montar(t);

        (await handler.Handle(Ficha(t, null), CancellationToken.None)).EsExitoso.Should().BeTrue();

        t.MarcaExtranjeroCobrable.Should().BeFalse();
    }

    [Theory]
    [InlineData("Cliente")]
    [InlineData("RolQueNoExiste")]
    [InlineData(null)]
    public async Task Quien_no_lee_la_marca_no_la_cambia_aunque_la_orden_la_traiga(string? rol)
    {
        var t = Amina();
        var (handler, _) = Montar(t, PoliticaNotaInternaPruebas.Con(rol));

        (await handler.Handle(Ficha(t, new(false)), CancellationToken.None)).EsExitoso.Should().BeTrue();

        t.MarcaExtranjeroCobrable.Should().BeNull();
        t.Puesto.Should().Be("Electricista", "el resto de la ficha sí se guarda");
    }

    [Fact]
    public async Task La_impersonacion_no_cambia_la_marca()
    {
        var t = Amina();
        var (handler, _) = Montar(t, PoliticaNotaInternaPruebas.Con("GestorCae",
            PoliticaNotaInternaPruebas.Sesion(CapacidadPrivilegio.Impersonacion)));

        (await handler.Handle(Ficha(t, new(true)), CancellationToken.None)).EsExitoso.Should().BeTrue();

        t.MarcaExtranjeroCobrable.Should().BeNull();
    }

    private static (Trabajador Trabajador, ObtenerTrabajadorPorIdQueryHandler Handler) Leer(
        string documento, bool? marca, IPoliticaNotaInterna politica)
    {
        var empresa = new Empresa("Montajes", "B12345674");
        var t = Trabajador.DeEmpresa(empresa.Id, "Amina", "El Idrissi", documento);
        t.FijarMarcaExtranjeroCobrable(marca);
        var empresas = new EmpresasQueryContextFalso(); empresas.ListaEmpresas.Add(empresa);
        var trabajadores = new TrabajadoresQueryContextFalso(); trabajadores.ListaTrabajadores.Add(t);
        return (t, new ObtenerTrabajadorPorIdQueryHandler(empresas, trabajadores, new AlcanceDatosServiceFalso(), politica));
    }

    [Theory]
    [InlineData("AB1234567", null, true)]
    [InlineData("X1234567L", null, false)]
    [InlineData("X1234567L", true, false)]
    public async Task El_equipo_del_Tenant_recibe_la_marca_y_lo_que_dice_el_documento(
        string documento, bool? marca, bool porDocumento)
    {
        var (t, handler) = Leer(documento, marca, PoliticaNotaInternaPruebas.Con("GestorCae"));

        var dto = await handler.Handle(new(t.Id), CancellationToken.None);

        dto!.ExtranjeroCobrableVisible.Should().BeTrue();
        dto.MarcaExtranjeroCobrable.Should().Be(marca);
        dto.ExtranjeroCobrablePorDocumento.Should().Be(porDocumento);
    }

    [Theory]
    [InlineData("Cliente")]
    [InlineData(null)]
    public async Task El_usuario_de_Cliente_no_recibe_la_marca(string? rol)
    {
        var (t, handler) = Leer("AB1234567", false, PoliticaNotaInternaPruebas.Con(rol));

        var dto = await handler.Handle(new(t.Id), CancellationToken.None);

        dto!.ExtranjeroCobrableVisible.Should().BeFalse();
        dto.MarcaExtranjeroCobrable.Should().BeNull();
        dto.ExtranjeroCobrablePorDocumento.Should().BeFalse();
    }
}
