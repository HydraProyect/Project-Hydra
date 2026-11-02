using CaeManager.Application.Contactos;
using CaeManager.Application.Contactos.Commands.FijarNotaContactoAgenda;
using CaeManager.Application.Contactos.Commands.GuardarContactoAgenda;
using CaeManager.Application.Contactos.Queries.ObtenerAgendaContactos;
using CaeManager.Application.Contactos.Queries.ObtenerNotaContacto;
using CaeManager.Application.Tests.Asignaciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.ContratosAcceso;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Contactos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Contactos;

/// <summary>
/// La nota fija del contacto: quién la escribió, a qué agenda pertenece y quién puede leerla. El alcance de escritura
/// por rama lo mide <c>EscriturasDeCentroAlcanceGestionTests</c>; aquí, lo que depende de la nota.
/// </summary>
public class NotaContactoAgendaTests
{
    private readonly Guid _centroId = Guid.NewGuid();
    private readonly Guid _autor = Guid.NewGuid();
    private readonly ContactoAgenda _contacto;
    private readonly ContactosEnMemoria _contactos = new();
    private readonly ContactosAgendaQueryContextFalso _lectura = new();
    private readonly UnitOfWorkFalso _unitOfWork = new();
    private readonly AlcanceDatosServiceFalso _alcance;

