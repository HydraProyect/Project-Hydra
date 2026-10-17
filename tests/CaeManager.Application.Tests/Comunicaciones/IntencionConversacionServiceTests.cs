using CaeManager.Application.Comunicaciones.Deteccion;
using CaeManager.Application.Cumplimiento;
using CaeManager.Application.Tests.Configuracion;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using FluentAssertions;

namespace CaeManager.Application.Tests.Comunicaciones;

public class IntencionConversacionServiceTests
{
    [Theory]
    [InlineData(false, false, 0, IntencionCae.CoordinacionAcceso)]
    [InlineData(true, false, 1, IntencionCae.CoordinacionAcceso)]
    [InlineData(true, true, 1, IntencionCae.UrgenciaAcceso)]
    public async Task Usa_IA_con_instruccion_vigente_y_reglas_si_no_esta_disponible(
        bool instruccion, bool iaDisponible, int llamadas, IntencionCae esperada)
    {
        var ia = new DeteccionFalsa(iaDisponible);
        var repositorio = new RepositorioFalso();
        var servicio = Crear(ia, repositorio, instruccion);
        var conversacion = new Conversacion("Coordinación de acceso");
        conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cae@example.test", "Coordinar acceso");
        await servicio.ProcesarAsync(conversacion);
        ia.Llamadas.Should().Be(llamadas);
        repositorio.Intenciones.Should().ContainSingle().Which.IntencionActual.Should().Be(esperada);
        repositorio.Intenciones[0].ConfirmadaPorPersona.Should().BeFalse();
    }

    [Fact]
    public async Task Sin_Tenant_propietario_no_clasifica_ni_consulta_entidades()
    {
        var ia = new DeteccionFalsa(true);
        var repositorio = new RepositorioFalso();
        var servicio = new IntencionConversacionService(ia, repositorio, new InstruccionFalsa(true),
            new TenantActualFijo(null), null!, null!, null!, null!);
        var conversacion = new Conversacion("Acceso");
        conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cae@example.test", "Acceso bloqueado");
        await servicio.ProcesarAsync(conversacion);
        ia.Llamadas.Should().Be(0);
        repositorio.Intenciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Solo_clasifica_el_primer_entrante_y_no_sobrescribe_una_confirmacion()
    {
        var ia = new DeteccionFalsa(true);
        var repositorio = new RepositorioFalso();
        var servicio = Crear(ia, repositorio, true);
        var conversacion = new Conversacion("Hilo");
        conversacion.AgregarMensaje(DireccionMensaje.Saliente, CanalConversacion.Correo, "cae@example.test", "Hola");
        await servicio.ProcesarAsync(conversacion);
        ia.Llamadas.Should().Be(0);
        conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cae@example.test", "Acceso bloqueado");
        await servicio.ProcesarAsync(conversacion);
        repositorio.Intenciones[0].Confirmar(IntencionCae.DocumentacionMensual);
        await servicio.ProcesarAsync(conversacion);
        conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cae@example.test", "Consulta de requisitos");
        await servicio.ProcesarAsync(conversacion);
        ia.Llamadas.Should().Be(1);
        repositorio.Intenciones.Should().ContainSingle().Which.IntencionActual.Should().Be(IntencionCae.DocumentacionMensual);
    }

    private static IntencionConversacionService Crear(DeteccionFalsa ia, RepositorioFalso repositorio, bool instruccion) =>
        new(ia, repositorio, new InstruccionFalsa(instruccion), new TenantActualFijo(Guid.NewGuid()),
            new Plantillas.TrabajadoresQueryContextFalso(), new Documentos.EmpresasQueryContextFalso(),
            new Plantillas.CentrosQueryContextFalso(), new TiposDocumento.TiposDocumentoQueryContextFalso());

    private sealed class InstruccionFalsa(bool habilitada) : IInstruccionTratamientoIaService
    {
        public Task<bool> EstaHabilitadaAsync(Guid tenantId, CancellationToken cancellationToken = default) => Task.FromResult(habilitada);
    }

    private sealed class DeteccionFalsa(bool disponible) : IDeteccionIntencionCaeService
    {
        public int Llamadas { get; private set; }
        public Task<Result<PropuestaIntencionCae>> DetectarAsync(string texto, CancellationToken cancellationToken = default)
        {
            Llamadas++;
            return Task.FromResult(disponible
                ? Result.Exito(new PropuestaIntencionCae(IntencionCae.UrgenciaAcceso, ConfianzaIntencion.Media))
                : Result.Fallo<PropuestaIntencionCae>(Error.Crear("IA.NoConfigurada", "Sin configuración IA.")));
        }
    }

    private sealed class RepositorioFalso : IIntencionConversacionRepository
    {
        public List<IntencionConversacion> Intenciones { get; } = [];
        public void Agregar(IntencionConversacion intencion) => Intenciones.Add(intencion);
        public Task<IntencionConversacion?> ObtenerPorConversacionIdAsync(Guid id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Intenciones.SingleOrDefault(i => i.ConversacionId == id));
    }
}
