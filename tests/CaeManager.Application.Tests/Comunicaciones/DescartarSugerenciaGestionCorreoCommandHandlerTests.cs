using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.DescartarSugerenciaGestion;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Comunicaciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Comunicaciones;

/// <summary>
/// Descartar un ítem de Gestión sugerido exige alcanzar la Conversación del mensaje que lo originó: el mismo criterio
/// con el que ObtenerConversacionPorIdQuery lo enseña. Un Gestor CAE con Asignación de Cartera explícita no lo retira de
/// la Bandeja de otra cartera conociendo su Guid, y fuera de alcance recibe lo mismo que si no existiera.
/// </summary>
public class DescartarSugerenciaGestionCorreoCommandHandlerTests
{
    private readonly Guid _clienteDeMiCartera = Guid.NewGuid();
    private readonly Guid _clienteDeOtraCartera = Guid.NewGuid();
    private readonly ComunicacionesQueryContextFalso _contexto = new();
    private readonly DetallesFalsos _repositorio = new();
    private readonly UnitOfWorkFalso _unitOfWork = new();

    private DetalleSugerenciaGestionCorreo SembrarEnConversacionDe(Guid clienteId)
    {
        var conversacion = new Conversacion("Os mando el TC2", clienteId: clienteId);
        var mensaje = conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "rrhh@contraparte.es", "<p>Adjunto.</p>");
        var sugerencia = new SugerenciaGestionCorreo(mensaje.Id, "Resumen", 90);
        var detalle = sugerencia.AgregarDetalle(Guid.NewGuid(), Guid.NewGuid(), 90, 90);

        _contexto.ListaConversaciones.Add(conversacion);
        _contexto.ListaMensajes.Add(mensaje);
        _contexto.ListaSugerenciasGestionCorreo.Add(sugerencia);
        _contexto.ListaDetallesSugerenciaGestionCorreo.Add(detalle);
        _repositorio.Lista.Add(detalle);
        return detalle;
    }

    private DescartarSugerenciaGestionCorreoCommandHandler Crear(IAlcanceDatosService alcance) =>
        new(_repositorio, _contexto, alcance, new CurrentUserServiceFalso(), _unitOfWork);

    private AlcanceDatosServiceFalso GestorConCarteraExplicita() =>
        new(tieneAccesoTotal: false, clienteIdsVisibles: [_clienteDeMiCartera]);

    [Fact]
    public async Task Falla_si_el_item_no_existe()
    {
        var resultado = await Crear(new AlcanceDatosServiceFalso()).Handle(new DescartarSugerenciaGestionCorreoCommand(Guid.NewGuid()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("SugerenciaGestionCorreo.NoEncontrada");
    }

    [Fact]
    public async Task Un_Gestor_con_cartera_explicita_no_descarta_el_de_otra_cartera_y_recibe_lo_mismo_que_si_no_existiera()
    {
        var ajeno = SembrarEnConversacionDe(_clienteDeOtraCartera);

        var resultado = await Crear(GestorConCarteraExplicita()).Handle(new DescartarSugerenciaGestionCorreoCommand(ajeno.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("SugerenciaGestionCorreo.NoEncontrada", "fuera de cartera no se delata que existe");
        ajeno.Resuelta.Should().BeFalse();
        _unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Un_Gestor_con_cartera_explicita_descarta_el_de_su_cartera_sin_tocar_los_demas_items()
    {
        var propio = SembrarEnConversacionDe(_clienteDeMiCartera);
        var otroItemDelMismoCorreo = _contexto.ListaSugerenciasGestionCorreo.Single().AgregarDetalle(Guid.NewGuid(), Guid.NewGuid(), 80, 80);

        var resultado = await Crear(GestorConCarteraExplicita()).Handle(new DescartarSugerenciaGestionCorreoCommand(propio.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        propio.Resolucion.Should().Be(ResolucionSugerencia.Descartada);
        otroItemDelMismoCorreo.Resuelta.Should().BeFalse();
        _unitOfWork.VecesGuardado.Should().Be(1);
    }

    [Fact]
    public async Task Sin_restriccion_de_cartera_descarta_el_de_cualquier_Cliente_empresarial()
    {
        var ajeno = SembrarEnConversacionDe(_clienteDeOtraCartera);

        var resultado = await Crear(new AlcanceDatosServiceFalso(tieneAccesoTotal: true)).Handle(
            new DescartarSugerenciaGestionCorreoCommand(ajeno.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        ajeno.Resolucion.Should().Be(ResolucionSugerencia.Descartada);
    }

    private sealed class DetallesFalsos : IDetalleSugerenciaGestionCorreoRepository
    {
        public List<DetalleSugerenciaGestionCorreo> Lista { get; } = [];

        public Task<DetalleSugerenciaGestionCorreo?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Lista.FirstOrDefault(d => d.Id == id));
    }
}