    public NotaContactoAgendaTests()
    {
        _contacto = ContactoAgenda.DeCentro(_centroId, "Marta Ruiz", "marta@ejemplo.test");
        _contactos.Agregar(_contacto);
        _lectura.ListaContactosAgenda.Add(_contacto);
        _alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [_centroId], centroIdsParaGestion: [_centroId]);
    }

    [Fact]
    public async Task Fijar_la_nota_sella_al_usuario_actual_como_autor()
    {
        var resultado = await Fijar(new CurrentUserServiceFalso(_autor), _centroId, _contacto.Id);

        resultado.EsExitoso.Should().BeTrue();
        _contacto.Notas.Should().Be("Llamar antes de las 11");
        _contacto.NotasAutorUsuarioId.Should().Be(_autor);
        _contacto.NotasActualizadasEnUtc.Should().NotBeNull();
        _unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Sin_usuario_identificado_la_nota_no_se_escribe()
    {
        var resultado = await Fijar(new CurrentUserServiceFalso(), _centroId, _contacto.Id);

        resultado.Error.Codigo.Should().Be("ContactoAgenda.SinAutor");
        _contacto.Notas.Should().BeNull();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Un_contacto_de_otra_agenda_no_se_alcanza_conociendo_su_id()
    {
        var ajeno = ContactoAgenda.DeCentro(Guid.NewGuid(), "Luis Ortega", "luis@ejemplo.test");
        _contactos.Agregar(ajeno);

        var resultado = await Fijar(new CurrentUserServiceFalso(_autor), _centroId, ajeno.Id);

        resultado.Error.Codigo.Should().Be("ContactoAgenda.NoEncontrado");
        ajeno.Notas.Should().BeNull();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Guardar_el_formulario_sin_tocar_la_nota_conserva_a_su_autor()
    {
        _contacto.FijarNotas("Llamar antes de las 11", _autor, DateTime.UtcNow);

        var resultado = await GuardarAsync(_contacto.Id, "Llamar antes de las 11", new CurrentUserServiceFalso(Guid.NewGuid()), "Llamar antes de las 11");

        resultado.EsExitoso.Should().BeTrue();
        _contacto.NotasAutorUsuarioId.Should().Be(_autor);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Nota anterior")]
    public async Task El_formulario_abierto_antes_de_fijar_la_nota_no_la_borra_ni_la_firma(string? cargada)
    {
        _contacto.FijarNotas("Llamar antes de las 11", _autor, DateTime.UtcNow);

        await GuardarAsync(_contacto.Id, cargada, new CurrentUserServiceFalso(Guid.NewGuid()), cargada);

        _contacto.Notas.Should().Be("Llamar antes de las 11");
        _contacto.NotasAutorUsuarioId.Should().Be(_autor);
    }

    [Fact]
    public async Task Vaciar_la_nota_en_el_formulario_la_retira()
    {
        _contacto.FijarNotas("Llamar antes de las 11", _autor, DateTime.UtcNow);

        await GuardarAsync(_contacto.Id, null, new CurrentUserServiceFalso(_autor), "Llamar antes de las 11");

        _contacto.Notas.Should().BeNull();
    }

    [Fact]
    public async Task Fijar_la_nota_de_una_Empresa_exige_alcance_de_gestion()
    {
        var empresaId = Guid.NewGuid();
        var contacto = ContactoAgenda.DeEmpresa(empresaId, "Luis Ortega", "luis@ejemplo.test");
        _contactos.Agregar(contacto);
        var soloLectura = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, empresaIdsVisibles: [empresaId], empresaIdsParaGestion: []);

        var resultado = await new FijarNotaContactoAgendaCommandHandler(_contactos, soloLectura, new CurrentUserServiceFalso(_autor), _unitOfWork).Handle(
            new FijarNotaContactoAgendaCommand(TipoPropietarioAgenda.Empresa, empresaId, contacto.Id, "Llamar antes de las 11"),
            CancellationToken.None);

        resultado.Error.Codigo.Should().Be("ContactoAgenda.SinAcceso");
        contacto.Notas.Should().BeNull();
    }

    [Fact]
    public async Task Cambiar_la_nota_desde_el_formulario_cambia_la_autoria()
    {
        _contacto.FijarNotas("Llamar antes de las 11", _autor, DateTime.UtcNow);
        var otro = Guid.NewGuid();

        await GuardarAsync(_contacto.Id, "Solo por correo", new CurrentUserServiceFalso(otro));

        _contacto.Notas.Should().Be("Solo por correo");
        _contacto.NotasAutorUsuarioId.Should().Be(otro);
    }

    [Fact]
    public async Task Dar_de_alta_un_contacto_con_nota_sella_al_autor()
    {
        var resultado = await GuardarAsync(null, "Solo por correo", new CurrentUserServiceFalso(_autor));

        var alta = await _contactos.ObtenerPorIdAsync(resultado.Valor);
        alta!.Notas.Should().Be("Solo por correo");
        alta.NotasAutorUsuarioId.Should().Be(_autor);
    }

    [Fact]
    public async Task La_nota_se_encuentra_por_correo_sin_distinguir_mayusculas()
    {
        _contacto.FijarNotas("Llamar antes de las 11", _autor, DateTime.UtcNow);

        var nota = await Leer(_alcance, _centroId, " MARTA@ejemplo.test ");

        nota!.ContactoId.Should().Be(_contacto.Id);
        nota.Nombre.Should().Be("Marta Ruiz");
        nota.Notas.Should().Be("Llamar antes de las 11");
        nota.AutorUsuarioId.Should().Be(_autor);
    }

    [Fact]
    public async Task Un_contacto_sin_nota_devuelve_su_ficha_para_poder_anadirla()
    {
        var nota = await Leer(_alcance, _centroId, "marta@ejemplo.test");

        nota!.ContactoId.Should().Be(_contacto.Id);
        nota.Notas.Should().BeNull();
    }

    [Fact]
    public async Task Si_el_correo_se_repite_gana_el_contacto_predeterminado()
    {
        var predeterminado = ContactoAgenda.DeCentro(_centroId, "Zoe Marín", "marta@ejemplo.test", esPredeterminado: true);
        _lectura.ListaContactosAgenda.Add(predeterminado);

        var nota = await Leer(_alcance, _centroId, "marta@ejemplo.test");

        nota!.ContactoId.Should().Be(predeterminado.Id);
    }

    [Fact]
    public async Task Sin_alcance_de_lectura_sobre_la_entidad_no_hay_nota()
    {
        _contacto.FijarNotas("Llamar antes de las 11", _autor, DateTime.UtcNow);
        var sinAlcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, centroIdsVisibles: [], centroIdsParaGestion: []);

        (await Leer(sinAlcance, _centroId, "marta@ejemplo.test")).Should().BeNull();
    }

    [Fact]
    public async Task El_rol_de_portal_no_lee_la_nota_aunque_vea_la_entidad()
    {
        _contacto.FijarNotas("Llamar antes de las 11", _autor, DateTime.UtcNow);

        (await Leer(_alcance, _centroId, "marta@ejemplo.test", leeNotaInterna: false)).Should().BeNull();
    }

    [Fact]
    public async Task El_correo_de_otra_agenda_no_se_lee_desde_la_entidad_visible()
    {
        var otroCentro = Guid.NewGuid();
        _lectura.ListaContactosAgenda.Add(ContactoAgenda.DeCentro(otroCentro, "Luis Ortega", "luis@ejemplo.test"));

        (await Leer(_alcance, _centroId, "luis@ejemplo.test")).Should().BeNull();
    }

    private Task<Domain.Common.Result> Fijar(CurrentUserServiceFalso usuario, Guid propietarioId, Guid contactoId) =>
        new FijarNotaContactoAgendaCommandHandler(_contactos, _alcance, usuario, _unitOfWork).Handle(
            new FijarNotaContactoAgendaCommand(TipoPropietarioAgenda.Centro, propietarioId, contactoId, "Llamar antes de las 11"),
            CancellationToken.None);

    private Task<Domain.Common.Result<Guid>> GuardarAsync(Guid? contactoId, string? notas, CurrentUserServiceFalso usuario, string? notasAlCargar = null) =>
        new GuardarContactoAgendaCommandHandler(_contactos, new TiposDocumentoQueryContextFalso(), _alcance, usuario, _unitOfWork).Handle(
            new GuardarContactoAgendaCommand(
                TipoPropietarioAgenda.Centro, _centroId, contactoId, "Marta Ruiz", "marta@ejemplo.test", null, null, notas,
                false, false, false, [], [], notasAlCargar),
            CancellationToken.None);

    private Task<NotaContactoDto?> Leer(AlcanceDatosServiceFalso alcance, Guid propietarioId, string email, bool leeNotaInterna = true) =>
        new ObtenerNotaContactoQueryHandler(_lectura, alcance, new PoliticaNotaInternaFalsa(leeNotaInterna)).Handle(
            new ObtenerNotaContactoQuery(TipoPropietarioAgenda.Centro, propietarioId, email), CancellationToken.None);

    private sealed class ContactosEnMemoria : IContactoAgendaRepository
    {
        private readonly List<ContactoAgenda> _lista = [];

        public void Agregar(ContactoAgenda contacto) => _lista.Add(contacto);

        public void Eliminar(ContactoAgenda contacto) => _lista.Remove(contacto);

        public Task<ContactoAgenda?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_lista.FirstOrDefault(c => c.Id == id));
    }
}
