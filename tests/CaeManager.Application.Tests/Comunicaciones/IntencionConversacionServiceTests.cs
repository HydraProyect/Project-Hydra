using CaeManager.Application.Comunicaciones.Deteccion;
using CaeManager.Application.Cumplimiento;
using CaeManager.Application.Tests.Configuracion;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Application.Tests.Comunicaciones;

public class IntencionConversacionServiceTests
{
    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 1)]
    public async Task Sin_Nivel_0_o_sin_respuesta_de_la_IA_no_propone_nada(bool instruccion, bool iaDisponible, int llamadas)
    {
        var ia = new DeteccionFalsa(iaDisponible);
        var repositorio = new RepositorioFalso();
        var servicio = Crear(ia, repositorio, instruccion);
        await servicio.ProcesarAsync(ConversacionConEntrante("Coordinar acceso"));
        ia.Llamadas.Should().Be(llamadas);
        repositorio.Intenciones.Should().BeEmpty();
    }

    [Fact]
    public async Task Con_Nivel_0_y_IA_propone_sin_confirmar()
    {
        var ia = new DeteccionFalsa(true);
        var repositorio = new RepositorioFalso();
        await Crear(ia, repositorio, true).ProcesarAsync(ConversacionConEntrante("Coordinar acceso"));
        repositorio.Intenciones.Should().ContainSingle().Which.IntencionActual.Should().Be(IntencionCae.UrgenciaAcceso);
        repositorio.Intenciones[0].ConfirmadaPorPersona.Should().BeFalse();
    }

    [Fact]
    public async Task Sin_Cliente_empresarial_ni_Empresa_no_sale_al_proveedor()
    {
        var ia = new DeteccionFalsa(true);
        var repositorio = new RepositorioFalso();
        var conversacion = new Conversacion("Acceso");
        conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cae@example.test", "Acceso bloqueado");
        await Crear(ia, repositorio, true).ProcesarAsync(conversacion);
        ia.Llamadas.Should().Be(0);
        repositorio.Intenciones.Should().BeEmpty();
    }

    private static Conversacion ConversacionConEntrante(string cuerpo)
    {
        var conversacion = new Conversacion("Coordinación de acceso");
        conversacion.AsignarCliente(Guid.NewGuid());
        conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cae@example.test", cuerpo);
        return conversacion;
    }

    [Fact]
    public async Task Sin_Tenant_propietario_no_clasifica_ni_consulta_entidades()
    {
        var ia = new DeteccionFalsa(true);
        var repositorio = new RepositorioFalso();
        var servicio = new IntencionConversacionService(ia, repositorio, new InstruccionFalsa(true),
            new TenantActualFijo(null), null!, null!, null!, null!, NullLogger<IntencionConversacionService>.Instance);
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
        conversacion.AsignarCliente(Guid.NewGuid());
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
            new Plantillas.CentrosQueryContextFalso(), new TiposDocumento.TiposDocumentoQueryContextFalso(),
            NullLogger<IntencionConversacionService>.Instance);

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
