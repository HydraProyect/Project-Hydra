using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.DescartarSugerenciaVisita;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Comunicaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Comunicaciones;

/// <summary>
/// Descartar una sugerencia de Visita exige alcanzar la Conversación del mensaje que la originó: el mismo criterio con
/// el que ObtenerConversacionPorIdQuery la enseña. Un Gestor CAE con Asignación de Cartera explícita no la retira de la
/// Bandeja de otra cartera conociendo su Guid, y fuera de alcance recibe lo mismo que si no existiera.
/// </summary>
public class DescartarSugerenciaVisitaCorreoCommandHandlerTests
{
    private readonly Guid _clienteDeMiCartera = Guid.NewGuid();
    private readonly Guid _clienteDeOtraCartera = Guid.NewGuid();
    private readonly ComunicacionesQueryContextFalso _contexto = new();
    private readonly SugerenciaVisitaCorreoRepositorioFalso _repositorio = new();
    private readonly UnitOfWorkFalso _unitOfWork = new();

    private readonly Guid _buzonPersonalDeOtroGestor = Guid.NewGuid();

    private SugerenciaVisitaCorreo SembrarEnConversacionDe(Guid clienteId) =>
        SembrarEn(new Conversacion("Visita el lunes", clienteId: clienteId));

    private SugerenciaVisitaCorreo SembrarEn(Conversacion conversacion)
    {
        var mensaje = conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "obra@contraparte.es", "<p>El lunes vamos.</p>");
        var sugerencia = new SugerenciaVisitaCorreo(mensaje.Id, null, null, null, "Resumen", 90, 0, 0);

        _contexto.ListaConversaciones.Add(conversacion);
        _contexto.ListaMensajes.Add(mensaje);
        _contexto.ListaSugerenciasVisitaCorreo.Add(sugerencia);
        _repositorio.Agregar(sugerencia);
        return sugerencia;
    }

    private DescartarSugerenciaVisitaCorreoCommandHandler Crear(IAlcanceDatosService alcance) =>
        new(_repositorio, _contexto, alcance, new CurrentUserServiceFalso(), _unitOfWork);

    private AlcanceDatosServiceFalso GestorConCarteraExplicita() =>
        new(tieneAccesoTotal: false, clienteIdsVisibles: [_clienteDeMiCartera], conexionesIntegracionAjenas: [_buzonPersonalDeOtroGestor]);

    [Fact]
    public async Task Un_Gestor_con_cartera_explicita_no_descarta_la_de_un_hilo_anclado_a_una_Empresa_fuera_de_su_cartera()
    {
        var ajena = SembrarEn(new Conversacion("Reclamación de TC2", empresaId: Guid.NewGuid()));

        var resultado = await Crear(GestorConCarteraExplicita()).Handle(new DescartarSugerenciaVisitaCorreoCommand(ajena.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("SugerenciaVisitaCorreo.NoEncontrada");
        ajena.Resuelta.Should().BeFalse();
    }

    [Fact]
    public async Task Un_Gestor_no_descarta_la_de_un_hilo_del_buzon_personal_de_otro_Gestor_aunque_el_Cliente_sea_de_su_cartera()
    {
        var conversacion = new Conversacion("Visita el lunes", clienteId: _clienteDeMiCartera);
        conversacion.AsociarConexion(_buzonPersonalDeOtroGestor, "hilo-1");
        var ajena = SembrarEn(conversacion);

        var resultado = await Crear(GestorConCarteraExplicita()).Handle(new DescartarSugerenciaVisitaCorreoCommand(ajena.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("SugerenciaVisitaCorreo.NoEncontrada");
        ajena.Resuelta.Should().BeFalse();
    }

    [Fact]
    public async Task Un_Gestor_con_cartera_explicita_descarta_la_de_un_hilo_de_triage_sin_ancla()
    {
        var deTriage = SembrarEn(new Conversacion("Sin Cliente todavía"));

        var resultado = await Crear(GestorConCarteraExplicita()).Handle(new DescartarSugerenciaVisitaCorreoCommand(deTriage.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue("la cola de triage es compartida: se lee y se descarta desde el lado de gestión");
        deTriage.Resolucion.Should().Be(ResolucionSugerencia.Descartada);
    }

    [Fact]
    public async Task Marca_la_sugerencia_como_resuelta()
    {
        var sugerencia = SembrarEnConversacionDe(_clienteDeMiCartera);

        var resultado = await Crear(new AlcanceDatosServiceFalso()).Handle(new DescartarSugerenciaVisitaCorreoCommand(sugerencia.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        sugerencia.Resuelta.Should().BeTrue();
        sugerencia.Resolucion.Should().Be(ResolucionSugerencia.Descartada);
    }

    [Fact]
    public async Task Falla_si_la_sugerencia_no_existe()
    {
        var resultado = await Crear(new AlcanceDatosServiceFalso()).Handle(new DescartarSugerenciaVisitaCorreoCommand(Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("SugerenciaVisitaCorreo.NoEncontrada");
    }

    [Fact]
    public async Task Un_Gestor_con_cartera_explicita_no_descarta_la_de_otra_cartera_y_recibe_lo_mismo_que_si_no_existiera()
    {
        var ajena = SembrarEnConversacionDe(_clienteDeOtraCartera);

        var resultado = await Crear(GestorConCarteraExplicita()).Handle(new DescartarSugerenciaVisitaCorreoCommand(ajena.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("SugerenciaVisitaCorreo.NoEncontrada", "fuera de cartera no se delata que existe");
        ajena.Resuelta.Should().BeFalse();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Un_Gestor_con_cartera_explicita_descarta_la_de_su_cartera()
    {
        var propia = SembrarEnConversacionDe(_clienteDeMiCartera);

        var resultado = await Crear(GestorConCarteraExplicita()).Handle(new DescartarSugerenciaVisitaCorreoCommand(propia.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        propia.Resolucion.Should().Be(ResolucionSugerencia.Descartada);
        _unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Sin_restriccion_de_cartera_descarta_la_de_cualquier_Cliente_empresarial()
    {
        var ajena = SembrarEnConversacionDe(_clienteDeOtraCartera);

        var resultado = await Crear(new AlcanceDatosServiceFalso(tieneAccesoTotal: true)).Handle(
            new DescartarSugerenciaVisitaCorreoCommand(ajena.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        ajena.Resolucion.Should().Be(ResolucionSugerencia.Descartada);
    }

    [Fact]
    public async Task Sin_mensaje_ni_conversacion_que_la_respalde_falla_cerrado()
    {
        var huerfana = new SugerenciaVisitaCorreo(Guid.NewGuid(), null, null, null, "Resumen", 90, 0, 0);
        _contexto.ListaSugerenciasVisitaCorreo.Add(huerfana);
        _repositorio.Agregar(huerfana);

        var resultado = await Crear(new AlcanceDatosServiceFalso()).Handle(new DescartarSugerenciaVisitaCorreoCommand(huerfana.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        huerfana.Resuelta.Should().BeFalse();
    }
}
