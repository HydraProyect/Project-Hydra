using CaeManager.Application.Comunicaciones.Commands.ConfirmarIntencionConversacion;
using CaeManager.Application.Comunicaciones.Deteccion;
using CaeManager.Application.Comunicaciones.Queries.ObtenerIntencionConversacion;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Comunicaciones;
using FluentAssertions;

namespace CaeManager.Application.Tests.Comunicaciones;

public class IntencionConversacionTests
{
    [Theory]
    [InlineData("Coordinación de acceso", IntencionCae.CoordinacionAcceso)]
    [InlineData("Documentación mensual", IntencionCae.DocumentacionMensual)]
    [InlineData("Alta de contrato", IntencionCae.AltaContratoAccesoCentro)]
    [InlineData("Os ponemos en copia en esta obra", IntencionCae.CopiaProyectoObra)]
    [InlineData("Técnico de emergencia: acceso bloqueado", IntencionCae.UrgenciaAcceso)]
    [InlineData("¿Qué falta para el trabajador extranjero?", IntencionCae.ConsultaRequisitos)]
    public void Las_reglas_cubren_el_catalogo_sin_IA(string texto, IntencionCae esperada)
    {
        var propuesta = IntencionConversacionService.ClasificarPorReglas(texto);
        propuesta.Intencion.Should().Be(esperada);
        propuesta.Confianza.Should().Be(ConfianzaIntencion.Alta);
    }

    [Fact]
    public void La_ambiguedad_y_la_ausencia_de_evidencia_no_tienen_confianza_alta()
    {
        IntencionConversacionService.ClasificarPorReglas("Documentación mensual y acceso bloqueado").Confianza.Should().Be(ConfianzaIntencion.Media);
        IntencionConversacionService.ClasificarPorReglas("Buenos días").Should().Be(new PropuestaIntencionCae(IntencionCae.Otras, ConfianzaIntencion.Baja));
    }

    [Fact]
    public void Solo_propone_una_entidad_inequivoca_y_con_limites_de_palabra()
    {
        var candidato = new IntencionConversacionService.CandidatoEntidadIntencion(Guid.NewGuid(), TipoEntidadIntencion.Empresa, "Arcos");
        IntencionConversacionService.ResolverEntidad("Documentación de ARCOS.", [candidato]).Should().Be(candidato);
        IntencionConversacionService.ResolverEntidad("Barcos", [candidato]).Should().BeNull();
        IntencionConversacionService.ResolverEntidad("Arcos", [candidato, candidato with { Id = Guid.NewGuid() }]).Should().BeNull();
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Sin_cartera_o_con_buzon_ajeno_no_se_lee_ni_confirma(bool cartera, bool buzon)
    {
        var conversacion = new Conversacion("Acceso", Guid.NewGuid());
        conversacion.AsociarConexion(Guid.NewGuid(), "hilo");
        var conversaciones = new ConversacionRepositorioFalso();
        conversaciones.Agregar(conversacion);
        var intenciones = new RepositorioFalso(new IntencionConversacion(conversacion.Id, Guid.NewGuid(), IntencionCae.Otras, ConfianzaIntencion.Baja));
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: cartera, conexionIntegracionVisible: buzon);
        var unidad = new UnitOfWorkFalso();
        var resultado = await new ConfirmarIntencionConversacionCommandHandler(conversaciones, intenciones, alcance, unidad)
            .Handle(new(conversacion.Id, IntencionCae.UrgenciaAcceso), default);
        resultado.EsFallido.Should().BeTrue();
        unidad.VecesGuardado.Should().Be(0);
        intenciones.Intencion.ConfirmadaPorPersona.Should().BeFalse();
        (await new ObtenerIntencionConversacionQueryHandler(conversaciones, intenciones, alcance).Handle(new(conversacion.Id), default)).Should().BeNull();
    }

    [Fact]
    public async Task La_intencion_visible_no_expone_una_entidad_fuera_de_cartera()
    {
        var empresaId = Guid.NewGuid();
        var conversacion = new Conversacion("Acceso", empresaId);
        var conversaciones = new ConversacionRepositorioFalso();
        conversaciones.Agregar(conversacion);
        var intencion = new IntencionConversacion(conversacion.Id, Guid.NewGuid(), IntencionCae.CoordinacionAcceso,
            ConfianzaIntencion.Alta, TipoEntidadIntencion.Trabajador, Guid.NewGuid());
        var alcance = new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: [empresaId], trabajadorIdsVisibles: []);
        var resultado = await new ObtenerIntencionConversacionQueryHandler(conversaciones, new RepositorioFalso(intencion), alcance)
            .Handle(new(conversacion.Id), default);
        resultado.Should().NotBeNull();
        resultado!.IntencionActual.Should().Be(IntencionCae.CoordinacionAcceso);
        resultado.EntidadPropuestaId.Should().BeNull();
        resultado.TipoEntidadPropuesta.Should().BeNull();
    }

    [Fact]
    public async Task Con_cartera_confirma_y_persiste_la_correccion()
    {
        var conversacion = new Conversacion("Acceso", Guid.NewGuid());
        var conversaciones = new ConversacionRepositorioFalso();
        conversaciones.Agregar(conversacion);
        var intenciones = new RepositorioFalso(new IntencionConversacion(conversacion.Id, Guid.NewGuid(), IntencionCae.Otras, ConfianzaIntencion.Baja));
        var unidad = new UnitOfWorkFalso();
        var resultado = await new ConfirmarIntencionConversacionCommandHandler(conversaciones, intenciones, new AlcanceDatosServiceFalso(), unidad)
            .Handle(new(conversacion.Id, IntencionCae.DocumentacionMensual), default);
        resultado.EsExitoso.Should().BeTrue();
        unidad.VecesGuardado.Should().Be(1);
        intenciones.Intencion.CambiadaPorPersona.Should().BeTrue();
    }

    private sealed class RepositorioFalso(IntencionConversacion intencion) : IIntencionConversacionRepository
    {
        public IntencionConversacion Intencion => intencion;
        public void Agregar(IntencionConversacion nueva) => throw new NotSupportedException();
        public Task<IntencionConversacion?> ObtenerPorConversacionIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult<IntencionConversacion?>(id == intencion.ConversacionId ? intencion : null);
    }
}
