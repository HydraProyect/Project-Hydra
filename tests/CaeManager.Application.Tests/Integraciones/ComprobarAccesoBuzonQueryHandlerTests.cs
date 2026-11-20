using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Commands.ReactivarConexion;
using CaeManager.Application.Integraciones.Queries.ComprobarAccesoBuzon;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Integraciones;

/// <summary>
/// «Comprobar acceso» de una fila de Buzones: la guarda de «Reconectar» (Administrador efectivo antes de leer nada, conexión
/// visible), ok / sin acceso / ocupado, y que no cambia la conexión aunque Microsoft diga que no hay acceso.
/// </summary>
public class ComprobarAccesoBuzonQueryHandlerTests
{
    private const string Directorio = "11111111-2222-3333-4444-555555555555";

    private sealed class Entorno
    {
        public ConexionIntegracionRepositorioFalso Repositorio { get; } = new();
        public IntegracionesQueryContextFalso QueryContext { get; } = new();
        public Microsoft365GraphClientFalso Graph { get; } = new() { IdentidadBuzonPropio = "inbox-x" };
        public UnitOfWorkFalso UnitOfWork { get; } = new();
        public DescarteContado Descarte { get; } = new();

        public ConexionIntegracion Agregar(ConexionIntegracion conexion)
        {
            Repositorio.Agregar(conexion);
            QueryContext.ConexionesLista.Add(conexion);
            return conexion;
        }

        public ComprobarAccesoBuzonQueryHandler Handler(string? rol = "Administrador", AlcanceDatosServiceFalso? alcance = null) =>
            new(Repositorio, QueryContext, alcance ?? new AlcanceDatosServiceFalso(), new CurrentUserServiceFalso(Guid.NewGuid(), rol),
                new AccesoGraphService(new CredencialIntegracionRepositorioFalso { CredencialParaCualquierConexion = true }, Graph),
                Graph, UnitOfWork, Descarte);
    }

    private sealed class DescarteContado : IDescarteCambiosPendientes
    {
        public int Veces { get; private set; }

        public void DescartarCambiosPendientes() => Veces++;
    }

    private static ConexionIntegracion Personal(string identidad = "inbox-x")
    {
        var conexion = new ConexionIntegracion("cae@operador.test", "Buzón CAE");
        conexion.FijarIdentidadBuzon(identidad);
        return conexion;
    }

    private static ConexionIntegracion Compartido(string direccion = "compartido@operador.test")
    {
        var conexion = ConexionIntegracion.CrearBuzonCompartido(direccion, "Compartido", Directorio, Guid.NewGuid());
        conexion.FijarIdentidadBuzon($"inbox-{direccion}");
        return conexion;
    }

