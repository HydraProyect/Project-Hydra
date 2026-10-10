using CaeManager.Application.Centros.Commands.CrearCanalGestion;
using CaeManager.Application.Centros.Commands.EditarCanalGestion;
using CaeManager.Application.Centros.Commands.EditarCentro;
using CaeManager.Application.Centros.Commands.EliminarCanalGestion;
using CaeManager.Application.Centros.Commands.EliminarCentro;
using CaeManager.Application.Centros.Commands.MarcarCanalGestionPrincipal;
using CaeManager.Application.Common;
using CaeManager.Application.Contactos.Commands.EliminarContactoAgenda;
using CaeManager.Application.Contactos.Commands.GuardarContactoAgenda;
using CaeManager.Application.Contactos.Queries.ObtenerAgendaContactos;
using CaeManager.Application.Tests.Asignaciones;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Plantillas;
using CaeManager.Application.Tests.TiposDocumento;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Contactos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Centros;

/// <summary>
/// Segunda barrera de las ocho escrituras propias de un Centro (su ficha, sus canales de gestión
/// documental y su agenda de contactos): el alcance de GESTIÓN
/// (<see cref="AlcanceDatosServiceExtensions.CentroParaGestionVisibleAsync"/>), no el de lectura.
///
/// La diferencia entre los dos es el rol Cliente —el usuario de portal de un Cliente empresarial—,
/// al que la cartera de lectura le devuelve sus propios Centros y la de gestión, ninguno. La primera
/// barrera (<c>AutorizacionEscrituraBehavior</c>) ya no le deja llegar al handler; aquí se prueba que
/// el handler, por sí solo, tampoco le deja escribir. Con el alcance de lectura como puerta, los ocho
/// handlers escribirían.
/// </summary>
public class EscriturasDeCentroAlcanceGestionTests
{
    private const string EditarCentro = nameof(EditarCentro);
    private const string EliminarCentro = nameof(EliminarCentro);
    private const string CrearCanalGestion = nameof(CrearCanalGestion);
    private const string EditarCanalGestion = nameof(EditarCanalGestion);
    private const string EliminarCanalGestion = nameof(EliminarCanalGestion);
    private const string MarcarCanalGestionPrincipal = nameof(MarcarCanalGestionPrincipal);
    private const string GuardarContactoAgenda = nameof(GuardarContactoAgenda);
    private const string EliminarContactoAgenda = nameof(EliminarContactoAgenda);

    private readonly Centro _centro = new(Guid.NewGuid(), Guid.NewGuid(), "Planta de Getafe");
    private readonly CentroRepositorioFalso _centros = new();
    private readonly CentrosQueryContextFalso _centrosContexto = new();
    private readonly CanalesGestionEnMemoria _canales = new();
    private readonly ContactosAgendaEnMemoria _contactos = new();
    private readonly UnitOfWorkFalso _unitOfWork = new();
    private readonly CurrentUserServiceFalso _usuario = new(Guid.NewGuid());
    private readonly CanalGestionDocumental _canal;
    private readonly ContactoAgenda _contacto;

    public EscriturasDeCentroAlcanceGestionTests()
    {
        _centros.Agregar(_centro);
        _centrosContexto.ListaCentros.Add(_centro);
        _canal = CanalGestionDocumental.PorEmail(_centro.Id, "Envío de documentación", "cae@ejemplo.test", null);
        _canales.Agregar(_canal);
        _contacto = ContactoAgenda.DeCentro(_centro.Id, "Marta Ruiz", "marta@ejemplo.test");
        _contactos.Agregar(_contacto);
    }

    // El caso y el código con el que cada handler responde «no existe» a quien no gestiona el Centro.
    public static TheoryData<string, string> Escrituras => new()
    {
        { EditarCentro, "Centro.NoEncontrado" },
        { EliminarCentro, "Centro.NoEncontrado" },
        { CrearCanalGestion, "CanalGestion.CentroNoEncontrado" },
        { EditarCanalGestion, "CanalGestion.NoEncontrado" },
        { EliminarCanalGestion, "CanalGestion.NoEncontrado" },
        { MarcarCanalGestionPrincipal, "CanalGestion.NoEncontrado" },
        { GuardarContactoAgenda, "ContactoAgenda.SinAcceso" },
        { EliminarContactoAgenda, "ContactoAgenda.SinAcceso" },
    };

    [Theory]
    [MemberData(nameof(Escrituras))]
    public async Task Con_el_Centro_visible_en_lectura_pero_sin_alcance_de_gestion_la_escritura_responde_como_inexistente(
        string escritura, string codigoEsperado)
    {
        // Lo que ve el rol Cliente: su Centro en la cartera de lectura, ninguno en la de gestión.
        var alcance = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, centroIdsVisibles: [_centro.Id], centroIdsParaGestion: []);

