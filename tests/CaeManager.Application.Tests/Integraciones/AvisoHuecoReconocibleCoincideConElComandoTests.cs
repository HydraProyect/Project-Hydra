using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Commands.ReconocerAvisoHueco;
using CaeManager.Application.Integraciones.Queries.ObtenerConexionesIntegracion;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Comunicaciones;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

/// <summary>
/// Issue #2124, seguimiento de la revisión de la #2319: la condición de estado de Confirmar estaba escrita dos veces (en la
/// proyección <see cref="ConexionIntegracionListaDto.AvisoHuecoReconocible"/> y en el comando) y nada las ligaba. Ahora
/// hay un solo predicado, <see cref="ConexionIntegracion.EsAvisoHuecoReconocible"/>, y este test fija el predicado y sus
/// tres usos (la conexión, el listado y el comando) contra la misma tabla de estados.
/// <para>
/// Qué cuenta como «el comando reconoce»: Administrador (quien actúa, con permiso) con la marca vista correcta, y el
/// resultado es éxito <b>y</b> guardó (la marca se limpia y se guarda una vez). Sale <c>false</c> con
/// <see cref="ReconocerAvisoHuecoCommandHandler.CodigoNoReconocible"/> y también cuando no hay marca: ahí el comando
/// devuelve éxito sin guardar (otra persona confirmó antes), y no hay aviso que reconocer, así que el listado no debe
/// ofrecer Confirmar. Un éxito sin guardar no cuenta como reconocer.
/// </para>
/// </summary>
public class AvisoHuecoReconocibleCoincideConElComandoTests
{
    private static readonly DateTime Marca = new(2026, 11, 15, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Los tres estados por con y sin marca del hueco por con y sin marca por registrar: doce combinaciones. Algunas no las
    /// alcanzan las transiciones del dominio (una Habilitada nunca lleva marca por registrar; ver
    /// <see cref="ConexionIntegracion.Rehabilitar"/>), pero la base puede traerlas y las dos condiciones las deciden igual,
    /// así que se fijan por reflexión.
    /// </summary>
    public static TheoryData<EstadoConexionIntegracion, bool, bool> Combinaciones
    {
        get
        {
            var datos = new TheoryData<EstadoConexionIntegracion, bool, bool>();
            foreach (var estado in Enum.GetValues<EstadoConexionIntegracion>())
                foreach (var conMarca in new[] { false, true })
                    foreach (var porRegistrar in new[] { false, true })
                        datos.Add(estado, conMarca, porRegistrar);
            return datos;
        }
    }

    private static bool DebeSerReconocible(EstadoConexionIntegracion estado, bool conMarca, bool porRegistrar) =>
        conMarca && estado == EstadoConexionIntegracion.Habilitada && !porRegistrar;

    [Theory]
    [MemberData(nameof(Combinaciones))]
    public async Task El_listado_ofrece_Confirmar_exactamente_cuando_el_comando_reconoce(
        EstadoConexionIntegracion estado, bool conMarca, bool porRegistrar)
    {
        var cliente = Empresa.CrearComoCliente("Montajes Springfield", "B12345674", false, null, null);
        var conexion = new ConexionIntegracion("cae@operador.test", "Buzón CAE", cliente.Id);
        Fijar(conexion, nameof(ConexionIntegracion.Estado), estado);
        Fijar(conexion, nameof(ConexionIntegracion.HuecoPendienteDesdeUtc), conMarca ? Marca : null);
        Fijar(conexion, nameof(ConexionIntegracion.MarcaHuecoPorRegistrarDesdeUtc), porRegistrar ? Marca.AddHours(-1) : null);
        // Control: el estado que se prueba es el que se ha montado.
        conexion.Estado.Should().Be(estado);
        (conexion.HuecoPendienteDesdeUtc is not null).Should().Be(conMarca);
        (conexion.MarcaHuecoPorRegistrarDesdeUtc is not null).Should().Be(porRegistrar);

        // El listado se lee antes: el comando, si reconoce, limpia la marca de la misma instancia.
        var integraciones = new IntegracionesQueryContextFalso();
        integraciones.ConexionesLista.Add(conexion);
        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaEmpresas.Add(cliente);
        var listado = await new ObtenerConexionesIntegracionQueryHandler(
                integraciones, empresas, new AlcanceDatosServiceFalso(), new CurrentUserServiceFalso(Guid.NewGuid()),
                new SinSesionPrivilegiada())
            .Handle(new ObtenerConexionesIntegracionQuery(), CancellationToken.None);
        var fila = listado.Should().ContainSingle("control: el listado trae la conexión").Subject;

        var repositorio = new ConexionIntegracionRepositorioFalso();
        repositorio.Agregar(conexion);
        var unitOfWork = new UnitOfWorkFalso();
        var resultado = await new ReconocerAvisoHuecoCommandHandler(
                repositorio, new AlcanceDatosServiceFalso(), new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador"),
                new CerrojoVerificacionBuzonFalso(), unitOfWork, new DescarteNulo())
            .Handle(new ReconocerAvisoHuecoCommand(conexion.Id, Marca), CancellationToken.None);

        var reconoce = resultado.EsExitoso && unitOfWork.VecesGuardado == 1;
        var esperado = DebeSerReconocible(estado, conMarca, porRegistrar);
        var contexto = $"{estado}, con marca: {conMarca}, con marca por registrar: {porRegistrar}";

        reconoce.Should().Be(esperado, "el comando reconoce solo en el estado reconocible: " + contexto);
        fila.AvisoHuecoReconocible.Should().Be(reconoce, "el listado y el comando deciden lo mismo: " + contexto);
        fila.AvisoHuecoReconocible.Should().Be(esperado, contexto);
        if (!reconoce && conMarca)
            resultado.Error.Codigo.Should().Be(ReconocerAvisoHuecoCommandHandler.CodigoNoReconocible, "con marca, el rechazo es de estado: " + contexto);
        if (!conMarca)
        {
            resultado.EsExitoso.Should().BeTrue("sin marca el comando devuelve éxito sin guardar: " + contexto);
            unitOfWork.VecesGuardado.Should().Be(0, contexto);
        }
    }

    /// <summary>El predicado, la propiedad de la conexión y la proyección del listado dan la misma tabla.</summary>
    [Theory]
    [MemberData(nameof(Combinaciones))]
    public void El_predicado_de_la_conexion_y_la_proyeccion_del_listado_dan_la_misma_tabla(
        EstadoConexionIntegracion estado, bool conMarca, bool porRegistrar)
    {
        var esperado = DebeSerReconocible(estado, conMarca, porRegistrar);
        var contexto = $"{estado}, con marca: {conMarca}, con marca por registrar: {porRegistrar}";

        ConexionIntegracion.EsAvisoHuecoReconocible(conMarca ? Marca : null, estado, porRegistrar)
            .Should().Be(esperado, "predicado: " + contexto);

        var conexion = new ConexionIntegracion("cae@operador.test", "Buzón CAE");
        Fijar(conexion, nameof(ConexionIntegracion.Estado), estado);
        Fijar(conexion, nameof(ConexionIntegracion.HuecoPendienteDesdeUtc), conMarca ? Marca : null);
        Fijar(conexion, nameof(ConexionIntegracion.MarcaHuecoPorRegistrarDesdeUtc), porRegistrar ? Marca.AddHours(-1) : null);
        conexion.AvisoHuecoReconocible.Should().Be(esperado, "conexión: " + contexto);

        new ConexionIntegracionListaDto(
                conexion.Id, conexion.BuzonEmail, conexion.Nombre, null, null, estado, DateTime.UtcNow, null, null,
                CorreoNoRecuperadoDesdeUtc: conMarca ? Marca : null, HuecoSinRegistrar: porRegistrar)
            .AvisoHuecoReconocible.Should().Be(esperado, "listado: " + contexto);

        // El método de la conexión rechaza lo mismo que el predicado: con marca y no reconocible, lanza; sin marca, no hace nada.
        if (!conMarca)
            conexion.ReconocerAvisoHueco().Should().BeFalse(contexto);
        else if (esperado)
            conexion.ReconocerAvisoHueco().Should().BeTrue(contexto);
        else
            FluentActions.Invoking(() => conexion.ReconocerAvisoHueco()).Should().Throw<InvalidOperationException>(contexto);
    }

    private static void Fijar(ConexionIntegracion conexion, string propiedad, object? valor) =>
        typeof(ConexionIntegracion).GetProperty(propiedad)!.SetValue(conexion, valor);

    private sealed class DescarteNulo : IDescarteCambiosPendientes
    {
        public void DescartarCambiosPendientes() { }
    }

    private sealed class SinSesionPrivilegiada : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }
}