    /// <summary>
    /// La misma guarda que «Reconectar»: sin rol efectivo Administrador no se lee la conexión, no se pide token ni se llama a
    /// Graph, y no se guarda nada.
    /// </summary>
    [Theory]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    [InlineData("Consulta")]
    [InlineData(null)]
    public async Task Sin_rol_efectivo_Administrador_no_comprueba_ni_llama_a_Microsoft(string? rol)
    {
        var entorno = new Entorno();
        var conexion = entorno.Agregar(Personal());

        var resultado = await entorno.Handler(rol).Handle(new ComprobarAccesoBuzonQuery(conexion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Integraciones.Buzon.SoloAdministrador");
        entorno.Graph.VecesRefrescado.Should().Be(0);
        entorno.Graph.DirectoriosConTokenDeAplicacion.Should().BeEmpty();
        entorno.Graph.TokensDeIdentidad.Should().BeEmpty();
        entorno.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Sin_rol_efectivo_Administrador_una_conexion_inexistente_no_se_distingue()
    {
        var resultado = await new Entorno().Handler("GestorCae").Handle(new ComprobarAccesoBuzonQuery(Guid.NewGuid()), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("Integraciones.Buzon.SoloAdministrador");
    }

    [Fact]
    public async Task Una_conexion_no_visible_para_quien_pregunta_no_se_comprueba()
    {
        var entorno = new Entorno();
        var conexion = entorno.Agregar(Personal());

        var resultado = await entorno.Handler(alcance: new AlcanceDatosServiceFalso(conexionIntegracionVisible: false))
            .Handle(new ComprobarAccesoBuzonQuery(conexion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("ConexionIntegracion.NoEncontrada");
        entorno.Graph.TokensDeIdentidad.Should().BeEmpty();
    }

    [Fact]
    public async Task Una_conexion_de_un_Cliente_fuera_de_la_cartera_no_se_comprueba()
    {
        var entorno = new Entorno();
        var conexion = new ConexionIntegracion("cliente@operador.test", "Buzón del Cliente", clienteId: Guid.NewGuid());
        conexion.FijarIdentidadBuzon("inbox-x");
        entorno.Agregar(conexion);

        var resultado = await entorno.Handler(alcance: new AlcanceDatosServiceFalso(tieneAccesoTotal: false, clienteIdsVisibles: []))
            .Handle(new ComprobarAccesoBuzonQuery(conexion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be("ConexionIntegracion.NoEncontrada");
        entorno.Graph.VecesRefrescado.Should().Be(0);
        entorno.Graph.TokensDeIdentidad.Should().BeEmpty();
    }

    /// <summary>Una desconectada no se abre en Microsoft: no se refresca su credencial ni se da un «ok» que TALVEG no lee.</summary>
    [Fact]
    public async Task Una_conexion_desconectada_que_nadie_ocupa_pide_reconectar_sin_llamar_a_Microsoft()
    {
        var entorno = new Entorno();
        var conexion = entorno.Agregar(Personal());
        conexion.Deshabilitar();

        var resultado = await entorno.Handler().Handle(new ComprobarAccesoBuzonQuery(conexion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ReactivarConexionCommandHandler.CodigoReconexionNecesaria);
        entorno.Graph.VecesRefrescado.Should().Be(0);
        entorno.Graph.TokensDeIdentidad.Should().BeEmpty();
        entorno.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Si_no_se_puede_guardar_el_token_rotado_se_descarta_y_no_queda_rastreado()
    {
        var entorno = new Entorno();
        var conexion = entorno.Agregar(Personal());
        entorno.UnitOfWork.ExcepcionAlGuardar = new InvalidOperationException("conflicto simulado");

        var comprobar = () => entorno.Handler().Handle(new ComprobarAccesoBuzonQuery(conexion.Id), CancellationToken.None);

        await comprobar.Should().ThrowAsync<InvalidOperationException>();
        entorno.Descarte.Veces.Should().Be(1);
    }

    [Fact]
    public async Task Si_la_llamada_a_Microsoft_se_corta_tras_refrescar_la_credencial_rotada_se_descarta()
    {
        var entorno = new Entorno();
        var conexion = entorno.Agregar(Personal());
        entorno.Graph.AlLeerIdentidad = () => throw new OperationCanceledException();

        var comprobar = () => entorno.Handler().Handle(new ComprobarAccesoBuzonQuery(conexion.Id), CancellationToken.None);

        await comprobar.Should().ThrowAsync<OperationCanceledException>();
        entorno.Graph.VecesRefrescado.Should().Be(1);
        entorno.Descarte.Veces.Should().Be(1);
        entorno.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Un_buzon_personal_que_responde_con_su_identidad_da_ok_y_guarda_el_token_rotado()
    {
        var entorno = new Entorno();
        var conexion = entorno.Agregar(Personal());

        var resultado = await entorno.Handler().Handle(new ComprobarAccesoBuzonQuery(conexion.Id), CancellationToken.None);

        resultado.Valor.Should().Be(ResultadoComprobacionAccesoBuzon.Ok);
        entorno.Graph.BuzonesUsados.Should().ContainSingle().Which.Should().Be(new BuzonGraph("cae@operador.test", false));
        entorno.Graph.VecesRefrescado.Should().Be(1, "sin access token cacheado, se refresca");
        entorno.UnitOfWork.VecesGuardado.Should().Be(1, "el refresh token rotado se persiste, como en ObtenerEstructuraBuzonQuery");
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Habilitada);
    }

    [Fact]
    public async Task Un_compartido_se_abre_con_el_token_de_aplicacion_de_su_directorio_y_no_guarda_nada()
    {
        var entorno = new Entorno();
        var conexion = entorno.Agregar(Compartido());

        var resultado = await entorno.Handler().Handle(new ComprobarAccesoBuzonQuery(conexion.Id), CancellationToken.None);

        resultado.Valor.Should().Be(ResultadoComprobacionAccesoBuzon.Ok);
        entorno.Graph.DirectoriosConTokenDeAplicacion.Should().Equal(Directorio);
        entorno.Graph.TokensDeIdentidad.Should().Equal("token-aplicacion-falso");
        entorno.Graph.VecesRefrescado.Should().Be(0, "un compartido nunca cae en el token de una persona");
        entorno.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>
    /// 403/404 en la Bandeja de entrada es «sin acceso», y la comprobación no tiene efectos: a diferencia de «Reconectar», la
    /// conexión no pasa a ConError ni cambia su último error.
    /// </summary>
    [Theory]
    [InlineData(CodigosErrorMicrosoft365.SinAccesoBuzon)]
    [InlineData(CodigosErrorMicrosoft365.SinBuzonExchange)]
    public async Task Un_403_o_404_en_la_Bandeja_es_sin_acceso_y_no_cambia_la_conexion(string codigo)
    {
        var entorno = new Entorno();
        var conexion = entorno.Agregar(Compartido());
        entorno.Graph.ErrorIdentidadBuzon = Error.Crear(codigo, "403");

        var resultado = await entorno.Handler().Handle(new ComprobarAccesoBuzonQuery(conexion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.Should().Be(ResultadoComprobacionAccesoBuzon.SinAcceso);
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Habilitada);
        conexion.UltimoError.Should().BeNull();
        entorno.UnitOfWork.VecesGuardado.Should().Be(0);
    }

    /// <summary>Un fallo que no dice nada del acceso (429, 5xx, red) no se presenta como «sin acceso»: se pide repetir.</summary>
    [Fact]
    public async Task Un_fallo_transitorio_de_Microsoft_no_es_sin_acceso()
    {
        var entorno = new Entorno();
        var conexion = entorno.Agregar(Compartido());
        entorno.Graph.ErrorIdentidadBuzon = Error.Crear(CodigosErrorMicrosoft365.Transitorio, "429");

        var resultado = await entorno.Handler().Handle(new ComprobarAccesoBuzonQuery(conexion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ReactivarConexionCommandHandler.CodigoSinComprobacion);
    }

    [Fact]
    public async Task Sin_token_de_aplicacion_no_se_puede_comprobar_y_no_se_llama_a_Graph()
    {
        var entorno = new Entorno();
        var conexion = entorno.Agregar(Compartido());
        entorno.Graph.FallaTokenAplicacion = true;

        var resultado = await entorno.Handler().Handle(new ComprobarAccesoBuzonQuery(conexion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ReactivarConexionCommandHandler.CodigoSinComprobacion);
        entorno.Graph.TokensDeIdentidad.Should().BeEmpty();
    }

    [Fact]
    public async Task Si_la_direccion_responde_con_otro_buzon_no_da_ok()
    {
        var entorno = new Entorno();
        var conexion = entorno.Agregar(Personal(identidad: "inbox-anterior"));

        var resultado = await entorno.Handler().Handle(new ComprobarAccesoBuzonQuery(conexion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ReactivarConexionCommandHandler.CodigoReconexionNecesaria);
        conexion.Estado.Should().Be(EstadoConexionIntegracion.Habilitada, "comprobar no deja la conexión en ConError");
    }

    [Fact]
    public async Task Sin_identidad_guardada_no_se_compara_con_nada_ni_se_llama_a_Graph()
    {
        var entorno = new Entorno();
        var conexion = entorno.Agregar(new ConexionIntegracion("cae@operador.test", "Buzón CAE"));

        var resultado = await entorno.Handler().Handle(new ComprobarAccesoBuzonQuery(conexion.Id), CancellationToken.None);

        resultado.Error.Codigo.Should().Be(ReactivarConexionCommandHandler.CodigoReconexionNecesaria);
        entorno.Graph.TokensDeIdentidad.Should().BeEmpty();
    }

    /// <summary>
    /// «Ocupado» con el dato que ya existe: otra conexión no desconectada del mismo Tenant con la misma identidad o la misma
    /// dirección (el criterio de ConectarBuzonMicrosoft365). Se decide sin llamar a Microsoft.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Un_buzon_desconectado_que_otra_conexion_activa_del_Tenant_tiene_es_ocupado(bool mismaIdentidad)
    {
        var entorno = new Entorno();
        var desconectada = entorno.Agregar(Compartido());
        desconectada.Deshabilitar();
        var otra = mismaIdentidad
            ? ConexionIntegracion.CrearBuzonCompartido("alias@operador.test", "Alias", Directorio, Guid.NewGuid())
            : ConexionIntegracion.CrearBuzonCompartido("COMPARTIDO@operador.test", "Otra", Directorio, Guid.NewGuid());
        otra.FijarIdentidadBuzon(mismaIdentidad ? desconectada.IdentidadBuzon! : "inbox-otro");
        entorno.Agregar(otra);

        var resultado = await entorno.Handler().Handle(new ComprobarAccesoBuzonQuery(desconectada.Id), CancellationToken.None);

        resultado.Valor.Should().Be(ResultadoComprobacionAccesoBuzon.Ocupado);
        entorno.Graph.TokensDeIdentidad.Should().BeEmpty();
    }

    /// <summary>Control del anterior: una conexión desconectada con la misma dirección no ocupa el buzón.</summary>
    [Fact]
    public async Task Otra_conexion_desconectada_con_el_mismo_buzon_no_lo_ocupa()
    {
        var entorno = new Entorno();
        var conexion = entorno.Agregar(Compartido());
        var antigua = entorno.Agregar(Compartido());
        antigua.Deshabilitar();

        var resultado = await entorno.Handler().Handle(new ComprobarAccesoBuzonQuery(conexion.Id), CancellationToken.None);

        resultado.Valor.Should().Be(ResultadoComprobacionAccesoBuzon.Ok);
    }
}