        var resultado = await EjecutarAsync(escritura, alcance);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be(codigoEsperado);
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    // Control positivo: el mismo recorrido, con el Centro en el alcance de gestión, sí escribe. Sin él,
    // un escenario mal montado (un Id que no existe, un doble que falla) daría verde arriba por el
    // motivo equivocado.
    [Theory]
    [MemberData(nameof(Escrituras))]
    public async Task Con_alcance_de_gestion_sobre_el_Centro_la_escritura_se_ejecuta(string escritura, string codigoSinGestion)
    {
        var alcance = new AlcanceDatosServiceFalso(
            tieneAccesoTotal: false, centroIdsVisibles: [_centro.Id], centroIdsParaGestion: [_centro.Id]);

        var resultado = await EjecutarAsync(escritura, alcance);

        resultado.EsExitoso.Should().BeTrue($"con gestión sobre el Centro no debe responder {codigoSinGestion}");
        _unitOfWork.VecesGuardado.Should().Be(1);
    }

    private async Task<Result> EjecutarAsync(string escritura, IAlcanceDatosService alcance) => escritura switch
    {
        EditarCentro => await new EditarCentroCommandHandler(_centros, alcance, _unitOfWork).Handle(
            new EditarCentroCommand(_centro.Id, "Planta de Getafe (nave 2)", null, null, null, null), CancellationToken.None),

        EliminarCentro => await new EliminarCentroCommandHandler(
                _centros, new AsignacionRepositorioFalso(), alcance, _unitOfWork, _usuario)
            .Handle(new EliminarCentroCommand(_centro.Id), CancellationToken.None),

        CrearCanalGestion => await new CrearCanalGestionCommandHandler(
                _canales, alcance, new ProveedoresPlataformaCaeQueryContextFalso(), _centrosContexto,
                new AltaAcreditacionesPlataformaServiceFalso(), _unitOfWork)
            .Handle(
                new CrearCanalGestionCommand(
                    _centro.Id, TipoCanalGestion.Email, "Avisos de visitas", null, null, null, null, "visitas@ejemplo.test", null, null),
                CancellationToken.None),

        EditarCanalGestion => await new EditarCanalGestionCommandHandler(
                _canales, alcance, new ProveedoresPlataformaCaeQueryContextFalso(), _unitOfWork)
            .Handle(
                new EditarCanalGestionCommand(_canal.Id, "Envío de documentación", null, null, "prl@ejemplo.test", null, null),
                CancellationToken.None),

        EliminarCanalGestion => await new EliminarCanalGestionCommandHandler(_canales, alcance, _unitOfWork, _usuario)
            .Handle(new EliminarCanalGestionCommand(_canal.Id), CancellationToken.None),

        MarcarCanalGestionPrincipal => await new MarcarCanalGestionPrincipalCommandHandler(_canales, alcance, _unitOfWork)
            .Handle(new MarcarCanalGestionPrincipalCommand(_canal.Id), CancellationToken.None),

        GuardarContactoAgenda => await new GuardarContactoAgendaCommandHandler(
                _contactos, new TiposDocumentoQueryContextFalso(), alcance, _unitOfWork)
            .Handle(
                new GuardarContactoAgendaCommand(
                    TipoPropietarioAgenda.Centro, _centro.Id, null, "Luis Ortega", "luis@ejemplo.test", null, null, null,
                    false, false, false, [], []),
                CancellationToken.None),

        EliminarContactoAgenda => await new EliminarContactoAgendaCommandHandler(_contactos, alcance, _unitOfWork)
            .Handle(
                new EliminarContactoAgendaCommand(TipoPropietarioAgenda.Centro, _centro.Id, _contacto.Id), CancellationToken.None),

        _ => throw new ArgumentOutOfRangeException(nameof(escritura), escritura, "Escritura de Centro sin caso en este test."),
    };

    private sealed class CanalesGestionEnMemoria : ICanalGestionDocumentalRepository
    {
        private readonly List<CanalGestionDocumental> _canales = [];

        public Task<CanalGestionDocumental?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_canales.FirstOrDefault(c => c.Id == id));

        public Task<IReadOnlyList<CanalGestionDocumental>> ObtenerPorCentroAsync(Guid centroId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CanalGestionDocumental>>(_canales.Where(c => c.CentroId == centroId).ToList());

        public void Agregar(CanalGestionDocumental canal) => _canales.Add(canal);
    }

    private sealed class ContactosAgendaEnMemoria : IContactoAgendaRepository
    {
        private readonly List<ContactoAgenda> _contactos = [];

        public void Agregar(ContactoAgenda contacto) => _contactos.Add(contacto);

        public void Eliminar(ContactoAgenda contacto) => _contactos.Remove(contacto);

        public Task<ContactoAgenda?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(_contactos.FirstOrDefault(c => c.Id == id));
    }
}
