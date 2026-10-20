using CaeManager.Application.Common;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Trabajadores.Commands.CorregirDocumentoIdentidadTrabajador;
using CaeManager.Domain.Trabajadores;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Trabajadores;

public class CorregirDocumentoIdentidadTrabajadorCommandTests
{
    private const string DniValido = "12345678Z";
    private const string OtroDniValido = "00000000T";
    private const string NieValido = "X0000000T";

    private static Trabajador Crear(string dni = DniValido) => Trabajador.DeEmpresa(Guid.NewGuid(), "Paula", "Campos", dni);

    private static CorregirDocumentoIdentidadTrabajadorCommand Comando(Trabajador t, string documento) =>
        new(t.Id, t.Version, documento);

    private static (CorregirDocumentoIdentidadTrabajadorCommandHandler Handler, UnitOfWorkFalso Uow, TrabajadorRepositorioFalso Repo) Montar(
        Trabajador? t, AlcanceDatosServiceFalso? alcance = null)
    {
        var repo = new TrabajadorRepositorioFalso();
        if (t is not null) repo.Agregar(t);
        var uow = new UnitOfWorkFalso();
        return (new(repo, alcance ?? new(), uow), uow, repo);
    }

    [Fact]
    public async Task Corrige_el_documento_normalizado_y_guarda_una_vez()
    {
        var t = Crear(); var (handler, uow, _) = Montar(t);
        var resultado = await handler.Handle(Comando(t, " 0000 0000-t "), CancellationToken.None);
        resultado.EsExitoso.Should().BeTrue();
        t.Dni.Should().Be(OtroDniValido); uow.VecesGuardado.Should().Be(1);
    }

    [Theory]
    [InlineData(DniValido, OtroDniValido)]
    [InlineData(DniValido, NieValido)]
    public async Task Un_documento_nuevo_dni_o_nie_quita_la_marca_a_mano_y_vuelve_a_decidir_el_documento(string anterior, string nuevo)
    {
        var t = Crear(anterior); t.FijarMarcaExtranjeroCobrable(true); var (handler, uow, _) = Montar(t);
        (await handler.Handle(Comando(t, nuevo), CancellationToken.None)).EsExitoso.Should().BeTrue();
        t.Dni.Should().Be(nuevo);
        t.MarcaExtranjeroCobrable.Should().BeNull("la casilla vuelve a decidirla el documento nuevo");
        uow.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task El_mismo_documento_no_cambia_nada_ni_quita_la_marca_ni_guarda()
    {
        var t = Crear(); t.FijarMarcaExtranjeroCobrable(true); var (handler, uow, _) = Montar(t);
        (await handler.Handle(Comando(t, " 12345678z "), CancellationToken.None)).EsExitoso.Should().BeTrue();
        t.MarcaExtranjeroCobrable.Should().BeTrue(); uow.VecesGuardado.Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task No_encontrado_no_guarda_ni_modifica_si_no_existe_o_no_es_visible(bool existe)
    {
        var t = Crear(); var (handler, uow, _) = Montar(existe ? t : null, new(trabajadorIdsVisibles: []));
        var resultado = await handler.Handle(Comando(t, OtroDniValido), CancellationToken.None);
        resultado.Error!.Codigo.Should().Be("Trabajador.NoEncontrado");
        t.Dni.Should().Be(DniValido); uow.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Sin_alcance_de_gestion_sobre_la_empresa_no_corrige_ni_guarda()
    {
        var t = Crear(); var (handler, uow, _) = Montar(t, new(empresaIdsParaGestion: []));
        var resultado = await handler.Handle(Comando(t, OtroDniValido), CancellationToken.None);
        resultado.EsExitoso.Should().BeFalse();
        resultado.Error!.Codigo.Should().Be("Trabajador.DocumentoSinGestion");
        t.Dni.Should().Be(DniValido); uow.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Sin_alcance_de_gestion_sobre_la_subcontrata_no_corrige_ni_guarda()
    {
        var t = Trabajador.DeSubcontrata(Guid.NewGuid(), "Paula", "Campos", DniValido);
        var (handler, uow, _) = Montar(t, new(subcontrataIdsParaGestion: []));
        var resultado = await handler.Handle(Comando(t, OtroDniValido), CancellationToken.None);
        resultado.Error!.Codigo.Should().Be("Trabajador.DocumentoSinGestion");
        t.Dni.Should().Be(DniValido); uow.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Una_persona_anonimizada_no_recupera_su_documento()
    {
        var t = Crear(); t.Anonimizar(DateTime.UtcNow); var dniAnonimizado = t.Dni; var (handler, uow, _) = Montar(t);
        var resultado = await handler.Handle(Comando(t, OtroDniValido), CancellationToken.None);
        resultado.EsExitoso.Should().BeFalse();
        resultado.Error!.Codigo.Should().Be("Trabajador.Anonimizado");
        t.Dni.Should().Be(dniAnonimizado); uow.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Una_version_antigua_no_modifica_ni_guarda()
    {
        var t = Crear(); t.FijarMarcaExtranjeroCobrable(true); var (handler, uow, _) = Montar(t);
        var resultado = await handler.Handle(Comando(t, OtroDniValido) with { Version = Guid.NewGuid() }, CancellationToken.None);
        resultado.Error!.Codigo.Should().Be(ConcurrenciaOptimista.CodigoConflicto);
        t.Dni.Should().Be(DniValido); t.MarcaExtranjeroCobrable.Should().BeTrue(); uow.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Un_documento_de_otro_trabajador_se_rechaza_como_duplicado_sin_guardar()
    {
        var t = Crear(); var otro = Crear(OtroDniValido); var (handler, uow, repo) = Montar(t);
        repo.Agregar(otro);
        var resultado = await handler.Handle(Comando(t, "00000000t"), CancellationToken.None);
        resultado.EsExitoso.Should().BeFalse();
        resultado.Error!.Codigo.Should().Be("Trabajador.DniDuplicado");
        t.Dni.Should().Be(DniValido); uow.VecesGuardado.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("PENDIENTE")]
    [InlineData("SIN DNI")]
    [InlineData("12345678A")]
    public async Task Un_marcador_o_un_documento_con_letra_de_control_mala_se_rechaza_sin_guardar(string documento)
    {
        var t = Crear(); var (handler, uow, _) = Montar(t);
        var resultado = await handler.Handle(Comando(t, documento), CancellationToken.None);
        resultado.Error!.Codigo.Should().Be("Trabajador.DocumentoNoValido");
        t.Dni.Should().Be(DniValido); uow.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public void El_validador_exige_identidad_version_y_documento_valido()
    {
        var validator = new CorregirDocumentoIdentidadTrabajadorCommandValidator(); var c = Comando(Crear(), OtroDniValido);
        validator.Validate(c).IsValid.Should().BeTrue();
        validator.Validate(c with { Id = Guid.Empty, Version = Guid.Empty, Documento = "PENDIENTE" })
            .Errors.Select(e => e.PropertyName).Should().BeEquivalentTo(["Id", "Version", "Documento"]);
    }
}
